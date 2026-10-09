using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx.Logging;

namespace FineDining;

// One immutable ZDO value replaces the complete plan set, including an empty set.
// Removing a ZDO key does not remove that key from existing remote copies.
internal static class CookingStationPlanStore
{
    internal const string PayloadKey = "sighsorry.FineDining.CookingStation.plans";
    internal const byte FormatVersion = 2;
    internal const int MaxPayloadBytes = 1024 * 1024;
    private static readonly int PayloadHash = PayloadKey.GetStableHashCode();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static ConditionalWeakTable<ZDO, CachedPlans> _cache = new();
    private static ManualLogSource? _log;

    internal static void Reset(ManualLogSource? log)
    {
        _cache = new ConditionalWeakTable<ZDO, CachedPlans>();
        _log = log;
    }

    internal static bool TryRead(ZDO zdo, int slot, out CookingStationSlotPlan plan)
    {
        plan = default;
        CachedPlans cached = GetPlans(zdo);
        return cached.Valid && cached.Plans.TryGetValue(slot, out plan);
    }

    internal static bool Write(ZDO zdo, int slot, CookingStationSlotPlan plan)
    {
        if (slot < 0 || slot > ushort.MaxValue
            || string.IsNullOrEmpty(plan.ExpectedInput) || string.IsNullOrEmpty(plan.ExpectedOutput))
        {
            return false;
        }

        CachedPlans cached = GetPlans(zdo);
        if (!cached.Valid)
        {
            return false;
        }

        if (cached.Plans.TryGetValue(slot, out CookingStationSlotPlan previous)
            && SamePlan(previous, plan))
        {
            return true;
        }

        Dictionary<int, CookingStationSlotPlan> plans = new(cached.Plans) { [slot] = plan };
        return Save(zdo, cached, plans);
    }

    internal static bool Clear(ZDO zdo, int slot)
    {
        CachedPlans cached = GetPlans(zdo);
        if (!cached.Valid)
        {
            return false;
        }

        if (!cached.Plans.ContainsKey(slot))
        {
            return true;
        }

        Dictionary<int, CookingStationSlotPlan> plans = new(cached.Plans);
        plans.Remove(slot);
        return Save(zdo, cached, plans);
    }

    private static bool Save(ZDO zdo, CachedPlans cached, Dictionary<int, CookingStationSlotPlan> plans)
    {
        byte[] payload = Encode(plans);
        // Never modify a stored array in place: ZDO.Set compares array references.
        // Normal ZDO.Set also updates revision, the client change queue and save dirtiness.
        zdo.Set(PayloadHash, payload);
        if (!ReferenceEquals(zdo.GetByteArray(PayloadHash), payload))
        {
            return false;
        }

        cached.Payload = payload;
        cached.Plans = plans;
        return true;
    }

    private static CachedPlans GetPlans(ZDO zdo)
    {
        CachedPlans cached = _cache.GetValue(zdo, _ => new CachedPlans());
        byte[]? payload = zdo.GetByteArray(PayloadHash);
        bool sameObject = cached.Initialized && cached.Uid == zdo.m_uid;
        bool sameOwner = sameObject && cached.OwnerRevision == zdo.OwnerRevision;
        if (sameOwner && ReferenceEquals(cached.Payload, payload))
        {
            return cached;
        }

        // A native cooking-time update sends the whole ZDO, including an unchanged
        // plan array. Avoid decoding it again, but refresh after ownership changes.
        if (sameOwner && SameBytes(cached.Payload, payload))
        {
            cached.Payload = payload;
            return cached;
        }

        cached.Initialized = true;
        cached.Uid = zdo.m_uid; // ZDO instances can be pooled and reused for another object.
        cached.OwnerRevision = zdo.OwnerRevision;
        cached.Payload = payload;
        cached.Valid = TryDecode(payload, out Dictionary<int, CookingStationSlotPlan> plans);
        cached.Plans = plans;
        if (!cached.Valid)
        {
            _log?.LogWarning(
                "Invalid or unsupported CookingStation plan data; the stored block was left unchanged.");
        }

        return cached;
    }

