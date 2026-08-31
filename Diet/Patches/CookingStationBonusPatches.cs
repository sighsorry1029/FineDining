using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace FineDining;

internal static class CookingStationBonusSystem
{
    private static bool _outputLookupWarningLogged;

    internal static float ApplyConfiguredChance(
        float calculatedChance,
        CookingStation station,
        Humanoid user)
    {
        if (CookingStationAutoPopSystem.TryGetFirstDonePlan(
                station,
                out _,
                out CookingStationSlotPlan plan,
                out bool outputMatches))
        {
            return outputMatches && plan.BonusCount > 0 ? 2f : 0f;
        }

        if (IsFirstDoneOutputExcluded(station))
        {
            return 0f;
        }

        return user is Player player && (UnityObject)(object)player != null
            ? CookingProductionBonusSystem.CalculateConfiguredCookingChance(
                player.GetSkillFactor(Skills.SkillType.Cooking))
            : calculatedChance;
    }

    internal static int GetPlannedBonusAmount(
        InventoryGui gui,
        CookingStation station)
    {
        if (CookingStationAutoPopSystem.TryGetFirstDonePlan(
                station,
                out _,
                out CookingStationSlotPlan plan,
                out bool outputMatches))
        {
            return outputMatches ? plan.BonusCount : 0;
        }

        return gui.m_craftBonusAmount;
    }

    internal static bool IsFirstDoneOutputExcluded(CookingStation station)
    {
        try
        {
            if (!CookingStationAutoPopSystem.TryGetFirstDoneSlot(
                    station,
                    out _,
                    out string prefabName))
            {
                return false;
            }

            if ((UnityObject)(object)station.m_overCookedItem != null
                && prefabName == station.m_overCookedItem.gameObject.name)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(
                       DietConfig.GetCookingBonusExcludedOutputPrefabs())
                   && CookingProductionBonusSystem.IsExcludedOutputPrefab(
                       prefabName);
        }
        catch (Exception exception)
        {
            if (!_outputLookupWarningLogged)
            {
                _outputLookupWarningLogged = true;
                FineDiningPlugin.Log.LogWarning(
                    $"Could not resolve a CookingStation output for the exclusion list; "
                    + $"the configured chance is still applied ({exception.Message}).");
            }

            return false;
        }
    }
}

[HarmonyPatch(typeof(CookingStation), "OnInteract")]
[HarmonyPriority(Priority.Last)]
internal static class CookingStationBonusChancePatch
{
    private static readonly MethodInfo RandomValueGetter = AccessTools.PropertyGetter(
        typeof(UnityEngine.Random),
        nameof(UnityEngine.Random.value))!;

    private static readonly FieldInfo CraftBonusChanceField = AccessTools.Field(
        typeof(InventoryGui),
        nameof(InventoryGui.m_craftBonusChance))!;

    private static readonly FieldInfo CraftBonusAmountField = AccessTools.Field(
        typeof(InventoryGui),
        nameof(InventoryGui.m_craftBonusAmount))!;

    private static readonly MethodInfo ApplyConfiguredChanceMethod = AccessTools.Method(
        typeof(CookingStationBonusSystem),
        nameof(CookingStationBonusSystem.ApplyConfiguredChance))!;

    private static readonly MethodInfo GetPlannedBonusAmountMethod = AccessTools.Method(
        typeof(CookingStationBonusSystem),
        nameof(CookingStationBonusSystem.GetPlannedBonusAmount))!;

    private static bool _patternWarningLogged;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = new(instructions);
        try
        {
            int randomIndex = FindCall(codes, RandomValueGetter, 0);
            int chanceIndex = FindFieldLoad(codes, CraftBonusChanceField, randomIndex + 1);
            int multiplyIndex = chanceIndex + 1;
            int branchIndex = multiplyIndex + 1;
            if (randomIndex < 0
                || chanceIndex <= randomIndex
                || branchIndex >= codes.Count
                || codes[multiplyIndex].opcode != OpCodes.Mul
                || !IsBranchGreaterOrEqualUnsigned(codes[branchIndex].opcode))
            {
                LogPatternFailure();
                return codes;
            }

            CodeInstruction loadStation = new(OpCodes.Ldarg_0);
            loadStation.labels.AddRange(codes[branchIndex].labels);
            codes[branchIndex].labels.Clear();
            loadStation.blocks.AddRange(codes[branchIndex].blocks);
            codes[branchIndex].blocks.Clear();

            codes.InsertRange(
                branchIndex,
                new[]
                {
                    loadStation,
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, ApplyConfiguredChanceMethod)
                });
            ReplaceBonusAmountLoads(codes);
        }
        catch (Exception exception)
        {
            LogPatternFailure(exception);
        }

        return codes;
    }

    private static int FindCall(
        List<CodeInstruction> codes,
        MethodInfo method,
        int startIndex)
    {
        for (int index = Math.Max(0, startIndex); index < codes.Count; index++)
        {
            if ((codes[index].opcode == OpCodes.Call || codes[index].opcode == OpCodes.Callvirt)
                && Equals(codes[index].operand, method))
            {
                return index;
            }
        }

        return -1;
    }

    private static void ReplaceBonusAmountLoads(List<CodeInstruction> codes)
    {
        for (int index = 0; index < codes.Count; index++)
        {
            if (codes[index].opcode != OpCodes.Ldfld
                || !Equals(codes[index].operand, CraftBonusAmountField))
            {
                continue;
            }

            CodeInstruction loadStation = new(OpCodes.Ldarg_0);
            loadStation.labels.AddRange(codes[index].labels);
            codes[index].labels.Clear();
            loadStation.blocks.AddRange(codes[index].blocks);
            codes[index].blocks.Clear();
            codes.Insert(index, loadStation);
            codes[index + 1].opcode = OpCodes.Call;
            codes[index + 1].operand = GetPlannedBonusAmountMethod;
            index++;
        }
    }

    private static int FindFieldLoad(
        List<CodeInstruction> codes,
        FieldInfo field,
        int startIndex)
    {
        for (int index = Math.Max(0, startIndex); index < codes.Count; index++)
        {
            if (codes[index].opcode == OpCodes.Ldfld
                && Equals(codes[index].operand, field))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsBranchGreaterOrEqualUnsigned(OpCode opcode)
    {
        return opcode == OpCodes.Bge_Un || opcode == OpCodes.Bge_Un_S;
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
            "CookingStation bonus settings were not applied; vanilla behavior remains active"
            + detail
            + ".");
    }
}

[HarmonyPatch(typeof(CookingStation), "RPC_RemoveDoneItem")]
[HarmonyPriority(Priority.Last)]
internal static class CookingStationExcludedOutputGuardPatch
{
    [HarmonyPrefix]
    private static void Prefix(
        CookingStation __instance,
        ref int amount,
        out int __state)
    {
        __state = -1;
        if (CookingStationAutoPopSystem.TryGetFirstDonePlan(
                __instance,
                out int slot,
                out CookingStationSlotPlan plan,
                out bool outputMatches))
        {
            amount = outputMatches ? 1 + plan.BonusCount : 1;
            __state = slot;
        }
        else if (amount > 1
                 && CookingStationBonusSystem.IsFirstDoneOutputExcluded(__instance))
        {
            amount = 1;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(
        CookingStation __instance,
        int __state,
        bool __runOriginal)
    {
        if (__runOriginal
            && __state >= 0
            && CookingStationAutoPopSystem.IsSlotEmpty(__instance, __state))
        {
            CookingStationAutoPopSystem.ClearPlan(__instance, __state);
        }
    }
}
