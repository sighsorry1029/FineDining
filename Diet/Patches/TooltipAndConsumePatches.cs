using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

[HarmonyPatch(
    typeof(ItemDrop.ItemData),
    nameof(ItemDrop.ItemData.GetTooltip),
    new[]
    {
        typeof(ItemDrop.ItemData),
        typeof(int),
        typeof(bool),
        typeof(float),
        typeof(int)
    })]
internal static class DietTooltipPatch
{
    private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
        "MemberwiseClone",
        BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new MissingMethodException(typeof(object).FullName, "MemberwiseClone");

    private static bool _cloneFailureLogged;

    [HarmonyPrefix]
    private static void Prefix(ref ItemDrop.ItemData __0, out FoodEffect? __state)
    {
        __state = null;
        Player player = Player.m_localPlayer;
        ItemDrop.ItemData item = __0;
        if (player == null || !FoodKeys.IsConsumableFood(item))
        {
            return;
        }

        try
        {
            PlayerFoodStateData state = FoodStateStore.GetState(player);
            FoodEffect effect = FoodRules.PreviewNextFoodEffect(player, state, item);
            if (effect.EffectiveScale < 0f ||
                float.IsNaN(effect.EffectiveScale) ||
                float.IsInfinity(effect.EffectiveScale))
            {
                return;
            }

            ItemDrop.ItemData tooltipItem = item.Clone();
            tooltipItem.m_shared =
                (ItemDrop.ItemData.SharedData)MemberwiseCloneMethod.Invoke(item.m_shared, null);
            tooltipItem.m_shared.m_food = RoundTooltipStat(effect.Health);
            tooltipItem.m_shared.m_foodStamina = RoundTooltipStat(effect.Stamina);
            tooltipItem.m_shared.m_foodEitr = RoundTooltipStat(effect.Eitr);
            tooltipItem.m_shared.m_foodRegen = RoundTooltipStat(effect.Regen);
            __0 = tooltipItem;
            __state = effect;
        }
        catch (Exception exception)
        {
            if (!_cloneFailureLogged)
            {
                _cloneFailureLogged = true;
                FineDiningPlugin.Log.LogWarning(
                    "Failed to build transformed diet tooltip: " + exception.Message);
            }
        }
    }

    [HarmonyPostfix]
    private static void Postfix(FoodEffect? __state, ref string __result)
    {
        if (!__state.HasValue)
        {
            return;
        }

        FoodEffect effect = __state.Value;
        Localization localization = Localization.instance;
        StringBuilder builder = new();
        if (effect.Health <= 0f &&
            effect.Stamina <= 0f &&
            effect.Eitr <= 0f &&
            effect.Regen > 0f)
        {
            builder.Append(
                $"\n$item_food_duration: <color=orange>{ItemDrop.ItemData.GetDurationString(effect.Duration)}</color>");
            builder.Append(
                $"\n$item_food_regen: <color=orange>{FormatTooltipStat(effect.Regen)} " +
                $"{localization.Localize("$finedining_diet_tooltip_hp_per_tick")}</color>");
        }

        builder.Append("\n\n");
        builder.Append(localization.Localize(
            "$finedining_diet_tooltip_diminish_factor",
            effect.DiminishingScale.ToString("0.00", CultureInfo.InvariantCulture)));

        if (effect.IsChef)
        {
            builder.Append('\n');
            builder.Append(localization.Localize(
                "$finedining_diet_tooltip_chef_multiplier",
                effect.ChefMultiplier.ToString("0.00", CultureInfo.InvariantCulture)));
        }

        if (effect.FreshnessScale < 0.999999f)
        {
            string freshnessLine = SpoilageUiText.BuildFreshnessEffectLine(effect.FreshnessScale);
            if (!SpoilageUiText.ContainsLine(__result ?? string.Empty, freshnessLine) &&
                !SpoilageUiText.ContainsLine(builder.ToString(), freshnessLine))
            {
                builder.Append('\n');
                builder.Append(freshnessLine);
            }
        }

        __result += builder.ToString();
    }

    private static float RoundTooltipStat(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return value;
        }

        float rounded = (float)Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return value > 0f && rounded == 0f ? 0.1f : rounded;
    }

    private static string FormatTooltipStat(float value) =>
        RoundTooltipStat(value).ToString("0.#", CultureInfo.CurrentCulture);
}

[HarmonyPatch]
internal static class DietConsumePatch
{
    private static int _lastRerollFrame = -1;
    private static long _lastRerollPlayerId;

    [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Eat))]
    [HarmonyPostfix]
    private static void ItemDropEatPostfix(ItemDrop __instance, bool __result)
    {
        TryRerollChefCollection(Player.m_localPlayer, __instance.m_itemData, __result);
    }

    [HarmonyPatch(
        typeof(Player),
        nameof(Player.ConsumeItem),
        new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(bool) })]
    [HarmonyPostfix]
    private static void PlayerConsumeItemPostfix(
        Player __instance,
        ItemDrop.ItemData item,
        bool __result)
    {
        TryRerollChefCollection(__instance, item, __result);
    }

    private static void TryRerollChefCollection(
        Player? player,
        ItemDrop.ItemData? item,
        bool consumed)
    {
        if (!consumed ||
            player == null ||
            player != Player.m_localPlayer ||
            item?.m_shared?.m_consumeStatusEffect is not SE_Puke ||
            player.GetFoods().Count < DietConfig.GetMaxFoodSlots())
        {
            return;
        }

        long playerId = player.GetPlayerID();
        if (_lastRerollFrame == Time.frameCount && _lastRerollPlayerId == playerId)
        {
            return;
        }

        _lastRerollFrame = Time.frameCount;
        _lastRerollPlayerId = playerId;
        ChefCollectionService.RerollAll(player);
    }
}
