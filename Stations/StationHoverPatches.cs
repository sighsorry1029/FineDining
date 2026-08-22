using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

[HarmonyPatch(typeof(Hud), nameof(Hud.Awake))]
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

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdateCrosshair))]
internal static class StationHintHudCrosshairPatch
{
    private static float _lastLogTime;
    private static int _lastHoverId;

    private static void Postfix(Hud __instance, Player player)
    {
        if (!StationModule.CanShowDisplay
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

        Switch? switchRef = null;
        object? useTarget = null;
        CookingStation? cookingStation = null;
        Smelter? smelter = null;
        Fermenter? fermenter = null;
        if (hoverable is Switch selectedSwitch)
        {
            switchRef = selectedSwitch;
            useTarget = selectedSwitch.m_onUse?.Target;
            if (useTarget is CookingStation targetCookingStation)
            {
                cookingStation = targetCookingStation;
                StationInputResolver.ShowCookingStation(cookingStation, selectedSwitch);
            }
            else if (useTarget is Smelter targetSmelter)
            {
                smelter = targetSmelter;
                StationInputResolver.ShowSmelter(smelter, selectedSwitch);
            }
            else if (useTarget is Fermenter targetFermenter)
            {
                fermenter = targetFermenter;
                StationInputResolver.ShowFermenter(fermenter);
            }
            else if ((cookingStation = selectedSwitch.GetComponentInParent<CookingStation>()) != null)
            {
                StationInputResolver.ShowCookingStation(cookingStation, selectedSwitch);
            }
            else if ((smelter = selectedSwitch.GetComponentInParent<Smelter>()) != null)
            {
                StationInputResolver.ShowSmelter(smelter, selectedSwitch);
            }
            else if ((fermenter = selectedSwitch.GetComponentInParent<Fermenter>()) != null)
            {
                StationInputResolver.ShowFermenter(fermenter);
            }
        }
        else if (hoverable is CookingStation selectedCookingStation)
        {
            cookingStation = selectedCookingStation;
            StationInputResolver.ShowCookingStation(cookingStation, null);
        }
        else if (hoverable is Fermenter selectedFermenter)
        {
            fermenter = selectedFermenter;
            StationInputResolver.ShowFermenter(fermenter);
        }

        LogDiagnostics(
            hoverObject,
            hoverable,
            switchRef,
            useTarget,
            cookingStation,
            smelter,
            fermenter);
    }

    private static void LogDiagnostics(
        GameObject hoverObject,
        Hoverable hoverable,
        Switch? switchRef,
        object? useTarget,
        CookingStation? cookingStation,
        Smelter? smelter,
        Fermenter? fermenter)
    {
        if (!StationModule.Diagnostics.Value)
        {
            return;
        }

        int hoverId = hoverObject.GetInstanceID();
        float now = Time.unscaledTime;
        if (_lastHoverId == hoverId && now - _lastLogTime < 1f)
        {
            return;
        }

        _lastHoverId = hoverId;
        _lastLogTime = now;

        string hoverables = string.Join(
            ", ",
            hoverObject
                .GetComponentsInParent<MonoBehaviour>(true)
                .Where(component => component is Hoverable)
                .Select(component => component.GetType().Name)
                .Distinct());
        FineDiningPlugin.Log.LogInfo(
            "[Station Diagnostics] Hud hover object: "
            + $"name='{hoverObject.name}', "
            + $"selectedHoverable='{hoverable.GetType().Name}', "
            + $"hoverables=[{hoverables}], "
            + $"switch='{(switchRef != null ? switchRef.name : "<none>")}', "
            + $"switchTarget='{useTarget?.GetType().FullName ?? "<none>"}', "
            + $"cookingStation='{(cookingStation != null ? cookingStation.name : "<none>")}', "
            + $"smelter='{(smelter != null ? smelter.name : "<none>")}', "
            + $"fermenter='{(fermenter != null ? fermenter.name : "<none>")}'.");
    }
}
