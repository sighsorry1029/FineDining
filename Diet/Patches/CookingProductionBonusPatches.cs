using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityObject = UnityEngine.Object;
using UnityRandom = UnityEngine.Random;

namespace FineDining;

internal static class CookingProductionBonusCore
{
    internal static float CalculatePerItemChance(
        float skillFactor,
        float vanillaBonusChance,
        float bonusOutputPercent)
    {
        double skill = NormalizeNonNegative(skillFactor);
        double chance = NormalizeNonNegative(vanillaBonusChance);
        double outputRatio = ClampPercent(bonusOutputPercent) / 100d;
        if (skill <= 0d || chance <= 0d || outputRatio <= 0d)
        {
            return 0f;
        }

        return (float)ClampProbability(skill * chance * outputRatio);
    }

    internal static int RollBonusItems(
        int baseItemCount,
        float itemChance,
        Func<float> nextRandomValue)
    {
        if (baseItemCount <= 0)
        {
            return 0;
        }

        int maximumBonus = int.MaxValue - baseItemCount;
        if (maximumBonus <= 0)
        {
            return 0;
        }

        double chance = ClampProbability(itemChance);
        if (chance <= 0d)
        {
            return 0;
        }

        if (chance >= 1d)
        {
            return Math.Min(baseItemCount, maximumBonus);
        }

        if (nextRandomValue == null)
        {
            throw new ArgumentNullException(nameof(nextRandomValue));
        }

        int bonus = 0;
        for (int index = 0; index < baseItemCount && bonus < maximumBonus; index++)
        {
            if (nextRandomValue() < chance)
            {
                bonus++;
            }
        }

        return bonus;
    }

    private static double NormalizeNonNegative(float value)
    {
        return float.IsNaN(value) || value <= 0f ? 0d : value;
    }

    private static double ClampProbability(double value)
    {
        if (double.IsNaN(value) || value <= 0d)
        {
            return 0d;
        }

        return double.IsPositiveInfinity(value) || value >= 1d ? 1d : value;
    }

    private static double ClampPercent(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0d;
        }

        return float.IsPositiveInfinity(value) || value >= 100f ? 100d : value;
    }
}

internal static class CookingProductionBonusSystem
{
    internal const int UseVanillaBonus = -1;
    internal const float VanillaBonusChance = 0.25f;

    private const int MaximumIndependentRolls = 10_000;
    private static readonly char[] ExclusionSeparators = { ',', ';', '\r', '\n' };
    private static readonly AccessTools.FieldRef<InventoryGui, Recipe> CraftRecipeField =
        AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe");
    private static bool _largeOutputWarningLogged;

    internal static int CalculateCookingSkillBonusOrUseVanilla(
        InventoryGui gui,
        CraftingStation station,
        int baseItemCount,
        float skillFactor)
    {
        if ((UnityObject)(object)station == null
            || station.m_craftingSkill != Skills.SkillType.Cooking)
        {
            return UseVanillaBonus;
        }

        if ((UnityObject)(object)gui == null
            || baseItemCount <= 0)
        {
            return 0;
        }

        Recipe recipe = CraftRecipeField(gui);
        if ((UnityObject)(object)recipe == null
            || (UnityObject)(object)recipe.m_item == null
            || recipe.m_item.m_itemData.m_shared.m_maxStackSize <= 1)
        {
            return 0;
        }

        string outputPrefabName = recipe.m_item.gameObject.name;
        if (IsExcludedOutputPrefab(outputPrefabName))
        {
            return 0;
        }

        float itemChance = CookingProductionBonusCore.CalculatePerItemChance(
            skillFactor,
            gui.m_craftBonusChance,
            DietConfig.GetCookingBonusOutputPercent());

        if (itemChance <= 0f)
        {
            return 0;
        }

        if (baseItemCount > MaximumIndependentRolls && itemChance < 1f)
        {
            if (!_largeOutputWarningLogged)
            {
                _largeOutputWarningLogged = true;
                FineDiningPlugin.Log.LogWarning(
                    $"A Cooking recipe produced more than {MaximumIndependentRolls} base items; "
                    + "using Valheim's production-bonus calculation to avoid a long main-thread roll loop.");
            }

            return UseVanillaBonus;
        }

        return CookingProductionBonusCore.RollBonusItems(
            baseItemCount,
            itemChance,
            NextRandomValue);
    }

