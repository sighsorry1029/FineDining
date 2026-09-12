using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

[HarmonyPatch(typeof(ItemDrop), "SlowUpdate")]
internal static class ItemDropSlowUpdateSpoilageSafetyPatch
{
    private static void Postfix(ItemDrop __instance)
    {
        DecayRuntime.RefreshOwnedPlacedDrop(__instance);
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.Load))]
internal static class ItemDropLoadSpoilagePatch
{
    private static void Postfix(ItemDrop __instance)
    {
        DecayRuntime.RegisterGroundDrop(__instance);
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.LoadFromExternalZDO))]
internal static class ItemDropExternalLoadSpoilagePatch
{
    private static void Postfix(ItemDrop __instance)
    {
        DecayRuntime.RegisterGroundDrop(__instance);
    }
}

[HarmonyPatch(typeof(ItemDrop), "Save")]
internal static class ItemDropSavedSpoilagePatch
{
    private static void Postfix(ItemDrop __instance)
    {
        DecayRuntime.RegisterGroundDrop(__instance);
    }
}

[HarmonyPatch(typeof(ItemDrop), "OnDestroy")]
internal static class ItemDropDestroyedSpoilagePatch
{
    private static void Prefix(ItemDrop __instance)
    {
        DecayRuntime.UnregisterGroundDrop(__instance);
    }
}

[HarmonyPatch(typeof(Container), "Awake")]
internal static class ContainerAwakeSpoilagePatch
{
    private static void Prefix(Container __instance)
    {
        IceboxSubsystem.ApplyConfiguredStorageSize(__instance);
    }

    private static void Postfix(Container __instance)
    {
        IceboxSubsystem.ApplyConfiguredStorageSize(__instance);
        IceboxSubsystem.ApplyStoredRecipe(__instance);
        DecayRuntime.ContainerLoaded(__instance);
    }
}

[HarmonyPatch(typeof(Container), "Load")]
internal static class ContainerLoadSpoilagePatch
{
    private static void Prefix(Container __instance, out bool __state)
    {
        __state = IceboxSubsystem.PrepareStorageLoad(__instance);
        DecayRuntime.RegisterContainer(__instance);
    }

