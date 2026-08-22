using BepInEx.Configuration;
using ServerSync;

namespace FineDining;

internal static class StationModule
{
    internal const int MaxHints = 10;

    private static bool _initialized;

    internal static ConfigEntry<bool> EnableDisplay { get; private set; } = null!;
    internal static ConfigEntry<int> CookingStationMax { get; private set; } = null!;
    internal static ConfigEntry<int> SmelterMax { get; private set; } = null!;
    internal static ConfigEntry<int> WindmillMax { get; private set; } = null!;
    internal static ConfigEntry<int> FermenterMax { get; private set; } = null!;
    internal static ConfigEntry<float> IconGroupScale { get; private set; } = null!;
    internal static ConfigEntry<float> IconGroupOffsetX { get; private set; } = null!;
    internal static ConfigEntry<float> IconGroupOffsetY { get; private set; } = null!;
    internal static ConfigEntry<float> CookingGroupGap { get; private set; } = null!;
    internal static ConfigEntry<float> NearbyContainerRange { get; private set; } = null!;
    internal static ConfigEntry<bool> Diagnostics { get; private set; } = null!;
    internal static ConfigEntry<float> FermenterCoverMaxSpeedMultiplier { get; private set; } = null!;
    internal static ConfigEntry<float> FermenterDepthMaxSpeedMultiplier { get; private set; } = null!;

    internal static bool IsInitialized => _initialized;

    internal static bool CanShowDisplay => _initialized && EnableDisplay.Value;

    internal static void Initialize(ConfigFile config, ConfigSync configSync)
    {
        if (_initialized)
        {
            return;
        }

        EnableDisplay = BindLocal(
            config,
            "20 - Station Hints",
            "Enable",
            true,
            "Show station input hints, processing times, and fermenter environment details. This display toggle does not disable gameplay speed bonuses.");
        CookingStationMax = BindLocal(
            config,
            "20 - Station Hints",
            "CookingStation",
            MaxHints,
            new ConfigDescription(
                "Maximum cooking station input hints to show. 0 disables cooking station hints.",
                new AcceptableValueRange<int>(0, MaxHints)));
        SmelterMax = BindLocal(
            config,
            "20 - Station Hints",
            "Smelter",
            MaxHints,
            new ConfigDescription(
                "Maximum smelter input hints to show. 0 disables smelter hints.",
                new AcceptableValueRange<int>(0, MaxHints)));
        WindmillMax = BindLocal(
            config,
            "20 - Station Hints",
            "Windmill",
            MaxHints,
            new ConfigDescription(
                "Maximum windmill input hints to show. 0 disables windmill hints.",
                new AcceptableValueRange<int>(0, MaxHints)));
        FermenterMax = BindLocal(
            config,
            "20 - Station Hints",
            "Fermenter",
            MaxHints,
            new ConfigDescription(
                "Maximum fermenter input hints to show. 0 disables fermenter input hints, remaining time, and environment details.",
                new AcceptableValueRange<int>(0, MaxHints)));

        IconGroupScale = BindLocal(
            config,
            "21 - Station Hint UI",
            "Icon Group Scale",
            0.82f,
            new ConfigDescription(
                "Scale of the station hover icon group.",
                new AcceptableValueRange<float>(0.25f, 2f)));
        IconGroupOffsetX = BindLocal(
            config,
            "21 - Station Hint UI",
            "Icon Group Offset X",
            0f,
            new ConfigDescription(
                "Horizontal offset of the station hover icon group. Positive values move it right.",
                new AcceptableValueRange<float>(-2000f, 2000f)));
        IconGroupOffsetY = BindLocal(
            config,
            "21 - Station Hint UI",
            "Icon Group Offset Y",
            0f,
            new ConfigDescription(
                "Vertical offset of the station hover icon group. Positive values move it up.",
                new AcceptableValueRange<float>(-2000f, 2000f)));
        CookingGroupGap = BindLocal(
            config,
            "21 - Station Hint UI",
            "Cooking Group Gap",
            12f,
            new ConfigDescription(
                "Vertical gap between the cooking progress group and available input group.",
                new AcceptableValueRange<float>(0f, 500f)));
        NearbyContainerRange = BindLocal(
            config,
            "22 - Station Compatibility",
            "Nearby Container Range",
            20f,
            new ConfigDescription(
                "AzuCraftyBoxes range used when checking nearby containers for available station inputs.",
                new AcceptableValueRange<float>(1f, 100f)));
        Diagnostics = BindLocal(
            config,
            "23 - Station Diagnostics",
            "Enable Diagnostics",
            false,
            "Log why station hover hints are or are not shown. Leave off during normal play.");

        FermenterCoverMaxSpeedMultiplier = BindSynced(
            config,
            configSync,
            "24 - Fermentation Environment",
            "Cover Maximum Speed Multiplier",
            2f,
            new ConfigDescription(
                "Fermentation speed multiplier at 100% cover. The bonus scales linearly from x1 at the vanilla minimum required cover to this value at full cover.",
                new AcceptableValueRange<float>(1f, 10f)));
        FermenterDepthMaxSpeedMultiplier = BindSynced(
            config,
            configSync,
            "24 - Fermentation Environment",
            "Depth Maximum Speed Multiplier",
            2f,
            new ConfigDescription(
                "Fermentation speed multiplier at 8 meters or more beneath the original terrain baseline. The bonus scales linearly from x1 at the baseline to this value at 8 meters depth.",
                new AcceptableValueRange<float>(1f, 10f)));

        _initialized = true;
        AzuCraftyBoxesCompatibility.Initialize();
    }