    internal static float ApplyConfiguredOutputPercent(float calculatedChance)
    {
        float outputPercent = DietConfig.GetCookingBonusOutputPercent();
        if (float.IsNaN(outputPercent) || outputPercent <= 0f)
        {
            return 0f;
        }

        if (outputPercent >= 100f)
        {
            return calculatedChance;
        }

        return CookingProductionBonusCore.CalculatePerItemChance(
            1f,
            calculatedChance,
            outputPercent);
    }

    internal static int RollConfiguredBonusItems(
        string outputPrefabName,
        int baseItemCount,
        float skillFactor,
        float vanillaBonusChance)
    {
        if (baseItemCount <= 0 || IsExcludedOutputPrefab(outputPrefabName))
        {
            return 0;
        }

        float itemChance = CookingProductionBonusCore.CalculatePerItemChance(
            skillFactor,
            vanillaBonusChance,
            DietConfig.GetCookingBonusOutputPercent());

        return CookingProductionBonusCore.RollBonusItems(
            baseItemCount,
            itemChance,
            NextRandomValue);
    }

    internal static bool IsExcludedOutputPrefab(string prefabName)
    {
        return MatchesExcludedOutputPrefab(
            prefabName,
            DietConfig.GetCookingBonusExcludedOutputPrefabs());
    }

    private static float NextRandomValue()
    {
        return UnityRandom.value;
    }

