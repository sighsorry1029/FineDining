using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using ServerSync;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FineDining;

internal static class SpoilagePolicy
{
    private const string PolicyFileName = "Spoilage.yml";
    private const string DefaultPolicyResourceName = "FineDining.Resources.Defaults.Spoilage.yml";
    private const string SyncedYamlIdentifier = "finedining_spoilage_yaml";
    internal const string KeepOriginalKeyword = "keep";
    private const int SupportedVersion = 1;
    private const double MaximumLifetimeHours = 720d;
    private const double ReloadDebounceMilliseconds = 350d;

    private static string PolicyFilePath =>
        Path.Combine(FineDiningPlugin.ConfigDirectoryPath, PolicyFileName);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithTypeConverter(new SpoilageYamlLifetimeValueConverter())
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .DisableAliases()
        .WithTypeConverter(new SpoilageYamlLifetimeValueConverter())
        .Build();

    private static ConfigSync? _configSync;
    private static CustomSyncedValue<string>? _syncedYaml;
    private static FileSystemWatcher? _watcher;
    private static System.Timers.Timer? _reloadTimer;
    private static AuthorityMode _authorityMode;
    private static NormalizedPolicy? _policy;
    private static string? _lastAppliedNormalizedYaml;
    private static bool _isReady;

    private enum AuthorityMode
    {
        Unknown,
        LocalFiles,
        SyncedOnly
    }

    internal static bool IsReady => _isReady && _policy != null;

    internal static bool IsChefChoiceBlacklisted(string? prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        return _policy?.ChefChoiceBlacklist.Contains(prefabName!.Trim()) == true;
    }

    internal static void Initialize(ConfigSync sync)
    {
        if (sync == null)
        {
            throw new ArgumentNullException(nameof(sync));
        }

        Shutdown();
        _configSync = sync;
        _configSync.SourceOfTruthChanged += OnSourceOfTruthChanged;
        _syncedYaml = new CustomSyncedValue<string>(sync, SyncedYamlIdentifier, "");
        _syncedYaml.ValueChanged += OnSyncedYamlChanged;
        RefreshAuthority(force: true);
    }

    internal static void Shutdown()
    {
        DisposeWatcher();

        if (_syncedYaml != null)
        {
            _syncedYaml.ValueChanged -= OnSyncedYamlChanged;
            _syncedYaml = null;
        }

        if (_configSync != null)
        {
            _configSync.SourceOfTruthChanged -= OnSourceOfTruthChanged;
            _configSync = null;
        }

        _authorityMode = AuthorityMode.Unknown;
        _policy = null;
        _lastAppliedNormalizedYaml = null;
        _isReady = false;
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
            _policy = null;
            _lastAppliedNormalizedYaml = null;
            _isReady = false;
            DecayRuntime.InvalidateAll();
        }

