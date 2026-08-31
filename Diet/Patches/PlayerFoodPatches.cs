using System;
using System.Collections.Generic;
using HarmonyLib;

namespace FineDining;

[HarmonyPatch(typeof(Player))]
internal static class DietPlayerFoodPatches
{
    [HarmonyPatch(nameof(Player.CanEat))]
    [HarmonyPrefix]
    private static bool CanEatPrefix(
        Player __instance,
        ItemDrop.ItemData item,
        bool showMessages,
        ref bool __result)
    {
        if (!FoodIdentity.IsDirectlyEdible(item))
        {
            return true;
        }

        __result = PlayerFoodLogic.CanEat(__instance, item, showMessages);
        return false;
    }

    [HarmonyPatch(nameof(Player.EatFood))]
    [HarmonyPrefix]
    private static bool EatFoodPrefix(
        Player __instance,
        ItemDrop.ItemData item,
        ref bool __result,
        out VanillaEatBoundaryState? __state)
    {
        __state = null;
        if (!FoodIdentity.IsDirectlyEdible(item))
        {
            __state = new VanillaEatBoundaryState(__instance, item);
            return true;
        }

        __result = PlayerFoodLogic.EatFood(__instance, item);
        return false;
    }

    [HarmonyPatch(nameof(Player.EatFood))]
    [HarmonyPostfix]
    private static void EatFoodPostfix(
        Player __instance,
        ItemDrop.ItemData item,
        bool __result,
        VanillaEatBoundaryState? __state)
    {
        if (!__result)
        {
            return;
        }

        if (__instance == Player.m_localPlayer &&
            FoodIdentity.IsDirectlyEdible(item))
        {
            float experience = DietConfig.GetCookingExperiencePerFoodEaten();
            if (experience > 0f)
            {
                __instance.RaiseSkill(Skills.SkillType.Cooking, experience);
            }
        }

        if (__state == null)
        {
            return;
        }

        PlayerFoodStateData state = FoodStateStore.GetState(__instance);
        RecordVanillaFoodConsumption(__instance, item, state);
        bool replacedOrRemoved =
            __state.TryGetReplacedOrRemovedFood(__instance, out Player.Food? protectedFood);
        if (replacedOrRemoved)
        {
            FoodSlotProgression.ApplyPendingAfterFoodRemoval(
                __instance,
                state,
                trimExcess: false);
            FoodSlotProgression.TrimExcessFoods(__instance, state, protectedFood);
        }

        FoodStateStore.SaveState(__instance, state);
        if (replacedOrRemoved)
        {
            PlayerFoodLogic.RefreshFoodStats(__instance);
        }
    }

    private static void RecordVanillaFoodConsumption(
        Player player,
        ItemDrop.ItemData item,
        PlayerFoodStateData state)
    {
        string key = FoodIdentity.GetCanonicalPrefabName(item);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        foreach (Player.Food food in player.GetFoods())
        {
            if (FoodIdentity.GetCanonicalPrefabName(food) == key)
            {
                // Non-direct foods use vanilla stats, but still participate in
                // the exact oldest/newest consumption order used by SE_Puke.
                FoodRules.SetActiveFoodScale(state, key, 1f);
                return;
            }
        }
    }

    [HarmonyPatch("UpdateFood")]
    [HarmonyPrefix]
    private static bool UpdateFoodPrefix(Player __instance, float dt, bool forceUpdate)
    {
        PlayerFoodLogic.UpdateFood(__instance, dt, forceUpdate);
        return false;
    }

