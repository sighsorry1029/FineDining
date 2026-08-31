using System;
using BepInEx.Configuration;
using ServerSync;

namespace FineDining;

internal enum PukeFoodRemovalOrder
{
    OldestFirst,
    Random,
    NewestFirst
}

internal static class DietConfig
{
    internal static ConfigEntry<int> MaxFoodSlots = null!;
    internal static ConfigEntry<float> SixSlotFoodStatScale = null!;
    internal static ConfigEntry<float> NineSlotFoodStatScale = null!;
    internal static ConfigEntry<float> FullCourseMultiplier = null!;
    internal static ConfigEntry<int> RecentHistorySize = null!;
    internal static ConfigEntry<int> DiminishingThreshold = null!;
    internal static ConfigEntry<float> DiminishingFactor = null!;
    internal static ConfigEntry<PukeFoodRemovalOrder> PukeRemovalOrder = null!;
    internal static ConfigEntry<int> ChefCollectionSize = null!;
    internal static ConfigEntry<float> ChefMultiplierMin = null!;
    internal static ConfigEntry<float> ChefMultiplierMax = null!;
    internal static ConfigEntry<float> ChefHighTierSelectionStrength = null!;
    internal static ConfigEntry<float> ChefMultiplierModeAtMaxCooking = null!;
    internal static ConfigEntry<float> ChefRecentFoodPreferencePercent = null!;
    internal static ConfigEntry<float> CookingExperiencePerFoodEaten = null!;
    internal static ConfigEntry<float> CookingBonusChanceAtMaxCookingPercent = null!;
    internal static ConfigEntry<float> FermenterOutputBonusChanceAtMaxCookingPercent = null!;
    internal static ConfigEntry<string> CookingBonusExcludedOutputPrefabs = null!;

