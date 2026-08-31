using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using ServerSync;

namespace FineDining;

internal static class PreservationConfig
{
    private const string DefaultBiomeList = "Mountain, DeepNorth";

    private delegate bool TryGetBiomeDelegate(string name, out Heightmap.Biome biome);
    private delegate Heightmap.Biome GetNatureDelegate(Heightmap.Biome biome);

    private static readonly HashSet<string> NoSpoilBiomeNames =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarnedUnknownBiomeNames =
        new(StringComparer.OrdinalIgnoreCase);

    private static ConfigEntry<string>? _noSpoilBiomes;
    private static TryGetBiomeDelegate? _expandWorldDataTryGetBiome;
    private static GetNatureDelegate? _expandWorldDataGetNature;
    private static Heightmap.Biome _resolvedBiomeMask;
    private static float _nextValidationAt;
    private static float _nextBridgeAttemptAt;
    private static bool _expandWorldDataBridgeWarningLogged;

    internal static void Initialize(ConfigFile config, ConfigSync configSync)
    {
        Shutdown();
        _noSpoilBiomes = config.Bind(
            ConfigPresentation.Spoilage.Name,
            "No-Spoil Biomes",
            DefaultBiomeList,
            ConfigPresentation.Synced(
                "Comma-separated biome identifiers where spoilage pauses. " +
                "Expand World Data custom biome names are supported when that mod is installed.",
                ConfigPresentation.Spoilage,
                600));
        SyncedConfigEntry<string> syncedEntry = configSync.AddConfigEntry(_noSpoilBiomes);
        syncedEntry.SynchronizedConfig = true;
        _noSpoilBiomes.SettingChanged += OnSettingChanged;
        Rebuild();
    }

    internal static void Shutdown()
    {
        if (_noSpoilBiomes != null)
        {
            _noSpoilBiomes.SettingChanged -= OnSettingChanged;
            _noSpoilBiomes = null;
        }

        NoSpoilBiomeNames.Clear();
        WarnedUnknownBiomeNames.Clear();
        _resolvedBiomeMask = Heightmap.Biome.None;
        _expandWorldDataTryGetBiome = null;
        _expandWorldDataGetNature = null;
        _nextValidationAt = 0f;
        _nextBridgeAttemptAt = 0f;
        _expandWorldDataBridgeWarningLogged = false;
    }

    internal static void Tick()
    {
        if (UnityEngine.Time.unscaledTime < _nextValidationAt ||
            ZoneSystem.instance == null || WorldGenerator.instance == null)
        {
            return;
        }

        _nextValidationAt = UnityEngine.Time.unscaledTime + 10f;
        ResolveConfiguredBiomeMask(warnUnknown: true);
    }

    internal static bool IsNoSpoilBiome(Heightmap.Biome biome)
    {
        if (biome == Heightmap.Biome.None || NoSpoilBiomeNames.Count == 0)
        {
            return false;
        }

        Heightmap.Biome effectiveBiome = ResolveEffectiveNature(biome);
        return _resolvedBiomeMask != Heightmap.Biome.None &&
               (effectiveBiome & _resolvedBiomeMask) != Heightmap.Biome.None;
    }

    private static void OnSettingChanged(object sender, EventArgs eventArgs)
    {
        Rebuild();
    }

    private static void Rebuild()
    {
        NoSpoilBiomeNames.Clear();
        WarnedUnknownBiomeNames.Clear();
        _nextValidationAt = 0f;

        string text = _noSpoilBiomes?.Value ?? DefaultBiomeList;
        foreach (string rawName in text
                     .Trim()
                     .Trim('[', ']')
                     .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string name = rawName.Trim().Trim('\'', '"');
            if (name.Length == 0)
            {
                continue;
            }

            if (name.Equals(nameof(Heightmap.Biome.None), StringComparison.OrdinalIgnoreCase))
            {
                FineDiningPlugin.Log.LogWarning(
                    "No-Spoil Biomes ignores 'None' because it represents an unavailable biome sample.");
                continue;
            }

            NoSpoilBiomeNames.Add(name);
        }

        ResolveConfiguredBiomeMask(warnUnknown: false);
    }

