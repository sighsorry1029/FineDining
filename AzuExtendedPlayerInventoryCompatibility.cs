using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace FineDining;

internal static class AzuExtendedPlayerInventoryCompatibility
{
    internal const string PluginGuid = "Azumatt.AzuExtendedPlayerInventory";

    private const string RecoveryPatchTypeName =
        "AzuEPI.Game.Patches.InventoryPatches+Load_TrackAndFixHiddenItems_Patch";

    private static readonly FieldInfo StackField =
        AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_stack));

    private static readonly MethodInfo ComposeMethod = AccessTools.Method(
        typeof(AzuExtendedPlayerInventoryCompatibility),
        nameof(ComposeDirectRecoveryMergeExpirySafe));

    private static int _matchedMergeSites;
    private static bool _runtimeFailureLogged;

    internal static void TryInstall(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out BepInEx.PluginInfo pluginInfo))
        {
            return;
        }

        MethodInfo? target = null;
        bool patchAttempted = false;
        try
        {
            target = ResolveTargetMethod(pluginInfo);
            if (target == null)
            {
                FineDiningPlugin.Log.LogWarning(
                    "AzuExtendedPlayerInventory is installed, but its hidden-slot recovery method no longer " +
                    "has the expected signature. Direct recovery merges will not compose spoilage timers.");
                return;
            }

            Patches? existingPatches = Harmony.GetPatchInfo(target);
            if (existingPatches?.Transpilers.Any(patch =>
                    string.Equals(patch.owner, harmony.Id, StringComparison.Ordinal) &&
                    Equals(patch.PatchMethod, AccessTools.Method(
                        typeof(AzuExtendedPlayerInventoryCompatibility),
                        nameof(Transpiler)))) == true)
            {
                return;
            }

            _matchedMergeSites = 0;
            HarmonyMethod transpiler = new(AccessTools.Method(
                typeof(AzuExtendedPlayerInventoryCompatibility),
                nameof(Transpiler)));
            patchAttempted = true;
            harmony.Patch(target, transpiler: transpiler);

            if (_matchedMergeSites != 1)
            {
                harmony.Unpatch(target, HarmonyPatchType.Transpiler, harmony.Id);
                FineDiningPlugin.Log.LogWarning(
                    "AzuExtendedPlayerInventory is installed, but its direct recovery merge no longer " +
                    "matches the verified pattern. The compatibility patch was left disabled so inventory " +
                    "loading remains unchanged.");
                return;
            }

            FineDiningPlugin.Log.LogInfo(
                "Enabled AzuExtendedPlayerInventory " + pluginInfo.Metadata.Version +
                " hidden-slot merge timer compatibility.");
        }
        catch (Exception exception)
        {
            if (patchAttempted && target != null)
            {
                try
                {
                    harmony.Unpatch(target, HarmonyPatchType.Transpiler, harmony.Id);
                }
                catch
                {
                    // Preserve the original installation error below. UnpatchSelf
                    // remains the final cleanup path when the plugin is destroyed.
                }
            }

            FineDiningPlugin.Log.LogWarning(
                "Could not enable AzuExtendedPlayerInventory merge timer compatibility. " + exception);
        }
    }

    internal static void Shutdown()
    {
        _matchedMergeSites = 0;
        _runtimeFailureLogged = false;
    }

    private static MethodInfo? ResolveTargetMethod(BepInEx.PluginInfo pluginInfo)
    {
        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        Type? patchType = assembly?.GetType(RecoveryPatchTypeName, throwOnError: false);
        MethodInfo? method = patchType?.GetMethod(
            "Postfix",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null || method.ReturnType != typeof(void))
        {
            return null;
        }

        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == typeof(Inventory)
            ? method
            : null;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        List<DirectMergeSite> matches = new();
        for (int storeIndex = 5; storeIndex < code.Count; storeIndex++)
        {
            if (TryMatchDirectMerge(code, storeIndex, out DirectMergeSite site))
            {
                matches.Add(site);
            }
        }

        _matchedMergeSites = matches.Count;
        if (matches.Count != 1 || ComposeMethod == null)
        {
            if (ComposeMethod == null)
            {
                _matchedMergeSites = 0;
            }

            return code;
        }

        DirectMergeSite match = matches[0];
        List<CodeInstruction> injected = new()
        {
            CloneWithoutFlowMetadata(code[match.TargetLoadIndex])
        };
        for (int index = 0; index < match.SourceLoadCount; index++)
        {
            injected.Add(CloneWithoutFlowMetadata(code[match.SourceLoadIndex + index]));
        }

        injected.Add(CloneWithoutFlowMetadata(code[match.AmountLoadIndex]));
        injected.Add(new CodeInstruction(OpCodes.Call, ComposeMethod));

        // Run only after both stack writes have completed and before AzuEPI can
        // remove an emptied source. Labels on the following instruction stay in
        // place so an unrelated branch cannot enter this merge-only hook.
        code.InsertRange(match.InsertIndex, injected);
        return code;
    }

    private static bool TryMatchDirectMerge(
        IReadOnlyList<CodeInstruction> code,
        int targetStoreIndex,
        out DirectMergeSite site)
    {
        site = default;
        int targetLoadIndex = targetStoreIndex - 5;
        if (!IsLocalLoad(code[targetLoadIndex]) ||
            code[targetLoadIndex + 1].opcode != OpCodes.Dup ||
            !IsFieldLoad(code[targetLoadIndex + 2], StackField) ||
            !IsLocalLoad(code[targetLoadIndex + 3]) ||
            code[targetLoadIndex + 4].opcode != OpCodes.Add ||
            !IsFieldStore(code[targetStoreIndex], StackField))
        {
            return false;
        }

        int sourceLoadIndex = targetStoreIndex + 1;
        int sourceLoadCount;
        int sourceDupIndex;
        if (sourceLoadIndex + 5 < code.Count &&
            IsLocalLoad(code[sourceLoadIndex]) &&
            code[sourceLoadIndex + 1].opcode == OpCodes.Dup)
        {
            sourceLoadCount = 1;
            sourceDupIndex = sourceLoadIndex + 1;
        }
        else if (sourceLoadIndex + 6 < code.Count &&
                 IsLocalLoad(code[sourceLoadIndex]) &&
                 IsItemDataFieldLoad(code[sourceLoadIndex + 1]) &&
                 code[sourceLoadIndex + 2].opcode == OpCodes.Dup)
        {
            sourceLoadCount = 2;
            sourceDupIndex = sourceLoadIndex + 2;
        }
        else
        {
            return false;
        }

        int sourceStackIndex = sourceDupIndex + 1;
        int sourceAmountIndex = sourceDupIndex + 2;
        int sourceSubtractIndex = sourceDupIndex + 3;
        int sourceStoreIndex = sourceDupIndex + 4;
        if (!IsFieldLoad(code[sourceStackIndex], StackField) ||
            !IsLocalLoad(code[sourceAmountIndex]) ||
            !AreSameLocalLoads(code[targetLoadIndex + 3], code[sourceAmountIndex]) ||
            code[sourceSubtractIndex].opcode != OpCodes.Sub ||
            !IsFieldStore(code[sourceStoreIndex], StackField))
        {
            return false;
        }

        site = new DirectMergeSite(
            targetLoadIndex,
            targetLoadIndex + 3,
            sourceLoadIndex,
            sourceLoadCount,
            sourceStoreIndex + 1);
        return true;
    }

    private static void ComposeDirectRecoveryMergeExpirySafe(
        ItemDrop.ItemData? target,
        ItemDrop.ItemData? source,
        int movedAmount)
    {
        try
        {
            DecayRuntime.ComposeDirectRecoveryMergeExpiry(target, source, movedAmount);
        }
        catch (Exception exception)
        {
            if (_runtimeFailureLogged)
            {
                return;
            }

            _runtimeFailureLogged = true;
            FineDiningPlugin.Log.LogWarning(
                "AzuExtendedPlayerInventory merged a recovered stack, but FineDining could not compose " +
                "its expiry timer. The original inventory merge will continue unchanged. " + exception);
        }
    }

    private static CodeInstruction CloneWithoutFlowMetadata(CodeInstruction instruction)
    {
        CodeInstruction clone = new(instruction);
        clone.labels.Clear();
        clone.blocks.Clear();
        return clone;
    }

    private static bool IsItemDataFieldLoad(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldfld &&
               instruction.operand is FieldInfo field &&
               field.FieldType == typeof(ItemDrop.ItemData);
    }

    private static bool IsFieldLoad(CodeInstruction instruction, FieldInfo field)
    {
        return instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, field);
    }

    private static bool IsFieldStore(CodeInstruction instruction, FieldInfo field)
    {
        return instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, field);
    }

    private static bool IsLocalLoad(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldloc ||
               instruction.opcode == OpCodes.Ldloc_S ||
               instruction.opcode == OpCodes.Ldloc_0 ||
               instruction.opcode == OpCodes.Ldloc_1 ||
               instruction.opcode == OpCodes.Ldloc_2 ||
               instruction.opcode == OpCodes.Ldloc_3;
    }

    private static bool AreSameLocalLoads(CodeInstruction first, CodeInstruction second)
    {
        if (first.opcode != second.opcode)
        {
            return false;
        }

        return first.opcode == OpCodes.Ldloc_0 ||
               first.opcode == OpCodes.Ldloc_1 ||
               first.opcode == OpCodes.Ldloc_2 ||
               first.opcode == OpCodes.Ldloc_3 ||
               Equals(first.operand, second.operand);
    }

    private readonly struct DirectMergeSite
    {
        internal DirectMergeSite(
            int targetLoadIndex,
            int amountLoadIndex,
            int sourceLoadIndex,
            int sourceLoadCount,
            int insertIndex)
        {
            TargetLoadIndex = targetLoadIndex;
            AmountLoadIndex = amountLoadIndex;
            SourceLoadIndex = sourceLoadIndex;
            SourceLoadCount = sourceLoadCount;
            InsertIndex = insertIndex;
        }

        internal int TargetLoadIndex { get; }
        internal int AmountLoadIndex { get; }
        internal int SourceLoadIndex { get; }
        internal int SourceLoadCount { get; }
        internal int InsertIndex { get; }
    }
}