    private static bool SamePlan(CookingStationSlotPlan left, CookingStationSlotPlan right) =>
        left.AutoPop == right.AutoPop
        && left.CollectionExperiencePrepaid == right.CollectionExperiencePrepaid
        && left.BonusCount == right.BonusCount
        && string.Equals(left.ExpectedInput, right.ExpectedInput, StringComparison.Ordinal)
        && string.Equals(left.ExpectedOutput, right.ExpectedOutput, StringComparison.Ordinal);

    private static bool SameBytes(byte[]? left, byte[]? right)
    {
        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    // Wire format: version:u8, count:u16, then sorted records of slot:u16,
    // flags:u8 (auto/prepaid/bonus), input/output (u16 UTF-8 byte length + bytes).
    // Version 1's six per-slot keys are deliberately neither read nor removed.
    private static byte[] Encode(Dictionary<int, CookingStationSlotPlan> plans)
    {
        if (plans.Count > ushort.MaxValue)
        {
            throw new InvalidDataException("Too many CookingStation plans.");
        }

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Utf8, true);
        writer.Write(FormatVersion);
        writer.Write((ushort)plans.Count);
        List<int> slots = new(plans.Keys);
        slots.Sort();
        foreach (int slot in slots)
        {
            CookingStationSlotPlan plan = plans[slot];
            if (slot < 0 || slot > ushort.MaxValue)
            {
                throw new InvalidDataException("Invalid CookingStation slot.");
            }

            writer.Write((ushort)slot);
            writer.Write((byte)((plan.AutoPop ? 1 : 0)
                                | (plan.CollectionExperiencePrepaid ? 2 : 0)
                                | (plan.BonusCount > 0 ? 4 : 0)));
            WriteName(writer, plan.ExpectedInput);
            WriteName(writer, plan.ExpectedOutput);
            if (stream.Length > MaxPayloadBytes)
            {
                throw new InvalidDataException("CookingStation plan data is too large.");
            }
        }

        return stream.ToArray();
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        int length = Utf8.GetByteCount(name);
        if (length == 0 || length > ushort.MaxValue)
        {
            throw new InvalidDataException("Invalid CookingStation prefab name length.");
        }

        writer.Write((ushort)length);
        writer.Write(Utf8.GetBytes(name));
    }

    private static bool TryDecode(byte[]? payload, out Dictionary<int, CookingStationSlotPlan> plans)
    {
        plans = new Dictionary<int, CookingStationSlotPlan>();
        if (payload == null)
        {
            return true;
        }

        if (payload.Length < 3 || payload.Length > MaxPayloadBytes || payload[0] != FormatVersion)
        {
            return false;
        }

        try
        {
            using MemoryStream stream = new(payload, false);
            using BinaryReader reader = new(stream, Utf8);
            reader.ReadByte();
            int count = reader.ReadUInt16();
            if (count > (payload.Length - 3) / 9) // Each record has two nonempty names.
            {
                return false;
            }

            int previousSlot = -1;
            for (int i = 0; i < count; i++)
            {
                int slot = reader.ReadUInt16();
                byte flags = reader.ReadByte();
                if (slot <= previousSlot || (flags & ~7) != 0)
                {
                    return false;
                }

                string input = ReadName(reader);
                string output = ReadName(reader);
                plans.Add(slot, new CookingStationSlotPlan(
                    (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0 ? 1 : 0, input, output));
                previousSlot = slot;
            }

            return stream.Position == stream.Length;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ReadName(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        if (length == 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid CookingStation prefab name length.");
        }

        return Utf8.GetString(reader.ReadBytes(length));
    }

    private sealed class CachedPlans
    {
        internal bool Initialized;
        internal ZDOID Uid;
        internal ushort OwnerRevision;
        internal byte[]? Payload;
        internal bool Valid;
        internal Dictionary<int, CookingStationSlotPlan> Plans = new();
    }
}
