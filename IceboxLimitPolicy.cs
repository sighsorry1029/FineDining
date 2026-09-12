using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FineDining;

internal sealed class IceboxLimitSnapshot
{
    internal IceboxLimitSnapshot(IReadOnlyDictionary<string, int> overrides)
    {
        Overrides = overrides;
    }

    internal IReadOnlyDictionary<string, int> Overrides { get; }

    internal int GetLimit(string accountId, int defaultLimit)
    {
        string canonical = IceboxSubsystem.NormalizeAccountId(accountId);
        return Overrides.TryGetValue(canonical, out int limit) ? limit : defaultLimit;
    }
}

/// <summary>
/// Server-only, last-known-good per-Steam64 Icebox limit overrides. The shared
/// default is synchronized through BepInEx; account identifiers remain server-only
/// because clients do not need and should not receive them.
/// </summary>
internal static class IceboxLimitPolicy
{
    internal const string FileName = "Icebox.yml";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1d);
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    private static readonly IceboxLimitSnapshot EmptySnapshot =
        new(new Dictionary<string, int>(StringComparer.Ordinal));

    private static IceboxLimitSnapshot _current = EmptySnapshot;
    private static DateTime _nextPollUtc = DateTime.MinValue;
    private static DateTime _lastProcessedWriteUtc = DateTime.MinValue;
    private static long _lastProcessedLength = -1L;
    private static bool _initialized;
    private static bool _authorityWasServer;

    internal static IceboxLimitSnapshot Current => _current;

    internal static string FilePath =>
        Path.Combine(FineDiningPlugin.ConfigDirectoryPath, FileName);

    internal static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _current = EmptySnapshot;
        _nextPollUtc = DateTime.MinValue;
        _lastProcessedWriteUtc = DateTime.MinValue;
        _lastProcessedLength = -1L;
        _authorityWasServer = false;
    }

    internal static void Tick()
    {
        if (!_initialized)
        {
            return;
        }

        bool isAuthoritativeServer = ZNet.instance != null && ZNet.instance.IsServer();
        if (!isAuthoritativeServer)
        {
            _authorityWasServer = false;
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool becameServer = !_authorityWasServer;
        _authorityWasServer = true;
        if (!becameServer && nowUtc < _nextPollUtc)
        {
            return;
        }

        _nextPollUtc = nowUtc.Add(PollInterval);
        EnsureFileExists();
        ReloadIfChanged(force: becameServer);
    }

    internal static void Shutdown()
    {
        _initialized = false;
        _authorityWasServer = false;
        _current = EmptySnapshot;
        _nextPollUtc = DateTime.MinValue;
        _lastProcessedWriteUtc = DateTime.MinValue;
        _lastProcessedLength = -1L;
    }

    private static void EnsureFileExists()
    {
        if (File.Exists(FilePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(FineDiningPlugin.ConfigDirectoryPath);
            File.WriteAllText(
                FilePath,
                "# FineDining per-Steam64 Icebox limit overrides (server only).\n" +
                "# The synchronized Icebox Default Placement Limit config applies to unlisted accounts.\n" +
                "# This file accepts only overrides; the legacy defaultLimit field is not supported.\n" +
                "#  0: deny placement, -1: unlimited, positive: maximum count.\n" +
                "# Quote Steam64 ids so YAML always treats them as strings.\n" +
                "overrides: {}\n" +
                "# overrides:\n" +
                "#   \"76561198000000000\": 6\n" +
                "#   \"76561198000000001\": -1\n");
            FineDiningPlugin.Log.LogInfo($"Created server-only Icebox limit policy '{FilePath}'.");
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                $"Could not create Icebox limit policy '{FilePath}': {exception.GetBaseException().Message}");
        }
    }

    private static void ReloadIfChanged(bool force)
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        DateTime writeUtc;
        long length;
        string yaml;
        try
        {
            FileInfo file = new(FilePath);
            writeUtc = file.LastWriteTimeUtc;
            length = file.Length;
            if (!force && writeUtc == _lastProcessedWriteUtc && length == _lastProcessedLength)
            {
                return;
            }

            yaml = File.ReadAllText(FilePath);
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                $"Could not read Icebox limit policy '{FilePath}'; keeping the last-known-good policy. " +
                exception.GetBaseException().Message);
            return;
        }

        // Remember the observed version even when invalid so one malformed save
        // does not emit the same warning every polling interval.
        _lastProcessedWriteUtc = writeUtc;
        _lastProcessedLength = length;

        if (!TryParse(yaml, out IceboxLimitSnapshot? snapshot, out string error))
        {
            FineDiningPlugin.Log.LogError(
                $"Could not parse Icebox limit policy '{FilePath}'; keeping the last-known-good policy. {error}");
            return;
        }

        _current = snapshot!;
        FineDiningPlugin.Log.LogInfo(
            $"Applied Icebox limit policy: overrides={snapshot!.Overrides.Count}.");
    }

    private static bool TryParse(
        string yaml,
        out IceboxLimitSnapshot? snapshot,
        out string error)
    {
        snapshot = null;
        error = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(yaml))
            {
                throw new InvalidDataException("The document cannot be empty.");
            }

            IceboxLimitYaml document = Deserializer.Deserialize<IceboxLimitYaml>(yaml) ??
                throw new InvalidDataException("The document cannot be null.");

            Dictionary<string, int> normalizedOverrides = new(StringComparer.Ordinal);
            if (document.Overrides != null)
            {
                foreach (KeyValuePair<string, int> entry in document.Overrides)
                {
                    string accountId = IceboxSubsystem.NormalizeAccountId(entry.Key);
                    if (!ulong.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) ||
                        steamId == 0UL)
                    {
                        throw new InvalidDataException(
                            $"overrides key '{entry.Key}' must be a quoted, non-zero Steam64 id.");
                    }

                    ValidateLimit(entry.Value, $"overrides['{entry.Key}']");
                    if (normalizedOverrides.ContainsKey(accountId))
                    {
                        throw new InvalidDataException(
                            $"overrides contains duplicate normalized Steam64 id '{accountId}'.");
                    }

                    normalizedOverrides.Add(accountId, entry.Value);
                }
            }

            snapshot = new IceboxLimitSnapshot(normalizedOverrides);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    private static void ValidateLimit(int limit, string field)
    {
        if (limit < -1)
        {
            throw new InvalidDataException($"{field} must be -1, 0, or a positive integer.");
        }
    }

    private sealed class IceboxLimitYaml
    {
        public Dictionary<string, int>? Overrides { get; set; }
    }
}
