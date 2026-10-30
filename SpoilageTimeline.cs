using System;
using System.Globalization;

namespace FineDining;

// Persisted world time and item records share this small, Unity-independent codec.
// Winter changes the clock, never an individual item's environmental pause flag.
internal struct SpoilageTimeline
{
    internal const long InitialClockTicks = 1L << 60;
    internal long WorldUid;
    internal string Epoch;
    internal long WorldTicks;
    internal long ClockTicks;
    internal bool Frozen;

    internal SpoilageTimeline(long worldUid, string epoch, long worldTicks)
    {
        WorldUid = worldUid;
        Epoch = epoch;
        WorldTicks = worldTicks;
        ClockTicks = InitialClockTicks;
        Frozen = false;
    }

    internal bool Advance(long worldTicks)
    {
        if (worldTicks < 0L) return false;
        long next = ClockTicks;
        try
        {
            // Signed deltas intentionally preserve vanilla time rewinds outside
            // winter (Seasons also uses SetNetTime to wait for sleep).
            if (!Frozen) next = checked(ClockTicks + checked(worldTicks - WorldTicks));
        }
        catch (OverflowException) { return false; }
        if (next <= 0L) return false;
        WorldTicks = worldTicks;
        ClockTicks = next;
        return true;
    }

    internal string Serialize() => string.Join("|", "1", Number(WorldUid), Epoch,
        Number(WorldTicks), Number(ClockTicks), Frozen ? "1" : "0");

    internal static bool TryParse(string? value, out SpoilageTimeline state)
    {
        state = default;
        if (value == null || value.Length > 180) return false;
        string[] parts = value.Split('|');
        if (parts.Length != 6 || parts[0] != "1" ||
            !TryNumber(parts[1], out long world) || world == 0 || !IsEpoch(parts[2]) ||
            !TryNumber(parts[3], out long ticks) || ticks < 0 ||
            !TryNumber(parts[4], out long clock) || clock <= 0 ||
            (parts[5] != "0" && parts[5] != "1")) return false;
        state = new SpoilageTimeline(world, parts[2], ticks)
        { ClockTicks = clock, Frozen = parts[5] == "1" };
        return true;
    }

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static bool TryNumber(string text, out long value) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
        text == Number(value);
    internal static bool IsEpoch(string value) => value.Length == 32 &&
        Guid.TryParseExact(value, "N", out Guid guid) && value == guid.ToString("N");
}

internal readonly struct SpoilageClockRecord
{
    internal readonly long WorldUid;
    internal readonly string? Epoch;
    internal readonly long Value;
    internal readonly long PortableRemaining;
    internal bool Legacy => Epoch == null;

    internal SpoilageClockRecord(long value, SpoilageTimeline context)
    {
        WorldUid = context.WorldUid;
        Epoch = context.Epoch;
        Value = value;
        PortableRemaining = value < 0 ? -value : Math.Max(0L, value - context.ClockTicks);
    }

    private SpoilageClockRecord(long world, string? epoch, long value, long remaining)
    {
        WorldUid = world;
        Epoch = epoch;
        Value = value;
        PortableRemaining = remaining;
    }

    internal static bool TryParse(string? text, out SpoilageClockRecord record)
    {
        record = default;
        if (text == null || text.Length > 180) return false;
        if (!text.StartsWith("2|", StringComparison.Ordinal))
        {
            if (!SpoilageTimeline.TryNumber(text, out long legacy) || !ValidValue(legacy)) return false;
            record = new SpoilageClockRecord(0, null, legacy, 0);
            return true;
        }
        string[] parts = text.Split('|');
        if (parts.Length != 5 || !SpoilageTimeline.TryNumber(parts[1], out long world) || world == 0 ||
            !SpoilageTimeline.IsEpoch(parts[2]) || !SpoilageTimeline.TryNumber(parts[3], out long value) ||
            !ValidValue(value) || !SpoilageTimeline.TryNumber(parts[4], out long remaining) || remaining < 0 ||
            (value < 0 && remaining != -value)) return false;
        record = new SpoilageClockRecord(world, parts[2], value, remaining);
        return true;
    }

    internal bool TryResolve(SpoilageTimeline context, bool timestamp, out long value)
    {
        value = Value;
        if (Legacy)
        {
            if (Value < 0) return !timestamp;
            long delta = Value - context.WorldTicks;
            if (!timestamp) delta = Math.Max(0L, delta);
            try { value = checked(context.ClockTicks + delta); }
            catch (OverflowException) { return false; }
            return value > 0;
        }
        if (WorldUid == context.WorldUid)
        {
            // A missing/replaced world ledger is not permission to grant fresh life.
            return Epoch == context.Epoch && (!timestamp || value > 0);
        }
        if (timestamp) return false;
        // Different worlds cannot query one another's offline time. Transfers use
        // the last serialized remaining-life checkpoint, never the full lifetime.
        if (value < 0) return true;
        try { value = checked(context.ClockTicks + PortableRemaining); }
        catch (OverflowException) { return false; }
        return value > 0;
    }

    internal bool NeedsAdoption(SpoilageTimeline context) => Legacy || WorldUid != context.WorldUid;
    internal string Serialize() => Legacy ? SpoilageTimeline.Number(Value) :
        string.Join("|", "2", SpoilageTimeline.Number(WorldUid), Epoch,
            SpoilageTimeline.Number(Value), SpoilageTimeline.Number(PortableRemaining));
    internal static bool ValidValue(long value) => value != 0 && value != long.MinValue;
}