    [HarmonyPatch(nameof(Player.RemoveOneFood))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool RemoveOneFoodPrefix(Player __instance, ref bool __result)
    {
        if (!PukeFoodRemovalRuntime.TryRemoveOrderedFood(__instance, out bool removed))
        {
            return true;
        }

        __result = removed;
        return false;
    }

    [HarmonyPatch(nameof(Player.RemoveOneFood))]
    [HarmonyPostfix]
    private static void RemoveOneFoodPostfix(Player __instance, bool __result)
    {
        if (__result)
        {
            PlayerFoodStateData state = FoodStateStore.GetState(__instance);
            FoodSlotProgression.ApplyPendingAfterFoodRemoval(__instance, state);
            FoodStateStore.SaveState(__instance, state);
            PlayerFoodLogic.RefreshFoodStats(__instance);
        }
    }

    [HarmonyPatch(nameof(Player.ClearFood))]
    [HarmonyPostfix]
    private static void ClearFoodPostfix(Player __instance)
    {
        PlayerFoodStateData state = FoodStateStore.GetState(__instance);
        FoodSlotProgression.ApplyPendingAfterFoodRemoval(__instance, state);
        FoodStateStore.SaveState(__instance, state);
        PlayerFoodLogic.RefreshFoodStats(__instance);
    }

    [HarmonyPatch(nameof(Player.Load))]
    [HarmonyPostfix]
    private static void LoadPostfix(Player __instance)
    {
        FoodSlotProgression.Invalidate(__instance);
        FoodStateStore.Invalidate(__instance);
        DietModule.RequestDietReconcile();
        PlayerFoodStateData state = FoodStateStore.GetState(__instance);
        ChefCollectionService.EnsureChefCollection(__instance, state);
        FoodStateStore.SaveState(__instance, state);
        PlayerFoodLogic.RefreshFoodStats(__instance);
    }

    [HarmonyPatch("ResetCharacterKnownItems")]
    [HarmonyPostfix]
    private static void ResetCharacterKnownItemsPostfix(Player __instance)
    {
        FoodSlotProgression.Invalidate(__instance);
        DietModule.RequestDietReconcile();
    }

    [HarmonyPatch(nameof(Player.OnDeath))]
    [HarmonyPostfix]
    private static void OnDeathPostfix(Player __instance)
    {
        FoodStateStore.SaveState(__instance);
    }

    [HarmonyPatch("OnDestroy")]
    [HarmonyPrefix]
    private static void OnDestroyPrefix(Player __instance)
    {
        if (__instance == Player.m_localPlayer)
        {
            HudFoodPanels.ResetAll();
        }

        FoodStateStore.Invalidate(__instance);
        FoodSlotProgression.Invalidate(__instance);
    }

    private sealed class VanillaEatBoundaryState
    {
        private readonly FoodSnapshot[] _foods;
        private readonly Player.Food? _matchingFood;

        internal VanillaEatBoundaryState(
            Player player,
            ItemDrop.ItemData incomingItem)
        {
            var foods = player.GetFoods();
            _foods = new FoodSnapshot[foods.Count];
            string incomingKey = FoodIdentity.GetCanonicalPrefabName(incomingItem);
            for (int index = 0; index < foods.Count; index++)
            {
                _foods[index] = new FoodSnapshot(foods[index]);
                if (_matchingFood == null &&
                    FoodIdentity.GetCanonicalPrefabName(foods[index]) == incomingKey)
                {
                    _matchingFood = foods[index];
                }
            }
        }

        internal bool TryGetReplacedOrRemovedFood(
            Player player,
            out Player.Food? protectedFood)
        {
            protectedFood = null;
            var foods = player.GetFoods();
            if (foods.Count > _foods.Length)
            {
                return false;
            }

            if (_matchingFood != null && foods.Contains(_matchingFood))
            {
                protectedFood = _matchingFood;
                return true;
            }

            foreach (Player.Food food in foods)
            {
                int snapshotIndex = -1;
                for (int index = 0; index < _foods.Length; index++)
                {
                    if (_foods[index].References(food))
                    {
                        snapshotIndex = index;
                        break;
                    }
                }

                if (snapshotIndex < 0 || !_foods[snapshotIndex].Matches(food))
                {
                    protectedFood = food;
                    return true;
                }
            }

            return foods.Count < _foods.Length;
        }
    }

    private readonly struct FoodSnapshot
    {
        private readonly Player.Food _food;
        private readonly ItemDrop.ItemData _item;
        private readonly string _key;

        internal FoodSnapshot(Player.Food food)
        {
            _food = food;
            _item = food.m_item;
            _key = FoodIdentity.GetCanonicalPrefabName(food);
        }

        internal bool References(Player.Food food) =>
            ReferenceEquals(_food, food);

        internal bool Matches(Player.Food food) =>
            References(food) &&
            ReferenceEquals(_item, food.m_item) &&
            _key == FoodIdentity.GetCanonicalPrefabName(food);
    }
}

internal static class PukeFoodRemovalRuntime
{
    [ThreadStatic]
    private static int _pukeUpdateDepth;

    internal static void EnterPukeUpdate()
    {
        if (_pukeUpdateDepth < int.MaxValue)
        {
            _pukeUpdateDepth++;
        }
    }

    internal static void ExitPukeUpdate()
    {
        if (_pukeUpdateDepth > 0)
        {
            _pukeUpdateDepth--;
        }
    }

    internal static bool TryRemoveOrderedFood(Player? player, out bool removed)
    {
        removed = false;
        if (_pukeUpdateDepth <= 0 || player == null)
        {
            return false;
        }

        PukeFoodRemovalOrder order = DietConfig.GetPukeFoodRemovalOrder();
        if (order == PukeFoodRemovalOrder.Random)
        {
            // Delegate to Player.RemoveOneFood so the vanilla random choice and
            // any compatibility patches on the original method remain intact.
            return false;
        }

        List<Player.Food> foods = player.GetFoods();
        PlayerFoodStateData state = FoodStateStore.GetState(player);
        int removalIndex = SelectRemovalIndex(foods, order, state.Active);
        if (removalIndex >= 0)
        {
            foods.RemoveAt(removalIndex);
            removed = true;
        }

        return true;
    }