    private static void Postfix(Container __instance, bool __result, bool __state)
    {
        if (__state)
        {
            IceboxSubsystem.ApplyConfiguredStorageSize(__instance);
        }

        if (__result)
        {
            DecayRuntime.ContainerLoaded(__instance);
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Container __instance,
        bool __state,
        Exception? __exception)
    {
        if (!__state || __exception == null)
        {
            return __exception;
        }

        try
        {
            IceboxSubsystem.ApplyConfiguredStorageSize(__instance);
        }
        catch (Exception restoreException)
        {
            try
            {
                FineDiningPlugin.Log?.LogWarning(
                    "Could not restore the Icebox storage size after Container.Load failed: " +
                    restoreException.GetBaseException().Message);
            }
            catch
            {
                // A cleanup or logging failure must not replace the original load exception.
            }
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(Inventory), "Changed")]
internal static class InventoryChangedSpoilagePatch
{
    private static void Postfix(Inventory __instance)
    {
        DecayRuntime.MarkDirty(__instance);
        IceboxSubsystem.HandleInventoryChanged(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.Save), typeof(ZPackage))]
internal static class PlayerSaveSpoilageClockPatch
{
    private static void Prefix(
        Player __instance,
        out List<KeyValuePair<ItemDrop.ItemData, string>> __state)
    {
        // Character files should keep advancing while the player is offline.
        // Only the serialized snapshot is resumed; the live preserved inventory
        // is restored by the finalizer and remains paused while online.
        __state = DecayRuntime.BeginPlayerSaveClockSnapshot(__instance);
    }

    private static Exception? Finalizer(
        List<KeyValuePair<ItemDrop.ItemData, string>> __state,
        Exception? __exception)
    {
        DecayRuntime.EndPlayerSaveClockSnapshot(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(ItemDrop), "AutoStackItems")]
internal static class ItemDropAutoStackSpoilagePatch
{
    private static readonly FieldInfo ItemDataField = AccessTools.Field(typeof(ItemDrop), nameof(ItemDrop.m_itemData));
    private static readonly FieldInfo StackField = AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_stack));
    private static readonly MethodInfo ComposeMethod = AccessTools.Method(
        typeof(DecayRuntime),
        nameof(DecayRuntime.ComposeGroundStackExpiry));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new(instructions);
        List<int> matches = new();
        for (int index = 8; index < code.Count; index++)
        {
            if (IsGroundStackIncrement(code, index))
            {
                matches.Add(index - 8);
            }
        }

        if (matches.Count != 1 || ComposeMethod == null)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not safely patch ItemDrop.AutoStackItems expiry merge; expected one stack increment but found " +
                matches.Count + ". Ground-stack timers may not compose on this game version.");
            return code;
        }

        int insertAt = matches[0];
        CodeInstruction loadDestination = new(OpCodes.Ldarg_0);
        loadDestination.labels.AddRange(code[insertAt].labels);
        code[insertAt].labels.Clear();
        loadDestination.blocks.AddRange(code[insertAt].blocks);
        code[insertAt].blocks.Clear();

        // Reuse the method's existing ItemDrop local operand rather than assuming
        // a fixed LocalBuilder index across game compiler updates.
        CodeInstruction loadSource = new(code[insertAt + 4]);
        loadSource.labels.Clear();
        loadSource.blocks.Clear();
        code.InsertRange(insertAt, new[]
        {
            loadDestination,
            loadSource,
            new CodeInstruction(OpCodes.Call, ComposeMethod)
        });
        return code;
    }

    private static bool IsGroundStackIncrement(IReadOnlyList<CodeInstruction> code, int storeIndex)
    {
        int start = storeIndex - 8;
        return code[storeIndex].opcode == OpCodes.Stfld && Equals(code[storeIndex].operand, StackField) &&
               code[start].opcode == OpCodes.Ldarg_0 &&
               IsFieldLoad(code[start + 1], ItemDataField) &&
               code[start + 2].opcode == OpCodes.Dup &&
               IsFieldLoad(code[start + 3], StackField) &&
               IsLocalLoad(code[start + 4]) &&
               IsFieldLoad(code[start + 5], ItemDataField) &&
               IsFieldLoad(code[start + 6], StackField) &&
               code[start + 7].opcode == OpCodes.Add;
    }

    private static bool IsFieldLoad(CodeInstruction instruction, FieldInfo field)
    {
        return instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, field);
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
}

internal static class SpoilageContentLifecycle
{
    internal static void Refresh()
    {
        GeneratedPrefabRegistry.RegisterContent();
        FoodClassifier.Invalidate();
        DietModule.InvalidateChefTierCatalog();
        DecayRuntime.InvalidateAll();
    }
}

[HarmonyPatch(typeof(ObjectDB), "UpdateRegisters")]
internal static class ObjectDbUpdateRegistersSpoilagePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ObjectDB __instance)
    {
        // Late-registering content mods commonly update existing prefab data
        // without changing ObjectDB counts. Refresh classification and the
        // dynamic Icebox recipe after their registration pass completes.
        SpoilageContentLifecycle.Refresh();
    }
}

[HarmonyPatch(typeof(ZNet), "OnDestroy")]
internal static class ZNetDestroySpoilagePatch
{
    private static void Postfix()
    {
        DecayRuntime.Reset();
        FoodClassifier.Invalidate();
    }
}

internal sealed class InventoryAddMergeState
{
    internal readonly Dictionary<ItemDrop.ItemData, int> PreviousStacks = new();
    internal readonly Dictionary<ItemDrop.ItemData, AssignedLifetimeSnapshot> PreviousLifetimes = new();
    internal bool SourceHadValidClock;
    internal bool SourceHadExpiryKey;
    internal string? OriginalSourceExpiryValue;
    internal bool SourceHadLifetimeKey;
    internal string? OriginalSourceLifetimeValue;
    internal long SourceClockValue;
    internal AssignedLifetimeSnapshot SourceLifetime;
    internal bool SourceSpoiled;
}

internal static class InventoryAddMergeTracker
{
    private static bool _loggedTemporarySourceRestoreFailure;

