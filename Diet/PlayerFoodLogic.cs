using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

internal static class PlayerFoodLogic
{
    internal static bool CanEat(Player player, ItemDrop.ItemData item, bool showMessages)
    {
        List<Player.Food> foods = player.GetFoods();
        PlayerFoodStateData state = FoodStateStore.GetState(player);
        int unlockedFoodSlots = FoodSlotProgression.GetCurrentSlots(player, state);
        string itemKey = FoodIdentity.GetCanonicalPrefabName(item);
        foreach (Player.Food food in foods)
        {
            if (FoodIdentity.GetCanonicalPrefabName(food) != itemKey)
            {
                continue;
            }

            if (food.CanEatAgain())
            {
                return true;
            }

            if (showMessages)
            {
                player.Message(
                    MessageHud.MessageType.Center,
                    Localization.instance.Localize("$msg_nomore", item.m_shared.m_name));
            }

            return false;
        }

        foreach (Player.Food food in foods)
        {
            if (food.CanEatAgain())
            {
                return true;
            }
        }

        if (foods.Count >= unlockedFoodSlots)
        {
            if (showMessages)
            {
                player.Message(MessageHud.MessageType.Center, "$msg_isfull");
            }

            return false;
        }

        return true;
    }

    internal static bool EatFood(Player player, ItemDrop.ItemData item)
    {
        if (!CanEat(player, item, showMessages: false))
        {
            return false;
        }

        string key = FoodIdentity.GetCanonicalPrefabName(item);
        PlayerFoodStateData state = FoodStateStore.GetState(player);
        Player.Food? targetFood = FoodRules.FindTargetFood(player, state, item);
        if (targetFood == null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        bool replacesExistingFood = player.GetFoods().Contains(targetFood);
        bool replacesDietFood = replacesExistingFood &&
                                FoodIdentity.IsDirectlyEdible(targetFood.m_item);
        if (replacesExistingFood)
        {
            FoodSlotProgression.ApplyPendingAfterFoodRemoval(
                player,
                state,
                trimExcess: false);
        }

        bool isChef = ChefCollectionService.TryConsumeChefEntry(
            player,
            state,
            key,
            out float chefMultiplier);
        int stack = RecentHistoryService.RegisterConsumption(state, key, isChef);
        if (isChef)
        {
            // Refill only after registering this consumption so the replacement
            // sees the newly updated Health/Stamina/Eitr history composition.
            ChefCollectionService.RefillAfterConsumption(player, state, key);
        }

        bool fullCourseActive = FoodRules.WillHaveFullCourseAfterEating(
            player,
            state,
            item,
            replacesExistingFood,
            replacesDietFood);
        FoodEffect effect = FoodRules.CalculateFoodEffect(
            item,
            stack,
            isChef,
            chefMultiplier,
            fullCourseActive,
            FoodSlotProgression.GetCurrentSlots(player, state));

        // This is the single persisted consumption snapshot. It already combines
        // slot/Chef/diminishing scale with this item's freshness, but deliberately
        // excludes the dynamic Full Course multiplier.
        FoodRules.SetActiveFoodScale(state, key, effect.AppliedScale);
        ApplyFoodSnapshot(targetFood, item, key, effect.EffectiveScale);

        List<Player.Food> foods = player.GetFoods();
        if (!foods.Contains(targetFood))
        {
            foods.Add(targetFood);
        }

        FoodSlotProgression.TrimExcessFoods(player, state, targetFood);

        FoodStateStore.SaveState(player, state);
        string message = BuildFoodMessage(effect);
        if (!string.IsNullOrWhiteSpace(message))
        {
            player.Message(MessageHud.MessageType.Center, message);
        }

        RecalculateFoodStats(player, state);
        Game.instance?.IncrementPlayerStat(PlayerStatType.FoodEaten);
        return true;
    }

    internal static void UpdateFood(Player player, float dt, bool forceUpdate)
    {
        PlayerFoodStateData? state = null;
        List<Player.Food> foods = player.GetFoods();
        ref float foodUpdateTimer = ref PlayerPrivateAccess.FoodUpdateTimer(player);

        foodUpdateTimer += dt;
        if (foodUpdateTimer >= 1f || forceUpdate)
        {
            state = FoodStateStore.GetState(player);
            foodUpdateTimer -= 1f;
            bool removedFood = false;
            int naturalExpiryCount = 0;

            for (int index = foods.Count - 1; index >= 0; index--)
            {
                Player.Food food = foods[index];
                food.m_time -= 1f;
                if (food.m_time > 0f)
                {
                    continue;
                }

                player.Message(MessageHud.MessageType.Center, "$msg_food_done");
                foods.RemoveAt(index);
                removedFood = true;
                if (!forceUpdate && FoodIdentity.IsDirectlyEdible(food.m_item))
                {
                    naturalExpiryCount++;
                }
            }

            if (removedFood)
            {
                FoodSlotProgression.ApplyPendingAfterFoodRemoval(player, state);
                FoodStateStore.SaveState(player, state);
            }

            if (naturalExpiryCount > 0 && player == Player.m_localPlayer)
            {
                ChefCollectionService.RotateOldest(player, naturalExpiryCount);
            }

            RecalculateFoodStats(player, state);
        }

        if (forceUpdate)
        {
            return;
        }

        ref float foodRegenTimer = ref PlayerPrivateAccess.FoodRegenTimer(player);
        foodRegenTimer += dt;
        if (foodRegenTimer < 10f)
        {
            return;
        }

        foodRegenTimer = 0f;
        state ??= FoodStateStore.GetState(player);
        float regen = 0f;
        float fullCourseScale = FoodRules.GetFullCourseScale(
            player,
            state,
            FoodRules.CountActiveDietFoods(foods));
        foreach (Player.Food food in foods)
        {
            bool isDietFood = FoodIdentity.IsDirectlyEdible(food.m_item);
            float scale = isDietFood
                ? FoodRules.GetAppliedScale(player, state, food) * fullCourseScale
                : 1f;
            regen += food.m_item.m_shared.m_foodRegen * scale;
        }

        if (regen <= 0f)
        {
            return;
        }

        float regenMultiplier = 1f;
        player.GetSEMan().ModifyHealthRegen(ref regenMultiplier);
        player.Heal(regen * regenMultiplier);
    }

    internal static void RefreshFoodStats(Player? player)
    {
        if (player != null)
        {
            RecalculateFoodStats(player, FoodStateStore.GetState(player));
        }
    }

    private static void RecalculateFoodStats(Player player, PlayerFoodStateData state)
    {
        List<Player.Food> foods = player.GetFoods();
        float fullCourseScale = FoodRules.GetFullCourseScale(
            player,
            state,
            FoodRules.CountActiveDietFoods(foods));
        foreach (Player.Food food in foods)
        {
            float normalizedTime = Mathf.Clamp01(
                food.m_time / food.m_item.m_shared.m_foodBurnTime);
            normalizedTime = Mathf.Pow(normalizedTime, 0.3f);

            bool isDietFood = FoodIdentity.IsDirectlyEdible(food.m_item);
            float effectiveScale = isDietFood
                ? FoodRules.GetAppliedScale(player, state, food) * fullCourseScale
                : 1f;
            food.m_health = food.m_item.m_shared.m_food * effectiveScale * normalizedTime;
            food.m_stamina = food.m_item.m_shared.m_foodStamina * effectiveScale * normalizedTime;
            food.m_eitr = food.m_item.m_shared.m_foodEitr * effectiveScale * normalizedTime;
        }

        GetTotalFoodValue(player, foods, out float health, out float stamina, out float eitr);
        player.SetMaxHealth(health, flashBar: true);
        player.SetMaxStamina(stamina, flashBar: true);
        PlayerPrivateAccess.SetMaxEitr(player, eitr, flashBar: true);
        if (eitr > 0f)
        {
            player.ShowTutorial("eitr");
        }
    }

    private static void GetTotalFoodValue(
        Player player,
        List<Player.Food> foods,
        out float health,
        out float stamina,
        out float eitr)
    {
        health = player.GetBaseFoodHP();
        stamina = player.m_baseStamina;
        eitr = 0f;
        foreach (Player.Food food in foods)
        {
            health += food.m_health;
            stamina += food.m_stamina;
            eitr += food.m_eitr;
        }
    }

    private static void ApplyFoodSnapshot(
        Player.Food food,
        ItemDrop.ItemData item,
        string key,
        float effectiveScale)
    {
        food.m_name = key;
        food.m_item = item;
        food.m_time = item.m_shared.m_foodBurnTime;
        food.m_health = item.m_shared.m_food * effectiveScale;
        food.m_stamina = item.m_shared.m_foodStamina * effectiveScale;
        food.m_eitr = item.m_shared.m_foodEitr * effectiveScale;
    }

    private static string BuildFoodMessage(FoodEffect effect)
    {
        string message = string.Empty;
        if (effect.Health > 0f)
        {
            message += $" +{FormatValue(effect.Health)} $item_food_health ";
        }

        if (effect.Stamina > 0f)
        {
            message += $" +{FormatValue(effect.Stamina)} $item_food_stamina ";
        }

        if (effect.Eitr > 0f)
        {
            message += $" +{FormatValue(effect.Eitr)} $item_food_eitr ";
        }

        if (effect.FullCourseActive)
        {
            message += " " + Localization.instance.Localize(
                "$finedining_diet_full_course_message",
                DietConfig.GetFullCourseMultiplier().ToString(
                    "0.00",
                    CultureInfo.InvariantCulture));
        }

        return message;
    }

    private static string FormatValue(float value) =>
        Mathf.Approximately(value, Mathf.Round(value))
            ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.0", CultureInfo.InvariantCulture);
}

internal static class PlayerPrivateAccess
{
    internal delegate void SetMaxEitrDelegate(Player player, float eitr, bool flashBar);

    internal static readonly AccessTools.FieldRef<Player, float> FoodUpdateTimer =
        AccessTools.FieldRefAccess<Player, float>("m_foodUpdateTimer");

    internal static readonly AccessTools.FieldRef<Player, float> FoodRegenTimer =
        AccessTools.FieldRefAccess<Player, float>("m_foodRegenTimer");

    internal static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes =
        AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");

    internal static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownMaterials =
        AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownMaterial");

    internal static readonly SetMaxEitrDelegate SetMaxEitr =
        AccessTools.MethodDelegate<SetMaxEitrDelegate>(
            AccessTools.DeclaredMethod(
                typeof(Player),
                "SetMaxEitr",
                new[] { typeof(float), typeof(bool) }));
}
