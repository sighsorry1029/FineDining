using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

internal static class CookingStationAutoPopCore
{
    internal const float CookingExperienceOnAdd = 0.4f;
    internal const float CookingExperienceOnCollect = 0.6f;

    internal static float ClampSkillFactor(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0f;
        }

        return float.IsPositiveInfinity(value) || value >= 1f ? 1f : value;
    }

    internal static bool ShouldAutoPop(
        float skillFactor,
        float roll,
        bool hasDistinctOvercookStage)
    {
        if (!hasDistinctOvercookStage)
        {
            return false;
        }

        float chance = ClampSkillFactor(skillFactor);
        if (chance <= 0f)
        {
            return false;
        }

        if (chance >= 1f)
        {
            return true;
        }

        return !float.IsNaN(roll) && roll < chance;
    }

    internal static bool HasDistinctOvercookStage(
        float cookTime,
        string? cookedPrefabName,
        string? overcookedPrefabName)
    {
        return !float.IsNaN(cookTime)
               && !float.IsInfinity(cookTime)
               && cookTime > 0f
               && !string.IsNullOrEmpty(cookedPrefabName)
               && !string.IsNullOrEmpty(overcookedPrefabName)
               && !string.Equals(cookedPrefabName, "Coal", StringComparison.Ordinal)
               && !string.Equals(
                   cookedPrefabName,
                   overcookedPrefabName,
                   StringComparison.Ordinal);
    }

    internal static int ClampBonusCount(int value) => value <= 0 ? 0 : 1;
}

internal readonly struct CookingStationSlotPlan
{
    internal CookingStationSlotPlan(
        bool autoPop,
        bool collectionExperiencePrepaid,
        int bonusCount,
        string expectedInput,
        string expectedOutput)
    {
        AutoPop = autoPop;
        CollectionExperiencePrepaid = collectionExperiencePrepaid;
        BonusCount = CookingStationAutoPopCore.ClampBonusCount(bonusCount);
        ExpectedInput = expectedInput ?? string.Empty;
        ExpectedOutput = expectedOutput ?? string.Empty;
    }

    internal bool AutoPop { get; }
    internal bool CollectionExperiencePrepaid { get; }
    internal int BonusCount { get; }
    internal string ExpectedInput { get; }
    internal string ExpectedOutput { get; }

    internal CookingStationSlotPlan DisableAutoPop() =>
        new(
            false,
            CollectionExperiencePrepaid,
            BonusCount,
            ExpectedInput,
            ExpectedOutput);
}

internal static class CookingStationAutoPopSystem
{
    internal const string RequestPlanRpc = "FineDining_CookingStation_RequestPlan";
    internal const string AutoPopBonusEffectRpc = "FineDining_CookingStation_AutoPopBonusEffect";
    internal const string SlotStateKeyPrefix = "sighsorry.FineDining.CookingStation.";
    internal const int SlotPlanVersion = 1;

    private const float PendingPlanTimeoutSeconds = 5f;
    private const int StatusNotDone = 0;
    private const int StatusDone = 1;
    private const int StatusBurnt = 2;

    private static readonly MethodInfo? SpawnItemMethod =
        AccessTools.DeclaredMethod(
            typeof(CookingStation),
            "SpawnItem",
            new[] { typeof(string), typeof(int), typeof(Vector3), typeof(bool) });

    private static ConditionalWeakTable<CookingStation, RegistrationMarker>
        _registeredStations = new();
    private static ConditionalWeakTable<CookingStation, OwnerPendingPlans>
        _ownerPendingPlans = new();

    private static bool _requestPatchReady;
    private static bool _experiencePatchReady;
    private static bool _registrationWarningLogged;
    private static bool _requestWarningLogged;
    private static bool _autoPopWarningLogged;
    private static bool _bonusEffectWarningLogged;

    internal static bool ManagedAddPatchesReady =>
        _requestPatchReady && _experiencePatchReady;

    internal static void Reset()
    {
        _registeredStations = new ConditionalWeakTable<CookingStation, RegistrationMarker>();
        _ownerPendingPlans = new ConditionalWeakTable<CookingStation, OwnerPendingPlans>();
        _requestPatchReady = false;
        _experiencePatchReady = false;
        _registrationWarningLogged = false;
        _requestWarningLogged = false;
        _autoPopWarningLogged = false;
        _bonusEffectWarningLogged = false;
    }

    internal static void MarkRequestPatchReady()
    {
        _requestPatchReady = true;
    }

    internal static void MarkExperiencePatchReady()
    {
        _experiencePatchReady = true;
    }

