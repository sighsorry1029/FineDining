using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ServerSync;
using UnityEngine;

namespace FineDining;

internal static class StationModule
{
    internal const int HintColumns = 5;
    internal const int MaxHintRows = 4;
    internal const int DefaultHintRows = 2;
    internal const int MaxHints = HintColumns * MaxHintRows;

    private static bool _initialized;
    private static HashSet<string> _fermenterBonusExcludedPrefabNames =
        new(StringComparer.OrdinalIgnoreCase);

    internal static ConfigEntry<int> CookingStationRows { get; private set; } = null!;
    internal static ConfigEntry<int> SmelterRows { get; private set; } = null!;
    internal static ConfigEntry<int> WindmillRows { get; private set; } = null!;
    internal static ConfigEntry<int> FermenterRows { get; private set; } = null!;
    internal static ConfigEntry<int> GrimpyBoxRows { get; private set; } = null!;
    internal static ConfigEntry<float> IconGroupScale { get; private set; } = null!;
    internal static ConfigEntry<float> FermenterCoverMaxSpeedMultiplier { get; private set; } = null!;
    internal static ConfigEntry<float> FermenterDepthMaxSpeedMultiplier { get; private set; } = null!;
    internal static ConfigEntry<string> FermenterBonusExcludedPrefabs { get; private set; } = null!;

    internal static bool IsInitialized => _initialized;

    internal static void Initialize(ConfigFile config, ConfigSync configSync)
    {
        if (_initialized)
        {
            return;
        }

        IconGroupScale = config.Bind(
            ConfigPresentation.ClientSection.Name,
            "Station Icon Scale",
            1f,
            ConfigPresentation.Client(
                "Scale of the station hover icon group.",
                ConfigPresentation.ClientSection,
                600,
                new AcceptableValueRange<float>(0.25f, 2f)));
        CookingStationRows = config.Bind(
            ConfigPresentation.ClientSection.Name,
            "Station Icon Rows - Cooking Station",
            DefaultHintRows,
            ConfigPresentation.Client(
                "Number of five-icon rows available to cooking progress and input hints. Progress rows take priority. 0 hides cooking station icons.",
                ConfigPresentation.ClientSection,
                500,
                new AcceptableValueRange<int>(0, MaxHintRows)));
        SmelterRows = config.Bind(
            ConfigPresentation.ClientSection.Name,
            "Station Icon Rows - Smelter",
            DefaultHintRows,
            ConfigPresentation.Client(
                "Number of five-icon rows available to smelter input hints. 0 hides smelter icons without hiding processing-time text.",
                ConfigPresentation.ClientSection,
                400,
                new AcceptableValueRange<int>(0, MaxHintRows)));
        WindmillRows = config.Bind(
            ConfigPresentation.ClientSection.Name,
            "Station Icon Rows - Windmill",
            DefaultHintRows,
            ConfigPresentation.Client(
                "Number of five-icon rows available to windmill input hints. 0 hides windmill icons without hiding processing-time text.",
                ConfigPresentation.ClientSection,
                300,
                new AcceptableValueRange<int>(0, MaxHintRows)));
        FermenterRows = config.Bind(
            ConfigPresentation.ClientSection.Name,
            "Station Icon Rows - Fermenter",
            DefaultHintRows,
            ConfigPresentation.Client(
                "Number of five-icon rows available to fermenter input hints. 0 hides fermenter icons without hiding time or environment details.",
                ConfigPresentation.ClientSection,
                200,
                new AcceptableValueRange<int>(0, MaxHintRows)));
        GrimpyBoxRows = config.Bind(
            ConfigPresentation.ClientSection.Name,
            "Station Icon Rows - Grimpy Box",
            DefaultHintRows,
            ConfigPresentation.Client(
                "Number of five-icon rows available to optional ValheimCuisine Grimpy Box conversion hints. 0 hides FineDining's Grimpy Box icons.",
                ConfigPresentation.ClientSection,
                100,
                new AcceptableValueRange<int>(0, MaxHintRows)));

        FermenterBonusExcludedPrefabs = BindSynced(
            config,
            configSync,
            ConfigPresentation.General,
            "Fermenter Bonus Excluded Prefabs",
            string.Empty,
            ConfigPresentation.Synced(
                "Exact internal prefab names of Fermenters that keep native timing and output behavior. Separate names with commas, semicolons, or new lines. FineDining still shows remaining time and input icons, but does not apply or show cover/depth acceleration, Cooking output bonuses, or insertion/collection Cooking experience.",
                ConfigPresentation.General,
                250));
        RebuildFermenterBonusExcludedPrefabNames();
        FermenterBonusExcludedPrefabs.SettingChanged +=
            OnFermenterBonusExcludedPrefabsChanged;

        FermenterCoverMaxSpeedMultiplier = BindSynced(
            config,
            configSync,
            ConfigPresentation.General,
            "Fermenter Cover Maximum Multiplier",
            2f,
            ConfigPresentation.Synced(
                "Fermentation speed multiplier at 100% cover. The bonus scales linearly from x1 at the vanilla minimum required cover to this value at full cover.",
                ConfigPresentation.General,
                200,
                new AcceptableValueRange<float>(1f, 10f)));
        FermenterDepthMaxSpeedMultiplier = BindSynced(
            config,
            configSync,
            ConfigPresentation.General,
            "Fermenter Depth Maximum Multiplier",
            2f,
            ConfigPresentation.Synced(
                "Fermentation speed multiplier at 8 meters or more beneath the original terrain baseline. The bonus scales linearly from x1 at the baseline to this value at 8 meters depth.",
                ConfigPresentation.General,
                100,
                new AcceptableValueRange<float>(1f, 10f)));

        _initialized = true;
        AzuCraftyBoxesCompatibility.Initialize();
        ValheimCuisineCompatibility.Initialize();
    }

