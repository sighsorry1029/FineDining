using System;
using System.Collections.Generic;

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
        bool isChef,
        float chefMultiplier,
        bool fullCourseActive)
    {
        FreshnessScale = freshnessScale;
        AppliedScale = appliedScale;
        EffectiveScale = effectiveScale;
        DiminishingScale = diminishingScale;
        Health = health;
        Stamina = stamina;
        Eitr = eitr;
        Regen = regen;
        IsChef = isChef;
        ChefMultiplier = chefMultiplier;
        FullCourseActive = fullCourseActive;
    }

    internal float FreshnessScale { get; }
    internal float AppliedScale { get; }
    internal float EffectiveScale { get; }
    internal float DiminishingScale { get; }
    internal float Health { get; }
    internal float Stamina { get; }
    internal float Eitr { get; }
    internal float Regen { get; }
    internal bool IsChef { get; }
    internal float ChefMultiplier { get; }
    internal bool FullCourseActive { get; }
}

internal static class FoodRules
{
    internal const int MinimumFullCourseSlots = 6;

    internal static float CalculateRegularFoodScale(
        Player player,
        PlayerFoodStateData state,
        int historyStack) =>
        DietConfig.GetBaseSlotScale(FoodSlotProgression.GetCurrentSlots(player, state)) *
        CalculateDiminishingScale(historyStack);

    internal static float CalculateDiminishingScale(int historyStack) =>
        historyStack >= DietConfig.GetDiminishingThreshold()
            ? DietConfig.GetDiminishingFactor()
            : 1f;

    internal static bool IsFullCourseActive(Player? player)
    {
        if (player == null)
        {
            return false;
        }

        PlayerFoodStateData state = FoodStateStore.GetState(player);
        return IsFullCourseActive(
            player,
            state,
            CountActiveDietFoods(player.GetFoods()));
    }

    internal static bool IsFullCourseActive(
        Player player,
        PlayerFoodStateData state,
        int activeFoodCount)
    {
        int unlockedFoodSlots = FoodSlotProgression.GetCurrentSlots(player, state);
        return IsFullCourseEligible(unlockedFoodSlots, activeFoodCount);
    }

    internal static bool IsFullCourseEligible(
        int unlockedFoodSlots,
        int activeFoodCount) =>
        unlockedFoodSlots >= MinimumFullCourseSlots &&
        activeFoodCount >= unlockedFoodSlots;

    internal static float GetFullCourseScale(
        Player player,
        PlayerFoodStateData state,
        int activeFoodCount) =>
        IsFullCourseActive(player, state, activeFoodCount)
            ? DietConfig.GetFullCourseMultiplier()
            : 1f;

    internal static bool WillHaveFullCourseAfterEating(
        Player player,
        PlayerFoodStateData state,
        ItemDrop.ItemData item,
        bool replacesExistingFood = false,
        bool replacesDietFood = false)
    {
        int appliedSlotsAfterEating = replacesExistingFood
            ? FoodSlotProgression.GetSlotsAfterFoodRemoval(player, state)
            : FoodSlotProgression.GetCurrentSlots(player, state);
        if (appliedSlotsAfterEating < MinimumFullCourseSlots)
        {
            return false;
        }

        List<Player.Food> foods = player.GetFoods();
        int activeFoodCount = CountActiveDietFoods(foods);
        string itemKey = FoodIdentity.GetCanonicalPrefabName(item);
        foreach (Player.Food food in foods)
        {
            if (FoodIdentity.IsDirectlyEdible(food.m_item) &&
                FoodIdentity.GetCanonicalPrefabName(food) == itemKey)
            {
                return IsFullCourseEligible(appliedSlotsAfterEating, activeFoodCount);
            }
        }

        if ((!replacesExistingFood || !replacesDietFood) &&
            activeFoodCount < appliedSlotsAfterEating)
        {
            activeFoodCount++;
        }

        return IsFullCourseEligible(appliedSlotsAfterEating, activeFoodCount);
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
        Player.Food? targetFood = FindTargetFood(player, state, item);
        bool replacesExistingFood = targetFood != null &&
                                    player.GetFoods().Contains(targetFood);
        bool replacesDietFood = replacesExistingFood &&
                                FoodIdentity.IsDirectlyEdible(targetFood!.m_item);
        bool fullCourseActive = WillHaveFullCourseAfterEating(
            player,
            state,
            item,
            replacesExistingFood,
            replacesDietFood);
        int foodSlots = replacesExistingFood
            ? FoodSlotProgression.GetSlotsAfterFoodRemoval(player, state)
            : FoodSlotProgression.GetCurrentSlots(player, state);
        return CalculateFoodEffect(
            item,
            stack,
            isChef,
            chefMultiplier,
            fullCourseActive,
            foodSlots);
    }

