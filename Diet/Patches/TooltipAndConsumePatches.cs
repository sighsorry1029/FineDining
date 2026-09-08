using System;
using System.Reflection;
using System.Text;
using HarmonyLib;

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
        if (player == null || !FoodIdentity.IsDirectlyEdible(item))
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
        StringBuilder modifierBuilder = new();
        if (effect.IsChef)
        {
            AppendModifierLine(
                modifierBuilder,
                FoodEffectUiText.BuildChefChoiceModifierLine(effect.ChefMultiplier));
        }

        if (FoodEffectUiText.TryBuildDiminishingReturnsLine(
                effect.DiminishingScale,
                out string diminishingLine))
        {
            AppendModifierLine(modifierBuilder, diminishingLine);
        }

        if (FoodEffectUiText.TryBuildStalenessLine(
                effect.FreshnessScale,
                out string stalenessLine) &&
            !FoodEffectUiText.ContainsLine(__result ?? string.Empty, stalenessLine))
        {
            AppendModifierLine(modifierBuilder, stalenessLine);
        }

        if (modifierBuilder.Length > 0)
        {
            __result += "\n\n" + modifierBuilder;
        }
    }

    private static void AppendModifierLine(StringBuilder builder, string line)
    {
        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(line);
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
}

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
internal static class DietPukeTooltipPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemDrop.ItemData __0, ref string __result)
    {
        if (__0?.m_shared?.m_consumeStatusEffect is not SE_Puke ||
            Localization.instance == null)
        {
            return;
        }

        string line = Localization.instance.Localize(
            "$finedining_diet_tooltip_puke_chef_refresh");
        if (string.IsNullOrWhiteSpace(line) ||
            FoodEffectUiText.ContainsLine(__result ?? string.Empty, line))
        {
            return;
        }

        __result += "\n\n" + line;
    }
}

/// <summary>
/// Feast stores edible stats on m_foodItem but Feaster-style placement stores
/// persistent spoilage metadata on the host Piece ItemDrop. Bridge the metadata
/// only for the synchronous eat-confirmation call.
/// </summary>
[HarmonyPatch(typeof(Feast), "RPC_EatConfirmation")]
internal static class PlacedFeastFreshnessConsumptionPatch
{
    internal sealed class State
    {
        internal ItemDrop? FoodDrop;
        internal ItemDrop.ItemData? OriginalItem;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Feast __instance, out State? __state)
    {
        __state = null;
        ItemDrop? foodDrop = __instance?.m_foodItem;
        ItemDrop? placedDrop = __instance != null ? __instance.GetComponent<ItemDrop>() : null;
        if (foodDrop?.m_itemData == null || placedDrop?.m_itemData == null)
        {
            return;
        }

        try
        {
            placedDrop.Load();
            if (DecayRuntime.IsCreatorlessPlacedDrop(placedDrop))
            {
                return;
            }

            ItemDrop.ItemData bridged = foodDrop.m_itemData.Clone();
            FreshnessRuntime.CopyFreshnessMetadata(placedDrop.m_itemData, bridged);
            __state = new State
            {
                FoodDrop = foodDrop,
                OriginalItem = foodDrop.m_itemData
            };
            foodDrop.m_itemData = bridged;
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not bridge placed-feast freshness into its food item: " + exception.Message);
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(State? __state, Exception? __exception)
    {
        if (__state?.FoodDrop != null && __state.OriginalItem != null)
        {
            __state.FoodDrop.m_itemData = __state.OriginalItem;
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(SE_Puke), nameof(SE_Puke.UpdateStatusEffect))]
internal static class DietPukeChefRotationPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(SE_Puke __instance, out PukeUpdateState __state)
    {
        PukeFoodRemovalRuntime.EnterPukeUpdate();
        Player? player = __instance.m_character as Player;
        __state = player != null && player == Player.m_localPlayer
            ? new PukeUpdateState(player, player.GetFoods().Count)
            : default;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(PukeUpdateState __state)
    {
        Player? player = __state.Player;
        if (player == null || player != Player.m_localPlayer)
        {
            return;
        }

        int removedFoodCount = CountRemovedFoods(
            __state.FoodCount,
            player.GetFoods().Count);
        if (removedFoodCount > 0)
        {
            ChefCollectionService.RotateOldest(player, removedFoodCount);
        }
    }

    [HarmonyFinalizer]
    [HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Exception? __exception)
    {
        PukeFoodRemovalRuntime.ExitPukeUpdate();
        return __exception;
    }

    internal static int CountRemovedFoods(int before, int after) =>
        before > after ? before - Math.Max(0, after) : 0;

    private readonly struct PukeUpdateState
    {
        internal PukeUpdateState(Player player, int foodCount)
        {
            Player = player;
            FoodCount = foodCount;
        }

        internal Player? Player { get; }
        internal int FoodCount { get; }
    }
}