    private static void ResolveConfiguredBiomeMask(bool warnUnknown)
    {
        Heightmap.Biome resolvedMask = Heightmap.Biome.None;
        foreach (string name in NoSpoilBiomeNames)
        {
            if (!TryResolveBiomeName(name, out Heightmap.Biome biome) ||
                biome == Heightmap.Biome.None)
            {
                if (warnUnknown && WarnedUnknownBiomeNames.Add(name))
                {
                    FineDiningPlugin.Log.LogWarning(
                        $"No-Spoil Biomes contains unresolved biome identifier '{name}'. " +
                        "It remains configured and will be retried for late-loaded Expand World Data content.");
                }

                continue;
            }

            Heightmap.Biome effective = ResolveEffectiveNature(biome);
            if (effective != Heightmap.Biome.None)
            {
                resolvedMask |= effective;
                WarnedUnknownBiomeNames.Remove(name);
            }
        }

        if (_resolvedBiomeMask == resolvedMask)
        {
            return;
        }

        _resolvedBiomeMask = resolvedMask;
        DecayRuntime.InvalidateAll();
    }

    private static bool TryResolveBiomeName(string name, out Heightmap.Biome biome)
    {
        EnsureExpandWorldDataBridge();
        if (_expandWorldDataTryGetBiome != null)
        {
            try
            {
                if (_expandWorldDataTryGetBiome(name, out biome))
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                LogExpandWorldDataBridgeWarning(exception);
            }
        }

        return Enum.TryParse(name, ignoreCase: true, out biome);
    }

    private static Heightmap.Biome ResolveEffectiveNature(Heightmap.Biome biome)
    {
        EnsureExpandWorldDataBridge();
        if (_expandWorldDataGetNature == null)
        {
            return biome;
        }

        try
        {
            return _expandWorldDataGetNature(biome);
        }
        catch (Exception exception)
        {
            LogExpandWorldDataBridgeWarning(exception);
            return biome;
        }
    }

    private static void EnsureExpandWorldDataBridge()
    {
        if (_expandWorldDataTryGetBiome != null && _expandWorldDataGetNature != null)
        {
            return;
        }

        float now = UnityEngine.Time.unscaledTime;
        if (now < _nextBridgeAttemptAt)
        {
            return;
        }

        _nextBridgeAttemptAt = now + 10f;
        Type? managerType = Type.GetType(
            "ExpandWorldData.BiomeManager, ExpandWorldData",
            throwOnError: false);
        if (managerType == null)
        {
            return;
        }

        try
        {
            MethodInfo? tryGetBiomeMethod = managerType.GetMethod(
                "TryGetBiome",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(Heightmap.Biome).MakeByRefType() },
                modifiers: null);
            MethodInfo? getNatureMethod = managerType.GetMethod(
                "GetNature",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(Heightmap.Biome) },
                modifiers: null);
            if (tryGetBiomeMethod == null || getNatureMethod == null)
            {
                throw new MissingMethodException(
                    "ExpandWorldData.BiomeManager.TryGetBiome/GetNature was not found.");
            }

            _expandWorldDataTryGetBiome = (TryGetBiomeDelegate)tryGetBiomeMethod.CreateDelegate(
                typeof(TryGetBiomeDelegate));
            _expandWorldDataGetNature = (GetNatureDelegate)getNatureMethod.CreateDelegate(
                typeof(GetNatureDelegate));
        }
        catch (Exception exception)
        {
            LogExpandWorldDataBridgeWarning(exception);
        }
    }

    private static void LogExpandWorldDataBridgeWarning(Exception exception)
    {
        if (_expandWorldDataBridgeWarningLogged)
        {
            return;
        }

        _expandWorldDataBridgeWarningLogged = true;
        FineDiningPlugin.Log.LogWarning(
            "Expand World Data biome compatibility is unavailable; custom no-spoil biome names " +
            $"will be retried. {exception.GetBaseException().Message}");
    }
}