    internal static int SelectRemovalIndex(
        List<Player.Food>? foods,
        PukeFoodRemovalOrder order,
        IReadOnlyList<ActiveFoodData>? consumptionOrder = null)
    {
        if (foods == null || foods.Count == 0 || order == PukeFoodRemovalOrder.Random)
        {
            return -1;
        }

        bool newestFirst = order == PukeFoodRemovalOrder.NewestFirst;
        int selectedIndex = -1;
        float selectedElapsed = 0f;
        int selectedConsumptionRank = -1;
        for (int index = 0; index < foods.Count; index++)
        {
            if (!TryGetElapsedSinceEaten(foods[index], out float elapsed))
            {
                continue;
            }

            int comparison = elapsed.CompareTo(selectedElapsed);
            int consumptionRank = GetConsumptionRank(foods[index], consumptionOrder);
            bool winsElapsedComparison = newestFirst ? comparison < 0 : comparison > 0;
            bool winsEqualTimeComparison = comparison == 0 &&
                                           IsPreferredTie(
                                               index,
                                               consumptionRank,
                                               selectedIndex,
                                               selectedConsumptionRank,
                                               newestFirst);
            if (selectedIndex < 0 ||
                winsElapsedComparison ||
                winsEqualTimeComparison)
            {
                selectedIndex = index;
                selectedElapsed = elapsed;
                selectedConsumptionRank = consumptionRank;
            }
        }

        // A malformed third-party Food should not break Puke. If every entry
        // lacks usable timing data, prefer persisted consumption order before
        // falling back to deterministic slot-order semantics.
        int orderedFallback = SelectByConsumptionOrder(
            foods,
            consumptionOrder,
            newestFirst);
        return selectedIndex >= 0
            ? selectedIndex
            : orderedFallback >= 0
                ? orderedFallback
            : newestFirst
                ? foods.Count - 1
                : 0;
    }

    private static bool IsPreferredTie(
        int candidateIndex,
        int candidateRank,
        int selectedIndex,
        int selectedRank,
        bool newestFirst)
    {
        if (candidateRank >= 0 && selectedRank >= 0 && candidateRank != selectedRank)
        {
            return newestFirst
                ? candidateRank > selectedRank
                : candidateRank < selectedRank;
        }

        return newestFirst
            ? candidateIndex > selectedIndex
            : candidateIndex < selectedIndex;
    }

    private static int SelectByConsumptionOrder(
        IReadOnlyList<Player.Food> foods,
        IReadOnlyList<ActiveFoodData>? consumptionOrder,
        bool newestFirst)
    {
        int selectedIndex = -1;
        int selectedRank = -1;
        for (int index = 0; index < foods.Count; index++)
        {
            int rank = GetConsumptionRank(foods[index], consumptionOrder);
            if (rank < 0 ||
                selectedIndex >= 0 &&
                (newestFirst ? rank <= selectedRank : rank >= selectedRank))
            {
                continue;
            }

            selectedIndex = index;
            selectedRank = rank;
        }

        return selectedIndex;
    }

    private static int GetConsumptionRank(
        Player.Food? food,
        IReadOnlyList<ActiveFoodData>? consumptionOrder)
    {
        if (food == null || consumptionOrder == null)
        {
            return -1;
        }

        string savedKey = food.m_name?.Trim() ?? string.Empty;
        for (int index = 0; index < consumptionOrder.Count; index++)
        {
            if (consumptionOrder[index]?.Key == savedKey)
            {
                return index;
            }
        }

        string canonicalKey = FoodIdentity.GetCanonicalPrefabName(food);
        if (canonicalKey == savedKey)
        {
            return -1;
        }

        for (int index = 0; index < consumptionOrder.Count; index++)
        {
            if (consumptionOrder[index]?.Key == canonicalKey)
            {
                return index;
            }
        }

        return -1;
    }

    internal static bool TryGetElapsedSinceEaten(Player.Food? food, out float elapsed)
    {
        elapsed = 0f;
        ItemDrop.ItemData.SharedData? shared = food?.m_item?.m_shared;
        if (shared == null)
        {
            return false;
        }

        float burnTime = shared.m_foodBurnTime;
        float remainingTime = food!.m_time;
        if (float.IsNaN(burnTime) || float.IsInfinity(burnTime) || burnTime <= 0f ||
            float.IsNaN(remainingTime) || float.IsInfinity(remainingTime))
        {
            return false;
        }

        elapsed = Math.Max(0f, burnTime - remainingTime);
        return !float.IsNaN(elapsed) && !float.IsInfinity(elapsed);
    }
}
