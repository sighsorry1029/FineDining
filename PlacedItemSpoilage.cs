using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

internal sealed class PlacementSpoilageState
{
    internal string TargetPrefabName = "";
    internal long PlacementTicks;
    internal long InheritedRemainingTicks = -1L;
    internal bool Consumed;
}

internal static class PlacementSpoilageTracker
{
    [ThreadStatic]
    private static Stack<PlacementSpoilageState>? _scopes;
    private static bool _loggedCaptureFailure;

    internal static PlacementSpoilageState Begin(Player player, Piece piece)
    {
        PlacementSpoilageState state = new();
        try
        {
            if (player != null && piece != null && player == Player.m_localPlayer)
            {
                state.TargetPrefabName = FoodIdentity.NormalizePrefabName(piece.gameObject.name);
                SpoilageClock.TryGetWorldTicks(out state.PlacementTicks);
                if (!IsNoCostPlacement(player, piece))
                {
                    state.InheritedRemainingTicks = CaptureConsumedRemaining(player.GetInventory(), piece);
                }
            }
        }
        catch (Exception exception)
        {
            if (!_loggedCaptureFailure)
            {
                _loggedCaptureFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not capture a placed food source deadline; the placed item will receive a fresh timer: " +
                    exception);
            }
        }

        (_scopes ??= new Stack<PlacementSpoilageState>()).Push(state);
        return state;
    }

    internal static void End(PlacementSpoilageState? state)
    {
        if (state == null || _scopes == null || _scopes.Count == 0)
        {
            return;
        }

        if (ReferenceEquals(_scopes.Peek(), state))
        {
            _scopes.Pop();
        }
        else
        {
            // A mismatched nested scope is safer to discard than to leak into a
            // later placement and attach the wrong stack deadline.
            _scopes.Clear();
        }
    }

    internal static bool TryConsumeFor(
        ItemDrop placedDrop,
        bool sendRpc,
        out long inheritedRemainingTicks,
        out long placementTicks)
    {
        inheritedRemainingTicks = -1L;
        placementTicks = 0L;
        if (!sendRpc || placedDrop == null || _scopes == null || _scopes.Count == 0)
        {
            return false;
        }

        PlacementSpoilageState state = _scopes.Peek();
        string placedPrefabName = FoodIdentity.NormalizePrefabName(placedDrop.gameObject.name);
        if (state.Consumed || state.TargetPrefabName.Length == 0 ||
            !string.Equals(state.TargetPrefabName, placedPrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        state.Consumed = true;
        inheritedRemainingTicks = state.InheritedRemainingTicks;
        placementTicks = state.PlacementTicks;
        return true;
    }

    private static long CaptureConsumedRemaining(Inventory? inventory, Piece piece)
    {
        if (inventory == null || piece.m_resources == null)
        {
            return -1L;
        }

        Dictionary<ItemDrop.ItemData, int> virtualStacks = new();
        foreach (ItemDrop.ItemData item in inventory.m_inventory)
        {
            if (item != null)
            {
                virtualStacks[item] = Math.Max(0, item.m_stack);
            }
        }

        bool initializedTimer = false;
        long earliestRemainingTicks = -1L;
        if (!SpoilageClock.TryGetWorldTicks(out long nowTicks))
        {
            return earliestRemainingTicks;
        }
        foreach (Piece.Requirement requirement in piece.m_resources)
        {
            if (requirement?.m_resItem?.m_itemData?.m_shared == null)
            {
                continue;
            }

            int remaining = Math.Max(0, requirement.GetAmount(0));
            string requiredName = requirement.m_resItem.m_itemData.m_shared.m_name;
            if (remaining <= 0 || string.IsNullOrEmpty(requiredName))
            {
                continue;
            }

            foreach (ItemDrop.ItemData item in inventory.m_inventory)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (item?.m_shared == null || item.m_shared.m_name != requiredName ||
                    item.m_worldLevel < Game.m_worldLevel ||
                    !virtualStacks.TryGetValue(item, out int available) || available <= 0)
                {
                    continue;
                }

                int consumed = Math.Min(available, remaining);
                virtualStacks[item] = available - consumed;
                remaining -= consumed;
                if (consumed <= 0)
                {
                    continue;
                }

                initializedTimer |= DecayRuntime.PrepareItemForAdd(inventory, item);
                if (DecayRuntime.TryGetSpoilageClock(
                        item,
                        nowTicks,
                        out long remainingTicks,
                        out _))
                {
                    earliestRemainingTicks = earliestRemainingTicks < 0L
                        ? remainingTicks
                        : Math.Min(earliestRemainingTicks, remainingTicks);
                }
            }
        }

        // PlacePiece runs before vanilla ConsumeResources. Persist a newly
        // initialized deadline even when a no-cost/compatibility path leaves the
        // source stack in the inventory.
        if (initializedTimer)
        {
            inventory.Changed();
        }

        return earliestRemainingTicks;
    }

    private static bool IsNoCostPlacement(Player player, Piece piece)
    {
        try
        {
            return player.NoCostCheat() ||
                   ZoneSystem.instance != null &&
                   ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey());
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class PieceRecoverySpoilageState
{
    internal long RemainingTicks = -1L;
    internal readonly HashSet<string> RecoverySourcePrefabs =
        new(StringComparer.OrdinalIgnoreCase);
    internal readonly HashSet<string> RecoverablePrefabs =
        new(StringComparer.OrdinalIgnoreCase);
}

internal static class PieceRecoverySpoilageTracker
{
    [ThreadStatic]
    private static Stack<PieceRecoverySpoilageState>? _scopes;
    private static bool _loggedRecoveryFailure;

    internal static PieceRecoverySpoilageState Begin(Piece piece)
    {
        PieceRecoverySpoilageState state = new();
        try
        {
            ItemDrop? placedDrop = piece != null ? piece.GetComponent<ItemDrop>() : null;
            if (placedDrop != null)
            {
                try
                {
                    placedDrop.Load();
                }
                catch
                {
                    // The local item data may still contain a valid persisted value.
                }

                if (!DecayRuntime.IsCreatorlessPlacedDrop(placedDrop) &&
                    SpoilageClock.TryGetWorldTicks(out long nowTicks))
                {
                    if (DecayRuntime.TryGetSpoilageClock(
                            placedDrop.m_itemData,
                            nowTicks,
                            out long remainingTicks,
                            out _))
                    {
                        state.RemainingTicks = remainingTicks;
                    }
                }
            }

            if (piece?.m_resources != null)
            {
                foreach (Piece.Requirement requirement in piece.m_resources)
                {
                    if (requirement?.m_recover != true || requirement.m_resItem == null)
                    {
                        continue;
                    }

                    string prefabName = FoodIdentity.NormalizePrefabName(requirement.m_resItem.gameObject.name);
                    if (prefabName.Length > 0)
                    {
                        state.RecoverySourcePrefabs.Add(prefabName);
                        state.RecoverablePrefabs.Add(prefabName);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (!_loggedRecoveryFailure)
            {
                _loggedRecoveryFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not capture a placed food deadline before resource recovery: " + exception);
            }
        }

        (_scopes ??= new Stack<PieceRecoverySpoilageState>()).Push(state);
        return state;
    }

    internal static void End(PieceRecoverySpoilageState? state)
    {
        if (state == null || _scopes == null || _scopes.Count == 0)
        {
            return;
        }

        if (ReferenceEquals(_scopes.Peek(), state))
        {
            _scopes.Pop();
        }
        else
        {
            _scopes.Clear();
        }
    }

    internal static void ApplyToGroundDrop(ItemDrop? recoveredDrop)
    {
        PieceRecoverySpoilageState? state = Current;
        if (state == null || recoveredDrop == null || !Matches(state, recoveredDrop.m_itemData))
        {
            return;
        }

        DecayRuntime.InitializeRecoveredDrop(recoveredDrop, state.RemainingTicks);
    }

    internal static bool ApplyToInventoryItem(Inventory inventory, ItemDrop.ItemData? item)
    {
        try
        {
            PieceRecoverySpoilageState? state = Current;
            return state != null && item != null && Matches(state, item) &&
                   DecayRuntime.PrepareInheritedItemForAdd(inventory, item, state.RemainingTicks);
        }
        catch (Exception exception)
        {
            if (!_loggedRecoveryFailure)
            {
                _loggedRecoveryFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not transfer a placed food deadline to recovered inventory data: " +
                    exception);
            }

            return false;
        }
    }

    internal static void IncludeConvertedResult(ItemDrop? source, GameObject? resultPrefab)
    {
        try
        {
            PieceRecoverySpoilageState? state = Current;
            if (state == null || state.RemainingTicks < 0L || source == null || resultPrefab == null)
            {
                return;
            }

            string sourcePrefabName = FoodIdentity.GetCanonicalPrefabName(source.m_itemData);
            if (sourcePrefabName.Length == 0 ||
                !state.RecoverySourcePrefabs.Contains(sourcePrefabName) ||
                resultPrefab.GetComponent<ItemDrop>() == null)
            {
                return;
            }

            string resultPrefabName = FoodIdentity.NormalizePrefabName(resultPrefab.name);
            if (resultPrefabName.Length > 0)
            {
                state.RecoverablePrefabs.Add(resultPrefabName);
            }
        }
        catch (Exception exception)
        {
            if (!_loggedRecoveryFailure)
            {
                _loggedRecoveryFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not include a converted Piece recovery item in deadline inheritance: " +
                    exception);
            }
        }
    }

    private static PieceRecoverySpoilageState? Current =>
        _scopes != null && _scopes.Count > 0 ? _scopes.Peek() : null;

    private static bool Matches(PieceRecoverySpoilageState state, ItemDrop.ItemData item)
    {
        if (state.RemainingTicks < 0L || state.RecoverablePrefabs.Count == 0)
        {
            return false;
        }

        string prefabName = FoodIdentity.GetCanonicalPrefabName(item);
        return prefabName.Length > 0 && state.RecoverablePrefabs.Contains(prefabName);
    }
}

[HarmonyPatch]
internal static class GameCheckDropConversionSpoilagePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.DeclaredMethod(
            typeof(Game),
            nameof(Game.CheckDropConversion),
            new[]
            {
                typeof(HitData),
                typeof(ItemDrop),
                typeof(GameObject),
                typeof(int).MakeByRefType()
            });
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemDrop itemDrop, GameObject __result)
    {
        PieceRecoverySpoilageTracker.IncludeConvertedResult(itemDrop, __result);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
internal static class PlayerPlacePieceSpoilagePatch
{
    private static void Prefix(Player __instance, Piece piece, out PlacementSpoilageState __state)
    {
        __state = PlacementSpoilageTracker.Begin(__instance, piece);
    }

    private static Exception? Finalizer(PlacementSpoilageState __state, Exception? __exception)
    {
        PlacementSpoilageTracker.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.MakePiece))]
internal static class ItemDropMakePieceSpoilagePatch
{
    private static bool _loggedFailure;

    private static void Postfix(ItemDrop __instance, bool sendRPC)
    {
        try
        {
            PlacementSpoilageTracker.TryConsumeFor(
                __instance,
                sendRPC,
                out long inheritedRemainingTicks,
                out long placementTicks);
            DecayRuntime.InitializePlacedDrop(__instance, inheritedRemainingTicks, placementTicks);
        }
        catch (Exception exception)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not initialize spoilage for an ItemDrop Piece: " + exception);
            }
        }
    }
}

[HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
internal static class PieceDropResourcesSpoilagePatch
{
    private static void Prefix(Piece __instance, out PieceRecoverySpoilageState __state)
    {
        __state = PieceRecoverySpoilageTracker.Begin(__instance);
    }

    private static Exception? Finalizer(PieceRecoverySpoilageState __state, Exception? __exception)
    {
        PieceRecoverySpoilageTracker.End(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.OnCreateNew), typeof(ItemDrop))]
internal static class ItemDropCreateRecoveredSpoilagePatch
{
    private static bool _loggedFailure;

    private static void Postfix(ItemDrop item)
    {
        try
        {
            PieceRecoverySpoilageTracker.ApplyToGroundDrop(item);
        }
        catch (Exception exception)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not transfer a placed food deadline to a recovered ground item: " +
                    exception);
            }
        }
    }
}
