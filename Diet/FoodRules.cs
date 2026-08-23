using System;

namespace FineDining;

internal readonly struct FoodEffect
{
    internal FoodEffect(
        float freshnessScale,
        float appliedScale,
        float effectiveScale,
        float diminishingScale,
        float health,
        float stamina,
        float eitr,
        float regen,
        float duration,
        bool isChef,
        float chefMultiplier,
        bool fullStraightActive)
    {
        FreshnessScale = freshnessScale;
        AppliedScale = appliedScale;
        EffectiveScale = effectiveScale;
        DiminishingScale = diminishingScale;
        Health = health;
        Stamina = stamina;
        Eitr = eitr;
        Regen = regen;
        Duration = duration;
        IsChef = isChef;
        ChefMultiplier = chefMultiplier;
        FullStraightActive = fullStraightActive;
    }

    internal float FreshnessScale { get; }
    internal float AppliedScale { get; }
    internal float EffectiveScale { get; }
    internal float DiminishingScale { get; }
    internal float Health { get; }
    internal float Stamina { get; }
    internal float Eitr { get; }
    internal float Regen { get; }
    internal float Duration { get; }
    internal bool IsChef { get; }
    internal float ChefMultiplier { get; }
    internal bool FullStraightActive { get; }
}

internal static class FoodRules
{
    internal const float FullStraightMultiplier = 1.2f;

    internal static float CalculateRegularFoodScale(int historyStack) =>
        DietConfig.GetBaseSlotScale() * CalculateDiminishingScale(historyStack);

    internal static float CalculateDiminishingScale(int historyStack) =>
        historyStack >= DietConfig.GetDiminishingThreshold()
            ? DietConfig.GetDiminishingFactor()
            : 1f;

    internal static bool IsFullStraightActive(Player? player) =>
        player != null && IsFullStraightActive(player.GetFoods().Count);

    internal static bool IsFullStraightActive(int activeFoodCount) =>
        activeFoodCount >= DietConfig.GetMaxFoodSlots();

    internal static float GetFullStraightScale(int activeFoodCount) =>
        IsFullStraightActive(activeFoodCount) ? FullStraightMultiplier : 1f;

    internal static bool WillHaveFullStraightAfterEating(Player player, ItemDrop.ItemData item)
    {
        int activeFoodCount = player.GetFoods().Count;
        string itemKey = FoodIdentity.GetCanonicalPrefabName(item);
        foreach (Player.Food food in player.GetFoods())
        {
            if (FoodIdentity.GetCanonicalPrefabName(food) == itemKey)
            {
                return IsFullStraightActive(activeFoodCount);
            }
        }

        if (activeFoodCount < DietConfig.GetMaxFoodSlots())
        {
            activeFoodCount++;
        }

        return IsFullStraightActive(activeFoodCount);
    }

    internal static FoodEffect PreviewNextFoodEffect(
        Player player,
        PlayerFoodStateData state,
        ItemDrop.ItemData item)
    {
        string key = FoodIdentity.GetCanonicalPrefabName(item);
        ChefEntryData? chefEntry = ChefCollectionService.GetEntry(state, key);
        bool isChef = chefEntry != null;
        int stack = RecentHistoryService.GetNextStack(state, key, isChef);
        float chefMultiplier = chefEntry?.Multiplier ?? 1f;
        bool fullStraightActive = WillHaveFullStraightAfterEating(player, item);
        return CalculateFoodEffect(item, stack, isChef, chefMultiplier, fullStraightActive);
    }

    internal static FoodEffect CalculateFoodEffect(
        ItemDrop.ItemData item,
        int stack,
        bool isChef,
        float chefMultiplier,
        bool fullStraightActive)
    {
        float diminishingScale = isChef ? 1f : CalculateDiminishingScale(stack);
        float baseScale = DietConfig.GetBaseSlotScale() *
                          (isChef ? chefMultiplier : diminishingScale);
        float freshnessScale = FreshnessRuntime.GetFoodStatMultiplier(item);
        if (float.IsNaN(freshnessScale) || float.IsInfinity(freshnessScale))
        {
            freshnessScale = 1f;
        }

        freshnessScale = Math.Max(0f, Math.Min(1f, freshnessScale));
        float appliedScale = baseScale * freshnessScale;
        float effectiveScale = appliedScale *
                               (fullStraightActive ? FullStraightMultiplier : 1f);
        return new FoodEffect(
            freshnessScale,
            appliedScale,
            effectiveScale,
            diminishingScale,
            item.m_shared.m_food * effectiveScale,
            item.m_shared.m_foodStamina * effectiveScale,
            item.m_shared.m_foodEitr * effectiveScale,
            item.m_shared.m_foodRegen * effectiveScale,
            item.m_shared.m_foodBurnTime,
            isChef,
            chefMultiplier,
            fullStraightActive);
    }

    internal static float GetAppliedScale(PlayerFoodStateData state, Player.Food food)
    {
        string key = FoodIdentity.GetCanonicalPrefabName(food);
        ActiveFoodData? active = GetActiveFood(state, key);
        if (active != null)
        {
            return active.AppliedScale;
        }

        HistoryEntryData? history = RecentHistoryService.GetEntry(state, key);
        return history != null
            ? CalculateRegularFoodScale(history.Stack)
            : DietConfig.GetBaseSlotScale();
    }

    internal static void SetActiveFoodScale(
        PlayerFoodStateData state,
        string key,
        float scale)
    {
        ActiveFoodData? active = GetActiveFood(state, key);
        if (active == null)
        {
            state.Active.Add(new ActiveFoodData
            {
                Key = key,
                AppliedScale = scale
            });
            return;
        }

        active.AppliedScale = scale;
    }

    private static ActiveFoodData? GetActiveFood(PlayerFoodStateData state, string key)
    {
        foreach (ActiveFoodData active in state.Active)
        {
            if (active.Key == key)
            {
                return active;
            }
        }

        return null;
    }
}