    internal static void Initialize(ConfigFile config, ConfigSync configSync)
    {
        MaxFoodSlots = BindSynced(
            config,
            configSync,
            ConfigPresentation.Diet,
            "Maximum Food Slots",
            9,
            ConfigPresentation.Synced(
                "Maximum number of active food slots. Choose either six or nine; slots unlock from three as the player learns more directly edible Health/Stamina/Eitr foods.",
                ConfigPresentation.Diet,
                500,
                new AcceptableValueList<int>(6, 9)));

        SixSlotFoodStatScale = BindSynced(
            config,
            configSync,
            ConfigPresentation.Diet,
            "6-Slot Food Stat Scale",
            0.45f,
            ConfigPresentation.Synced(
                "Per-food stat multiplier at the six-slot maximum. Earlier unlocked tiers are automatically raised so a completely filled current diet keeps the same total base strength. The default keeps that total at 90% of three vanilla food slots before Full Course.",
                ConfigPresentation.Diet,
                450,
                new AcceptableValueRange<float>(0.1f, 3f)));

        NineSlotFoodStatScale = BindSynced(
            config,
            configSync,
            ConfigPresentation.Diet,
            "9-Slot Food Stat Scale",
            0.3f,
            ConfigPresentation.Synced(
                "Per-food stat multiplier at the nine-slot maximum. Earlier unlocked tiers are automatically raised so a completely filled current diet keeps the same total base strength. The default keeps that total at 90% of three vanilla food slots before Full Course.",
                ConfigPresentation.Diet,
                400,
                new AcceptableValueRange<float>(0.1f, 3f)));

        FullCourseMultiplier = BindSynced(
            config,
            configSync,
            ConfigPresentation.Diet,
            "Full Course Multiplier",
            1.2f,
            ConfigPresentation.Synced(
                "Multiplier applied to health, stamina, eitr, and health regeneration from every active food once at least six slots are unlocked and every currently unlocked slot is filled with a directly edible Health/Stamina/Eitr food. 1 disables the bonus.",
                ConfigPresentation.Diet,
                350,
                new AcceptableValueRange<float>(1f, 5f)));

        RecentHistorySize = BindSynced(
            config,
            configSync,
            ConfigPresentation.Diet,
            "Recent Food History Size",
            7,
            ConfigPresentation.Synced(
                "Number of unique recent foods tracked in the HUD and history.",
                ConfigPresentation.Diet,
                300,
                new AcceptableValueRange<int>(1, 12)));

        DiminishingThreshold = BindSynced(
            config,
            configSync,
            ConfigPresentation.Diet,
            "Diminishing Returns Start Count",
            4,
            ConfigPresentation.Synced(
                "The consumption count that starts diminishing returns for regular foods.",
                ConfigPresentation.Diet,
                200,
                new AcceptableValueRange<int>(1, 20)));

        DiminishingFactor = BindSynced(
            config,
            configSync,
            ConfigPresentation.Diet,
            "Diminishing Returns Multiplier",
            0.75f,
            ConfigPresentation.Synced(
                "Single multiplier applied at and after the diminishing threshold.",
                ConfigPresentation.Diet,
                100,
                new AcceptableValueRange<float>(0.1f, 1f)));

        PukeRemovalOrder = BindSynced(
            config,
            configSync,
            ConfigPresentation.Diet,
            "Puke Food Removal Order",
            PukeFoodRemovalOrder.NewestFirst,
            ConfigPresentation.Synced(
                "Controls which active food each SE_Puke removal tick removes. Vanilla uses Random. OldestFirst and NewestFirst compare the time elapsed since each food was last eaten.",
                ConfigPresentation.Diet,
                50));

        ChefCollectionSize = BindSynced(
            config,
            configSync,
            ConfigPresentation.ChefChoice,
            "List Size",
            7,
            ConfigPresentation.Synced(
                "Number of active Chef's Choice foods shown in the HUD.",
                ConfigPresentation.ChefChoice,
                600,
                new AcceptableValueRange<int>(1, 12)));

        ChefMultiplierMin = BindSynced(
            config,
            configSync,
            ConfigPresentation.ChefChoice,
            "Minimum Multiplier",
            1.1f,
            ConfigPresentation.Synced(
                "Minimum random multiplier for Chef's Choice foods. If it exceeds the configured maximum, the effective maximum is raised to this value.",
                ConfigPresentation.ChefChoice,
                500,
                new AcceptableValueRange<float>(ChefChoiceMath.MinimumAllowedMultiplier, 5f)));

        ChefMultiplierModeAtMaxCooking = BindSynced(
            config,
            configSync,
            ConfigPresentation.ChefChoice,
            "Most Likely Multiplier at Max Cooking Level",
            1.5f,
            ConfigPresentation.Synced(
                "Most likely Chef's Choice multiplier at maximum Cooking level. At level 0, Minimum Multiplier is most likely; intermediate levels move the triangular distribution's mode linearly between them. The effective value is clamped to the configured multiplier range.",
                ConfigPresentation.ChefChoice,
                450,
                new AcceptableValueRange<float>(ChefChoiceMath.MinimumAllowedMultiplier, 5f)));

        ChefMultiplierMax = BindSynced(
            config,
            configSync,
            ConfigPresentation.ChefChoice,
            "Maximum Multiplier",
            1.5f,
            ConfigPresentation.Synced(
                "Maximum random multiplier for Chef's Choice foods. The effective maximum cannot be lower than Minimum Multiplier.",
                ConfigPresentation.ChefChoice,
                400,
                new AcceptableValueRange<float>(ChefChoiceMath.MinimumAllowedMultiplier, 5f)));

        ChefHighTierSelectionStrength = BindSynced(
            config,
            configSync,
            ConfigPresentation.ChefChoice,
            "High-Tier Selection Strength",
            5f,
            ConfigPresentation.Synced(
                "Controls how strongly Cooking level favors higher ResourceMap tiers in Chef's Choice. Higher values make high-tier foods more likely at high Cooking levels. 0 disables tier weighting.",
                ConfigPresentation.ChefChoice,
                300,
                new AcceptableValueRange<float>(
                    0f,
                    ChefChoiceMath.MaximumTierSelectionStrength)));

        ChefRecentFoodPreferencePercent = BindSynced(
            config,
            configSync,
            ConfigPresentation.ChefChoice,
            "Recent Food-Type Preference (%)",
            70f,
            ConfigPresentation.Synced(
                "Percentage used to blend the existing Chef food-type probabilities with the Health/Stamina/Eitr proportions in recent unique-food history. 0 keeps the existing distribution; 100 follows the history proportions exactly when those food types are available.",
                ConfigPresentation.ChefChoice,
                100,
                new AcceptableValueRange<float>(0f, 100f)));

        CookingExperiencePerFoodEaten = BindSynced(
            config,
            configSync,
            ConfigPresentation.General,
            "Cooking Experience per Food Eaten",
            0.15f,
            ConfigPresentation.Synced(
                "Cooking skill experience granted after successfully eating a directly edible Health/Stamina/Eitr food. 0 disables this reward.",
                ConfigPresentation.General,
                450,
                new AcceptableValueRange<float>(0f, 1f)));

        CookingBonusChanceAtMaxCookingPercent = BindSynced(
            config,
            configSync,
            ConfigPresentation.General,
            "Production Bonus Chance at Max Cooking (%)",
            25f,
            ConfigPresentation.Synced(
                "Independent bonus chance per base output item at Cooking level 100 for Cooking recipes and CookingStation outputs. Lower Cooking levels scale this chance linearly.",
                ConfigPresentation.General,
                400,
                new AcceptableValueRange<float>(
                    0f,
                    CookingProductionBonusCore.MaximumChanceAtMaxCookingPercent)));

        FermenterOutputBonusChanceAtMaxCookingPercent = BindSynced(
            config,
            configSync,
            ConfigPresentation.General,
            "Fermenter Output Bonus Chance at Max Cooking (%)",
            20f,
            ConfigPresentation.Synced(
                "Independent bonus chance per base Fermenter output item at Cooking level 100. Lower Cooking levels scale this chance linearly.",
                ConfigPresentation.General,
                350,
                new AcceptableValueRange<float>(
                    0f,
                    CookingProductionBonusCore.MaximumChanceAtMaxCookingPercent)));

        CookingBonusExcludedOutputPrefabs = BindSynced(
            config,
            configSync,
            ConfigPresentation.General,
            "Production Bonus Excluded Output Prefabs",
            string.Empty,
            ConfigPresentation.Synced(
                "Comma-, semicolon-, or newline-separated output prefab names that receive no Cooking production bonus. '*' is a case-insensitive whole-name wildcard.",
                ConfigPresentation.General,
                300));
    }

