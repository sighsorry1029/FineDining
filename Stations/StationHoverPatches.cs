using HarmonyLib;
using UnityEngine;

namespace FineDining;

[HarmonyPatch(typeof(Hud), "Awake")]
internal static class StationHintHudAwakePatch
{
    private static void Postfix()
    {
        if (StationModule.IsInitialized)
        {
            StationHintUi.EnsureCreated();
        }
    }
}

[HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
internal static class StationHintHudCrosshairPatch
{
    private static void Postfix(Hud __instance, Player player)
    {
        if (!StationModule.IsInitialized
            || player == null
            || !__instance.IsVisible()
            || __instance.m_crosshair == null
            || !__instance.m_crosshair.gameObject.activeInHierarchy
            || (TextViewer.instance != null && TextViewer.instance.IsVisible()))
        {
            return;
        }

        GameObject hoverObject = player.GetHoverObject();
        if (hoverObject == null)
        {
            return;
        }

        Hoverable? hoverable = hoverObject.GetComponentInParent<Hoverable>();
        if (hoverable == null)
        {
            return;
        }

        if (ValheimCuisineCompatibility.TryShow(
                __instance,
                hoverObject,
                hoverable,
                player))
        {
            return;
        }

        if (hoverable is Switch selectedSwitch)
        {
            Component? resolvedTarget = ResolveSwitchTarget(selectedSwitch);
            switch (resolvedTarget)
            {
                case CookingStation cookingStation:
                    StationInputResolver.ShowCookingStation(cookingStation, selectedSwitch);
                    break;
                case Smelter smelter:
                    StationInputResolver.ShowSmelter(smelter, selectedSwitch);
                    break;
                case Fermenter fermenter:
                    StationInputResolver.ShowFermenter(fermenter);
                    break;
            }
        }
        else if (hoverable is CookingStation selectedCookingStation)
        {
            StationInputResolver.ShowCookingStation(selectedCookingStation, null);
        }
        else if (hoverable is Fermenter selectedFermenter)
        {
            StationInputResolver.ShowFermenter(selectedFermenter);
        }
    }

    private static Component? ResolveSwitchTarget(Switch selectedSwitch)
    {
        Component? directTarget = selectedSwitch.m_onUse?.Target as Component;
        if (directTarget is CookingStation or Smelter or Fermenter)
        {
            return directTarget;
        }

        Component? parentTarget = selectedSwitch.GetComponentInParent<CookingStation>();
        parentTarget ??= selectedSwitch.GetComponentInParent<Smelter>();
        parentTarget ??= selectedSwitch.GetComponentInParent<Fermenter>();
        return parentTarget;
    }
}
