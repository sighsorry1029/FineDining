using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

[HarmonyPatch(typeof(Smelter), "OnHoverAddOre")]
internal static class StationSmelterHoverTimePatch
{
    private static void Postfix(Smelter __instance, ref string __result)
    {
        if (!StationHoverTime.CanShow(__instance) || string.IsNullOrEmpty(__result))
        {
            return;
        }

        if (!StationHoverTime.TryGetSmelterRemaining(
                __instance,
                out int queueSize,
                out double seconds))
        {
            return;
        }

        string countText = $"({queueSize}/{__instance.m_maxOre})";
        __result = StationHoverTime.InsertAfter(
            __result,
            countText,
            StationText.ColorizeTimer(
                StationText.FormatSeconds(seconds, keepAtLeastOneSecond: true)));
    }
}

[HarmonyPatch(typeof(Fermenter), nameof(Fermenter.GetHoverText))]
internal static class StationFermenterHoverTimePatch
{
    private const string DetailColor = "orange";

    private static void Postfix(Fermenter __instance, ref string __result)
    {
        if (!StationHoverTime.CanShow(__instance)
            || string.IsNullOrEmpty(__result))
        {
            return;
        }

        if (StationModule.IsFermenterBonusExcluded(__instance))
        {
            if (FermenterEnvironmentSpeedSystem.TryGetRemainingSeconds(
                    __instance,
                    out double excludedSeconds,
                    out _))
            {
                string excludedDuration = StationText.FormatDuration(excludedSeconds);
                if (!string.IsNullOrEmpty(excludedDuration))
                {
                    InsertAfterFirstLine(
                        ref __result,
                        StationText.ColorizeTimer(excludedDuration));
                }
            }

            return;
        }

        bool hasRemainingTime =
            FermenterEnvironmentSpeedSystem.TryGetRemainingSeconds(
                __instance,
                out double seconds,
                out float speedMultiplier);

        FermenterEnvironmentSpeedSystem.EnvironmentStatus environment =
            FermenterEnvironmentSpeedSystem.GetEnvironmentStatus(__instance);
        float coverMultiplier = environment.CanApplyBonus
            ? environment.CoverMultiplier
            : 1f;
        float depthMultiplier = environment.CanApplyBonus
            ? environment.DepthMultiplier
            : 1f;
        string detailText = ColorizeDetailLine(
            $"{StationText.CoverLabel}: {Mathf.RoundToInt(environment.Cover * 100f)}% "
            + $"({StationText.RateLabel}: {FormatMultiplier(coverMultiplier)})");
        if (environment.DepthKnown)
        {
            detailText += "\n" + ColorizeDetailLine(
                $"{StationText.DepthLabel}: "
                + $"{environment.DepthMeters.ToString("0.0", CultureInfo.InvariantCulture)} m "
                + $"({StationText.RateLabel}: {FormatMultiplier(depthMultiplier)})");
        }

        if (hasRemainingTime)
        {
            string duration = StationText.FormatDuration(seconds);
            if (!string.IsNullOrEmpty(duration))
            {
                string timerText = StationText.ColorizeTimer(duration);
                timerText +=
                    $" ({StationText.FermentationSpeedLabel} "
                    + $"{StationText.ColorizeTimer(FormatMultiplier(speedMultiplier))}"
                    + ")";
                detailText += "\n" + ColorizeDetailLine(timerText);
                detailText += "\n" + ColorizeDetailLine(
                    StationText.FermentationGuidanceLabel);
            }
        }

        InsertAfterFirstLine(ref __result, detailText);
    }

    private static string ColorizeDetailLine(string line) =>
        $"<color={DetailColor}>{line}</color>";

    private static void InsertAfterFirstLine(ref string hoverText, string text)
    {
        int firstLineEnd = hoverText.IndexOf('\n');
        hoverText = firstLineEnd >= 0
            ? hoverText.Insert(firstLineEnd, "\n" + text)
            : hoverText + "\n" + text;
    }

    private static string FormatMultiplier(float multiplier) =>
        "x" + multiplier.ToString("0.##", CultureInfo.InvariantCulture);
}

internal static class StationHoverTime
{
    private const float MinimumWindPower = 0.0001f;

    internal static bool CanShow(Component station) =>
        StationModule.IsInitialized
        && Player.m_localPlayer != null
        && PrivateArea.CheckAccess(station.transform.position, 0f, false);

    internal static bool TryGetSmelterRemaining(
        Smelter smelter,
        out int queueSize,
        out double seconds)
    {
        queueSize = 0;
        seconds = 0d;
        if (smelter.m_secPerProduct <= 0f || !TryGetZdo(smelter, out ZDO? zdo))
        {
            return false;
        }

        queueSize = zdo!.GetInt(ZDOVars.s_queued);
        if (queueSize <= 0)
        {
            return false;
        }

        double remainingWork = Math.Max(
            0d,
            smelter.m_secPerProduct - zdo.GetFloat(ZDOVars.s_bakeTimer));
        if (smelter.m_windmill != null)
        {
            float power = smelter.m_windmill.GetPowerOutput();
            if (power <= MinimumWindPower)
            {
                return false;
            }

            remainingWork /= power;
        }

        seconds = remainingWork;
        return true;
    }

    internal static string InsertAfter(string hoverText, string marker, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return hoverText;
        }

        int markerIndex = hoverText.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return InsertAtEndOfFirstLine(hoverText, text);
        }

        int insertionIndex = markerIndex + marker.Length;
        return hoverText.Insert(insertionIndex, " " + text);
    }

    internal static string InsertAtEndOfFirstLine(string hoverText, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return hoverText;
        }

        int newlineIndex = hoverText.IndexOf('\n');
        int insertionIndex = newlineIndex >= 0 ? newlineIndex : hoverText.Length;
        return hoverText.Insert(insertionIndex, " " + text);
    }

    private static bool TryGetZdo(Component component, out ZDO? zdo)
    {
        ZNetView? view = component.GetComponent<ZNetView>()
                         ?? component.GetComponentInParent<ZNetView>();
        zdo = view != null && view.IsValid() ? view.GetZDO() : null;
        return zdo != null;
    }
}