    internal static void RegisterRpcs(CookingStation station)
    {
        ZNetView? nview = GetNView(station);
        _registeredStations.Remove(station);
        if (nview == null || !nview.IsValid())
        {
            return;
        }

        try
        {
            nview.Unregister(RequestPlanRpc);
            nview.Unregister(AutoPopBonusEffectRpc);
            nview.Register<string, int, float>(
                RequestPlanRpc,
                (sender, itemName, skillPermille, autoRoll) =>
                    ReceivePlan(
                        station,
                        sender,
                        itemName,
                        skillPermille,
                        autoRoll));
            nview.Register<int>(
                AutoPopBonusEffectRpc,
                (sender, bonusCount) => ReceiveBonusEffect(
                    station,
                    sender,
                    bonusCount));
            _registeredStations.Add(station, new RegistrationMarker());
        }
        catch (Exception exception)
        {
            if (_registrationWarningLogged)
            {
                return;
            }

            _registrationWarningLogged = true;
            FineDiningPlugin.Log.LogWarning(
                $"Could not register the CookingStation auto-eject RPCs; vanilla insertion remains active ({exception}).");
        }
    }

    internal static void RequestAdd(
        ZNetView? nview,
        string vanillaRpcName,
        object[]? vanillaParameters,
        Humanoid user,
        CookingStation station)
    {
        if (!ManagedAddPatchesReady)
        {
            InvokeVanillaAdd(nview, vanillaRpcName, vanillaParameters);
            return;
        }

        Player? localPlayer = Player.m_localPlayer;
        Player? player = user as Player;
        if (nview == null
            || station == null
            || player == null
            || localPlayer == null
            || player != localPlayer
            || vanillaParameters == null
            || vanillaParameters.Length != 2
            || vanillaParameters[1] is not bool
            || vanillaParameters[0] is not string itemName
            || string.IsNullOrEmpty(itemName))
        {
            InvokeVanillaAdd(nview, vanillaRpcName, vanillaParameters);
            localPlayer?.RaiseSkill(
                Skills.SkillType.Cooking,
                CookingStationAutoPopCore.CookingExperienceOnAdd);
            return;
        }

        bool planRequested = false;
        bool autoPop = false;
        if (nview.IsValid()
            && _registeredStations.TryGetValue(station, out _))
        {
            float skillFactor = CookingStationAutoPopCore.ClampSkillFactor(
                player.GetSkillFactor(Skills.SkillType.Cooking));
            int skillPermille = Mathf.RoundToInt(skillFactor * 1000f);
            float quantizedSkillFactor = skillPermille / 1000f;
            float autoRoll = UnityEngine.Random.value;
            autoPop = CookingStationAutoPopCore.ShouldAutoPop(
                quantizedSkillFactor,
                autoRoll,
                CanAutoPopConversion(
                    station,
                    FindInputConversion(station, itemName)));

            try
            {
                // Both RPCs use the station's current owner and the same reliable
                // peer stream. Queue the plan immediately before vanilla add.
                nview.InvokeRPC(
                    RequestPlanRpc,
                    itemName,
                    skillPermille,
                    autoRoll);
                planRequested = true;
            }
            catch (Exception exception)
            {
                LogRequestFailure(exception);
            }
        }

        InvokeVanillaAdd(nview, vanillaRpcName, vanillaParameters);

        // Keep Valheim's two separate raises across a possible level boundary.
        player.RaiseSkill(
            Skills.SkillType.Cooking,
            CookingStationAutoPopCore.CookingExperienceOnAdd);
        if (planRequested && autoPop)
        {
            player.RaiseSkill(
                Skills.SkillType.Cooking,
                CookingStationAutoPopCore.CookingExperienceOnCollect);
        }
    }

    internal static void RaiseAddExperienceOrDefer(
        Character character,
        Skills.SkillType skill,
        float vanillaAmount)
    {
        if (!ManagedAddPatchesReady)
        {
            character.RaiseSkill(skill, vanillaAmount);
        }
    }

    internal static void RaiseCollectionExperience(
        Character character,
        Skills.SkillType skill,
        float vanillaAmount,
        CookingStation station)
    {
        if (TryGetFirstDonePlan(
                station,
                out _,
                out CookingStationSlotPlan plan,
                out _)
            && plan.CollectionExperiencePrepaid)
        {
            return;
        }

        character.RaiseSkill(skill, vanillaAmount);
    }

    internal static void PrepareAdd(
        CookingStation station,
        long sender,
        string itemName)
    {
        ZNetView? nview = GetNView(station);
        ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
        int slot = FindFreeSlot(station, zdo);
        if (nview == null || !nview.IsOwner() || zdo == null || slot < 0)
        {
            DiscardPendingPlan(station, sender);
            return;
        }

        // Any vanilla add invalidates a stale plan on the reused slot.
        ClearPlan(station, slot);
        if (TryTakePendingPlan(
                station,
                sender,
                itemName,
                out CookingStationSlotPlan plan))
        {
            WritePlan(station, slot, plan);
        }
    }