    internal static FoodEffect CalculateFoodEffect(
        ItemDrop.ItemData item,
        int stack,
        bool isChef,
        float chefMultiplier,
        bool fullCourseActive,
        int unlockedFoodSlots)
    {
        float diminishingScale = isChef ? 1f : CalculateDiminishingScale(stack);
        float baseScale = DietConfig.GetBaseSlotScale(unlockedFoodSlots) *
                          (isChef ? chefMultiplier : diminishingScale);
        float freshnessScale = FreshnessRuntime.GetFoodStatMultiplier(item);
        if (float.IsNaN(freshnessScale) || float.IsInfinity(freshnessScale))
        {
            freshnessScale = 1f;
        }

        freshnessScale = Math.Max(0f, Math.Min(1f, freshnessScale));
        float appliedScale = baseScale * freshnessScale;
        float effectiveScale = appliedScale *
                               (fullCourseActive
                                   ? DietConfig.GetFullCourseMultiplier()
                                   : 1f);
        return new FoodEffect(
            freshnessScale,
            appliedScale,
            effectiveScale,
            diminishingScale,
            item.m_shared.m_food * effectiveScale,
            item.m_shared.m_foodStamina * effectiveScale,
            item.m_shared.m_foodEitr * effectiveScale,
            item.m_shared.m_foodRegen * effectiveScale,
            isChef,
            chefMultiplier,
            fullCourseActive);
    }

    internal static float GetAppliedScale(
        Player player,
        PlayerFoodStateData state,
        Player.Food food)
    {
        if (!FoodIdentity.IsDirectlyEdible(food?.m_item))
        {
            return 1f;
        }

        string key = FoodIdentity.GetCanonicalPrefabName(food);
        ActiveFoodData? active = GetActiveFood(state, key);
        if (active != null)
        {
            return active.AppliedScale;
        }

        HistoryEntryData? history = RecentHistoryService.GetEntry(state, key);
        return history != null
            ? CalculateRegularFoodScale(player, state, history.Stack)
            : DietConfig.GetBaseSlotScale(
                FoodSlotProgression.GetCurrentSlots(player, state));
    }

    internal static int CountActiveDietFoods(IReadOnlyList<Player.Food>? foods)
    {
        if (foods == null)
        {
            return 0;
        }

        int count = 0;
        for (int index = 0; index < foods.Count; index++)
        {
            if (FoodIdentity.IsDirectlyEdible(foods[index]?.m_item))
            {
                count++;
            }
        }

        return count;
    }

    internal static Player.Food? FindTargetFood(
        Player player,
        PlayerFoodStateData state,
        ItemDrop.ItemData item)
    {
        List<Player.Food> foods = player.GetFoods();
        string itemKey = FoodIdentity.GetCanonicalPrefabName(item);
        foreach (Player.Food food in foods)
        {
            if (FoodIdentity.GetCanonicalPrefabName(food) == itemKey)
            {
                return food.CanEatAgain() ? food : null;
            }
        }

        if (foods.Count < FoodSlotProgression.GetCurrentSlots(player, state))
        {
            return new Player.Food();
        }

        Player.Food? mostDepleted = null;
        foreach (Player.Food food in foods)
        {
            if (food.CanEatAgain() &&
                (mostDepleted == null || food.m_time < mostDepleted.m_time))
            {
                mostDepleted = food;
            }
        }

        return mostDepleted;
    }