    internal static int GetHintLimit(int rows)
    {
        int clampedRows = rows < 0
            ? 0
            : rows > MaxHintRows
                ? MaxHintRows
                : rows;
        return clampedRows * HintColumns;
    }

    internal static bool IsFermenterBonusExcluded(Fermenter? fermenter)
    {
        string prefabName = GetFermenterPrefabName(fermenter);
        return prefabName.Length > 0
               && _fermenterBonusExcludedPrefabNames.Contains(prefabName);
    }

    internal static string GetFermenterPrefabName(Fermenter? fermenter)
    {
        if (fermenter == null)
        {
            return string.Empty;
        }

        ZNetView? view = fermenter.GetComponent<ZNetView>()
                         ?? fermenter.GetComponentInParent<ZNetView>();
        if (view != null)
        {
            if (view.IsValid())
            {
                ZDO? zdo = view.GetZDO();
                if (zdo != null && ZNetScene.instance != null)
                {
                    GameObject? networkPrefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                    string networkPrefabName = FoodIdentity.NormalizePrefabName(networkPrefab?.name);
                    if (networkPrefabName.Length > 0)
                    {
                        return networkPrefabName;
                    }
                }
            }

            string viewPrefabName = FoodIdentity.NormalizePrefabName(view.gameObject.name);
            if (viewPrefabName.Length > 0)
            {
                return viewPrefabName;
            }
        }

        return FoodIdentity.NormalizePrefabName(fermenter.gameObject.name);
    }

    internal static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        FermenterEnvironmentSpeedSystem.CheckpointAllOwners();
        FermenterBonusExcludedPrefabs.SettingChanged -=
            OnFermenterBonusExcludedPrefabsChanged;
        StationHintUi.Shutdown();
        StationInputResolver.Reset();
        AzuCraftyBoxesCompatibility.Shutdown();
        ValheimCuisineCompatibility.Shutdown();
        FermenterEnvironmentSpeedSystem.ResetRuntime();
        _fermenterBonusExcludedPrefabNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _initialized = false;
    }

    private static void OnFermenterBonusExcludedPrefabsChanged(
        object sender,
        EventArgs eventArgs)
    {
        // Capture work earned under the old policy before atomically replacing
        // the cached matcher, then checkpoint again so active batches immediately
        // publish the new zero/non-zero rate without rewinding elapsed work.
        if (_initialized)
        {
            FermenterEnvironmentSpeedSystem.CheckpointAllOwners();
        }

        RebuildFermenterBonusExcludedPrefabNames();

        if (_initialized)
        {
            FermenterEnvironmentSpeedSystem.CheckpointAllOwners();
        }
    }

    private static void RebuildFermenterBonusExcludedPrefabNames()
    {
        HashSet<string> prefabNames = new(StringComparer.OrdinalIgnoreCase);
        string configured = FermenterBonusExcludedPrefabs?.Value ?? string.Empty;
        foreach (string token in configured.Split(
                     new[] { ',', ';', '\r', '\n' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string prefabName = FoodIdentity.NormalizePrefabName(token);
            if (prefabName.Length > 0)
            {
                prefabNames.Add(prefabName);
            }
        }

        _fermenterBonusExcludedPrefabNames = prefabNames;
    }

    private static ConfigEntry<T> BindSynced<T>(
        ConfigFile config,
        ConfigSync configSync,
        ConfigPresentation.SectionDefinition section,
        string name,
        T value,
        ConfigDescription description)
    {
        ConfigEntry<T> entry = config.Bind(section.Name, name, value, description);
        SyncedConfigEntry<T> syncedEntry = configSync.AddConfigEntry(entry);
        syncedEntry.SynchronizedConfig = true;
        return entry;
    }
}