    internal static bool TryGetFirstDonePlan(
        CookingStation station,
        out int slot,
        out CookingStationSlotPlan plan,
        out bool outputMatches)
    {
        plan = default;
        outputMatches = false;
        if (!TryGetFirstDoneSlot(station, out slot, out string itemName)
            || !TryReadPlan(station, slot, out plan))
        {
            return false;
        }

        outputMatches = string.Equals(
            itemName,
            plan.ExpectedOutput,
            StringComparison.Ordinal);
        return true;
    }

    internal static bool TryGetFirstDoneSlot(
        CookingStation station,
        out int slot,
        out string itemName)
    {
        slot = -1;
        itemName = string.Empty;
        ZDO? zdo = GetZdo(station);
        if (zdo == null || station.m_slots == null)
        {
            return false;
        }

        for (int index = 0; index < station.m_slots.Length; index++)
        {
            string candidate = zdo.GetString("slot" + index);
            if (!string.IsNullOrEmpty(candidate) && IsDoneItem(station, candidate))
            {
                slot = index;
                itemName = candidate;
                return true;
            }
        }

        return false;
    }

    internal static bool TryReadPlan(
        CookingStation station,
        int slot,
        out CookingStationSlotPlan plan)
    {
        plan = default;
        ZDO? zdo = GetZdo(station);
        if (zdo == null
            || slot < 0
            || zdo.GetInt(GetPlanKey(slot, "version")) != SlotPlanVersion)
        {
            return false;
        }

        string expectedInput = zdo.GetString(GetPlanKey(slot, "input"));
        string expectedOutput = zdo.GetString(GetPlanKey(slot, "output"));
        if (string.IsNullOrEmpty(expectedInput)
            || string.IsNullOrEmpty(expectedOutput))
        {
            return false;
        }

        plan = new CookingStationSlotPlan(
            zdo.GetInt(GetPlanKey(slot, "auto")) != 0,
            zdo.GetInt(GetPlanKey(slot, "prepaid")) != 0,
            zdo.GetInt(GetPlanKey(slot, "bonus")),
            expectedInput,
            expectedOutput);
        return true;
    }

    internal static bool ShouldShowAutoEject(
        CookingStation station,
        int slot,
        string currentItemName,
        string conversionOutputName,
        bool outputPhase)
    {
        return TryReadPlan(station, slot, out CookingStationSlotPlan plan)
               && IsAutoPopPlanEligible(station, plan)
               && IsMatchingAutoEjectPlan(
                   plan,
                   currentItemName,
                   conversionOutputName,
                   outputPhase);
    }

    private static bool IsMatchingAutoEjectPlan(
        CookingStationSlotPlan plan,
        string currentItemName,
        string conversionOutputName,
        bool outputPhase)
    {
        if (!plan.AutoPop
            || string.IsNullOrEmpty(currentItemName)
            || string.Equals(plan.ExpectedOutput, "Coal", StringComparison.Ordinal))
        {
            return false;
        }

        if (outputPhase)
        {
            return string.Equals(
                currentItemName,
                plan.ExpectedOutput,
                StringComparison.Ordinal);
        }

        return string.Equals(
                   currentItemName,
                   plan.ExpectedInput,
                   StringComparison.Ordinal)
               && string.Equals(
                   conversionOutputName,
                   plan.ExpectedOutput,
                   StringComparison.Ordinal);
    }

    internal static void ClearPlan(CookingStation station, int slot)
    {
        ZDO? zdo = GetZdo(station);
        if (zdo == null || slot < 0)
        {
            return;
        }

        zdo.Set(GetPlanKey(slot, "input"), string.Empty);
        zdo.Set(GetPlanKey(slot, "output"), string.Empty);
        zdo.Set(GetPlanKey(slot, "auto"), 0);
        zdo.Set(GetPlanKey(slot, "prepaid"), 0);
        zdo.Set(GetPlanKey(slot, "bonus"), 0);
        zdo.Set(GetPlanKey(slot, "version"), 0);
    }

    internal static bool IsSlotEmpty(CookingStation station, int slot)
    {
        ZDO? zdo = GetZdo(station);
        return zdo != null
               && slot >= 0
               && string.IsNullOrEmpty(zdo.GetString("slot" + slot));
    }

