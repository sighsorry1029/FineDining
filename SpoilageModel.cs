namespace FineDining;

internal enum SpoilageGroup
{
    FarmingHarvest,
    CookingStationInput,
    CookingStationOutput,
    FermentedFood,
    UnfermentedFood,
    FeastMaterial,
    FeastResult,
    Fish,
    OtherEdible
}

internal static class SpoilageDefaults
{
    internal const string RottenMeatPrefabName = "RottenMeat";
    internal const string RottenProducePrefabName = "FineDining_RottenProduce";
    internal const string RottenFoodPrefabName = "FineDining_RottenFood";

    internal static string GetReplacementPrefab(SpoilageGroup group)
    {
        return group switch
        {
            SpoilageGroup.FarmingHarvest => RottenProducePrefabName,
            SpoilageGroup.CookingStationInput => RottenMeatPrefabName,
            SpoilageGroup.CookingStationOutput => RottenMeatPrefabName,
            SpoilageGroup.Fish => RottenMeatPrefabName,
            SpoilageGroup.FermentedFood => RottenFoodPrefabName,
            SpoilageGroup.UnfermentedFood => RottenFoodPrefabName,
            SpoilageGroup.FeastMaterial => RottenFoodPrefabName,
            SpoilageGroup.FeastResult => RottenFoodPrefabName,
            SpoilageGroup.OtherEdible => RottenFoodPrefabName,
            _ => throw new System.ArgumentOutOfRangeException(
                nameof(group),
                group,
                "Unknown spoilage group.")
        };
    }
}

internal enum SpoilageRuleState
{
    NotReady,
    NotTracked,
    Disabled,
    Enabled
}

internal readonly struct ResolvedSpoilageRule
{
    internal ResolvedSpoilageRule(
        SpoilageRuleState state,
        long lifetimeTicks = 0L,
        string replacementPrefab = "",
        SpoilageGroup group = SpoilageGroup.OtherEdible,
        bool isOverride = false)
    {
        State = state;
        LifetimeTicks = lifetimeTicks;
        ReplacementPrefab = replacementPrefab;
        Group = group;
        IsOverride = isOverride;
    }

    internal SpoilageRuleState State { get; }

    internal long LifetimeTicks { get; }

    internal string ReplacementPrefab { get; }

    internal SpoilageGroup Group { get; }

    internal bool IsOverride { get; }
}
