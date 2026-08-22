using System;

namespace FineDining;

internal static class StationText
{
    internal const string CoverToken = "$finedining_station_cover";
    internal const string DepthToken = "$finedining_station_depth";
    internal const string RateToken = "$finedining_station_rate";
    internal const string TimerToken = "$finedining_station_timer";

    internal static string CoverLabel => Localize(CoverToken, "Cover");

    internal static string DepthLabel => Localize(DepthToken, "Depth");

    internal static string RateLabel => Localize(RateToken, "Rate");

    internal static string TimerLabel => Localize(TimerToken, "Time");

    internal static string FormatDuration(double seconds, bool keepAtLeastOneSecond = false)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d)
        {
            return string.Empty;
        }

        long totalSeconds = (long)Math.Ceiling(Math.Min(seconds, int.MaxValue));
        if (keepAtLeastOneSecond && totalSeconds < 1L)
        {
            totalSeconds = 1L;
        }

        long hours = totalSeconds / 3600L;
        long minutes = totalSeconds % 3600L / 60L;
        long remainingSeconds = totalSeconds % 60L;
        return hours > 0L
            ? $"{hours}:{minutes:00}:{remainingSeconds:00}"
            : $"{minutes:00}:{remainingSeconds:00}";
    }

    internal static string FormatTimer(double seconds, bool keepAtLeastOneSecond = false)
    {
        string duration = FormatDuration(seconds, keepAtLeastOneSecond);
        return string.IsNullOrEmpty(duration) ? string.Empty : $"{TimerLabel}: {duration}";
    }

    private static string Localize(string token, string fallback)
    {
        Localization? localization = Localization.instance;
        if (localization == null)
        {
            return fallback;
        }

        string localized = localization.Localize(token);
        return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, token, StringComparison.Ordinal)
            ? fallback
            : localized;
    }
}