    private static bool MatchesExcludedOutputPrefab(string prefabName, string patterns)
    {
        if (string.IsNullOrEmpty(prefabName) || string.IsNullOrWhiteSpace(patterns))
        {
            return false;
        }

        foreach (string entry in patterns.Split(ExclusionSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string pattern = entry.Trim();
            if (pattern.Length > 0 && WildcardMatches(prefabName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WildcardMatches(string value, string pattern)
    {
        int valueIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int retryValueIndex = -1;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && pattern[patternIndex] != '*'
                && CharactersEqual(value[valueIndex], pattern[patternIndex]))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
                continue;
            }

            if (starIndex < 0)
            {
                return false;
            }

            patternIndex = starIndex + 1;
            valueIndex = ++retryValueIndex;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static bool CharactersEqual(char left, char right)
    {
        return left == right || char.ToUpperInvariant(left) == char.ToUpperInvariant(right);
    }
}

[HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
[HarmonyPriority(Priority.Last)]
[HarmonyAfter("sighsorry.RepairRequiresMaterials")]
internal static class InventoryGuiCookingProductionBonusPatch
{
    private static readonly MethodInfo GetAmountMethod = AccessTools.Method(
        typeof(Recipe),
        nameof(Recipe.GetAmount),
        new[]
        {
            typeof(int),
            typeof(int).MakeByRefType(),
            typeof(ItemDrop.ItemData).MakeByRefType(),
            typeof(int)
        })!;

    private static readonly MethodInfo GetCurrentCraftingStationMethod = AccessTools.Method(
        typeof(Player),
        nameof(Player.GetCurrentCraftingStation))!;

    private static readonly MethodInfo BonusHelperMethod = AccessTools.Method(
        typeof(CookingProductionBonusSystem),
        nameof(CookingProductionBonusSystem.CalculateCookingSkillBonusOrUseVanilla))!;

    private static readonly MethodInfo RandomValueGetter = AccessTools.PropertyGetter(
        typeof(UnityRandom),
        nameof(UnityRandom.value))!;

    private static readonly FieldInfo CraftUpgradeItemField = AccessTools.Field(
        typeof(InventoryGui),
        "m_craftUpgradeItem")!;

    private static readonly FieldInfo CraftBonusChanceField = AccessTools.Field(
        typeof(InventoryGui),
        nameof(InventoryGui.m_craftBonusChance))!;

    private static readonly FieldInfo CraftBonusAmountField = AccessTools.Field(
        typeof(InventoryGui),
        nameof(InventoryGui.m_craftBonusAmount))!;

    private static bool _patternWarningLogged;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        List<CodeInstruction> codes = new(instructions);
        try
        {
            if (!TryInject(codes, generator, out string failure))
            {
                LogPatternFailure(failure);
            }
        }
        catch (Exception exception)
        {
            LogPatternFailure($"unexpected transpiler error: {exception}");
        }

        return codes;
    }

    private static bool TryInject(
        List<CodeInstruction> codes,
        ILGenerator generator,
        out string failure)
    {
        failure = string.Empty;

        int getAmountCallIndex = FindCall(codes, GetAmountMethod, 0);
        int stationCallIndex = FindCall(codes, GetCurrentCraftingStationMethod, 0);
        if (getAmountCallIndex < 0 || stationCallIndex < 0 || getAmountCallIndex >= stationCallIndex)
        {
            failure = "could not locate Recipe.GetAmount and GetCurrentCraftingStation anchors";
            return false;
        }

        if (getAmountCallIndex + 1 >= codes.Count
            || !TryGetStoredLocal(codes[getAmountCallIndex + 1], out int resultLocal))
        {
            failure = "could not resolve the pre-bonus result amount local";
            return false;
        }

        int stationStoreIndex = stationCallIndex + 1;
        int bonusZeroIndex = stationCallIndex + 2;
        int bonusStoreIndex = stationCallIndex + 3;
        if (bonusStoreIndex >= codes.Count
            || !TryGetStoredLocal(codes[stationStoreIndex], out int stationLocal)
            || !IsLoadConstantZero(codes[bonusZeroIndex])
            || !TryGetStoredLocal(codes[bonusStoreIndex], out int bonusLocal))
        {
            failure = "the vanilla crafting-bonus local initialization changed";
            return false;
        }

        int randomValueIndex = FindCall(codes, RandomValueGetter, bonusStoreIndex + 1);
        int bonusChanceIndex = FindFieldLoad(codes, CraftBonusChanceField, randomValueIndex + 1);
        int bonusAmountIndex = FindFieldLoad(codes, CraftBonusAmountField, bonusChanceIndex + 1);
        if (randomValueIndex < 3
            || bonusChanceIndex <= randomValueIndex
            || bonusAmountIndex <= bonusChanceIndex)
        {
            failure = "could not locate the ordered vanilla production-bonus roll";
            return false;
        }

        int loopInitializationIndex = randomValueIndex - 3;
        if (loopInitializationIndex <= 0
            || !TryGetStoredLocal(codes[loopInitializationIndex - 1], out int skillFactorLocal)
            || !IsLoadConstantZero(codes[loopInitializationIndex])
            || !TryGetStoredLocal(codes[loopInitializationIndex + 1], out int loopLocal)
            || !TryGetBranchLabel(codes[loopInitializationIndex + 2], IsUnconditionalBranch, out Label loopConditionLabel))
        {
            failure = "could not locate the one-time vanilla bonus-loop initialization";
            return false;
        }

        int bonusLoadIndex = bonusAmountIndex - 2;
        int bonusStoreAfterIncrementIndex = bonusAmountIndex + 2;
        int resultLoadIndex = bonusAmountIndex + 3;
        int displayedBonusLoadIndex = bonusAmountIndex + 4;
        int resultStoreIndex = bonusAmountIndex + 6;
        if (bonusLoadIndex <= randomValueIndex
            || resultStoreIndex >= codes.Count
            || !LoadsLocal(codes[bonusLoadIndex], bonusLocal)
            || codes[bonusAmountIndex - 1].opcode != OpCodes.Ldarg_0
            || codes[bonusAmountIndex + 1].opcode != OpCodes.Add
            || !StoresLocal(codes[bonusStoreAfterIncrementIndex], bonusLocal)
            || !LoadsLocal(codes[resultLoadIndex], resultLocal)
            || !LoadsLocal(codes[displayedBonusLoadIndex], bonusLocal)
            || codes[bonusAmountIndex + 5].opcode != OpCodes.Add
            || !StoresLocal(codes[resultStoreIndex], resultLocal))
        {
            failure = "the vanilla production-bonus accumulation pattern changed";
            return false;
        }

        int loopConditionIndex = FindInstructionWithLabel(
            codes,
            loopConditionLabel,
            resultStoreIndex + 1);
        if (loopConditionIndex < 0
            || loopConditionIndex + 2 >= codes.Count
            || !LoadsLocal(codes[loopConditionIndex], loopLocal)
            || !TryGetBranchLabel(codes[loopConditionIndex + 2], IsBranchLess, out Label randomLoopLabel)
            || !codes[randomValueIndex].labels.Contains(randomLoopLabel))
        {
            failure = "the vanilla production-bonus loop boundary changed";
            return false;
        }

        int endFieldIndex = FindFieldLoad(codes, CraftUpgradeItemField, resultStoreIndex + 1);
        int endIndex = endFieldIndex - 1;
        if (endFieldIndex <= resultStoreIndex
            || endIndex < 0
            || endFieldIndex + 1 >= codes.Count
            || codes[endIndex].opcode != OpCodes.Ldarg_0
            || !IsBranchTrue(codes[endFieldIndex + 1]))
        {
            failure = "could not locate the post-bonus inventory-capacity check";
            return false;
        }

        Label fallbackLabel = generator.DefineLabel();
        Label endLabel;
        if (codes[endIndex].labels.Count > 0)
        {
            endLabel = codes[endIndex].labels[0];
        }
        else
        {
            endLabel = generator.DefineLabel();
            codes[endIndex].labels.Add(endLabel);
        }

        CodeInstruction fallbackReset = new(OpCodes.Ldc_I4_0);
        fallbackReset.labels.Add(fallbackLabel);

        List<CodeInstruction> injected = new()
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            CreateLoadLocal(stationLocal),
            CreateLoadLocal(resultLocal),
            CreateLoadLocal(skillFactorLocal),
            new CodeInstruction(OpCodes.Call, BonusHelperMethod),
            CloneWithoutMetadata(codes[bonusStoreIndex]),
            CreateLoadLocal(bonusLocal),
            new CodeInstruction(OpCodes.Ldc_I4_0),
            new CodeInstruction(OpCodes.Blt, fallbackLabel),
            CreateLoadLocal(resultLocal),
            CreateLoadLocal(bonusLocal),
            new CodeInstruction(OpCodes.Add),
            CloneWithoutMetadata(codes[getAmountCallIndex + 1]),
            new CodeInstruction(OpCodes.Br, endLabel),
            fallbackReset,
            CloneWithoutMetadata(codes[bonusStoreIndex])
        };

        injected[0].labels.AddRange(codes[loopInitializationIndex].labels);
        codes[loopInitializationIndex].labels.Clear();
        injected[0].blocks.AddRange(codes[loopInitializationIndex].blocks);
        codes[loopInitializationIndex].blocks.Clear();

        codes.InsertRange(loopInitializationIndex, injected);
        return true;
    }

    private static int FindCall(List<CodeInstruction> codes, MethodInfo method, int startIndex)
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

    private static int FindFieldLoad(List<CodeInstruction> codes, FieldInfo field, int startIndex)
    {
        for (int index = Math.Max(0, startIndex); index < codes.Count; index++)
        {
            if ((codes[index].opcode == OpCodes.Ldfld || codes[index].opcode == OpCodes.Ldsfld)
                && Equals(codes[index].operand, field))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindInstructionWithLabel(
        List<CodeInstruction> codes,
        Label label,
        int startIndex)
    {
        for (int index = Math.Max(0, startIndex); index < codes.Count; index++)
        {
            if (codes[index].labels.Contains(label))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool LoadsLocal(CodeInstruction instruction, int localIndex)
    {
        return TryGetLocalIndex(instruction, load: true, out int index) && index == localIndex;
    }

    private static bool StoresLocal(CodeInstruction instruction, int localIndex)
    {
        return TryGetLocalIndex(instruction, load: false, out int index) && index == localIndex;
    }

    private static bool TryGetStoredLocal(CodeInstruction instruction, out int localIndex)
    {
        return TryGetLocalIndex(instruction, load: false, out localIndex);
    }

    private static bool TryGetLocalIndex(
        CodeInstruction instruction,
        bool load,
        out int localIndex)
    {
        localIndex = -1;
        OpCode opcode = instruction.opcode;
        if (load)
        {
            if (opcode == OpCodes.Ldloc_0) { localIndex = 0; return true; }
            if (opcode == OpCodes.Ldloc_1) { localIndex = 1; return true; }
            if (opcode == OpCodes.Ldloc_2) { localIndex = 2; return true; }
            if (opcode == OpCodes.Ldloc_3) { localIndex = 3; return true; }
            if (opcode != OpCodes.Ldloc && opcode != OpCodes.Ldloc_S) { return false; }
        }
        else
        {
            if (opcode == OpCodes.Stloc_0) { localIndex = 0; return true; }
            if (opcode == OpCodes.Stloc_1) { localIndex = 1; return true; }
            if (opcode == OpCodes.Stloc_2) { localIndex = 2; return true; }
            if (opcode == OpCodes.Stloc_3) { localIndex = 3; return true; }
            if (opcode != OpCodes.Stloc && opcode != OpCodes.Stloc_S) { return false; }
        }

        switch (instruction.operand)
        {
            case LocalBuilder localBuilder:
                localIndex = localBuilder.LocalIndex;
                return true;
            case LocalVariableInfo localVariable:
                localIndex = localVariable.LocalIndex;
                return true;
            case byte byteIndex:
                localIndex = byteIndex;
                return true;
            case int intIndex:
                localIndex = intIndex;
                return true;
            default:
                return false;
        }
    }

    private static CodeInstruction CreateLoadLocal(int localIndex)
    {
        return localIndex switch
        {
            0 => new CodeInstruction(OpCodes.Ldloc_0),
            1 => new CodeInstruction(OpCodes.Ldloc_1),
            2 => new CodeInstruction(OpCodes.Ldloc_2),
            3 => new CodeInstruction(OpCodes.Ldloc_3),
            <= byte.MaxValue => new CodeInstruction(OpCodes.Ldloc_S, (byte)localIndex),
            _ => new CodeInstruction(OpCodes.Ldloc, localIndex)
        };
    }

    private static bool IsLoadConstantZero(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Ldc_I4_0
               || (instruction.opcode == OpCodes.Ldc_I4 && Equals(instruction.operand, 0))
               || (instruction.opcode == OpCodes.Ldc_I4_S
                   && Convert.ToInt32(instruction.operand) == 0);
    }

    private static bool IsUnconditionalBranch(OpCode opcode)
    {
        return opcode == OpCodes.Br || opcode == OpCodes.Br_S;
    }

    private static bool IsBranchLess(OpCode opcode)
    {
        return opcode == OpCodes.Blt || opcode == OpCodes.Blt_S;
    }

    private static bool IsBranchTrue(CodeInstruction instruction)
    {
        return instruction.opcode == OpCodes.Brtrue || instruction.opcode == OpCodes.Brtrue_S;
    }

    private static bool TryGetBranchLabel(
        CodeInstruction instruction,
        Func<OpCode, bool> opcodePredicate,
        out Label label)
    {
        if (opcodePredicate(instruction.opcode) && instruction.operand is Label branchLabel)
        {
            label = branchLabel;
            return true;
        }

        label = default;
        return false;
    }

    private static CodeInstruction CloneWithoutMetadata(CodeInstruction source)
    {
        return new CodeInstruction(source.opcode, source.operand);
    }

    private static void LogPatternFailure(string reason)
    {
        if (_patternWarningLogged)
        {
            return;
        }

        _patternWarningLogged = true;
        FineDiningPlugin.Log.LogWarning(
            $"Per-item Cooking bonus patch was not applied; vanilla behavior remains active ({reason}).");
    }
}


