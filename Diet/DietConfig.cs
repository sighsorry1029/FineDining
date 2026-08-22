using System;
using BepInEx.Configuration;
using ServerSync;

namespace FineDining;

internal static class DietConfig
{
    internal static ConfigEntry<int> MaxFoodSlots = null!;
    internal static ConfigEntry<float> SlotScaleConstant = null!;
    internal static ConfigEntry<int> RecentHistorySize = null!;
    internal static ConfigEntry<int> DiminishingThreshold = null!;
    internal static ConfigEntry<float> DiminishingFactor = null!;
    internal static ConfigEntry<int> ChefCollectionSize = null!;
    internal static ConfigEntry<float> ChefMultiplierMin = null!;
    internal static ConfigEntry<float> ChefMultiplierMax = null!;
    internal static ConfigEntry<float> CookingBonusOutputPercent = null!;
    internal static ConfigEntry<string> CookingBonusExcludedOutputPrefabs = null!;

    internal static void Initialize(ConfigFile config, ConfigSync configSync)
    {
        MaxFoodSlots = BindSynced(
            config,
            configSync,
            "10 - Diet - Food Slots",
            "Max Food Slots",
            9,
            new ConfigDescription(
                "Maximum number of active food slots.",
                new AcceptableValueRange<int>(3, 9)));

        SlotScaleConstant = BindSynced(
            config,
            configSync,
            "10 - Diet - Food Slots",
            "Slot Scale Constant",
            1f,
            new ConfigDescription(
                "Additional multiplier applied to the base slot scaling formula 3 / Max Food Slots.",
                new AcceptableValueRange<float>(0.1f, 3f)));

        RecentHistorySize = BindSynced(
            config,
            configSync,
            "11 - Diet - Diminishing Returns",
            "Recent History Size",
            7,
            new ConfigDescription(
                "Number of unique recent foods tracked in the HUD and history.",
                new AcceptableValueRange<int>(1, 12)));

        DiminishingThreshold = BindSynced(
            config,
            configSync,
            "11 - Diet - Diminishing Returns",
            "Diminishing Threshold",
            4,
            new ConfigDescription(
                "The consumption count that starts diminishing returns for regular foods.",
                new AcceptableValueRange<int>(1, 20)));

        DiminishingFactor = BindSynced(
            config,
            configSync,
            "11 - Diet - Diminishing Returns",
            "Diminishing Factor",
            0.75f,
            new ConfigDescription(
                "Single multiplier applied at and after the diminishing threshold.",
                new AcceptableValueRange<float>(0.1f, 1f)));

        ChefCollectionSize = BindSynced(
            config,
            configSync,
            "12 - Diet - Chef Choice",
            "Chef Collection Size",
            7,
            new ConfigDescription(
                "Number of active Chef's Choice foods shown in the HUD.",
                new AcceptableValueRange<int>(1, 12)));

        ChefMultiplierMin = BindSynced(
            config,
            configSync,
            "12 - Diet - Chef Choice",
            "Chef Multiplier Minimum",
            1f,
            new ConfigDescription(
                "Minimum random multiplier for Chef's Choice foods.",
                new AcceptableValueRange<float>(0.1f, 5f)));

        ChefMultiplierMax = BindSynced(
            config,
            configSync,
            "12 - Diet - Chef Choice",
            "Chef Multiplier Maximum",
            2f,
            new ConfigDescription(
                "Maximum random multiplier for Chef's Choice foods.",
                new AcceptableValueRange<float>(0.1f, 5f)));

        CookingBonusOutputPercent = BindSynced(
            config,
            configSync,
            "13 - Diet - Cooking Production Bonus",
            "Bonus Output Percent",
            100f,
            new ConfigDescription(
                "Percentage of Valheim's production-bonus chance applied to Cooking recipes, CookingStation outputs, and Fermenter outputs.",
                new AcceptableValueRange<float>(0f, 100f)));

        CookingBonusExcludedOutputPrefabs = BindSynced(
            config,
            configSync,
            "13 - Diet - Cooking Production Bonus",
            "Excluded Output Prefabs",
            string.Empty,
            new ConfigDescription(
                "Comma-, semicolon-, or newline-separated output prefab names that receive no Cooking production bonus. '*' is a case-insensitive whole-name wildcard."));
    }

    internal static void Shutdown()
    {
        MaxFoodSlots = null!;
        SlotScaleConstant = null!;
        RecentHistorySize = null!;
        DiminishingThreshold = null!;
        DiminishingFactor = null!;
        ChefCollectionSize = null!;
        ChefMultiplierMin = null!;
        ChefMultiplierMax = null!;
        CookingBonusOutputPercent = null!;
        CookingBonusExcludedOutputPrefabs = null!;
    }

    internal static int GetMaxFoodSlots() => MaxFoodSlots.Value;
    internal static float GetSlotScaleConstant() => SlotScaleConstant.Value;
    internal static float GetBaseSlotScale() => 3f / GetMaxFoodSlots() * GetSlotScaleConstant();
    internal static int GetRecentHistorySize() => RecentHistorySize.Value;
    internal static int GetDiminishingThreshold() => DiminishingThreshold.Value;
    internal static float GetDiminishingFactor() => DiminishingFactor.Value;
    internal static int GetChefCollectionSize() => ChefCollectionSize.Value;
    internal static float GetChefMultiplierMin() => ChefMultiplierMin.Value;
    internal static float GetChefMultiplierMax() => Math.Max(GetChefMultiplierMin(), ChefMultiplierMax.Value);
    internal static float GetCookingBonusOutputPercent() => CookingBonusOutputPercent.Value;
    internal static string GetCookingBonusExcludedOutputPrefabs() => CookingBonusExcludedOutputPrefabs.Value;

    private static ConfigEntry<T> BindSynced<T>(
        ConfigFile config,
        ConfigSync configSync,
        string section,
        string key,
        T defaultValue,
        ConfigDescription description)
    {
        ConfigEntry<T> entry = config.Bind(section, key, defaultValue, description);
        SyncedConfigEntry<T> synced = configSync.AddConfigEntry(entry);
        synced.SynchronizedConfig = true;
        return entry;
    }
}
