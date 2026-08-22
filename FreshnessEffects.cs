using System;
using HarmonyLib;
using ItemData = ItemDrop.ItemData;

namespace FineDining;

/// <summary>
/// Read-only freshness helpers shared by spoilage presentation and the combined
/// diet pipeline. Consumption snapshots and active-food persistence deliberately
/// live in DietModule so freshness can never be applied twice.
/// </summary>
internal static class FreshnessFoodEffects
{
    internal static bool HasFoodEffect(ItemData? item) => FoodIdentity.IsDirectlyEdible(item);

    internal static float GetMultiplier(ItemData? item)
    {
        if (!HasFoodEffect(item))
        {
            return 1f;
        }

        try
        {
            return FreshnessRuntime.GetFoodStatMultiplier(item);
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not calculate an item's freshness multiplier: " + exception.Message);
            return 1f;
        }
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
        internal ItemData? OriginalItem;
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
            ItemData bridged = foodDrop.m_itemData.Clone();
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
