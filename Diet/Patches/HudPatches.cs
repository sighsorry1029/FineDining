using HarmonyLib;

namespace FineDining;

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdateFood))]
internal static class DietHudPatches
{
    [HarmonyPostfix]
    private static void Postfix(Hud __instance, Player player)
    {
        if (__instance == null || player == null || player != Player.m_localPlayer)
        {
            return;
        }

        HudFoodSlots.EnsureFoodSlots(__instance);
        HudFoodSlots.LimitVisibleSlots(__instance, player);
        HudFoodPanels.Update(__instance, player);
    }
}