    internal static string GetPlanKey(int slot, string field)
    {
        if (slot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return SlotStateKeyPrefix + slot + "." + field;
    }

    internal static void ProcessCompletedSlots(CookingStation station)
    {
        ZNetView? nview = GetNView(station);
        ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
        if (nview == null
            || zdo == null
            || !nview.IsOwner()
            || station.m_slots == null)
        {
            return;
        }

        for (int slot = 0; slot < station.m_slots.Length; slot++)
        {
            if (!TryReadPlan(station, slot, out CookingStationSlotPlan plan))
            {
                continue;
            }

            string currentItem = zdo.GetString("slot" + slot);
            if (string.IsNullOrEmpty(currentItem))
            {
                ClearPlan(station, slot);
                continue;
            }

            if (plan.AutoPop && !IsAutoPopPlanEligible(station, plan))
            {
                plan = plan.DisableAutoPop();
                WritePlan(station, slot, plan);
            }

            int status = zdo.GetInt("slotstatus" + slot);
            switch (status)
            {
                case StatusNotDone:
                    if (!string.Equals(
                            currentItem,
                            plan.ExpectedInput,
                            StringComparison.Ordinal))
                    {
                        PreservePrepaidFallbackOrClear(station, slot, plan);
                    }

                    break;
                case StatusDone:
                    if (string.Equals(
                            currentItem,
                            plan.ExpectedOutput,
                            StringComparison.Ordinal))
                    {
                        if (plan.AutoPop)
                        {
                            TryAutoPopSlot(station, slot, plan);
                        }
                    }
                    else
                    {
                        PreservePrepaidFallbackOrClear(station, slot, plan);
                    }

                    break;
                case StatusBurnt:
                {
                    bool isVanillaBurntItem = station.m_overCookedItem != null
                                              && string.Equals(
                                                  currentItem,
                                                  station.m_overCookedItem.gameObject.name,
                                                  StringComparison.Ordinal);
                    if (plan.AutoPop && isVanillaBurntItem)
                    {
                        // A long unload can cross vanilla's burnt check before
                        // its done check. Preserve the cooked result fixed at add.
                        TryAutoPopSlot(station, slot, plan);
                    }
                    else if (plan.CollectionExperiencePrepaid)
                    {
                        WritePlan(station, slot, plan.DisableAutoPop());
                    }
                    else
                    {
                        ClearPlan(station, slot);
                    }

                    break;
                }
                default:
                    PreservePrepaidFallbackOrClear(station, slot, plan);
                    break;
            }
        }
    }

    private static void ReceivePlan(
        CookingStation station,
        long sender,
        string itemName,
        int skillPermille,
        float autoRoll)
    {
        ZNetView? nview = GetNView(station);
        if (!ManagedAddPatchesReady
            || sender == 0L
            || string.IsNullOrEmpty(itemName)
            || skillPermille is < 0 or > 1000
            || float.IsNaN(autoRoll)
            || autoRoll < 0f
            || autoRoll > 1f
            || nview == null
            || !nview.IsValid()
            || !nview.IsOwner())
        {
            return;
        }

        CookingStation.ItemConversion? conversion = FindInputConversion(
            station,
            itemName);
        if (conversion?.m_to == null)
        {
            return;
        }

        float skillFactor = skillPermille / 1000f;
        bool autoPop = CookingStationAutoPopCore.ShouldAutoPop(
            skillFactor,
            autoRoll,
            CanAutoPopConversion(station, conversion));
        int bonusCount = CookingStationAutoPopCore.ClampBonusCount(
            CookingProductionBonusSystem.RollConfiguredBonusItems(
                conversion.m_to.gameObject.name,
                1,
                skillFactor,
                DietConfig.GetCookingBonusChanceAtMaxCookingPercent()));
        CookingStationSlotPlan plan = new(
            autoPop,
            autoPop,
            bonusCount,
            itemName,
            conversion.m_to.gameObject.name);

        if (!_ownerPendingPlans.TryGetValue(
                station,
                out OwnerPendingPlans pending))
        {
            pending = new OwnerPendingPlans();
            _ownerPendingPlans.Add(station, pending);
        }

        pending.BySender[sender] = new PendingOwnerPlan(
            itemName,
            plan,
            Time.unscaledTime);
    }

    private static bool TryTakePendingPlan(
        CookingStation station,
        long sender,
        string itemName,
        out CookingStationSlotPlan plan)
    {
        plan = default;
        if (!_ownerPendingPlans.TryGetValue(station, out OwnerPendingPlans pending)
            || !pending.BySender.TryGetValue(sender, out PendingOwnerPlan queued))
        {
            return false;
        }

        pending.BySender.Remove(sender);
        if (Time.unscaledTime - queued.StartedAt > PendingPlanTimeoutSeconds
            || !string.Equals(
                queued.ExpectedInput,
                itemName,
                StringComparison.Ordinal))
        {
            return false;
        }

        plan = queued.Plan;
        return true;
    }

    private static void DiscardPendingPlan(CookingStation station, long sender)
    {
        if (_ownerPendingPlans.TryGetValue(station, out OwnerPendingPlans pending))
        {
            pending.BySender.Remove(sender);
        }
    }

    private static bool WritePlan(
        CookingStation station,
        int slot,
        CookingStationSlotPlan plan)
    {
        ZDO? zdo = GetZdo(station);
        if (zdo == null
            || slot < 0
            || string.IsNullOrEmpty(plan.ExpectedInput)
            || string.IsNullOrEmpty(plan.ExpectedOutput))
        {
            return false;
        }

        try
        {
            zdo.Set(GetPlanKey(slot, "input"), plan.ExpectedInput);
            zdo.Set(GetPlanKey(slot, "output"), plan.ExpectedOutput);
            zdo.Set(GetPlanKey(slot, "auto"), plan.AutoPop ? 1 : 0);
            zdo.Set(
                GetPlanKey(slot, "prepaid"),
                plan.CollectionExperiencePrepaid ? 1 : 0);
            zdo.Set(
                GetPlanKey(slot, "bonus"),
                CookingStationAutoPopCore.ClampBonusCount(plan.BonusCount));
            zdo.Set(GetPlanKey(slot, "version"), SlotPlanVersion);
            bool persisted =
                zdo.GetInt(GetPlanKey(slot, "version")) == SlotPlanVersion
                && string.Equals(
                    zdo.GetString(GetPlanKey(slot, "input")),
                    plan.ExpectedInput,
                    StringComparison.Ordinal)
                && string.Equals(
                    zdo.GetString(GetPlanKey(slot, "output")),
                    plan.ExpectedOutput,
                    StringComparison.Ordinal);
            if (!persisted)
            {
                ClearPlan(station, slot);
            }

            return persisted;
        }
        catch (Exception exception)
        {
            LogRequestFailure(exception);
            ClearPlan(station, slot);
            return false;
        }
    }

    private static void PreservePrepaidFallbackOrClear(
        CookingStation station,
        int slot,
        CookingStationSlotPlan plan)
    {
        if (plan.CollectionExperiencePrepaid)
        {
            WritePlan(station, slot, plan.DisableAutoPop());
        }
        else
        {
            ClearPlan(station, slot);
        }
    }

    private static void TryAutoPopSlot(
        CookingStation station,
        int slot,
        CookingStationSlotPlan plan)
    {
        if (!IsAutoPopPlanEligible(station, plan))
        {
            WritePlan(station, slot, plan.DisableAutoPop());
            return;
        }

        ZNetView? nview = GetNView(station);
        ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
        GameObject? outputPrefab = ObjectDB.instance != null
            ? ObjectDB.instance.GetItemPrefab(plan.ExpectedOutput)
            : null;
        if (nview == null
            || zdo == null
            || !nview.IsOwner()
            || outputPrefab == null
            || outputPrefab.GetComponent<Rigidbody>() == null
            || station.m_slots == null
            || slot < 0
            || slot >= station.m_slots.Length
            || station.m_slots[slot] == null
            || SpawnItemMethod == null)
        {
            WritePlan(station, slot, plan.DisableAutoPop());
            LogAutoPopFailure(
                "the cooked prefab or required CookingStation spawn data is unavailable");
            return;
        }

        int outputCount =
            1 + CookingStationAutoPopCore.ClampBonusCount(plan.BonusCount);
        int spawnedCount = 0;
        Vector3 autoPoint = station.transform.position + station.transform.forward * 2f;
        try
        {
            for (; spawnedCount < outputCount; spawnedCount++)
            {
                SpawnItemMethod.Invoke(
                    station,
                    new object[] { plan.ExpectedOutput, slot, autoPoint, zdo.GetBool(ZDOVars.s_cheatedQueued + slot) });
            }
        }
        catch (Exception exception)
        {
            if (spawnedCount > 0)
            {
                ForceClearSlot(slot, nview, zdo);
                ClearPlan(station, slot);
            }
            else
            {
                WritePlan(station, slot, plan.DisableAutoPop());
            }

            LogAutoPopFailure(exception.ToString());
            return;
        }

        ForceClearSlot(slot, nview, zdo);
        ClearPlan(station, slot);
        BroadcastBonusEffect(station, plan.BonusCount);
    }

    private static void BroadcastBonusEffect(
        CookingStation station,
        int bonusCount)
    {
        bonusCount = CookingStationAutoPopCore.ClampBonusCount(bonusCount);
        if (bonusCount <= 0)
        {
            return;
        }

        try
        {
            ZNetView? nview = GetNView(station);
            if (nview == null || !nview.IsValid() || !nview.IsOwner())
            {
                return;
            }

            nview.InvokeRPC(
                ZNetView.Everybody,
                AutoPopBonusEffectRpc,
                bonusCount);
        }
        catch (Exception exception)
        {
            LogBonusEffectFailure(exception);
        }
    }

    private static void ReceiveBonusEffect(
        CookingStation station,
        long sender,
        int bonusCount)
    {
        ZNetView? nview = GetNView(station);
        ZDO? zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
        bonusCount = CookingStationAutoPopCore.ClampBonusCount(bonusCount);
        if (zdo == null || sender != zdo.GetOwner() || bonusCount <= 0)
        {
            return;
        }

        Vector3 effectPosition = station.transform.position;
        CookingProductionBonusSystem.ShowBonusEffect(
            effectPosition + Vector3.up,
            bonusCount);
    }

    private static void ForceClearSlot(int slot, ZNetView nview, ZDO zdo)
    {
        zdo.Set("slot" + slot, string.Empty);
        zdo.Set("slot" + slot, 0f);
        zdo.Set("slotstatus" + slot, StatusNotDone);
        zdo.Set(ZDOVars.s_cheatedQueued + slot, false);
        try
        {
            nview.InvokeRPC(
                ZNetView.Everybody,
                "RPC_SetSlotVisual",
                slot,
                string.Empty);
        }
        catch (Exception exception)
        {
            LogAutoPopFailure(exception.ToString());
        }
    }

    private static bool IsDoneItem(CookingStation station, string itemName)
    {
        if (station.m_overCookedItem != null
            && itemName == station.m_overCookedItem.gameObject.name)
        {
            return true;
        }

        if (station.m_conversion == null)
        {
            return false;
        }

        foreach (CookingStation.ItemConversion conversion in station.m_conversion)
        {
            if (conversion?.m_to != null
                && itemName == conversion.m_to.gameObject.name)
            {
                return true;
            }
        }

        return false;
    }

    private static CookingStation.ItemConversion? FindInputConversion(
        CookingStation station,
        string itemName)
    {
        if (station.m_conversion == null)
        {
            return null;
        }

        foreach (CookingStation.ItemConversion conversion in station.m_conversion)
        {
            if (conversion?.m_from != null
                && conversion.m_from.gameObject.name == itemName)
            {
                return conversion;
            }
        }

        return null;
    }

    internal static bool CanAutoPopConversion(
        CookingStation? station,
        CookingStation.ItemConversion? conversion)
    {
        if (station == null
            || conversion?.m_from == null
            || conversion.m_to == null
            || station.m_overCookedItem == null)
        {
            return false;
        }

        return CookingStationAutoPopCore.HasDistinctOvercookStage(
            conversion.m_cookTime,
            conversion.m_to.gameObject.name,
            station.m_overCookedItem.gameObject.name);
    }

    internal static bool IsAutoPopPlanEligible(
        CookingStation station,
        CookingStationSlotPlan plan)
    {
        CookingStation.ItemConversion? conversion = FindInputConversion(
            station,
            plan.ExpectedInput);
        return CanAutoPopConversion(station, conversion)
               && string.Equals(
                   conversion!.m_to.gameObject.name,
                   plan.ExpectedOutput,
                   StringComparison.Ordinal);
    }

    private static int FindFreeSlot(CookingStation station, ZDO? zdo)
    {
        if (zdo == null || station.m_slots == null)
        {
            return -1;
        }

        for (int slot = 0; slot < station.m_slots.Length; slot++)
        {
            if (string.IsNullOrEmpty(zdo.GetString("slot" + slot)))
            {
                return slot;
            }
        }

        return -1;
    }

    private static void InvokeVanillaAdd(
        ZNetView? nview,
        string vanillaRpcName,
        object[]? vanillaParameters)
    {
        if (nview != null && vanillaParameters != null)
        {
            nview.InvokeRPC(vanillaRpcName, vanillaParameters);
        }
    }

    private static ZNetView? GetNView(CookingStation station)
    {
        return station == null
            ? null
            : station.GetComponent<ZNetView>();
    }

    private static ZDO? GetZdo(CookingStation station)
    {
        ZNetView? nview = GetNView(station);
        return nview != null && nview.IsValid()
            ? nview.GetZDO()
            : null;
    }

    private static void LogRequestFailure(Exception exception)
    {
        if (_requestWarningLogged)
        {
            return;
        }

        _requestWarningLogged = true;
        FineDiningPlugin.Log.LogWarning(
            $"CookingStation slot planning failed; vanilla insertion remains active ({exception}).");
    }

    private static void LogAutoPopFailure(string detail)
    {
        if (_autoPopWarningLogged)
        {
            return;
        }

        _autoPopWarningLogged = true;
        FineDiningPlugin.Log.LogWarning(
            $"CookingStation auto-pop could not finish; the slot remains manually collectible where possible ({detail}).");
    }

    private static void LogBonusEffectFailure(Exception exception)
    {
        if (_bonusEffectWarningLogged)
        {
            return;
        }

        _bonusEffectWarningLogged = true;
        FineDiningPlugin.Log.LogWarning(
            $"CookingStation auto-eject bonus effects could not be broadcast; item output remains complete ({exception}).");
    }

    private sealed class RegistrationMarker
    {
    }

    private sealed class OwnerPendingPlans
    {
        internal Dictionary<long, PendingOwnerPlan> BySender { get; } = new();
    }

    private readonly struct PendingOwnerPlan
    {
        internal PendingOwnerPlan(
            string expectedInput,
            CookingStationSlotPlan plan,
            float startedAt)
        {
            ExpectedInput = expectedInput;
            Plan = plan;
            StartedAt = startedAt;
        }

        internal string ExpectedInput { get; }
        internal CookingStationSlotPlan Plan { get; }
        internal float StartedAt { get; }
    }
}

[HarmonyPatch(typeof(CookingStation), "Awake")]
internal static class CookingStationAutoPopRpcPatch
{
    [HarmonyPostfix]
    private static void Postfix(CookingStation __instance)
    {
        CookingStationAutoPopSystem.RegisterRpcs(__instance);
    }
}

[HarmonyPatch(typeof(CookingStation), "CookItem")]
[HarmonyPriority(Priority.Last)]
internal static class CookingStationPlannedAddPatch
{
    private static readonly MethodInfo InvokeRpcMethod = AccessTools.Method(
        typeof(ZNetView),
        nameof(ZNetView.InvokeRPC),
        new[] { typeof(string), typeof(object[]) })!;
    private static readonly MethodInfo RequestAddMethod = AccessTools.Method(
        typeof(CookingStationAutoPopSystem),
        nameof(CookingStationAutoPopSystem.RequestAdd))!;
    private static bool _patternWarningLogged;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = new(instructions);
        try
        {
            int addNameIndex = -1;
            int invokeIndex = -1;
            for (int index = 0; index < codes.Count; index++)
            {
                if (codes[index].opcode == OpCodes.Ldstr
                    && Equals(codes[index].operand, "RPC_AddItem"))
                {
                    addNameIndex = index;
                    break;
                }
            }

            // Match the 1.0.7 two-argument payload, including provenance. A broader
            // scan could redirect an unrelated RPC inserted by another transpiler.
            if (addNameIndex >= 0 && addNameIndex + 13 < codes.Count)
            {
                int i = addNameIndex;
                if (codes[i + 1].opcode == OpCodes.Ldc_I4_2 &&
                    codes[i + 2].opcode == OpCodes.Newarr && Equals(codes[i + 2].operand, typeof(object)) &&
                    codes[i + 3].opcode == OpCodes.Dup && codes[i + 4].opcode == OpCodes.Ldc_I4_0 &&
                    codes[i + 6].opcode == OpCodes.Stelem_Ref && codes[i + 7].opcode == OpCodes.Dup &&
                    codes[i + 8].opcode == OpCodes.Ldc_I4_1 && codes[i + 9].opcode == OpCodes.Ldarg_2 &&
                    codes[i + 10].opcode == OpCodes.Ldfld &&
                    Equals(codes[i + 10].operand, AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_cheated))) &&
                    codes[i + 11].opcode == OpCodes.Box && Equals(codes[i + 11].operand, typeof(bool)) &&
                    codes[i + 12].opcode == OpCodes.Stelem_Ref && codes[i + 13].opcode == OpCodes.Callvirt &&
                    Equals(codes[i + 13].operand, InvokeRpcMethod))
                    invokeIndex = i + 13;
            }