    internal static void SetActiveFoodScale(
        PlayerFoodStateData state,
        string key,
        float scale)
    {
        for (int index = 0; index < state.Active.Count; index++)
        {
            ActiveFoodData active = state.Active[index];
            if (active.Key != key)
            {
                continue;
            }

            active.AppliedScale = scale;
            if (index != state.Active.Count - 1)
            {
                // Active is also the persisted consumption order. Re-eating a
                // food keeps its Player.Food slot but makes it the newest entry.
                state.Active.RemoveAt(index);
                state.Active.Add(active);
            }

            return;
        }

        state.Active.Add(new ActiveFoodData
        {
            Key = key,
            AppliedScale = scale
        });
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

internal static class FoodSlotProgression
{
    internal const int MinimumFoodSlots = 3;
    internal const int MaximumFoodSlots = 9;

    private static readonly Dictionary<Player, KnownFoodCache> KnownFoodCaches = new();

    internal static int CalculateUnlockedSlots(
        int knownFoodCount,
        int maximumFoodSlots)
    {
        int maximum = maximumFoodSlots <= 6 ? 6 : MaximumFoodSlots;
        int unlocked = MinimumFoodSlots +
                       (Math.Max(0, knownFoodCount - 6) + 2) / 3;
        return Math.Min(maximum, Math.Min(MaximumFoodSlots, unlocked));
    }

    internal static float CalculateScaleRebase(float previousBaseScale, float nextBaseScale)
    {
        if (float.IsNaN(previousBaseScale) ||
            float.IsInfinity(previousBaseScale) ||
            previousBaseScale <= 0f ||
            float.IsNaN(nextBaseScale) ||
            float.IsInfinity(nextBaseScale) ||
            nextBaseScale <= 0f)
        {
            return 1f;
        }

        return nextBaseScale / previousBaseScale;
    }

    internal static int GetCurrentSlots(Player player, PlayerFoodStateData state)
    {
        NormalizeState(player, state);
        return state.UnlockedFoodSlots;
    }

    internal static int GetKnownFoodCount(Player? player)
    {
        return TryGetKnownFoodKeys(player, out HashSet<string> knownFoods)
            ? knownFoods.Count
            : 0;
    }

    internal static bool IsKnownFood(
        Player player,
        ChefFoodTierInfo tierInfo)
    {
        string itemNameToken = tierInfo.ItemNameToken;
        return !string.IsNullOrWhiteSpace(itemNameToken) &&
               (player.IsRecipeKnown(itemNameToken) ||
                player.IsKnownMaterial(itemNameToken));
    }

    internal static bool ApplyPendingAfterFoodRemoval(
        Player player,
        PlayerFoodStateData state,
        bool trimExcess = true)
    {
        NormalizeState(player, state);
        if (!TryGetDesiredSlots(player, out int desiredSlots))
        {
            return false;
        }

        bool changed = ApplySlotCount(state, desiredSlots);
        if (trimExcess)
        {
            TrimExcessFoods(player, state);
        }

        return changed;
    }

    internal static int GetSlotsAfterFoodRemoval(
        Player player,
        PlayerFoodStateData state)
    {
        NormalizeState(player, state);
        return TryGetDesiredSlots(player, out int desiredSlots)
            ? desiredSlots
            : GetCurrentSlots(player, state);
    }

    internal static bool ReconcileConfiguration(
        Player player,
        PlayerFoodStateData state)
    {
        int previousSlots = state.UnlockedFoodSlots;
        float previousBaseScale = state.AppliedBaseSlotScale;
        NormalizeState(player, state);
        bool changed = previousSlots != state.UnlockedFoodSlots ||
                       Math.Abs(previousBaseScale - state.AppliedBaseSlotScale) > 0.00001f;
        if (!TryGetDesiredSlots(player, out int desiredSlots))
        {
            return changed;
        }

        int currentFoodCount = player.GetFoods().Count;
        return currentFoodCount == 0
            ? ApplySlotCount(state, desiredSlots) || changed
            : changed;
    }

    internal static void TrimExcessFoods(
        Player player,
        PlayerFoodStateData state,
        Player.Food? protectedFood = null)
    {
        List<Player.Food> foods = player.GetFoods();
        int maximum = GetCurrentSlots(player, state);
        while (foods.Count > maximum)
        {
            int mostDepletedIndex = -1;
            for (int index = 0; index < foods.Count; index++)
            {
                if (ReferenceEquals(foods[index], protectedFood) ||
                    mostDepletedIndex >= 0 &&
                    foods[index].m_time >= foods[mostDepletedIndex].m_time)
                {
                    continue;
                }

                mostDepletedIndex = index;
            }

            if (mostDepletedIndex < 0)
            {
                break;
            }

            foods.RemoveAt(mostDepletedIndex);
        }
    }

    internal static void NormalizeState(Player player, PlayerFoodStateData state)
    {
        int maximum = DietConfig.GetMaxFoodSlots();
        int currentFoodCount = player.GetFoods().Count;
        int current = state.UnlockedFoodSlots;
        if (current < MinimumFoodSlots || current > MaximumFoodSlots)
        {
            current = Math.Max(
                MinimumFoodSlots,
                Math.Min(maximum, currentFoodCount));
        }
        else
        {
            current = Math.Min(maximum, current);
        }

        if (currentFoodCount == 0 && TryGetDesiredSlots(player, out int desiredSlots))
        {
            current = desiredSlots;
        }

        ApplySlotCount(state, current);
    }

    internal static void Invalidate(Player? player)
    {
        if (player != null)
        {
            KnownFoodCaches.Remove(player);
        }
    }

    internal static void Reset() => KnownFoodCaches.Clear();

    private static bool TryGetDesiredSlots(Player? player, out int desiredSlots)
    {
        if (!TryGetKnownFoodKeys(player, out HashSet<string> knownFoods))
        {
            desiredSlots = MinimumFoodSlots;
            return false;
        }

        desiredSlots = CalculateUnlockedSlots(
            knownFoods.Count,
            DietConfig.GetMaxFoodSlots());
        return true;
    }

    private static bool ApplySlotCount(PlayerFoodStateData state, int desiredSlots)
    {
        int maximum = DietConfig.GetMaxFoodSlots();
        int next = Math.Max(MinimumFoodSlots, Math.Min(maximum, desiredSlots));
        int previous = Math.Max(
            MinimumFoodSlots,
            Math.Min(MaximumFoodSlots, state.UnlockedFoodSlots));
        float nextBaseScale = DietConfig.GetBaseSlotScale(next);
        float previousBaseScale = state.AppliedBaseSlotScale;
        bool previousBaseScaleIsValid =
            !float.IsNaN(previousBaseScale) &&
            !float.IsInfinity(previousBaseScale) &&
            previousBaseScale > 0f;
        if (!previousBaseScaleIsValid)
        {
            state.UnlockedFoodSlots = next;
            state.AppliedBaseSlotScale = nextBaseScale;
            return next != previous;
        }

        bool slotCountChanged = next != previous;
        bool baseScaleChanged = nextBaseScale != previousBaseScale;
        if (!slotCountChanged && !baseScaleChanged)
        {
            state.UnlockedFoodSlots = next;
            state.AppliedBaseSlotScale = nextBaseScale;
            return false;
        }

        float scaleRatio = CalculateScaleRebase(previousBaseScale, nextBaseScale);
        foreach (ActiveFoodData active in state.Active)
        {
            if (active == null ||
                float.IsNaN(active.AppliedScale) ||
                float.IsInfinity(active.AppliedScale) ||
                active.AppliedScale < 0f)
            {
                continue;
            }

            active.AppliedScale *= scaleRatio;
        }

        state.UnlockedFoodSlots = next;
        state.AppliedBaseSlotScale = nextBaseScale;
        return true;
    }

    private static bool TryGetKnownFoodKeys(
        Player? player,
        out HashSet<string> knownFoods)
    {
        knownFoods = null!;
        if (player == null || !ChefFoodTierCatalog.IsReady)
        {
            return false;
        }

        int recipeCount = PlayerPrivateAccess.KnownRecipes(player).Count;
        int materialCount = PlayerPrivateAccess.KnownMaterials(player).Count;
        if (KnownFoodCaches.TryGetValue(player, out KnownFoodCache? cached) &&
            cached.CatalogVersion == ChefFoodTierCatalog.Version &&
            cached.KnownRecipeCount == recipeCount &&
            cached.KnownMaterialCount == materialCount)
        {
            knownFoods = cached.Keys;
            return true;
        }

        HashSet<string> rebuilt = new(StringComparer.Ordinal);
        foreach (ChefFoodTierInfo tierInfo in ChefFoodTierCatalog.GetSnapshot())
        {
            if (tierInfo.Axis == FoodStatAxis.None ||
                string.IsNullOrWhiteSpace(tierInfo.PrefabName) ||
                !IsKnownFood(player, tierInfo))
            {
                continue;
            }

            rebuilt.Add(tierInfo.PrefabName);
        }

        KnownFoodCaches[player] = new KnownFoodCache(
            ChefFoodTierCatalog.Version,
            recipeCount,
            materialCount,
            rebuilt);
        knownFoods = rebuilt;
        return true;
    }

    private sealed class KnownFoodCache
    {
        internal KnownFoodCache(
            int catalogVersion,
            int knownRecipeCount,
            int knownMaterialCount,
            HashSet<string> keys)
        {
            CatalogVersion = catalogVersion;
            KnownRecipeCount = knownRecipeCount;
            KnownMaterialCount = knownMaterialCount;
            Keys = keys;
        }

        internal int CatalogVersion { get; }
        internal int KnownRecipeCount { get; }
        internal int KnownMaterialCount { get; }
        internal HashSet<string> Keys { get; }
    }
}
