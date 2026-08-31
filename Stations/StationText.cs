using System;
using System.Globalization;

namespace FineDining;

internal static class StationText
{
    internal const string TimerColorHex = "#FFD138";
    internal const string CoverToken = "$finedining_station_cover";
    internal const string DepthToken = "$finedining_station_depth";
    internal const string RateToken = "$finedining_station_rate";
    internal const string FermentationSpeedToken = "$finedining_station_fermentation_speed";
    internal const string FermentationGuidanceToken = "$finedining_station_fermentation_guidance";
    internal const string SecondsToken = "$finedining_station_seconds";
    internal const string AutoEjectToken = "$finedining_station_auto_eject";

    internal static string CoverLabel => FineDiningLocalization.LocalizeOrFallback(CoverToken, "Cover");

    internal static string DepthLabel => FineDiningLocalization.LocalizeOrFallback(DepthToken, "Depth");

    internal static string RateLabel => FineDiningLocalization.LocalizeOrFallback(RateToken, "Rate");

    internal static string FermentationSpeedLabel =>
        FineDiningLocalization.LocalizeOrFallback(FermentationSpeedToken, "Fermentation speed");

    internal static string FermentationGuidanceLabel =>
        FineDiningLocalization.LocalizeOrFallback(
            FermentationGuidanceToken,
            "More cover and greater depth make fermentation faster.");

    internal static string AutoEjectLabel =>
        FineDiningLocalization.LocalizeOrFallback(AutoEjectToken, "Auto eject");

    internal static string ColorizeTimer(string text) =>
        string.IsNullOrEmpty(text)
            ? text
            : $"<color={TimerColorHex}>{text}</color>";

    internal static string FormatDuration(double seconds, bool keepAtLeastOneSecond = false)
    {
        if (!TryGetTotalSeconds(seconds, keepAtLeastOneSecond, out long totalSeconds))
        {
            return string.Empty;
        }

        long hours = totalSeconds / 3600L;
        long minutes = totalSeconds % 3600L / 60L;
        long remainingSeconds = totalSeconds % 60L;
        return hours > 0L
            ? $"{hours}:{minutes:00}:{remainingSeconds:00}"
            : $"{minutes:00}:{remainingSeconds:00}";
    }

    internal static string FormatSeconds(double seconds, bool keepAtLeastOneSecond = false)
    {
        return FormatSecondsCore(
            seconds,
            keepAtLeastOneSecond,
            FineDiningLocalization.LocalizeOrFallback(SecondsToken, "{0}s"));
    }

    internal static string FormatSecondsCore(
        double seconds,
        bool keepAtLeastOneSecond,
        string format)
    {
        if (!TryGetTotalSeconds(seconds, keepAtLeastOneSecond, out long totalSeconds))
        {
            return string.Empty;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, totalSeconds);
        }
        catch (FormatException)
        {
            return totalSeconds.ToString(CultureInfo.InvariantCulture) + "s";
        }
    }

    private static bool TryGetTotalSeconds(
        double seconds,
        bool keepAtLeastOneSecond,
        out long totalSeconds)
    {
        totalSeconds = 0L;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d)
        {
            return false;
        }

        totalSeconds = (long)Math.Ceiling(Math.Min(seconds, int.MaxValue));
        if (keepAtLeastOneSecond && totalSeconds < 1L)
        {
            totalSeconds = 1L;
        }

        return true;
    }

}
