using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace FineDining;

/// <summary>
/// Owns the atomic item record. Calculation longs are deadlines in the current
/// spoilage timeline, or negative environmental-pause durations. Persisted v2
/// strings also carry the world/epoch and a portable remaining-life checkpoint.
/// </summary>
internal static class SpoilageClock
{
    internal const string ExpiryDataKey = "sighsorry.FineDining.ExpiryWorldTicks";
    internal const string SpoiledDataKey = "sighsorry.FineDining.Spoiled";
    private const string SpoiledDataValue = "1";
    private sealed class ParsedClock
    {
        internal string? Raw;
        internal bool Valid;
        internal SpoilageClockRecord Record;
    }
    private static readonly ConditionalWeakTable<ItemDrop.ItemData, ParsedClock> Parsed = new();

    internal static bool HasStoredClock(ItemDrop.ItemData? item) =>
        item?.m_customData?.ContainsKey(ExpiryDataKey) == true;
    internal static bool HasUnresolvedClock(ItemDrop.ItemData? item) =>
        HasStoredClock(item) && !TryGetExpiryTicks(item, out _);
    internal static bool SameStoredClock(ItemDrop.ItemData? left, ItemDrop.ItemData? right) =>
        left?.m_customData != null && right?.m_customData != null &&
        left.m_customData.TryGetValue(ExpiryDataKey, out string a) &&
        right.m_customData.TryGetValue(ExpiryDataKey, out string b) && string.Equals(a, b, StringComparison.Ordinal);

    internal static bool IsSpoiled(ItemDrop.ItemData? item) =>
        item?.m_customData != null &&
        item.m_customData.TryGetValue(SpoiledDataKey, out string value) &&
        string.Equals(value, SpoiledDataValue, StringComparison.Ordinal);

    internal static bool MarkSpoiled(ItemDrop.ItemData? item)
    {
        if (item == null)
        {
            return false;
        }

        item.m_customData ??= new System.Collections.Generic.Dictionary<string, string>();
        if (IsSpoiled(item))
        {
            return false;
        }

        item.m_customData[SpoiledDataKey] = SpoiledDataValue;
        return true;
    }

    internal static bool ClearSpoiled(ItemDrop.ItemData? item) =>
        item?.m_customData != null && item.m_customData.Remove(SpoiledDataKey);

    internal static string? ComposeSpoiledValues(string? destinationValue, string? sourceValue)
    {
        if (!CanMergeSpoiledValues(destinationValue, sourceValue))
        {
            return destinationValue;
        }

        return destinationValue == SpoiledDataValue || sourceValue == SpoiledDataValue
            ? SpoiledDataValue
            : null;
    }

    internal static bool CanMergeSpoiledValues(string? destinationValue, string? sourceValue) =>
        IsValidSpoiledValue(destinationValue) && IsValidSpoiledValue(sourceValue);

    private static bool IsValidSpoiledValue(string? value) =>
        value == null || string.Equals(value, SpoiledDataValue, StringComparison.Ordinal);

    internal static bool TryGetExpiryTicks(ItemDrop.ItemData? item, out long clockValue)
    {
        clockValue = 0L;
        return TryReadItem(item, out SpoilageClockRecord record) && TryResolve(record, false, out clockValue);
    }

    private static bool TryReadItem(ItemDrop.ItemData? item, out SpoilageClockRecord record)
    {
        record = default;
        if (item?.m_customData == null || !item.m_customData.TryGetValue(ExpiryDataKey, out string raw)) return false;
        ParsedClock cache = Parsed.GetValue(item, _ => new ParsedClock());
        if (cache.Raw != raw)
        {
            cache.Raw = raw;
            cache.Valid = SpoilageClockRecord.TryParse(raw, out cache.Record);
        }
        record = cache.Record;
        return cache.Valid;
    }

    internal static bool NeedsAdoption(ItemDrop.ItemData item) =>
        WorldSpoilageClock.TryGetContext(out SpoilageTimeline context) &&
        TryReadItem(item, out SpoilageClockRecord record) && record.NeedsAdoption(context);

    private static bool TryResolve(SpoilageClockRecord record, bool timestamp, out long value)
    {
        value = 0;
        if (WorldSpoilageClock.TryGetContext(out SpoilageTimeline context))
        {
            bool resolved = record.TryResolve(context, timestamp, out value);
            if (!resolved && !record.Legacy && record.WorldUid == context.WorldUid && record.Epoch != context.Epoch)
                WorldSpoilageClock.ReportMissingHistory();
            return resolved;
        }
        if (WorldSpoilageClock.Initialized || !record.Legacy) return false;
        value = record.Value; // Existing standalone codec/compatibility consumers.
        return !timestamp || value > 0;
    }

    internal static string? FormatClockValue(long value)
    {
        if (!IsValidClockValue(value)) return null;
        if (WorldSpoilageClock.TryGetContext(out SpoilageTimeline context))
            return new SpoilageClockRecord(value, context).Serialize();
        return WorldSpoilageClock.Initialized ? null : value.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool TryParseTimestamp(string? value, out long ticks)
    {
        ticks = 0;
        return SpoilageClockRecord.TryParse(value, out SpoilageClockRecord record) && TryResolve(record, true, out ticks);
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
        if (!WorldSpoilageClock.Initialized) return TryGetRawWorldTicks(out ticks) && ticks > 0;
        ticks = 0;
        if (!WorldSpoilageClock.TryGetContext(out SpoilageTimeline context)) return false;
        ticks = context.ClockTicks;
        return true;
    }

    internal static bool TryGetRawWorldTicks(out long ticks)
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
            return ticks >= 0L;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryParseClockValue(string? value, out long clockValue)
    {
        clockValue = 0L;
        return SpoilageClockRecord.TryParse(value, out SpoilageClockRecord record) && TryResolve(record, false, out clockValue);
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