        _authorityMode = nextMode;
        switch (nextMode)
        {
            case AuthorityMode.LocalFiles:
                SetupWatcher();
                ReloadFromDiskAndSync();
                break;
            case AuthorityMode.SyncedOnly:
                DisposeWatcher();
                // SourceOfTruthChanged is raised before ServerSync publishes the
                // server's CustomSyncedValue. The current value can therefore be
                // stale local policy data; remain gated until ValueChanged.
                break;
        }
    }

    internal static ResolvedSpoilageRule Resolve(ItemDrop.ItemData? item)
    {
        NormalizedPolicy? policy = _policy;
        if (!_isReady || policy == null)
        {
            return new ResolvedSpoilageRule(SpoilageRuleState.NotReady);
        }

        if (item?.m_shared == null)
        {
            return new ResolvedSpoilageRule(SpoilageRuleState.NotTracked);
        }

        string prefabName = FoodIdentity.GetCanonicalPrefabName(item);
        // Every configured replacement is a terminal, even when a user tries
        // to add an exact positive override for it. This prevents recursive
        // spoilage chains and also protects the two built-in rotten items.
        if (prefabName.Length > 0 && policy.ReplacementPrefabs.Contains(prefabName))
        {
            return new ResolvedSpoilageRule(SpoilageRuleState.NotTracked);
        }

        if (prefabName.Length > 0 &&
            policy.Overrides.TryGetValue(prefabName, out NormalizedItemOverride? itemOverride))
        {
            if (itemOverride.LifetimeTicks == 0L)
            {
                return new ResolvedSpoilageRule(
                    SpoilageRuleState.Disabled,
                    isOverride: true,
                    expiryAction: itemOverride.ExpiryAction);
            }

            SpoilageGroup overrideGroup = SpoilageGroup.OtherEdible;
            string replacementPrefab = itemOverride.ReplacementPrefab;
            SpoilageExpiryAction expiryAction = itemOverride.ExpiryAction;
            if (!itemOverride.HasResultOverride)
            {
                if (!FoodClassifier.IsReady)
                {
                    return new ResolvedSpoilageRule(SpoilageRuleState.NotReady);
                }

                if (FoodClassifier.TryClassify(item, out overrideGroup))
                {
                    expiryAction = policy.GetExpiryAction(overrideGroup);
                    replacementPrefab = expiryAction == SpoilageExpiryAction.KeepOriginal
                        ? string.Empty
                        : SpoilageDefaults.GetReplacementPrefab(overrideGroup);
                }
                else
                {
                    expiryAction = SpoilageExpiryAction.Replace;
                    replacementPrefab = SpoilageDefaults.RottenMeatPrefabName;
                }
            }
            else if (FoodClassifier.IsReady)
            {
                FoodClassifier.TryClassify(item, out overrideGroup);
            }

            return new ResolvedSpoilageRule(
                SpoilageRuleState.Enabled,
                itemOverride.LifetimeTicks,
                replacementPrefab,
                overrideGroup,
                isOverride: true,
                expiryAction: expiryAction);
        }

        if (!FoodClassifier.IsReady)
        {
            return new ResolvedSpoilageRule(SpoilageRuleState.NotReady);
        }

        if (!FoodClassifier.TryClassify(item, out SpoilageGroup group))
        {
            return new ResolvedSpoilageRule(SpoilageRuleState.NotTracked);
        }

        long lifetimeTicks = policy.GetLifetimeTicks(group);
        SpoilageExpiryAction groupExpiryAction = policy.GetExpiryAction(group);
        string groupReplacementPrefab = groupExpiryAction == SpoilageExpiryAction.KeepOriginal
            ? string.Empty
            : SpoilageDefaults.GetReplacementPrefab(group);
        if (lifetimeTicks == 0L)
        {
            return new ResolvedSpoilageRule(
                SpoilageRuleState.Disabled,
                replacementPrefab: groupReplacementPrefab,
                group: group,
                expiryAction: groupExpiryAction);
        }

        return new ResolvedSpoilageRule(
            SpoilageRuleState.Enabled,
            lifetimeTicks,
            groupReplacementPrefab,
            group,
            expiryAction: groupExpiryAction);
    }

    internal static string NormalizeReplacementPrefabName(string? prefabName)
    {
        string normalized = FoodIdentity.NormalizePrefabName(prefabName);
        if (string.Equals(
                normalized,
                SpoilageDefaults.RottenProducePrefabName,
                StringComparison.OrdinalIgnoreCase))
        {
            return SpoilageDefaults.RottenProducePrefabName;
        }

        return string.Equals(
            normalized,
            SpoilageDefaults.RottenFoodPrefabName,
            StringComparison.OrdinalIgnoreCase)
            ? SpoilageDefaults.RottenFoodPrefabName
            : normalized;
    }

    internal static bool TryGetReferenceOverrides(
        out List<SpoilagePolicyReferenceOverride> overrides)
    {
        overrides = new List<SpoilagePolicyReferenceOverride>();
        NormalizedPolicy? policy = _policy;
        if (!_isReady || policy == null)
        {
            return false;
        }

        overrides.AddRange(policy.Overrides.Values
            .OrderBy(itemOverride => itemOverride.PrefabName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(itemOverride => itemOverride.PrefabName, StringComparer.Ordinal)
            .Select(itemOverride => new SpoilagePolicyReferenceOverride(
                itemOverride.PrefabName,
                itemOverride.Hours,
                itemOverride.LifetimeTicks,
                itemOverride.HasResultOverride
                    ? GetOverrideResultDisplay(itemOverride)
                    : string.Empty)));
        return true;
    }

    internal static bool TryGetReferenceLifetimeHours(
        string prefabName,
        SpoilageGroup group,
        bool isOverride,
        out double hours)
    {
        hours = 0d;
        NormalizedPolicy? policy = _policy;
        if (!_isReady || policy == null)
        {
            return false;
        }

        if (isOverride)
        {
            if (!policy.Overrides.TryGetValue(prefabName, out NormalizedItemOverride itemOverride))
            {
                return false;
            }

            hours = itemOverride.Hours;
            return true;
        }

        hours = policy.GetLifetimeHours(group);
        return true;
    }

    private static bool UsesLocalAuthorityFiles()
    {
        if (_configSync?.IsSourceOfTruth != true)
        {
            return false;
        }

        return !ZNet.HasServerHost() ||
               ZNet.instance != null && ZNet.instance.IsServer();
    }

    private static void OnSourceOfTruthChanged(bool _)
    {
        RefreshAuthority(force: true);
    }

    private static void SetupWatcher()
    {
        EnsureLocalPolicyFileExists();
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
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Changed += OnPolicyFileChanged;
        _watcher.Created += OnPolicyFileChanged;
        _watcher.Deleted += OnPolicyFileChanged;
        _watcher.Renamed += OnPolicyFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private static void DisposeWatcher()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
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

    private static void OnPolicyFileChanged(object sender, FileSystemEventArgs args)
    {
        if (_authorityMode != AuthorityMode.LocalFiles ||
            _reloadTimer == null ||
            !IsPolicyFileChange(args))
        {
            return;
        }

        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private static bool IsPolicyFileChange(FileSystemEventArgs args)
    {
        if (IsPolicyFilePath(args.FullPath))
        {
            return true;
        }

        return args is RenamedEventArgs renamed && IsPolicyFilePath(renamed.OldFullPath);
    }

    private static bool IsPolicyFilePath(string path)
    {
        return Path.GetFileName(path).Equals(PolicyFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void OnReloadTimerElapsed(object sender, System.Timers.ElapsedEventArgs args)
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
            EnsureLocalPolicyFileExists();
            ApplyYamlText(
                File.ReadAllText(PolicyFilePath),
                publish: true,
                PolicyFilePath);
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogError(
                $"Could not reload {PolicyFilePath}; keeping the last-known-good spoilage policy. " +
                exception.GetBaseException().Message);
        }
    }

    private static void ApplyCurrentSyncedYaml()
    {
        string yamlText = _syncedYaml?.Value ?? "";
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return;
        }

        ApplyYamlText(yamlText, publish: false, "server-synced spoilage policy");
    }

    private static void OnSyncedYamlChanged()
    {
        if (_authorityMode == AuthorityMode.SyncedOnly)
        {
            ApplyCurrentSyncedYaml();
        }
    }

    private static void ApplyYamlText(string yamlText, bool publish, string source)
    {
        if (!TryParseAndNormalize(
                yamlText,
                out NormalizedPolicy? parsedPolicy,
                out string normalizedYaml,
                out string error))
        {
            FineDiningPlugin.Log.LogError(
                $"Could not parse {source}; keeping the last-known-good spoilage policy. {error}");
            return;
        }

        CommitPolicy(parsedPolicy!, normalizedYaml);
        if (publish &&
            _syncedYaml != null &&
            !string.Equals(_syncedYaml.Value ?? "", normalizedYaml, StringComparison.Ordinal))
        {
            _syncedYaml.AssignLocalValue(normalizedYaml);
        }
    }

    private static void CommitPolicy(NormalizedPolicy policy, string normalizedYaml)
    {
        if (string.Equals(normalizedYaml, _lastAppliedNormalizedYaml, StringComparison.Ordinal) &&
            _policy != null)
        {
            _isReady = true;
            return;
        }

        _policy = policy;
        _lastAppliedNormalizedYaml = normalizedYaml;
        _isReady = true;
        DecayRuntime.InvalidateAll();
        SpoilageReferenceGenerator.Invalidate();
        DietModule.RequestChefCollectionReconcile();
        FineDiningPlugin.Log.LogInfo(
            $"Applied FineDining policy with {policy.Overrides.Count} spoilage override(s) and " +
            $"{policy.ChefChoiceBlacklist.Count} Chef Choice blacklist entry/entries.");
    }

    private static bool TryParseAndNormalize(
        string yamlText,
        out NormalizedPolicy? policy,
        out string normalizedYaml,
        out string error)
    {
        policy = null;
        normalizedYaml = "";
        error = "";

        try
        {
            if (string.IsNullOrWhiteSpace(yamlText))
            {
                throw new InvalidDataException("The policy document cannot be empty.");
            }

            SpoilageYamlDocument document =
                Deserializer.Deserialize<SpoilageYamlDocument>(yamlText) ??
                throw new InvalidDataException("The policy document cannot be null.");
            if (document.Version != SupportedVersion)
            {
                throw new InvalidDataException(
                    $"version must be {SupportedVersion}; found {document.Version}.");
            }

            SpoilageYamlLifetimes lifetimes = document.Lifetimes ??
                throw new InvalidDataException("lifetimes is required.");

            NormalizedGroupLifetime farmingHarvest = RequireGroupLifetime(
                lifetimes.FarmingHarvest,
                "lifetimes.farmingHarvest");
            NormalizedGroupLifetime cookingStationInput = RequireGroupLifetime(
                lifetimes.CookingStationInput,
                "lifetimes.cookingStationInput");
            NormalizedGroupLifetime cookingStationOutput = RequireGroupLifetime(
                lifetimes.CookingStationOutput,
                "lifetimes.cookingStationOutput");
            NormalizedGroupLifetime unfermentedFood = RequireGroupLifetime(
                lifetimes.UnfermentedFood,
                "lifetimes.unfermentedFood");
            NormalizedGroupLifetime fermentedFood = RequireGroupLifetime(
                lifetimes.FermentedFood,
                "lifetimes.fermentedFood");
            NormalizedGroupLifetime feastMaterial = RequireGroupLifetime(
                lifetimes.FeastMaterial,
                "lifetimes.feastMaterial");
            NormalizedGroupLifetime feastResult = RequireGroupLifetime(
                lifetimes.FeastResult,
                "lifetimes.feastResult");
            NormalizedGroupLifetime fish = RequireGroupLifetime(
                lifetimes.Fish,
                "lifetimes.fish");
            NormalizedGroupLifetime otherEdible = RequireGroupLifetime(
                lifetimes.OtherEdible,
                "lifetimes.otherEdible");

            Dictionary<SpoilageGroup, NormalizedGroupLifetime> normalizedLifetimes = new()
            {
                [SpoilageGroup.FarmingHarvest] = farmingHarvest,
                [SpoilageGroup.CookingStationInput] = cookingStationInput,
                [SpoilageGroup.CookingStationOutput] = cookingStationOutput,
                [SpoilageGroup.UnfermentedFood] = unfermentedFood,
                [SpoilageGroup.FermentedFood] = fermentedFood,
                [SpoilageGroup.FeastMaterial] = feastMaterial,
                [SpoilageGroup.FeastResult] = feastResult,
                [SpoilageGroup.Fish] = fish,
                [SpoilageGroup.OtherEdible] = otherEdible
            };

            if (document.Overrides == null)
            {
                throw new InvalidDataException("overrides is required and must be a sequence.");
            }

            Dictionary<string, NormalizedItemOverride> overrides =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (string? rawEntry in document.Overrides)
            {
                NormalizedItemOverride entry = NormalizeOverride(rawEntry);
                if (overrides.ContainsKey(entry.PrefabName))
                {
                    throw new InvalidDataException(
                        $"Duplicate override entry '{entry.PrefabName}'.");
                }

                overrides.Add(entry.PrefabName, entry);
            }

            HashSet<string> chefChoiceBlacklist =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (string? rawPrefab in document.ChefChoiceBlacklist ?? new List<string>())
            {
                string prefab = RequirePrefab(rawPrefab, "chefChoiceBlacklist entry");
                if (!chefChoiceBlacklist.Add(prefab))
                {
                    throw new InvalidDataException(
                        $"Duplicate Chef Choice blacklist entry '{prefab}'.");
                }
            }

            HashSet<string> replacementPrefabs = new(StringComparer.OrdinalIgnoreCase)
            {
                SpoilageDefaults.RottenMeatPrefabName,
                SpoilageDefaults.RottenProducePrefabName,
                SpoilageDefaults.RottenFoodPrefabName
            };
            foreach (NormalizedItemOverride itemOverride in overrides.Values)
            {
                if (itemOverride.LifetimeTicks > 0L &&
                    itemOverride.ExpiryAction == SpoilageExpiryAction.Replace)
                {
                    replacementPrefabs.Add(itemOverride.ReplacementPrefab);
                }
            }

            foreach (NormalizedItemOverride itemOverride in overrides.Values)
            {
                if (itemOverride.LifetimeTicks > 0L &&
                    replacementPrefabs.Contains(itemOverride.PrefabName))
                {
                    throw new InvalidDataException(
                        $"Positive override source '{itemOverride.PrefabName}' is also configured as a " +
                        "replacement terminal.");
                }
            }

            policy = new NormalizedPolicy(
                normalizedLifetimes,
                overrides,
                replacementPrefabs,
                chefChoiceBlacklist);

            SpoilageYamlDocument normalizedDocument = new()
            {
                Version = SupportedVersion,
                Lifetimes = new SpoilageYamlLifetimes
                {
                    FarmingHarvest = farmingHarvest.ToYamlValue(),
                    CookingStationInput = cookingStationInput.ToYamlValue(),
                    CookingStationOutput = cookingStationOutput.ToYamlValue(),
                    UnfermentedFood = unfermentedFood.ToYamlValue(),
                    FermentedFood = fermentedFood.ToYamlValue(),
                    FeastMaterial = feastMaterial.ToYamlValue(),
                    FeastResult = feastResult.ToYamlValue(),
                    Fish = fish.ToYamlValue(),
                    OtherEdible = otherEdible.ToYamlValue()
                },
                ChefChoiceBlacklist = chefChoiceBlacklist
                    .OrderBy(prefab => prefab, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(prefab => prefab, StringComparer.Ordinal)
                    .ToList(),
                Overrides = overrides.Values
                    .OrderBy(entry => entry.PrefabName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.PrefabName, StringComparer.Ordinal)
                    .Select(FormatOverride)
                    .ToList()
            };
            normalizedYaml = CanonicalizeYaml(Serializer.Serialize(normalizedDocument));
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static NormalizedItemOverride NormalizeOverride(string? rawEntry)
    {
        if (string.IsNullOrWhiteSpace(rawEntry))
        {
            throw new InvalidDataException("Override entries cannot be null or empty.");
        }

        string[] fields = rawEntry!.Split(new[] { ',' }, StringSplitOptions.None);
        if (fields.Length is < 2 or > 3)
        {
            throw new InvalidDataException(
                $"Override '{rawEntry}' must be '<prefab>, <hours>[, <replacement prefab or keep>]'.");
        }

        string prefab = RequirePrefab(fields[0], "override prefab");
        double hours = ParseHours(fields[1], $"Override '{prefab}' hours");
        bool hasResult = fields.Length == 3;
        string result = hasResult
            ? RequirePrefab(fields[2], $"Override '{prefab}' expiry result")
            : string.Empty;
        SpoilageExpiryAction expiryAction = string.Equals(
            result,
            KeepOriginalKeyword,
            StringComparison.OrdinalIgnoreCase)
            ? SpoilageExpiryAction.KeepOriginal
            : SpoilageExpiryAction.Replace;
        if (hours == 0d && hasResult && expiryAction != SpoilageExpiryAction.KeepOriginal)
        {
            throw new InvalidDataException(
                $"Disabled override '{prefab}' can only retain '{KeepOriginalKeyword}' as an expiry result.");
        }

        string replacement = expiryAction == SpoilageExpiryAction.KeepOriginal
            ? string.Empty
            : hasResult
                ? NormalizeReplacementPrefabName(result)
                : SpoilageDefaults.RottenMeatPrefabName;
        if (hours > 0d &&
            expiryAction == SpoilageExpiryAction.Replace &&
            prefab.Equals(replacement, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Override '{prefab}' cannot replace an item with itself.");
        }

        return new NormalizedItemOverride(
            prefab,
            hours,
            HoursToTicks(hours),
            replacement,
            hasResult,
            expiryAction);
    }

    private static string FormatOverride(NormalizedItemOverride itemOverride)
    {
        string tuple = $"{itemOverride.PrefabName}, {FormatHours(itemOverride.Hours)}";
        return itemOverride.HasResultOverride
            ? $"{tuple}, {GetOverrideResultDisplay(itemOverride)}"
            : tuple;
    }

    private static string GetOverrideResultDisplay(NormalizedItemOverride itemOverride) =>
        itemOverride.ExpiryAction == SpoilageExpiryAction.KeepOriginal
            ? KeepOriginalKeyword
            : itemOverride.ReplacementPrefab;

    private static NormalizedGroupLifetime RequireGroupLifetime(
        SpoilageYamlLifetimeValue? value,
        string context)
    {
        if (value == null || string.IsNullOrWhiteSpace(value.Value))
        {
            throw new InvalidDataException($"{context} is required.");
        }

        string[] fields = value.Value.Split(new[] { ',' }, StringSplitOptions.None);
        if (fields.Length is < 1 or > 2)
        {
            throw new InvalidDataException(
                $"{context} must be '<hours>' or '<hours>, keep'.");
        }

        double hours = ParseHours(fields[0], context);
        SpoilageExpiryAction expiryAction = SpoilageExpiryAction.Replace;
        if (fields.Length == 2)
        {
            string action = fields[1].Trim();
            if (!string.Equals(action, KeepOriginalKeyword, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{context} expiry action must be '{KeepOriginalKeyword}'.");
            }

            expiryAction = SpoilageExpiryAction.KeepOriginal;
        }

        return new NormalizedGroupLifetime(hours, HoursToTicks(hours), expiryAction);
    }

    private static double ParseHours(string? value, string context)
    {
        if (!double.TryParse(
                value?.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed))
        {
            throw new InvalidDataException(
                $"{context} must be a number from 0 through {FormatHours(MaximumLifetimeHours)}.");
        }

        return ValidateHours(parsed, context);
    }

    private static double ValidateHours(double value, string context)
    {
        if (double.IsNaN(value) ||
            value < 0d ||
            value > MaximumLifetimeHours)
        {
            throw new InvalidDataException(
                $"{context} must be a finite value from 0 through {FormatHours(MaximumLifetimeHours)}.");
        }

        return value == 0d ? 0d : value;
    }

    private static long HoursToTicks(double hours)
    {
        if (hours <= 0d)
        {
            return 0L;
        }

        long ticks = checked((long)Math.Ceiling(hours * TimeSpan.TicksPerHour));
        return Math.Max(TimeSpan.TicksPerSecond, ticks);
    }

    private static string RequirePrefab(string? value, string context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{context} requires a prefab name.");
        }

        string prefab = value!.Trim();
        if (prefab.Any(char.IsControl))
        {
            throw new InvalidDataException($"{context} cannot contain control characters.");
        }

        if (prefab.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new InvalidDataException(
                $"{context} must use the registered prefab name without Unity's '(Clone)' marker.");
        }

        return prefab;
    }

    private static string FormatHours(double hours)
    {
        return hours.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string CanonicalizeYaml(string yaml)
    {
        return yaml.Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimEnd(new[] { '\n' }) + "\n";
    }

    private static void EnsureLocalPolicyFileExists()
    {
        Directory.CreateDirectory(FineDiningPlugin.ConfigDirectoryPath);
        if (File.Exists(PolicyFilePath))
        {
            return;
        }

        Assembly assembly = typeof(SpoilagePolicy).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(DefaultPolicyResourceName) ??
                              throw new InvalidOperationException(
                                  $"Embedded default YAML resource '{DefaultPolicyResourceName}' was not found.");
        using StreamReader reader = new(stream);
        File.WriteAllText(PolicyFilePath, reader.ReadToEnd());
        FineDiningPlugin.Log.LogInfo($"Created default spoilage policy: {PolicyFilePath}");
    }

    private readonly struct NormalizedGroupLifetime
    {
        internal NormalizedGroupLifetime(
            double hours,
            long ticks,
            SpoilageExpiryAction expiryAction)
        {
            Hours = hours;
            Ticks = ticks;
            ExpiryAction = expiryAction;
        }

        internal double Hours { get; }

        internal long Ticks { get; }

        internal SpoilageExpiryAction ExpiryAction { get; }

        internal SpoilageYamlLifetimeValue ToYamlValue()
        {
            string value = FormatHours(Hours);
            if (ExpiryAction == SpoilageExpiryAction.KeepOriginal)
            {
                value += ", " + KeepOriginalKeyword;
            }

            return new SpoilageYamlLifetimeValue(value);
        }
    }

    private sealed class NormalizedPolicy
    {
        internal NormalizedPolicy(
            Dictionary<SpoilageGroup, NormalizedGroupLifetime> lifetimes,
            Dictionary<string, NormalizedItemOverride> overrides,
            HashSet<string> replacementPrefabs,
            HashSet<string> chefChoiceBlacklist)
        {
            Lifetimes = lifetimes;
            Overrides = overrides;
            ReplacementPrefabs = replacementPrefabs;
            ChefChoiceBlacklist = chefChoiceBlacklist;
        }

        internal Dictionary<SpoilageGroup, NormalizedGroupLifetime> Lifetimes { get; }

        internal Dictionary<string, NormalizedItemOverride> Overrides { get; }

        internal HashSet<string> ReplacementPrefabs { get; }

        internal HashSet<string> ChefChoiceBlacklist { get; }

        internal long GetLifetimeTicks(SpoilageGroup group)
        {
            return Lifetimes[group].Ticks;
        }

        internal double GetLifetimeHours(SpoilageGroup group)
        {
            return Lifetimes[group].Hours;
        }

        internal SpoilageExpiryAction GetExpiryAction(SpoilageGroup group)
        {
            return Lifetimes[group].ExpiryAction;
        }
    }

    private sealed class NormalizedItemOverride
    {
        internal NormalizedItemOverride(
            string prefabName,
            double hours,
            long lifetimeTicks,
            string replacementPrefab,
            bool hasResultOverride,
            SpoilageExpiryAction expiryAction)
        {
            PrefabName = prefabName;
            Hours = hours;
            LifetimeTicks = lifetimeTicks;
            ReplacementPrefab = replacementPrefab;
            HasResultOverride = hasResultOverride;
            ExpiryAction = expiryAction;
        }

        internal string PrefabName { get; }

        internal double Hours { get; }

        internal long LifetimeTicks { get; }

        internal string ReplacementPrefab { get; }

        internal bool HasResultOverride { get; }

        internal SpoilageExpiryAction ExpiryAction { get; }
    }
}

internal readonly struct SpoilagePolicyReferenceOverride
{
    internal SpoilagePolicyReferenceOverride(
        string prefabName,
        double hours,
        long lifetimeTicks,
        string replacementPrefab)
    {
        PrefabName = prefabName;
        Hours = hours;
        LifetimeTicks = lifetimeTicks;
        ReplacementPrefab = replacementPrefab;
    }

    internal string PrefabName { get; }
    internal double Hours { get; }
    internal long LifetimeTicks { get; }
    internal string ReplacementPrefab { get; }
}

internal sealed class SpoilageYamlDocument
{
    public int Version { get; set; }

    public SpoilageYamlLifetimes? Lifetimes { get; set; }

    public List<string>? ChefChoiceBlacklist { get; set; }

    public List<string>? Overrides { get; set; }
}

internal sealed class SpoilageYamlLifetimes
{
    public SpoilageYamlLifetimeValue? FarmingHarvest { get; set; }

    public SpoilageYamlLifetimeValue? CookingStationInput { get; set; }

    public SpoilageYamlLifetimeValue? CookingStationOutput { get; set; }

    public SpoilageYamlLifetimeValue? UnfermentedFood { get; set; }

    public SpoilageYamlLifetimeValue? FermentedFood { get; set; }

    public SpoilageYamlLifetimeValue? FeastMaterial { get; set; }

    public SpoilageYamlLifetimeValue? FeastResult { get; set; }

    public SpoilageYamlLifetimeValue? Fish { get; set; }

    public SpoilageYamlLifetimeValue? OtherEdible { get; set; }
}

internal sealed class SpoilageYamlLifetimeValue
{
    internal SpoilageYamlLifetimeValue(string value)
    {
        Value = value ?? string.Empty;
    }

    internal string Value { get; }
}

internal sealed class SpoilageYamlLifetimeValueConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(SpoilageYamlLifetimeValue);

    public object ReadYaml(
        IParser parser,
        Type type,
        ObjectDeserializer rootDeserializer)
    {
        Scalar scalar = parser.Consume<Scalar>();
        return new SpoilageYamlLifetimeValue(scalar.Value ?? string.Empty);
    }

    public void WriteYaml(
        IEmitter emitter,
        object? value,
        Type type,
        ObjectSerializer serializer)
    {
        SpoilageYamlLifetimeValue lifetime = value as SpoilageYamlLifetimeValue ??
                                             throw new InvalidDataException(
                                                 "Spoilage lifetime YAML values cannot be null.");
        emitter.Emit(new Scalar(
            AnchorName.Empty,
            TagName.Empty,
            lifetime.Value,
            ScalarStyle.Plain,
            isPlainImplicit: true,
            isQuotedImplicit: false));
    }
}