    internal static void Shutdown()
    {
        MaxFoodSlots = null!;
        SixSlotFoodStatScale = null!;
        NineSlotFoodStatScale = null!;
        FullCourseMultiplier = null!;
        RecentHistorySize = null!;
        DiminishingThreshold = null!;
        DiminishingFactor = null!;
        PukeRemovalOrder = null!;
        ChefCollectionSize = null!;
        ChefMultiplierMin = null!;
        ChefMultiplierMax = null!;
        ChefHighTierSelectionStrength = null!;
        ChefMultiplierModeAtMaxCooking = null!;
        ChefRecentFoodPreferencePercent = null!;
        CookingExperiencePerFoodEaten = null!;
        CookingBonusChanceAtMaxCookingPercent = null!;
        FermenterOutputBonusChanceAtMaxCookingPercent = null!;
        CookingBonusExcludedOutputPrefabs = null!;
    }

    internal static int GetMaxFoodSlots() => MaxFoodSlots.Value;
    internal static float GetBaseSlotScale(int unlockedFoodSlots) =>
        CalculateBaseSlotScale(
            GetMaxFoodSlots(),
            unlockedFoodSlots,
            SixSlotFoodStatScale.Value,
            NineSlotFoodStatScale.Value);
    internal static float CalculateBaseSlotScale(
        int maximumFoodSlots,
        int unlockedFoodSlots,
        float sixSlotScale,
        float nineSlotScale)
    {
        int maximum = maximumFoodSlots <= 6 ? 6 : 9;
        int unlocked = Math.Max(3, Math.Min(maximum, unlockedFoodSlots));
        float endpointScale = maximum == 6 ? sixSlotScale : nineSlotScale;
        return endpointScale * maximum / unlocked;
    }
    internal static float GetFullCourseMultiplier() => FullCourseMultiplier.Value;
    internal static int GetRecentHistorySize() => RecentHistorySize.Value;
    internal static int GetDiminishingThreshold() => DiminishingThreshold.Value;
    internal static float GetDiminishingFactor() => DiminishingFactor.Value;
    internal static PukeFoodRemovalOrder GetPukeFoodRemovalOrder() =>
        PukeRemovalOrder?.Value ?? PukeFoodRemovalOrder.NewestFirst;
    internal static int GetChefCollectionSize() => ChefCollectionSize.Value;
    internal static float GetChefMultiplierMin() =>
        ChefChoiceMath.ClampMultiplierMinimum(ChefMultiplierMin.Value);
    internal static float GetChefMultiplierMax() =>
        ChefChoiceMath.ClampMultiplierMaximum(
            ChefMultiplierMax.Value,
            GetChefMultiplierMin());
    internal static float GetChefHighTierSelectionStrength() =>
        ChefHighTierSelectionStrength.Value;
    internal static float GetChefMultiplierModeAtMaxCooking() =>
        ChefChoiceMath.ClampMultiplierMode(
            ChefMultiplierModeAtMaxCooking.Value,
            GetChefMultiplierMin(),
            GetChefMultiplierMax());
    internal static float GetChefRecentFoodPreferencePercent() =>
        ChefRecentFoodPreferencePercent.Value;
    internal static float GetCookingExperiencePerFoodEaten() =>
        CookingExperiencePerFoodEaten.Value;
    internal static float GetCookingBonusChanceAtMaxCookingPercent() =>
        CookingBonusChanceAtMaxCookingPercent.Value;
    internal static float GetFermenterOutputBonusChanceAtMaxCookingPercent() =>
        FermenterOutputBonusChanceAtMaxCookingPercent.Value;
    internal static string GetCookingBonusExcludedOutputPrefabs() => CookingBonusExcludedOutputPrefabs.Value;

    private static ConfigEntry<T> BindSynced<T>(
        ConfigFile config,
        ConfigSync configSync,
        ConfigPresentation.SectionDefinition section,
        string key,
        T defaultValue,
        ConfigDescription description)
    {
        ConfigEntry<T> entry = config.Bind(section.Name, key, defaultValue, description);
        SyncedConfigEntry<T> synced = configSync.AddConfigEntry(entry);
        synced.SynchronizedConfig = true;
        return entry;
    }
}
