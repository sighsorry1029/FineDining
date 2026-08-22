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
        __result = PlayerFoodLogic.CanEat(__instance, item, showMessages);
        return false;
    }

    [HarmonyPatch(nameof(Player.EatFood))]
    [HarmonyPrefix]
    private static bool EatFoodPrefix(
        Player __instance,
        ItemDrop.ItemData item,
        ref bool __result)
    {
        __result = PlayerFoodLogic.EatFood(__instance, item);
        return false;
    }

    [HarmonyPatch("UpdateFood")]
    [HarmonyPrefix]
    private static bool UpdateFoodPrefix(Player __instance, float dt, bool forceUpdate)
    {
        PlayerFoodLogic.UpdateFood(__instance, dt, forceUpdate);
        return false;
    }

    [HarmonyPatch(nameof(Player.RemoveOneFood))]
    [HarmonyPostfix]
    private static void RemoveOneFoodPostfix(Player __instance, bool __result)
    {
        if (__result)
        {
            FoodStateStore.SaveState(__instance);
            PlayerFoodLogic.RefreshFoodStats(__instance);
        }
    }

    [HarmonyPatch(nameof(Player.ClearFood))]
    [HarmonyPostfix]
    private static void ClearFoodPostfix(Player __instance)
    {
        FoodStateStore.SaveState(__instance);
        PlayerFoodLogic.RefreshFoodStats(__instance);
    }

    [HarmonyPatch(nameof(Player.Load))]
    [HarmonyPostfix]
    private static void LoadPostfix(Player __instance)
    {
        FoodStateStore.Invalidate(__instance);
        PlayerFoodStateData state = FoodStateStore.GetState(__instance);
        ChefCollectionService.EnsureChefCollection(__instance, state);
        FoodStateStore.SaveState(__instance, state);
        PlayerFoodLogic.RefreshFoodStats(__instance);
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
    }
}