            if (addNameIndex < 0 || invokeIndex < 0)
            {
                LogPatternFailure();
                return codes;
            }

            CodeInstruction loadUser = new(OpCodes.Ldarg_1);
            loadUser.labels.AddRange(codes[invokeIndex].labels);
            codes[invokeIndex].labels.Clear();
            loadUser.blocks.AddRange(codes[invokeIndex].blocks);
            codes[invokeIndex].blocks.Clear();
            codes.InsertRange(
                invokeIndex,
                new[]
                {
                    loadUser,
                    new CodeInstruction(OpCodes.Ldarg_0)
                });
            codes[invokeIndex + 2].opcode = OpCodes.Call;
            codes[invokeIndex + 2].operand = RequestAddMethod;
            CookingStationAutoPopSystem.MarkRequestPatchReady();
        }
        catch (Exception exception)
        {
            LogPatternFailure(exception);
        }

        return codes;
    }

    private static void LogPatternFailure(Exception? exception = null)
    {
        if (_patternWarningLogged)
        {
            return;
        }

        _patternWarningLogged = true;
        string detail = exception == null ? string.Empty : $" ({exception})";
        FineDiningPlugin.Log.LogWarning(
            "CookingStation planned-add patch was not applied; vanilla insertion remains active"
            + detail
            + ".");
    }
}