    internal static InventoryAddMergeState? Prefix(Inventory inventory, ItemDrop.ItemData? source)
    {
        if (source == null ||
            DecayRuntime.IsContainerLoading(inventory) ||
            !DecayRuntime.IsAuthoritativeInventory(inventory))
        {
            return null;
        }

        InventoryAddMergeState state = new();
        CaptureOriginalSourceMetadata(source, state);
        DecayRuntime.PrepareItemForAdd(inventory, source);
        PieceRecoverySpoilageTracker.ApplyToInventoryItem(inventory, source);
        state.SourceLifetime = FreshnessRuntime.CaptureAssignedLifetime(source);
        state.SourceSpoiled = SpoilageClock.IsSpoiled(source);
        if (!DecayRuntime.TryGetExpiryTicks(source, out state.SourceClockValue) &&
            !state.SourceSpoiled)
        {
            return null;
        }

        foreach (ItemDrop.ItemData existing in inventory.GetAllItems())
        {
            if (existing == null)
            {
                continue;
            }

            if (CanPotentiallyStack(existing, source))
            {
                state.PreviousStacks[existing] = existing.m_stack;
                state.PreviousLifetimes[existing] =
                    FreshnessRuntime.CaptureAssignedLifetime(existing);
            }
        }

        return state;
    }

    internal static void Postfix(Inventory inventory, ItemDrop.ItemData? source, InventoryAddMergeState? state)
    {
        if (state == null || source == null)
        {
            return;
        }

        bool changedAfterVanillaSave = false;
        try
        {
            foreach (KeyValuePair<ItemDrop.ItemData, int> previous in state.PreviousStacks)
            {
                ItemDrop.ItemData target = previous.Key;
                if (ReferenceEquals(target, source) || target.m_stack <= previous.Value)
                {
                    continue;
                }

                changedAfterVanillaSave |= DecayRuntime.ComposeInventoryStackMetadata(
                    inventory,
                    target,
                    state.SourceClockValue,
                    state.PreviousLifetimes[target],
                    state.SourceLifetime,
                    state.SourceSpoiled);
            }
        }
        finally
        {
            RestoreTemporarySourceMetadataSafe(inventory, source, state);
        }

        // Inventory.AddItem serializes before Harmony postfixes. Notify again only
        // when the destination expiry was actually composed after that save.
        if (changedAfterVanillaSave)
        {
            inventory.Changed();
        }
    }

    internal static void RestoreTemporarySourceMetadataSafe(
        Inventory inventory,
        ItemDrop.ItemData? source,
        InventoryAddMergeState? state)
    {
        if (source == null || state == null)
        {
            return;
        }

        try
        {
            RestoreTemporarySourceMetadata(inventory, source, state);
        }
        catch (Exception exception)
        {
            if (_loggedTemporarySourceRestoreFailure)
            {
                return;
            }

            _loggedTemporarySourceRestoreFailure = true;
            FineDiningPlugin.Log.LogWarning(
                "Could not restore a temporarily activated source stack after Inventory.AddItem: " +
                exception);
        }
    }

    private static void CaptureOriginalSourceMetadata(
        ItemDrop.ItemData source,
        InventoryAddMergeState state)
    {
        state.SourceHadValidClock = DecayRuntime.TryGetExpiryTicks(source, out _);
        if (source.m_customData == null)
        {
            return;
        }

        state.SourceHadExpiryKey = source.m_customData.TryGetValue(
            SpoilageClock.ExpiryDataKey,
            out state.OriginalSourceExpiryValue);
        state.SourceHadLifetimeKey = source.m_customData.TryGetValue(
            FreshnessRuntime.AssignedLifetimeDataKey,
            out state.OriginalSourceLifetimeValue);
    }

    private static void RestoreTemporarySourceMetadata(
        Inventory inventory,
        ItemDrop.ItemData source,
        InventoryAddMergeState state)
    {
        // Prefix preparation lets vanilla clones and stack merges inherit the
        // new player-inventory clock. If the source reference did not actually
        // enter that inventory, restore its pre-call state so failed or partial
        // transfers do not activate the remainder left in a container/world drop.
        if (state.SourceHadValidClock || inventory.GetAllItems().Contains(source))
        {
            return;
        }

        source.m_customData ??= new Dictionary<string, string>();
        RestoreKey(
            source.m_customData,
            SpoilageClock.ExpiryDataKey,
            state.SourceHadExpiryKey,
            state.OriginalSourceExpiryValue);
        RestoreKey(
            source.m_customData,
            FreshnessRuntime.AssignedLifetimeDataKey,
            state.SourceHadLifetimeKey,
            state.OriginalSourceLifetimeValue);
    }