    internal static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        FermenterEnvironmentSpeedSystem.CheckpointAllOwners();
        StationHintUi.Shutdown();
        StationInputResolver.Reset();
        AzuCraftyBoxesCompatibility.Shutdown();
        FermenterEnvironmentSpeedSystem.ResetRuntime();
        _initialized = false;
    }

    // Shared integration surface for the Diet fermenter output path.
    internal static long GetFermenterBatchToken(Fermenter fermenter) =>
        FermenterEnvironmentSpeedSystem.GetBatchToken(fermenter);

    internal static void CheckpointFermenterBeforeCompletion(Fermenter fermenter) =>
        FermenterEnvironmentSpeedSystem.CheckpointBeforeBatchCompletion(fermenter);

    internal static void NotifyFermenterBatchStartedOrReset(Fermenter fermenter) =>
        FermenterEnvironmentSpeedSystem.NotifyBatchStartedOrReset(fermenter);

    internal static void NotifyFermenterBatchCleared(Fermenter fermenter) =>
        FermenterEnvironmentSpeedSystem.NotifyBatchCleared(fermenter);

    private static ConfigEntry<T> BindLocal<T>(
        ConfigFile config,
        string group,
        string name,
        T value,
        ConfigDescription description)
    {
        ConfigDescription extendedDescription = new(
            description.Description + " [Not Synced with Server]",
            description.AcceptableValues,
            description.Tags);
        return config.Bind(group, name, value, extendedDescription);
    }

    private static ConfigEntry<T> BindLocal<T>(
        ConfigFile config,
        string group,
        string name,
        T value,
        string description) =>
        BindLocal(config, group, name, value, new ConfigDescription(description));

    private static ConfigEntry<T> BindSynced<T>(
        ConfigFile config,
        ConfigSync configSync,
        string group,
        string name,
        T value,
        ConfigDescription description)
    {
        ConfigDescription extendedDescription = new(
            description.Description + " [Synced with Server]",
            description.AcceptableValues,
            description.Tags);
        ConfigEntry<T> entry = config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedEntry = configSync.AddConfigEntry(entry);
        syncedEntry.SynchronizedConfig = true;
        return entry;
    }
}