[HarmonyPatch(typeof(CookingStation), "RPC_AddItem")]
[HarmonyPriority(Priority.Last)]
internal static class CookingStationSlotPlanPatch
{
    [HarmonyPrefix]
    private static void Prefix(
        CookingStation __instance,
        long sender,
        string itemName)
    {
        CookingStationAutoPopSystem.PrepareAdd(
            __instance,
            sender,
            itemName);
    }
}

[HarmonyPatch(typeof(CookingStation), "OnInteract")]
[HarmonyPriority(Priority.First)]
internal static class CookingStationPlannedExperiencePatch
{
    private static readonly MethodInfo RaiseSkillMethod = AccessTools.Method(
        typeof(Character),
        nameof(Character.RaiseSkill),
        new[] { typeof(Skills.SkillType), typeof(float) })!;
    private static readonly MethodInfo RaiseAddExperienceMethod = AccessTools.Method(
        typeof(CookingStationAutoPopSystem),
        nameof(CookingStationAutoPopSystem.RaiseAddExperienceOrDefer))!;
    private static readonly MethodInfo RaiseCollectionExperienceMethod = AccessTools.Method(
        typeof(CookingStationAutoPopSystem),
        nameof(CookingStationAutoPopSystem.RaiseCollectionExperience))!;
    private static bool _patternWarningLogged;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = new(instructions);
        try
        {
            int addExperienceIndex = FindRaiseSkillAmount(
                codes,
                CookingStationAutoPopCore.CookingExperienceOnAdd);
            int collectExperienceIndex = FindRaiseSkillAmount(
                codes,
                CookingStationAutoPopCore.CookingExperienceOnCollect);
            if (addExperienceIndex < 0 || collectExperienceIndex < 0)
            {
                LogPatternFailure();
                return codes;
            }

            codes[addExperienceIndex + 1].opcode = OpCodes.Call;
            codes[addExperienceIndex + 1].operand = RaiseAddExperienceMethod;

            int collectCallIndex = collectExperienceIndex + 1;
            CodeInstruction loadStation = new(OpCodes.Ldarg_0);
            loadStation.labels.AddRange(codes[collectCallIndex].labels);
            codes[collectCallIndex].labels.Clear();
            loadStation.blocks.AddRange(codes[collectCallIndex].blocks);
            codes[collectCallIndex].blocks.Clear();
            codes.Insert(collectCallIndex, loadStation);
            codes[collectCallIndex + 1].opcode = OpCodes.Call;
            codes[collectCallIndex + 1].operand = RaiseCollectionExperienceMethod;
            CookingStationAutoPopSystem.MarkExperiencePatchReady();
        }
        catch (Exception exception)
        {
            LogPatternFailure(exception);
        }