    private static void RestoreKey(
        Dictionary<string, string> customData,
        string key,
        bool hadKey,
        string? originalValue)
    {
        if (hadKey)
        {
            customData[key] = originalValue ?? string.Empty;
        }
        else
        {
            customData.Remove(key);
        }
    }

    private static bool CanPotentiallyStack(ItemDrop.ItemData existing, ItemDrop.ItemData source)
    {
        if (existing.m_shared == null || source.m_shared == null)
        {
            return false;
        }

        return existing.m_shared.m_name == source.m_shared.m_name &&
               existing.m_worldLevel == source.m_worldLevel &&
               (existing.m_shared.m_maxQuality <= 1 || existing.m_quality == source.m_quality);
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), typeof(ItemDrop.ItemData))]
internal static class InventoryAddItemSpoilagePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore(new[] { InventorySlotsCompatibility.PluginGuid })]
    private static void Prefix(Inventory __instance, ItemDrop.ItemData item, out InventoryAddMergeState? __state)
    {
        __state = InventoryAddMergeTracker.Prefix(__instance, item);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(new[] { InventorySlotsCompatibility.PluginGuid })]
    private static void Postfix(Inventory __instance, ItemDrop.ItemData item, InventoryAddMergeState? __state)
    {
        InventoryAddMergeTracker.Postfix(__instance, item, __state);
    }

    private static Exception? Finalizer(
        Inventory __instance,
        ItemDrop.ItemData item,
        InventoryAddMergeState? __state,
        Exception? __exception)
    {
        InventoryAddMergeTracker.RestoreTemporarySourceMetadataSafe(__instance, item, __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), typeof(ItemDrop.ItemData), typeof(Vector2i))]
internal static class InventoryAddItemAtPositionSpoilagePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore(new[] { InventorySlotsCompatibility.PluginGuid })]
    private static void Prefix(Inventory __instance, ItemDrop.ItemData item, out InventoryAddMergeState? __state)
    {
        __state = InventoryAddMergeTracker.Prefix(__instance, item);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(new[] { InventorySlotsCompatibility.PluginGuid })]
    private static void Postfix(Inventory __instance, ItemDrop.ItemData item, InventoryAddMergeState? __state)
    {
        InventoryAddMergeTracker.Postfix(__instance, item, __state);
    }

    private static Exception? Finalizer(
        Inventory __instance,
        ItemDrop.ItemData item,
        InventoryAddMergeState? __state,
        Exception? __exception)
    {
        InventoryAddMergeTracker.RestoreTemporarySourceMetadataSafe(__instance, item, __state);
        return __exception;
    }
}

[HarmonyPatch(
    typeof(Inventory),
    nameof(Inventory.AddItem),
    typeof(ItemDrop.ItemData),
    typeof(int),
    typeof(int),
    typeof(int),
    typeof(bool))]
internal static class InventoryAddItemAmountAtPositionSpoilagePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore(new[] { InventorySlotsCompatibility.PluginGuid })]
    private static void Prefix(Inventory __instance, ItemDrop.ItemData item, out InventoryAddMergeState? __state)
    {
        __state = InventoryAddMergeTracker.Prefix(__instance, item);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(new[] { InventorySlotsCompatibility.PluginGuid })]
    private static void Postfix(Inventory __instance, ItemDrop.ItemData item, InventoryAddMergeState? __state)
    {
        InventoryAddMergeTracker.Postfix(__instance, item, __state);
    }

    private static Exception? Finalizer(
        Inventory __instance,
        ItemDrop.ItemData item,
        InventoryAddMergeState? __state,
        Exception? __exception)
    {
        InventoryAddMergeTracker.RestoreTemporarySourceMetadataSafe(__instance, item, __state);
        return __exception;
    }
}
