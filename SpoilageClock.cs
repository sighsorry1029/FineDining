using System;
using System.Globalization;

namespace FineDining;

/// <summary>
/// Encodes the one persisted spoilage clock used by inventory and world items.
/// Positive values are running absolute deadlines; negative values are paused
/// remaining durations.
/// </summary>
internal static class SpoilageClock
{
    internal const string ExpiryDataKey = "sighsorry.FineDining.ExpiryWorldTicks";

    internal static bool TryGetExpiryTicks(ItemDrop.ItemData? item, out long clockValue)
    {
        clockValue = 0L;
        return item?.m_customData != null &&
               item.m_customData.TryGetValue(ExpiryDataKey, out string value) &&
               TryParseClockValue(value, out clockValue);
    }

    internal static bool TryGetSpoilageClock(
        ItemDrop.ItemData? item,
        long nowTicks,
        out long remainingTicks,
        out bool paused)
    {
        remainingTicks = 0L;
        paused = false;
        return TryGetExpiryTicks(item, out long clockValue) &&
               TryDecodeClockValue(clockValue, nowTicks, out remainingTicks, out paused);
    }

    internal static bool TryGetWorldTicks(out long ticks)
    {
        ticks = 0L;
        ZNet znet = ZNet.instance;
        if (znet == null)
        {
            return false;
        }

        try
        {
            ticks = znet.GetTime().Ticks;
            return ticks > 0L;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParseClockValue(string? value, out long clockValue)
    {
        clockValue = 0L;
        return value != null &&
               long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out clockValue) &&
               IsValidClockValue(clockValue) &&
               string.Equals(
                   value,
                   clockValue.ToString(CultureInfo.InvariantCulture),
                   StringComparison.Ordinal);
    }

    internal static bool IsValidClockValue(long clockValue)
    {
        return clockValue != 0L && clockValue != long.MinValue;
    }

    internal static bool TryDecodeClockValue(
        long clockValue,
        long nowTicks,
        out long remainingTicks,
        out bool paused)
    {
        remainingTicks = 0L;
        paused = false;
        if (!IsValidClockValue(clockValue))
        {
            return false;
        }

        if (clockValue < 0L)
        {
            paused = true;
            remainingTicks = -clockValue;
            return true;
        }

        remainingTicks = nowTicks > 0L
            ? Math.Max(0L, clockValue - Math.Min(clockValue, nowTicks))
            : clockValue;
        return true;
    }

    internal static long EncodeClockValue(long nowTicks, long remainingTicks, bool paused)
    {
        long normalizedRemaining = Math.Max(0L, Math.Min(long.MaxValue, remainingTicks));
        if (paused && normalizedRemaining > 0L)
        {
            return -normalizedRemaining;
        }

        if (normalizedRemaining <= 0L)
        {
            return Math.Max(1L, nowTicks);
        }

        return AddTicksSaturating(Math.Max(0L, nowTicks), normalizedRemaining);
    }

    internal static long AddTicksSaturating(long left, long right)
    {
        long normalizedLeft = Math.Max(0L, left);
        long normalizedRight = Math.Max(0L, right);
        return normalizedRight >= long.MaxValue - normalizedLeft
            ? long.MaxValue
            : normalizedLeft + normalizedRight;
    }

    internal static long ComposeClockValues(
        long destinationClock,
        long sourceClock,
        long nowTicks,
        bool destinationPaused)
    {
        if (!TryDecodeClockValue(destinationClock, nowTicks, out long destinationRemaining, out _) ||
            !TryDecodeClockValue(sourceClock, nowTicks, out long sourceRemaining, out _))
        {
            return destinationClock;
        }

        long remaining = Math.Min(destinationRemaining, sourceRemaining);
        return EncodeClockValue(nowTicks, remaining, destinationPaused && remaining > 0L);
    }
}