        return codes;
    }

    private static int FindRaiseSkillAmount(
        List<CodeInstruction> codes,
        float amount)
    {
        for (int index = 0; index < codes.Count - 1; index++)
        {
            if (codes[index].opcode == OpCodes.Ldc_R4
                && codes[index].operand is float value
                && Math.Abs(value - amount) < 0.0001f
                && (codes[index + 1].opcode == OpCodes.Call
                    || codes[index + 1].opcode == OpCodes.Callvirt)
                && Equals(codes[index + 1].operand, RaiseSkillMethod))
            {
                return index;
            }
        }

        return -1;
    }

    private static void LogPatternFailure(Exception? exception = null)
    {
        if (_patternWarningLogged)
        {
            return;
        }

        _patternWarningLogged = true;
        string detail = exception == null ? string.Empty : $" ({exception})";
        FineDiningPlugin.Log.LogWarning(
            "CookingStation planned experience patch was not applied; vanilla experience remains active"
            + detail
            + ".");
    }
}

[HarmonyPatch(typeof(CookingStation), "UpdateCooking")]
[HarmonyPriority(Priority.Last)]
internal static class CookingStationAutoPopCompletionPatch
{
    [HarmonyPostfix]
    private static void Postfix(CookingStation __instance)
    {
        CookingStationAutoPopSystem.ProcessCompletedSlots(__instance);
    }
}
