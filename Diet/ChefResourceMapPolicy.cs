using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using ServerSync;
using YamlDotNet.RepresentationModel;

namespace FineDining;

internal sealed class ChefResourceMapSnapshot
{
    private readonly string[] _tierNames;
    private readonly Dictionary<string, int> _resourceTiers;

    internal ChefResourceMapSnapshot(
        IEnumerable<string> tierNames,
        IDictionary<string, int> resourceTiers)
    {
        _tierNames = (tierNames ?? Enumerable.Empty<string>()).ToArray();
        _resourceTiers = new Dictionary<string, int>(
            resourceTiers ?? new Dictionary<string, int>(),
            StringComparer.OrdinalIgnoreCase);
    }

    internal int TierCount => _tierNames.Length;

    internal int ResourceCount => _resourceTiers.Count;

    internal IReadOnlyList<string> TierNames => _tierNames;

    internal bool TryGetResourceTier(string? token, out int tier) =>
        _resourceTiers.TryGetValue(
            ChefResourceMapPolicy.NormalizeResourceToken(token),
            out tier);

    internal string GetTierName(int tier) =>
        tier >= 0 && tier < _tierNames.Length
            ? _tierNames[tier]
            : "AllTier";

    internal bool ContentEquals(ChefResourceMapSnapshot? other)
    {
        if (other == null
            || !_tierNames.SequenceEqual(other._tierNames, StringComparer.Ordinal)
            || _resourceTiers.Count != other._resourceTiers.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, int> entry in _resourceTiers)
        {
            if (!other._resourceTiers.TryGetValue(entry.Key, out int tier)
                || tier != entry.Value)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Owns the editable, server-authoritative ResourceMap.yml used by Chef's
/// Choice. File IO and synchronization stay separate from the prefab graph
/// resolver so a malformed edit cannot partially mutate a catalog rebuild.
/// </summary>
internal static class ChefResourceMapPolicy
{
    internal const string ResourceMapFileName = "ResourceMap.yml";
    internal const string DefaultResourceMapResourceName =
        "FineDining.Resources.Defaults.ResourceMap.yml";
    internal const string SyncedYamlIdentifier = "finedining_resource_map_yaml";

    private const double ReloadDebounceMilliseconds = 350d;

    private static readonly Lazy<string> DefaultResourceMap =
        new(LoadDefaultResourceMapYaml);

    private static ConfigSync? _configSync;
    private static ConfigSync? _registeredConfigSync;
    private static CustomSyncedValue<string>? _syncedYaml;
    private static FileSystemWatcher? _watcher;
    private static System.Timers.Timer? _reloadTimer;
    private static AuthorityMode _authorityMode;
    private static ChefResourceMapSnapshot? _snapshot;
    private static bool _isReady;

    private enum AuthorityMode
    {
        Unknown,
        LocalFiles,
        SyncedOnly
    }

    internal static string ResourceMapFilePath =>
        Path.Combine(FineDiningPlugin.ConfigDirectoryPath, ResourceMapFileName);

    internal static string DefaultResourceMapYaml => DefaultResourceMap.Value;

    internal static bool IsReady => _isReady && _snapshot != null;

    internal static int Version { get; private set; }

    internal static void Initialize(ConfigSync sync)
    {
        if (sync == null)
        {
            throw new ArgumentNullException(nameof(sync));
        }

        Shutdown();
        _configSync = sync;
        if (_syncedYaml == null)
        {
            _syncedYaml = new CustomSyncedValue<string>(
                sync,
                SyncedYamlIdentifier,
                string.Empty);
            _registeredConfigSync = sync;
        }
        else if (!ReferenceEquals(_registeredConfigSync, sync))
        {
            throw new InvalidOperationException(
                "ResourceMap.yml cannot be rebound to a different ConfigSync instance.");
        }

        // CustomSyncedValue must subscribe first so its internal ownership flag
        // is current before our authority callback publishes a local file.
        _syncedYaml.ValueChanged += OnSyncedYamlChanged;
        _configSync.SourceOfTruthChanged += OnSourceOfTruthChanged;
        RefreshAuthority(force: true);
    }

    internal static void Shutdown()
    {
        DisposeWatcher();

        if (_syncedYaml != null)
        {
            _syncedYaml.ValueChanged -= OnSyncedYamlChanged;
        }

        if (_configSync != null)
        {
            _configSync.SourceOfTruthChanged -= OnSourceOfTruthChanged;
            _configSync = null;
        }

        _authorityMode = AuthorityMode.Unknown;
        _snapshot = null;
        _isReady = false;
        Version = 0;
    }

    internal static void RefreshAuthority(bool force = false)
    {
        if (_configSync == null)
        {
            return;
        }

        AuthorityMode nextMode = UsesLocalAuthorityFiles()
            ? AuthorityMode.LocalFiles
            : AuthorityMode.SyncedOnly;
        bool modeChanged = nextMode != _authorityMode;
        if (!force && !modeChanged)
        {
            return;
        }

        if (modeChanged)
        {
            _snapshot = null;
            _isReady = false;
            DietModule.InvalidateChefTierCatalog();
        }

        _authorityMode = nextMode;
        switch (nextMode)
        {
            case AuthorityMode.LocalFiles:
                try
                {
                    SetupWatcher();
                }
                catch (Exception exception)
                {
                    DisposeWatcher();
                    FineDiningPlugin.Log.LogWarning(
                        $"Could not watch {ResourceMapFilePath}; startup loading remains available. " +
                        exception.GetBaseException().Message);
                }

                ReloadFromDiskAndSync();
                break;
            case AuthorityMode.SyncedOnly:
                DisposeWatcher();
                // SourceOfTruthChanged is raised before ServerSync publishes
                // the server's CustomSyncedValue. Do not apply the stale local
                // value here; ValueChanged will deliver the authoritative map.
                break;
        }
    }

    internal static bool TryGetSnapshot(out ChefResourceMapSnapshot snapshot)
    {
        if (IsReady)
        {
            snapshot = _snapshot!;
            return true;
        }

        snapshot = null!;
        return false;
    }

    internal static bool TryParseResourceMapYaml(
        string yaml,
        out ChefResourceMapSnapshot? snapshot,
        out string error)
    {
        snapshot = null;
        error = string.Empty;
        try
        {
            snapshot = ParseResourceMapYaml(yaml);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    internal static ChefResourceMapSnapshot ParseResourceMapYaml(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidDataException("ResourceMap.yml cannot be empty.");
        }

        YamlStream stream = new();
        using (StringReader reader = new(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count != 1)
        {
            throw new InvalidDataException(
                "ResourceMap.yml must contain exactly one YAML document.");
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException(
                "ResourceMap.yml root must map tier names to resource lists.");
        }

        if (root.Children.Count == 0)
        {
            throw new InvalidDataException(
                "ResourceMap.yml must contain at least one tier.");
        }

        List<string> tierNames = new(root.Children.Count);
        Dictionary<string, int> resourceTiers =
            new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> uniqueTierNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
        {
            if (entry.Key is not YamlScalarNode tierNode
                || string.IsNullOrWhiteSpace(tierNode.Value))
            {
                throw new InvalidDataException(
                    "ResourceMap.yml contains an empty or structured tier name.");
            }

            string tierName = tierNode.Value!.Trim();
            if (!uniqueTierNames.Add(tierName))
            {
                throw new InvalidDataException(
                    $"ResourceMap.yml tier '{tierName}' is duplicated with different casing.");
            }

            if (entry.Value is not YamlSequenceNode resources)
            {
                throw new InvalidDataException(
                    $"ResourceMap.yml tier '{tierName}' must contain a YAML sequence.");
            }

            int tier = tierNames.Count;
            tierNames.Add(tierName);
            foreach (YamlNode resourceNode in resources.Children)
            {
                if (resourceNode is not YamlScalarNode resourceScalar
                    || string.IsNullOrWhiteSpace(resourceScalar.Value))
                {
                    throw new InvalidDataException(
                        $"ResourceMap.yml tier '{tierName}' contains an empty or structured resource.");
                }

                string token = NormalizeResourceToken(resourceScalar.Value);
                if (token.Length == 0)
                {
                    throw new InvalidDataException(
                        $"ResourceMap.yml tier '{tierName}' contains a resource with no usable token.");
                }

                if (!resourceTiers.ContainsKey(token))
                {
                    resourceTiers.Add(token, tier);
                }
            }
        }

        return new ChefResourceMapSnapshot(tierNames, resourceTiers);
    }

    internal static string NormalizeResourceToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        string text = value!.Trim();
        const string cloneSuffix = "(Clone)";
        if (text.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(0, text.Length - cloneSuffix.Length).TrimEnd();
        }

        if (text.StartsWith("$item_", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("$item_".Length);
        }
        else if (text.StartsWith("$", StringComparison.Ordinal))
        {
            text = text.Substring(1);
        }

        return new string(text
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private static bool UsesLocalAuthorityFiles()
    {
        if (_configSync?.IsSourceOfTruth != true)
        {
            return false;
        }

        return !ZNet.HasServerHost()
               || ZNet.instance != null && ZNet.instance.IsServer();
    }

    private static void OnSourceOfTruthChanged(bool _) =>
        RefreshAuthority(force: true);

    private static void SetupWatcher()
    {
        EnsureLocalResourceMapExists();
        if (_watcher != null)
        {
            return;
        }

        _reloadTimer = new System.Timers.Timer(ReloadDebounceMilliseconds)
        {
            AutoReset = false,
            SynchronizingObject = ThreadingHelper.SynchronizingObject
        };
        _reloadTimer.Elapsed += OnReloadTimerElapsed;

        _watcher = new FileSystemWatcher(FineDiningPlugin.ConfigDirectoryPath, "*.yml")
        {
            IncludeSubdirectories = false,
            SynchronizingObject = ThreadingHelper.SynchronizingObject,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.CreationTime
        };
        _watcher.Changed += OnResourceMapFileChanged;
        _watcher.Created += OnResourceMapFileChanged;
        _watcher.Deleted += OnResourceMapFileChanged;
        _watcher.Renamed += OnResourceMapFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private static void DisposeWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnResourceMapFileChanged;
            _watcher.Created -= OnResourceMapFileChanged;
            _watcher.Deleted -= OnResourceMapFileChanged;
            _watcher.Renamed -= OnResourceMapFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        if (_reloadTimer != null)
        {
            _reloadTimer.Stop();
            _reloadTimer.Elapsed -= OnReloadTimerElapsed;
            _reloadTimer.Dispose();
            _reloadTimer = null;
        }
    }

    private static void OnResourceMapFileChanged(
        object sender,
        FileSystemEventArgs args)
    {
        if (_authorityMode != AuthorityMode.LocalFiles
            || _reloadTimer == null
            || !IsResourceMapFileChange(args))
        {
            return;
        }

        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private static bool IsResourceMapFileChange(FileSystemEventArgs args)
    {
        if (IsResourceMapFilePath(args.FullPath))
        {
            return true;
        }

        return args is RenamedEventArgs renamed
               && IsResourceMapFilePath(renamed.OldFullPath);
    }

    private static bool IsResourceMapFilePath(string path) =>
        Path.GetFileName(path).Equals(
            ResourceMapFileName,
            StringComparison.OrdinalIgnoreCase);

    private static void OnReloadTimerElapsed(
        object sender,
        System.Timers.ElapsedEventArgs args)
    {
        if (_authorityMode == AuthorityMode.LocalFiles)
        {
            ReloadFromDiskAndSync();
        }
    }

    private static void ReloadFromDiskAndSync()
    {
        if (_authorityMode != AuthorityMode.LocalFiles)
        {
            return;
        }

        try
        {
            EnsureLocalResourceMapExists();
            string yaml = File.ReadAllText(ResourceMapFilePath);
            if (!ApplyYamlText(yaml, publish: true, ResourceMapFilePath)
                && !IsReady)
            {
                ApplyBuiltInFallback();
            }
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogError(
                $"Could not reload {ResourceMapFilePath}; keeping the last-known-good resource map. " +
                exception.GetBaseException().Message);
            if (!IsReady)
            {
                ApplyBuiltInFallback();
            }
        }
    }

    private static void ApplyBuiltInFallback()
    {
        try
        {
            ApplyYamlText(
                DefaultResourceMapYaml,
                publish: true,
                "embedded FineDining ResourceMap.yml");
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogError(
                "Could not load the embedded FineDining ResourceMap.yml: " +
                exception.GetBaseException().Message);
        }
    }

    private static void ApplyCurrentSyncedYaml()
    {
        string yaml = _syncedYaml?.Value ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(yaml))
        {
            ApplyYamlText(
                yaml,
                publish: false,
                "server-synced ResourceMap.yml");
        }
    }

    private static void OnSyncedYamlChanged()
    {
        if (_authorityMode == AuthorityMode.SyncedOnly)
        {
            ApplyCurrentSyncedYaml();
        }
    }

    private static bool ApplyYamlText(
        string yaml,
        bool publish,
        string source)
    {
        if (!TryParseResourceMapYaml(
                yaml,
                out ChefResourceMapSnapshot? parsed,
                out string error))
        {
            FineDiningPlugin.Log.LogError(
                $"Could not parse {source}; keeping the last-known-good resource map. {error}");
            return false;
        }

        string normalizedYaml = NormalizeYamlText(yaml);
        CommitSnapshot(parsed!);
        if (publish
            && _syncedYaml != null
            && !string.Equals(
                _syncedYaml.Value ?? string.Empty,
                normalizedYaml,
                StringComparison.Ordinal))
        {
            _syncedYaml.AssignLocalValue(normalizedYaml);
        }

        return true;
    }

    private static void CommitSnapshot(ChefResourceMapSnapshot snapshot)
    {
        bool changed = _snapshot == null || !_snapshot.ContentEquals(snapshot);
        _snapshot = snapshot;
        _isReady = true;
        if (!changed)
        {
            return;
        }

        Version++;
        DietModule.InvalidateChefTierCatalog();
        FineDiningPlugin.Log.LogInfo(
            $"Applied ResourceMap.yml with {snapshot.TierCount} tier(s) and " +
            $"{snapshot.ResourceCount} unique resource token(s).");
    }

    private static void EnsureLocalResourceMapExists()
    {
        EnsureDefaultFileExists(ResourceMapFilePath);
    }

    internal static void EnsureDefaultFileExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A ResourceMap.yml path is required.", nameof(path));
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            return;
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.Write(DefaultResourceMapYaml);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another watcher or process created the file after our existence
            // check. Never overwrite that file; it may already contain edits.
        }
    }

    private static string LoadDefaultResourceMapYaml()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(
            DefaultResourceMapResourceName);
        if (stream == null)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{DefaultResourceMapResourceName}' was not found.");
        }

        using StreamReader reader = new(stream, Encoding.UTF8, true);
        return NormalizeYamlText(reader.ReadToEnd());
    }

    private static string NormalizeYamlText(string yaml) =>
        (yaml ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimEnd('\n') + "\n";
}
