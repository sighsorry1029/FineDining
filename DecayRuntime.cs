using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace FineDining;

internal static class DecayRuntime
{
    // One atomic persisted clock: positive values are running absolute ZNet
    // deadlines, negative values are preservation-frozen remaining durations.
    internal const string ExpiryDataKey = "sighsorry.FineDining.ExpiryWorldTicks";
    internal const string PlacedAnchorDataKey = "sighsorry.FineDining.PlacedWorldTicks";

    private sealed class InventoryState
    {
        internal bool Dirty = true;
        internal bool Reconciling;
        internal bool SuppressDirty;
        internal bool PendingNotification;
        internal bool EnvironmentKnown;
        internal bool PausedByCold;
        internal bool BiomeSampleKnown;
        internal bool SampledBiomePaused;
        internal Vector3 SampledBiomePosition;
        internal float NextVisiblePreparationAt;
        internal long NextExpiryTicks = long.MaxValue;
    }

    private sealed class GroundEnvironmentSample
    {
        internal bool Paused;
        internal Vector3 Position;
    }

    private enum PreservationState
    {
        Unknown,
        Running,
        Paused
    }

    private enum ReplacementResolution
    {
        NotReady,
        Invalid,
        Ready
    }

    private static readonly Dictionary<Inventory, InventoryState> InventoryStates = new();
    private static readonly Dictionary<Inventory, WeakReference<Container>> ContainersByInventory = new();
    private static readonly Dictionary<int, WeakReference<ItemDrop>> GroundDropsByInstanceId = new();
    private static readonly Dictionary<int, GroundEnvironmentSample> GroundEnvironmentSamples = new();
    private static readonly List<KeyValuePair<int, WeakReference<ItemDrop>>> GroundDropSnapshot = new();
    private static readonly HashSet<string> LoggedReplacementWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static float _nextTickAt;
    private static bool _loggedFirstInventoryCheck;
    private static bool _loggedFirstGroundRegistration;
    private static bool _loggedFirstGroundExpiration;
    private static bool _loggedFirstGroundLoadFailure;

    internal static void Tick()
    {
        if (Time.unscaledTime < _nextTickAt)
        {
            return;
        }

        _nextTickAt = Time.unscaledTime + 1f;
        if (!TryGetWorldTicks(out long nowTicks))
        {
            return;
        }

        Player localPlayer = Player.m_localPlayer;
        if (localPlayer != null)
        {
            Inventory playerInventory = localPlayer.GetInventory();
            if (playerInventory != null)
            {
                ProcessIfDue(playerInventory, nowTicks);
            }
        }

        PruneAndProcessContainers(nowTicks);
        PruneAndProcessGroundDrops(nowTicks);
    }

    internal static void RegisterContainer(Container? container)
    {
        if (container == null || container.m_inventory == null)
        {
            return;
        }

        bool alreadyRegistered = ContainersByInventory.TryGetValue(
                                     container.m_inventory,
                                     out WeakReference<Container> existing) &&
                                 existing.TryGetTarget(out Container existingContainer) &&
                                 ReferenceEquals(existingContainer, container);
        if (alreadyRegistered)
        {
            return;
        }

        ContainersByInventory[container.m_inventory] = new WeakReference<Container>(container);
        GetState(container.m_inventory).Dirty = true;
    }

    internal static void ContainerLoaded(Container? container)
    {
        RegisterContainer(container);
        if (container == null || !IsOwnedContainer(container) || !TryGetWorldTicks(out long nowTicks))
        {
            return;
        }

        InventoryState state = GetState(container.m_inventory);
        state.Dirty = true;
        ProcessIfDue(container.m_inventory, nowTicks);
    }

    internal static void MarkDirty(Inventory? inventory)
    {
        if (inventory == null || !InventoryStates.TryGetValue(inventory, out InventoryState state) || state.SuppressDirty)
        {
            return;
        }

        state.Dirty = true;
        state.NextVisiblePreparationAt = 0f;
    }

    internal static void InvalidateAll()
    {
        foreach (InventoryState state in InventoryStates.Values)
        {
            state.Dirty = true;
            state.EnvironmentKnown = false;
            state.BiomeSampleKnown = false;
            state.NextExpiryTicks = long.MinValue;
            state.NextVisiblePreparationAt = 0f;
        }

        GroundEnvironmentSamples.Clear();

        // A policy reload can turn a previously disabled/not-tracked placed
        // food into a tracked one. Re-register loaded ItemDrop pieces so they
        // are reconsidered without scanning world ZDOs or waiting for reload.
        if (ItemDrop.s_instances == null)
        {
            return;
        }

        foreach (ItemDrop drop in ItemDrop.s_instances.ToArray())
        {
            if (drop != null && IsPlacedGroundDrop(drop))
            {
                RegisterGroundDrop(drop);
            }
        }
    }

    internal static void Reset()
    {
        InventoryStates.Clear();
        ContainersByInventory.Clear();
        GroundDropsByInstanceId.Clear();
        GroundEnvironmentSamples.Clear();
        GroundDropSnapshot.Clear();
        LoggedReplacementWarnings.Clear();
        _nextTickAt = 0f;
        _loggedFirstInventoryCheck = false;
        _loggedFirstGroundRegistration = false;
        _loggedFirstGroundExpiration = false;
        _loggedFirstGroundLoadFailure = false;
    }

    internal static bool TryPrepareVisibleInventoryTimers(Inventory? inventory, out long nowTicks)
    {
        nowTicks = 0L;
        if (inventory == null || !TryGetWorldTicks(out nowTicks))
        {
            return false;
        }

        // Display remains read-only for inventories this peer does not own. An
        // authoritative player/container inventory is reconciled immediately only
        // after every visible rule is ready. World time is still returned while
        // policy sync/ObjectDB discovery is pending so persisted timers remain
        // visible without risking premature mutation.
        if (IsAuthoritativeInventory(inventory))
        {
            InventoryState state = GetState(inventory);
            if (Time.unscaledTime >= state.NextVisiblePreparationAt)
            {
                state.NextVisiblePreparationAt = Time.unscaledTime + 1f;
                if (IsPolicyReadyForInventory(inventory))
                {
                    ProcessIfDue(inventory, nowTicks);
                }
            }
        }

        return true;
    }

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

    internal static string? ComposeStackClockValues(string? destinationValue, string? sourceValue)
    {
        if (!TryParseClockValue(sourceValue, out long sourceClock))
        {
            // Preserve an unknown destination format; a malformed source must
            // never erase or propagate metadata.
            return destinationValue;
        }

        if (!TryParseClockValue(destinationValue, out long destinationClock))
        {
            // A genuinely missing destination inherits the source. An unknown
            // future destination format remains exact identity and is left for
            // its owning mod to interpret.
            return destinationValue == null
                ? sourceClock.ToString(CultureInfo.InvariantCulture)
                : destinationValue;
        }

        bool hasWorldTicks = TryGetWorldTicks(out long worldTicks);
        if (!hasWorldTicks && (destinationClock < 0L) != (sourceClock < 0L))
        {
            return destinationClock.ToString(CultureInfo.InvariantCulture);
        }

        long nowTicks = hasWorldTicks ? worldTicks : 0L;
        long composed = ComposeClockValues(
            destinationClock,
            sourceClock,
            nowTicks,
            destinationClock < 0L);
        return composed.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool PrepareItemForAdd(Inventory inventory, ItemDrop.ItemData? item)
    {
        if (item == null ||
            !IsAuthoritativeInventory(inventory) ||
            !TryGetWorldTicks(out long nowTicks))
        {
            return false;
        }

        ResolvedSpoilageRule rule = SpoilagePolicy.Resolve(item);
        return rule.State == SpoilageRuleState.Enabled &&
               EnsureItemState(
                   item,
                   rule,
                   nowTicks,
                   ResolveInventoryPausedState(inventory),
                   transitionExisting: false,
                   out _,
                   out _);
    }

    internal static bool PrepareInheritedItemForAdd(
        Inventory inventory,
        ItemDrop.ItemData? item,
        long inheritedRemainingTicks)
    {
        if (item == null || !IsAuthoritativeInventory(inventory))
        {
            return false;
        }

        bool changed = PrepareItemForAdd(inventory, item);
        if (inheritedRemainingTicks < 0L ||
            SpoilagePolicy.Resolve(item).State != SpoilageRuleState.Enabled)
        {
            return changed;
        }

        bool paused = ResolveInventoryPausedState(inventory);
        return ApplyEarlierRemaining(item, inheritedRemainingTicks, paused) || changed;
    }

    internal static bool ApplyEarlierInventoryClock(
        Inventory inventory,
        ItemDrop.ItemData? target,
        long sourceClockValue)
    {
        if (target == null || !IsValidClockValue(sourceClockValue) ||
            !TryGetWorldTicks(out long nowTicks))
        {
            return false;
        }

        return ApplyEarlierClock(
            target,
            sourceClockValue,
            nowTicks,
            ResolveInventoryPausedState(inventory));
    }

    internal static bool ComposeInventoryStackMetadata(
        Inventory inventory,
        ItemDrop.ItemData target,
        long sourceClockValue,
        AssignedLifetimeSnapshot targetLifetime,
        AssignedLifetimeSnapshot sourceLifetime)
    {
        if (inventory == null || target == null || !IsValidClockValue(sourceClockValue))
        {
            return false;
        }

        bool changed = ApplyEarlierInventoryClock(inventory, target, sourceClockValue);
        changed |= FreshnessRuntime.ComposeAssignedLifetime(
            target,
            targetLifetime,
            sourceLifetime);
        return changed;
    }

    internal static bool ComposeDirectRecoveryMergeExpiry(
        ItemDrop.ItemData? target,
        ItemDrop.ItemData? source,
        int movedAmount)
    {
        if (movedAmount <= 0 ||
            target == null ||
            source == null ||
            ReferenceEquals(target, source) ||
            target.m_customData == null ||
            !TryGetExpiryTicks(source, out long sourceClockValue))
        {
            return false;
        }

        AssignedLifetimeSnapshot targetLifetime =
            FreshnessRuntime.CaptureAssignedLifetime(target);
        AssignedLifetimeSnapshot sourceLifetime =
            FreshnessRuntime.CaptureAssignedLifetime(source);
        bool hasTargetClock = TryGetExpiryTicks(target, out long targetClockValue);
        if (!TryGetWorldTicks(out long nowTicks))
        {
            if (hasTargetClock && (targetClockValue < 0L) != (sourceClockValue < 0L))
            {
                return false;
            }

            nowTicks = 0L;
        }

        bool paused = hasTargetClock ? targetClockValue < 0L : sourceClockValue < 0L;
        bool changed = ApplyEarlierClock(
            target,
            sourceClockValue,
            nowTicks,
            paused);
        changed |= FreshnessRuntime.ComposeAssignedLifetime(
            target,
            targetLifetime,
            sourceLifetime);
        return changed;
    }

    internal static void ComposeGroundStackExpiry(ItemDrop? destination, ItemDrop? source)
    {
        if (destination == null || source == null ||
            destination.m_nview == null || source.m_nview == null ||
            !destination.m_nview.IsValid() || !source.m_nview.IsValid() ||
            !destination.m_nview.IsOwner() || !source.m_nview.IsOwner() ||
            !TryGetExpiryTicks(source.m_itemData, out long sourceClockValue))
        {
            return;
        }

        if (!TryGetWorldTicks(out long nowTicks))
        {
            return;
        }

        AssignedLifetimeSnapshot destinationLifetime =
            FreshnessRuntime.CaptureAssignedLifetime(destination.m_itemData);
        AssignedLifetimeSnapshot sourceLifetime =
            FreshnessRuntime.CaptureAssignedLifetime(source.m_itemData);
        bool paused = ResolveWorldDropPausedState(destination);
        ApplyEarlierClock(destination.m_itemData, sourceClockValue, nowTicks, paused);
        FreshnessRuntime.ComposeAssignedLifetime(
            destination.m_itemData,
            destinationLifetime,
            sourceLifetime);
        RegisterGroundDrop(destination);
    }

    internal static void RegisterGroundDrop(ItemDrop? drop)
    {
        if (drop == null)
        {
            return;
        }

        int instanceId = drop.GetInstanceID();
        if (!HasValidGroundView(drop) ||
            !TryGetExpiryTicks(drop.m_itemData, out _) && !IsPlacedGroundDrop(drop))
        {
            GroundDropsByInstanceId.Remove(instanceId);
            GroundEnvironmentSamples.Remove(instanceId);
            return;
        }

        if (GroundDropsByInstanceId.TryGetValue(
                instanceId,
                out WeakReference<ItemDrop> existing) &&
            existing.TryGetTarget(out ItemDrop existingDrop) &&
            ReferenceEquals(existingDrop, drop))
        {
            return;
        }

        GroundDropsByInstanceId[instanceId] = new WeakReference<ItemDrop>(drop);
        if (!_loggedFirstGroundRegistration)
        {
            _loggedFirstGroundRegistration = true;
            FineDiningPlugin.Log.LogInfo(
                "World spoilage tracking active; registered the first timestamped ItemDrop or placed food Piece.");
        }
    }

    internal static void RefreshOwnedPlacedDrop(ItemDrop? drop)
    {
        if (drop == null || !IsOwnedGroundDrop(drop) || !IsPlacedGroundDrop(drop))
        {
            return;
        }

        try
        {
            // ItemDrop.MakePiece sends its RPC before every ZDO field is
            // guaranteed to be visible on a remote peer. SlowUpdate runs on
            // loaded ItemDrops every ten seconds, so a new owner can refresh
            // late custom data and recover registration after a handoff.
            drop.Load();
            RegisterGroundDrop(drop);
        }
        catch (Exception exception)
        {
            if (!_loggedFirstGroundLoadFailure)
            {
                _loggedFirstGroundLoadFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not refresh an owned placed food item; FineDining will retry: " +
                    exception);
            }
        }
    }

    internal static void InitializePlacedDrop(
        ItemDrop? drop,
        long inheritedRemainingTicks,
        long placementTicks = 0L)
    {
        if (drop == null || !HasValidGroundView(drop) || !IsPlacedGroundDrop(drop))
        {
            return;
        }

        if (IsOwnedGroundDrop(drop))
        {
            bool hasPersistedExpiry = TryGetExpiryTicks(drop.m_itemData, out _);
            bool hasPendingAnchor =
                drop.m_itemData?.m_customData.ContainsKey(PlacedAnchorDataKey) == true;
            if (!ShouldInitializePlacedDeadline(
                    placementTicks,
                    hasPersistedExpiry,
                    hasPendingAnchor))
            {
                // A normal zone/server reload must not reinterpret an existing
                // absolute deadline using today's YAML lifetime. Only a fresh
                // placement or an explicitly pending anchor applies the target
                // prefab's lifetime cap.
                RegisterGroundDrop(drop);
                return;
            }

            InitializeOwnedWorldDrop(
                drop,
                inheritedRemainingTicks,
                placementTicks,
                keepPendingWhenNotReady: true);
            return;
        }

        RegisterGroundDrop(drop);
    }

    internal static bool ShouldInitializePlacedDeadline(
        long placementTicks,
        bool hasPersistedExpiry,
        bool hasPendingAnchor)
    {
        return placementTicks > 0L || !hasPersistedExpiry || hasPendingAnchor;
    }

    internal static void InitializeRecoveredDrop(ItemDrop? drop, long inheritedRemainingTicks)
    {
        if (drop == null || inheritedRemainingTicks < 0L || !IsOwnedGroundDrop(drop))
        {
            return;
        }

        InitializeOwnedWorldDrop(
            drop,
            inheritedRemainingTicks,
            anchorTicks: 0L,
            keepPendingWhenNotReady: false);
    }

    internal static long CalculateEffectiveRemainingTicks(
        long nowTicks,
        long anchorTicks,
        long lifetimeTicks,
        long inheritedRemainingTicks,
        bool paused)
    {
        long lifetime = Math.Max(TimeSpan.TicksPerSecond, lifetimeTicks);
        long elapsed = !paused && anchorTicks > 0L && nowTicks > anchorTicks
            ? nowTicks - anchorTicks
            : 0L;
        long freshRemaining = elapsed >= lifetime ? 0L : lifetime - elapsed;
        if (inheritedRemainingTicks < 0L)
        {
            return freshRemaining;
        }

        return Math.Min(inheritedRemainingTicks, freshRemaining);
    }

    internal static void UnregisterGroundDrop(ItemDrop? drop)
    {
        if (drop != null)
        {
            int instanceId = drop.GetInstanceID();
            GroundDropsByInstanceId.Remove(instanceId);
            GroundEnvironmentSamples.Remove(instanceId);
        }
    }

    internal static bool IsAuthoritativeInventory(Inventory? inventory)
    {
        if (inventory == null)
        {
            return false;
        }

        Player localPlayer = Player.m_localPlayer;
        if (localPlayer != null && ReferenceEquals(localPlayer.GetInventory(), inventory))
        {
            GetState(inventory);
            return true;
        }

        if (!ContainersByInventory.TryGetValue(inventory, out WeakReference<Container> weak) ||
            !weak.TryGetTarget(out Container container) || container == null)
        {
            return false;
        }

        return IsOwnedContainer(container);
    }

    internal static bool IsContainerLoading(Inventory? inventory)
    {
        return TryGetContainer(inventory, out Container? container) && container!.m_loading;
    }

    internal static bool TryGetContainer(Inventory? inventory, out Container? container)
    {
        container = null;
        if (inventory == null ||
            !ContainersByInventory.TryGetValue(inventory, out WeakReference<Container> weak) ||
            !weak.TryGetTarget(out Container resolved) ||
            resolved == null)
        {
            return false;
        }

        container = resolved;
        return true;
    }

    private static void ProcessIfDue(Inventory inventory, long nowTicks)
    {
        InventoryState state = GetState(inventory);
        PreservationState preservation = ResolveInventoryPreservationState(inventory);
        if (preservation != PreservationState.Unknown)
        {
            bool paused = preservation == PreservationState.Paused;
            if (!state.EnvironmentKnown || state.PausedByCold != paused)
            {
                state.EnvironmentKnown = true;
                state.PausedByCold = paused;
                state.Dirty = true;
            }
        }

        if (state.Dirty || nowTicks >= state.NextExpiryTicks)
        {
            Reconcile(inventory, state, nowTicks);
        }
    }

    private static bool IsPolicyReadyForInventory(Inventory inventory)
    {
        foreach (ItemDrop.ItemData item in inventory.m_inventory)
        {
            if (item != null && SpoilagePolicy.Resolve(item).State == SpoilageRuleState.NotReady)
            {
                return false;
            }
        }

        return true;
    }

    private static void PruneAndProcessContainers(long nowTicks)
    {
        List<Inventory>? staleInventories = null;
        foreach (KeyValuePair<Inventory, WeakReference<Container>> pair in ContainersByInventory)
        {
            if (!pair.Value.TryGetTarget(out Container container) || container == null || container.m_inventory == null)
            {
                staleInventories ??= new List<Inventory>();
                staleInventories.Add(pair.Key);
                continue;
            }

            if (IsOwnedContainer(container))
            {
                ProcessIfDue(pair.Key, nowTicks);
            }
        }

        if (staleInventories == null)
        {
            return;
        }

        foreach (Inventory inventory in staleInventories)
        {
            ContainersByInventory.Remove(inventory);
            InventoryStates.Remove(inventory);
        }
    }

    private static void PruneAndProcessGroundDrops(long nowTicks)
    {
        GroundDropSnapshot.Clear();
        foreach (KeyValuePair<int, WeakReference<ItemDrop>> pair in GroundDropsByInstanceId)
        {
            GroundDropSnapshot.Add(pair);
        }

        List<int>? registrationsToRemove = null;
        List<ItemDrop>? expiredOwnedDrops = null;
        foreach (KeyValuePair<int, WeakReference<ItemDrop>> pair in GroundDropSnapshot)
        {
            if (!pair.Value.TryGetTarget(out ItemDrop drop) ||
                drop == null ||
                !HasValidGroundView(drop))
            {
                registrationsToRemove ??= new List<int>();
                registrationsToRemove.Add(pair.Key);
                continue;
            }

            try
            {
                // Ownership and ground-stack merges can change the backing ZDO
                // after this peer's last local read. Load is revision-gated and
                // therefore cheap when nothing changed.
                drop.Load();
            }
            catch (Exception exception)
            {
                // A transient conflict with another ItemDrop patch should not
                // permanently stop spoilage tracking. Keep the weak entry and
                // try again on the next one-second runtime tick.
                if (!_loggedFirstGroundLoadFailure)
                {
                    _loggedFirstGroundLoadFailure = true;
                    FineDiningPlugin.Log.LogWarning(
                        "Could not refresh a tracked world item; FineDining will retry: " +
                        exception);
                }

                continue;
            }

            bool isPlacedDrop = IsPlacedGroundDrop(drop);
            if (isPlacedDrop && IsOwnedGroundDrop(drop) &&
                drop.m_itemData?.m_customData.ContainsKey(PlacedAnchorDataKey) == true)
            {
                long inheritedPlacedRemainingTicks = -1L;
                if (TryGetSpoilageClock(
                        drop.m_itemData,
                        nowTicks,
                        out long persistedRemainingTicks,
                        out _))
                {
                    inheritedPlacedRemainingTicks = persistedRemainingTicks;
                }
                InitializeOwnedWorldDrop(
                    drop,
                    inheritedPlacedRemainingTicks,
                    anchorTicks: 0L,
                    keepPendingWhenNotReady: true);
            }

            if (!TryGetExpiryTicks(drop.m_itemData, out _))
            {
                if (!isPlacedDrop)
                {
                    registrationsToRemove ??= new List<int>();
                    registrationsToRemove.Add(pair.Key);
                    continue;
                }

                if (!IsOwnedGroundDrop(drop))
                {
                    // The vanilla SlowUpdate ownership safety patch will
                    // re-register this Piece if ownership later moves here.
                    // Keeping every remote timerless Piece in the one-second
                    // registry would otherwise add needless O(N) polling.
                    registrationsToRemove ??= new List<int>();
                    registrationsToRemove.Add(pair.Key);
                    continue;
                }

                InitializeOwnedWorldDrop(
                    drop,
                    inheritedRemainingTicks: -1L,
                    anchorTicks: 0L,
                    keepPendingWhenNotReady: true);
                if (!TryGetExpiryTicks(drop.m_itemData, out _))
                {
                    continue;
                }
            }

            if (!IsOwnedGroundDrop(drop))
            {
                continue;
            }

            ResolvedSpoilageRule rule = SpoilagePolicy.Resolve(drop.m_itemData);
            if (rule.State == SpoilageRuleState.NotReady)
            {
                continue;
            }

            if (rule.State != SpoilageRuleState.Enabled)
            {
                ClearOwnedGroundExpiry(drop);
                registrationsToRemove ??= new List<int>();
                registrationsToRemove.Add(pair.Key);
                continue;
            }

            if (!TryEvaluateGroundExpiry(drop, rule, nowTicks, out bool expired) || !expired)
            {
                continue;
            }

            registrationsToRemove ??= new List<int>();
            registrationsToRemove.Add(pair.Key);
            expiredOwnedDrops ??= new List<ItemDrop>();
            expiredOwnedDrops.Add(drop);
        }
        GroundDropSnapshot.Clear();

        if (registrationsToRemove != null)
        {
            foreach (int instanceId in registrationsToRemove)
            {
                GroundDropsByInstanceId.Remove(instanceId);
                GroundEnvironmentSamples.Remove(instanceId);
            }
        }

        if (expiredOwnedDrops == null)
        {
            return;
        }

        foreach (ItemDrop drop in expiredOwnedDrops)
        {
            try
            {
                if (!IsOwnedGroundDrop(drop))
                {
                    RegisterGroundDrop(drop);
                    continue;
                }

                ResolvedSpoilageRule rule = SpoilagePolicy.Resolve(drop.m_itemData);
                if (rule.State == SpoilageRuleState.NotReady)
                {
                    RegisterGroundDrop(drop);
                    continue;
                }

                if (rule.State != SpoilageRuleState.Enabled)
                {
                    ClearOwnedGroundExpiry(drop);
                    continue;
                }

                if (!TryEvaluateGroundExpiry(drop, rule, nowTicks, out bool expired) || !expired)
                {
                    RegisterGroundDrop(drop);
                    continue;
                }

                ExpireGroundStack(drop, rule);
            }
            catch (Exception exception)
            {
                FineDiningPlugin.Log.LogError(
                    "Failed to expire a tracked world item: " + exception);
                RegisterGroundDrop(drop);
            }
        }
    }

    private static bool TryEvaluateGroundExpiry(
        ItemDrop drop,
        ResolvedSpoilageRule rule,
        long nowTicks,
        out bool expired)
    {
        expired = false;
        ItemDrop.ItemData? groundItem = drop.m_itemData;
        if (groundItem == null)
        {
            return false;
        }

        bool clockChanged = EnsureItemState(
            groundItem,
            rule,
            nowTicks,
            ResolveWorldDropPausedState(drop),
            transitionExisting: true,
            out long remainingTicks,
            out bool paused);
        if (clockChanged)
        {
            drop.Save();
        }

        expired = !paused && remainingTicks <= 0L;
        return true;
    }

    private static void ClearOwnedGroundExpiry(ItemDrop drop)
    {
        if (!IsOwnedGroundDrop(drop))
        {
            return;
        }

        bool changed = false;
        if (drop.m_itemData?.m_customData != null)
        {
            changed |= drop.m_itemData.m_customData.Remove(ExpiryDataKey);
            changed |= drop.m_itemData.m_customData.Remove(PlacedAnchorDataKey);
            changed |= FreshnessRuntime.ClearTrackedMetadata(drop.m_itemData);
        }

        if (changed)
        {
            drop.Save();
        }

        UnregisterGroundDrop(drop);
    }

    private static void InitializeOwnedWorldDrop(
        ItemDrop drop,
        long inheritedRemainingTicks,
        long anchorTicks,
        bool keepPendingWhenNotReady)
    {
        if (!IsOwnedGroundDrop(drop))
        {
            return;
        }

        bool changed = false;
        long effectiveAnchorTicks = 0L;
        if (keepPendingWhenNotReady)
        {
            if (!TryGetPositiveCustomTicks(drop.m_itemData, PlacedAnchorDataKey, out effectiveAnchorTicks))
            {
                effectiveAnchorTicks = anchorTicks;
                if (effectiveAnchorTicks <= 0L)
                {
                    TryGetWorldTicks(out effectiveAnchorTicks);
                }

                if (effectiveAnchorTicks > 0L)
                {
                    drop.m_itemData.m_customData[PlacedAnchorDataKey] =
                        effectiveAnchorTicks.ToString(CultureInfo.InvariantCulture);
                    changed = true;
                }
            }
        }

        ResolvedSpoilageRule rule = SpoilagePolicy.Resolve(drop.m_itemData);
        if (rule.State == SpoilageRuleState.NotReady)
        {
            changed |= inheritedRemainingTicks >= 0L &&
                       ApplyEarlierRemaining(
                           drop.m_itemData,
                           inheritedRemainingTicks,
                           ResolveWorldDropPausedState(drop));
            if (changed)
            {
                drop.Save();
            }

            if (keepPendingWhenNotReady || inheritedRemainingTicks >= 0L)
            {
                RegisterGroundDrop(drop);
            }

            return;
        }

        if (rule.State != SpoilageRuleState.Enabled)
        {
            ClearOwnedGroundExpiry(drop);
            return;
        }

        if (!TryGetWorldTicks(out long nowTicks))
        {
            RegisterGroundDrop(drop);
            return;
        }

        bool pausedByCold = ResolveWorldDropPausedState(drop);
        long effectiveRemainingTicks = CalculateEffectiveRemainingTicks(
            nowTicks,
            effectiveAnchorTicks,
            rule.LifetimeTicks,
            inheritedRemainingTicks,
            pausedByCold);
        long clockValue = EncodeClockValue(
            nowTicks,
            effectiveRemainingTicks,
            pausedByCold && effectiveRemainingTicks > 0L);
        changed |= SetClockValue(drop.m_itemData, clockValue);
        changed |= FreshnessRuntime.EnsureTrackedMetadata(
            drop.m_itemData,
            rule.LifetimeTicks);
        if (keepPendingWhenNotReady)
        {
            changed |= drop.m_itemData.m_customData.Remove(PlacedAnchorDataKey);
        }

        if (changed)
        {
            drop.Save();
        }

        RegisterGroundDrop(drop);
    }

    private static bool HasValidGroundView(ItemDrop drop)
    {
        try
        {
            return drop.m_nview != null && drop.m_nview.IsValid();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOwnedGroundDrop(ItemDrop drop)
    {
        try
        {
            return HasValidGroundView(drop) && drop.m_nview.IsOwner();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPlacedGroundDrop(ItemDrop drop)
    {
        try
        {
            if (!HasValidGroundView(drop))
            {
                return false;
            }

            return drop.m_nview.GetZDO().GetBool(ZDOVars.s_piece) || drop.IsPiece();
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetPositiveCustomTicks(
        ItemDrop.ItemData? item,
        string key,
        out long ticks)
    {
        ticks = 0L;
        return item?.m_customData != null &&
               item.m_customData.TryGetValue(key, out string value) &&
               long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) &&
               ticks > 0L &&
               string.Equals(value, ticks.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static PreservationState ResolveInventoryPreservationState(Inventory inventory)
    {
        InventoryState state = GetState(inventory);
        Player localPlayer = Player.m_localPlayer;
        if (localPlayer != null && ReferenceEquals(localPlayer.GetInventory(), inventory))
        {
            return SampleInventoryBiome(state, localPlayer.transform.position);
        }

        if (ContainersByInventory.TryGetValue(inventory, out WeakReference<Container> weak) &&
            weak.TryGetTarget(out Container container) && container != null)
        {
            if (GeneratedPrefabRegistry.IsIcebox(container))
            {
                return PreservationState.Paused;
            }

            return SampleInventoryBiome(state, container.transform.position);
        }

        return PreservationState.Unknown;
    }

    private static bool ResolveInventoryPausedState(Inventory inventory)
    {
        PreservationState preservation = ResolveInventoryPreservationState(inventory);
        if (preservation != PreservationState.Unknown)
        {
            return preservation == PreservationState.Paused;
        }

        InventoryState state = GetState(inventory);
        return state.EnvironmentKnown && state.PausedByCold;
    }

    private static bool ResolveWorldDropPausedState(ItemDrop drop)
    {
        int instanceId = drop.GetInstanceID();
        Vector3 position = drop.transform.position;
        if (GroundEnvironmentSamples.TryGetValue(instanceId, out GroundEnvironmentSample sample) &&
            PositionsMatch(sample.Position, position))
        {
            return sample.Paused;
        }

        if (TryIsNoSpoilBiome(position, out bool preserved, out bool cacheable))
        {
            if (cacheable)
            {
                GroundEnvironmentSamples[instanceId] = new GroundEnvironmentSample
                {
                    Position = position,
                    Paused = preserved
                };
            }

            return preserved;
        }

        return TryGetExpiryTicks(drop.m_itemData, out long clockValue) && clockValue < 0L;
    }

    private static PreservationState SampleInventoryBiome(InventoryState state, Vector3 position)
    {
        if (state.BiomeSampleKnown && PositionsMatch(state.SampledBiomePosition, position))
        {
            return state.SampledBiomePaused
                ? PreservationState.Paused
                : PreservationState.Running;
        }

        if (!TryIsNoSpoilBiome(position, out bool preserved, out bool cacheable))
        {
            return PreservationState.Unknown;
        }

        if (cacheable)
        {
            state.BiomeSampleKnown = true;
            state.SampledBiomePosition = position;
            state.SampledBiomePaused = preserved;
        }

        return preserved ? PreservationState.Paused : PreservationState.Running;
    }

    private static bool PositionsMatch(Vector3 left, Vector3 right)
    {
        return (left - right).sqrMagnitude <= 0.0001f;
    }

    private static bool TryIsNoSpoilBiome(
        Vector3 position,
        out bool preserved,
        out bool cacheable)
    {
        preserved = false;
        cacheable = false;
        try
        {
            Heightmap.Biome biome = Heightmap.FindBiome(position);
            cacheable = biome != Heightmap.Biome.None;
            if (biome == Heightmap.Biome.None && WorldGenerator.instance != null)
            {
                biome = WorldGenerator.instance.GetBiome(position.x, position.z);
            }

            if (biome == Heightmap.Biome.None)
            {
                return false;
            }

            preserved = PreservationConfig.IsNoSpoilBiome(biome);
            return true;
        }
        catch
        {
            cacheable = false;
            return false;
        }
    }

    internal static List<KeyValuePair<ItemDrop.ItemData, string>> BeginPlayerSaveClockSnapshot(Player? player)
    {
        List<KeyValuePair<ItemDrop.ItemData, string>> pausedClocks = new();
        Inventory? inventory = player?.GetInventory();
        if (inventory == null || !TryGetWorldTicks(out long nowTicks))
        {
            return pausedClocks;
        }

        foreach (ItemDrop.ItemData item in inventory.m_inventory)
        {
            if (TryGetSpoilageClock(item, nowTicks, out long remainingTicks, out bool paused) && paused &&
                item.m_customData.TryGetValue(ExpiryDataKey, out string originalValue))
            {
                pausedClocks.Add(new KeyValuePair<ItemDrop.ItemData, string>(item, originalValue));
                item.m_customData[ExpiryDataKey] =
                    EncodeClockValue(nowTicks, remainingTicks, paused: false)
                        .ToString(CultureInfo.InvariantCulture);
            }
        }

        return pausedClocks;
    }

    internal static void EndPlayerSaveClockSnapshot(
        List<KeyValuePair<ItemDrop.ItemData, string>>? pausedClocks)
    {
        if (pausedClocks == null)
        {
            return;
        }

        foreach (KeyValuePair<ItemDrop.ItemData, string> pair in pausedClocks)
        {
            if (pair.Key?.m_customData != null)
            {
                pair.Key.m_customData[ExpiryDataKey] = pair.Value;
            }
        }
    }

    private static bool IsOwnedContainer(Container container)
    {
        try
        {
            return container.m_nview != null && container.m_nview.IsValid() && container.m_nview.IsOwner();
        }
        catch
        {
            return false;
        }
    }

    private static InventoryState GetState(Inventory inventory)
    {
        if (!InventoryStates.TryGetValue(inventory, out InventoryState state))
        {
            state = new InventoryState();
            InventoryStates.Add(inventory, state);
        }

        return state;
    }

    private static void Reconcile(Inventory inventory, InventoryState state, long nowTicks)
    {
        if (state.Reconciling)
        {
            return;
        }

        state.Reconciling = true;
        bool changed = state.PendingNotification;
        int foodItemCount = 0;
        int existingTimerCount = 0;
        int initializedTimerCount = 0;
        long nextExpiryTicks = long.MaxValue;
        bool pausedByCold = state.EnvironmentKnown && state.PausedByCold;
        try
        {
            List<ItemDrop.ItemData> items = inventory.m_inventory;
            int inspectedItemCount = items.Count;
            for (int index = items.Count - 1; index >= 0; index--)
            {
                ItemDrop.ItemData item = items[index];
                if (item == null)
                {
                    continue;
                }

                ResolvedSpoilageRule rule = SpoilagePolicy.Resolve(item);
                if (rule.State == SpoilageRuleState.NotReady)
                {
                    // Do not reinterpret or clear persisted data while the synced
                    // policy/ObjectDB inputs are still incomplete. The policy
                    // loader invalidates runtime state after publishing a ready
                    // snapshot, at which point this stack is revisited.
                    continue;
                }

                if (rule.State != SpoilageRuleState.Enabled)
                {
                    if (item.m_customData.Remove(ExpiryDataKey))
                    {
                        changed = true;
                    }

                    changed |= FreshnessRuntime.ClearTrackedMetadata(item);

                    continue;
                }

                foodItemCount++;
                if (TryGetExpiryTicks(item, out _))
                {
                    existingTimerCount++;
                }

                if (EnsureItemState(
                        item,
                        rule,
                        nowTicks,
                        pausedByCold,
                        transitionExisting: state.EnvironmentKnown,
                        out long remainingTicks,
                        out bool paused))
                {
                    changed = true;
                    initializedTimerCount++;
                }

                if (paused)
                {
                    continue;
                }

                if (remainingTicks <= 0L)
                {
                    bool expired = ExpireItem(inventory, item, rule);
                    changed |= expired;
                    if (!expired)
                    {
                        nextExpiryTicks = Math.Min(nextExpiryTicks, nowTicks);
                    }

                    continue;
                }

                long expiryTicks = AddTicksSaturating(nowTicks, remainingTicks);
                nextExpiryTicks = Math.Min(nextExpiryTicks, expiryTicks);
            }

            if (changed)
            {
                state.PendingNotification = true;
                state.SuppressDirty = true;
                inventory.Changed();
                state.PendingNotification = false;
            }

            state.Dirty = false;
            state.NextExpiryTicks = nextExpiryTicks;
            if (inspectedItemCount > 0 && !_loggedFirstInventoryCheck)
            {
                _loggedFirstInventoryCheck = true;
                FineDiningPlugin.Log.LogInfo(
                    "Spoilage runtime check: inspected " + inspectedItemCount +
                    " stack(s), classified " + foodItemCount + " food stack(s), found " +
                    existingTimerCount + " existing timer(s), initialized " +
                    initializedTimerCount + " timer(s).");
            }
        }
        catch (Exception ex)
        {
            state.Dirty = true;
            FineDiningPlugin.Log.LogError("Failed to reconcile inventory spoilage: " + ex);
        }
        finally
        {
            state.SuppressDirty = false;
            state.Reconciling = false;
        }
    }

    private static bool EnsureItemState(
        ItemDrop.ItemData item,
        ResolvedSpoilageRule rule,
        long nowTicks,
        bool pausedByCold,
        bool transitionExisting,
        out long remainingTicks,
        out bool paused)
    {
        remainingTicks = 0L;
        paused = false;
        if (rule.State != SpoilageRuleState.Enabled)
        {
            return false;
        }

        bool metadataChanged = FreshnessRuntime.EnsureTrackedMetadata(item, rule.LifetimeTicks);

        if (TryGetSpoilageClock(item, nowTicks, out remainingTicks, out paused))
        {
            if (!transitionExisting || paused == pausedByCold)
            {
                return metadataChanged;
            }

            // Expired warm food is resolved before a cold environment can
            // freeze it. This prevents carrying a due stack into preservation from
            // reviving it with a paused zero-duration clock.
            if (!paused && remainingTicks <= 0L)
            {
                return metadataChanged;
            }

            paused = pausedByCold;
            long transitionedValue = EncodeClockValue(nowTicks, remainingTicks, paused);
            item.m_customData[ExpiryDataKey] = transitionedValue.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        long lifetimeTicks = Math.Max(TimeSpan.TicksPerSecond, rule.LifetimeTicks);
        remainingTicks = lifetimeTicks;
        paused = pausedByCold;
        long clockValue = EncodeClockValue(nowTicks, remainingTicks, paused);
        item.m_customData[ExpiryDataKey] = clockValue.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static ReplacementResolution ResolveReplacement(
        ItemDrop.ItemData sourceItem,
        ResolvedSpoilageRule rule,
        out string configuredPrefab,
        out ItemDrop replacementDrop)
    {
        configuredPrefab = FoodIdentity.NormalizePrefabName(rule.ReplacementPrefab);
        replacementDrop = null!;

        ObjectDB objectDb = ObjectDB.instance;
        if (!IsObjectDatabaseReady(objectDb))
        {
            return ReplacementResolution.NotReady;
        }

        bool generatedReplacement =
            GeneratedPrefabRegistry.IsGeneratedReplacementPrefabName(configuredPrefab);
        if (generatedReplacement &&
            !GeneratedPrefabRegistry.EnsureGeneratedReplacementAvailable(configuredPrefab))
        {
            return ReplacementResolution.NotReady;
        }

        GameObject? replacementPrefab = ResolveItemPrefab(objectDb, configuredPrefab);
        if (replacementPrefab == null && generatedReplacement)
        {
            // EnsureGeneratedReplacementAvailable succeeded only after both
            // registries pointed at the same owned prefab.  A failed ObjectDB
            // lookup here means another late registry mutation raced this
            // expiry pass, so preserve the original and retry next tick.
            return ReplacementResolution.NotReady;
        }

        ItemDrop? candidate = replacementPrefab != null
            ? replacementPrefab.GetComponent<ItemDrop>()
            : null;
        string sourcePrefab = FoodIdentity.GetCanonicalPrefabName(sourceItem);
        if (candidate?.m_itemData?.m_shared == null ||
            string.Equals(sourcePrefab, configuredPrefab, StringComparison.OrdinalIgnoreCase))
        {
            WarnReplacementOnce(
                configuredPrefab,
                "Spoiled prefab '" + configuredPrefab +
                "' is missing, invalid, or the same as the expired item. Removing expired stacks instead.");
            return ReplacementResolution.Invalid;
        }

        replacementDrop = candidate;
        return ReplacementResolution.Ready;
    }

    private static bool ExpireItem(
        Inventory inventory,
        ItemDrop.ItemData item,
        ResolvedSpoilageRule rule)
    {
        ReplacementResolution resolution = ResolveReplacement(
            item,
            rule,
            out string configuredPrefab,
            out ItemDrop replacementDrop);
        if (resolution == ReplacementResolution.NotReady)
        {
            return false;
        }

        if (resolution == ReplacementResolution.Invalid)
        {
            return inventory.m_inventory.Remove(item);
        }

        GameObject replacementPrefab = replacementDrop.gameObject;
        ItemDrop.ItemData template = replacementDrop.m_itemData;

        int sourceAmount = Math.Max(0, item.m_stack);
        Vector2i originalPosition = item.m_gridPos;
        int sourceWorldLevel = item.m_worldLevel;
        if (!inventory.m_inventory.Remove(item))
        {
            return false;
        }

        if (sourceAmount <= 0)
        {
            return true;
        }

        int maxStack = Math.Max(1, template.m_shared.m_maxStackSize);
        int remaining = CalculateReplacementAmount(sourceAmount);
        ItemDrop.ItemData stackTemplate = CreateReplacement(template, replacementPrefab, sourceWorldLevel);
        // Preserve the source count in its original slot even when the
        // replacement's nominal max stack is smaller. Inventory.AddItem's
        // positioned overload accepts that intentional over-stack for an
        // empty slot. The merge/split path below remains as a fallback for
        // inventory mods that reject or partially consume the insertion.
        int firstAmount = remaining;
        remaining -= InsertReplacementStack(inventory, stackTemplate, firstAmount, originalPosition);

        if (remaining > 0)
        {
            foreach (ItemDrop.ItemData existing in inventory.m_inventory)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (!CanStackReplacement(existing, stackTemplate))
                {
                    continue;
                }

                int moved = Math.Min(remaining, Math.Max(0, maxStack - existing.m_stack));
                existing.m_stack += moved;
                remaining -= moved;
            }
        }

        while (remaining > 0)
        {
            ItemDrop.ItemData extra = CreateReplacement(template, replacementPrefab, sourceWorldLevel);
            Vector2i position = inventory.FindEmptySlot(inventory.TopFirst(extra));
            if (position.x < 0)
            {
                break;
            }

            int requested = Math.Min(maxStack, remaining);
            int inserted = InsertReplacementStack(inventory, extra, requested, position);
            if (inserted <= 0)
            {
                break;
            }

            remaining -= inserted;
        }

        if (remaining > 0)
        {
            WarnReplacementOnce(
                "overflow:" + configuredPrefab,
                "Inventory had no room for " + remaining + " additional '" + configuredPrefab + "' items; overflow was discarded safely.");
        }

        return true;
    }

    private static void ExpireGroundStack(ItemDrop drop, ResolvedSpoilageRule rule)
    {
        if (!IsOwnedGroundDrop(drop))
        {
            RegisterGroundDrop(drop);
            return;
        }

        ZDO sourceZdo = drop.m_nview.GetZDO();
        ZDOID sourceZdoId = sourceZdo.m_uid;
        if (!TryGetExpiryTicks(drop.m_itemData, out long sourceExpiryTicks))
        {
            UnregisterGroundDrop(drop);
            return;
        }

        // Once Valheim's one-hour threshold has elapsed, let its own cleanup
        // decision run first. If the object survives (player proximity, base,
        // tar, or Piece protection), spoil it now. Before one hour, spoil it at
        // its deadline and carry the original spawn time to the replacement so
        // the vanilla cleanup schedule is not restarted by the visual swap.
        if (drop.m_autoDestroy && drop.GetTimeSinceSpawned() >= 3600d)
        {
            drop.TimedDestruction();
            if (!IsOwnedGroundDrop(drop))
            {
                return;
            }
        }

        ItemDrop.ItemData? sourceItem = drop.m_itemData;
        int sourceAmount = Math.Max(0, sourceItem?.m_stack ?? 0);
        if (sourceItem == null || sourceAmount <= 0)
        {
            drop.m_nview.Destroy();
            LogFirstGroundExpiration(sourceAmount, replacementStackCount: 0);
            return;
        }

        ReplacementResolution resolution = ResolveReplacement(
            sourceItem,
            rule,
            out string configuredPrefab,
            out ItemDrop replacementDrop);
        if (resolution == ReplacementResolution.NotReady)
        {
            RegisterGroundDrop(drop);
            return;
        }

        string sourcePrefab = FoodIdentity.GetCanonicalPrefabName(sourceItem);
        if (resolution == ReplacementResolution.Invalid)
        {
            drop.m_nview.Destroy();
            LogFirstGroundExpiration(sourceAmount, replacementStackCount: 0);
            return;
        }

        GameObject replacementPrefab = replacementDrop.gameObject;
        ItemDrop.ItemData template = replacementDrop.m_itemData;

        Vector3 position = drop.transform.position;
        Quaternion rotation = drop.transform.rotation;
        long inheritedSpawnTimeTicks = ShouldInheritGroundSpawnTime(
            drop.m_autoDestroy,
            IsPlacedGroundDrop(drop))
            ? GetGroundSpawnTimeTicks(drop)
            : 0L;
        if (inheritedSpawnTimeTicks > 0L && !replacementDrop.m_autoDestroy)
        {
            WarnReplacementOnce(
                "cleanup:" + configuredPrefab,
                "Spoiled prefab '" + configuredPrefab +
                "' does not use vanilla ItemDrop auto-destruction; the original ground cleanup age cannot be enforced for that replacement.");
        }

        int sourceWorldLevel = sourceItem.m_worldLevel;
        int replacementAmount = CalculateReplacementAmount(sourceAmount);
        // ItemDrop.DropItem accepts the preserved count even when it exceeds
        // the replacement's nominal stack limit, so one expired ground stack
        // remains one ground stack.
        ItemDrop.ItemData replacementTemplate = CreateReplacement(
            template,
            replacementPrefab,
            sourceWorldLevel);

        ItemDrop? spawnedReplacement = null;
        try
        {
            spawnedReplacement = ItemDrop.DropItem(
                replacementTemplate,
                replacementAmount,
                position,
                rotation);
            if (!IsOwnedGroundDrop(spawnedReplacement))
            {
                throw new InvalidOperationException(
                    "A newly spawned spoilage replacement was not locally owned.");
            }

            spawnedReplacement.OnPlayerDrop();
            InheritGroundSpawnTime(spawnedReplacement, inheritedSpawnTimeTicks);
        }
        catch (Exception exception)
        {
            DestroySpawnedGroundReplacement(spawnedReplacement);
            FineDiningPlugin.Log.LogError(
                "Could not create a ground spoilage replacement; the original stack was kept: " +
                exception);
            RegisterGroundDrop(drop);
            return;
        }

        if (!GroundSourceStillMatches(
                drop,
                sourceZdoId,
                sourceExpiryTicks,
                sourcePrefab,
                sourceAmount))
        {
            DestroySpawnedGroundReplacement(spawnedReplacement);
            RegisterGroundDrop(drop);
            return;
        }

        drop.m_nview.Destroy();
        LogFirstGroundExpiration(sourceAmount, replacementStackCount: 1);
    }

    private static bool GroundSourceStillMatches(
        ItemDrop drop,
        ZDOID sourceZdoId,
        long sourceExpiryTicks,
        string sourcePrefab,
        int sourceAmount)
    {
        if (!IsOwnedGroundDrop(drop) || drop.m_nview.GetZDO().m_uid != sourceZdoId ||
            drop.m_itemData == null || drop.m_itemData.m_stack != sourceAmount ||
            !TryGetExpiryTicks(drop.m_itemData, out long currentExpiryTicks) ||
            currentExpiryTicks != sourceExpiryTicks)
        {
            return false;
        }

        return string.Equals(
            FoodIdentity.GetCanonicalPrefabName(drop.m_itemData),
            sourcePrefab,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldInheritGroundSpawnTime(bool autoDestroy, bool isPiece)
    {
        return autoDestroy && !isPiece;
    }

    private static long GetGroundSpawnTimeTicks(ItemDrop drop)
    {
        try
        {
            return HasValidGroundView(drop)
                ? drop.m_nview.GetZDO().GetLong(ZDOVars.s_spawnTime, 0L)
                : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    private static void InheritGroundSpawnTime(ItemDrop replacement, long sourceSpawnTimeTicks)
    {
        if (sourceSpawnTimeTicks <= 0L || !IsOwnedGroundDrop(replacement))
        {
            return;
        }

        replacement.m_nview.GetZDO().Set(ZDOVars.s_spawnTime, sourceSpawnTimeTicks);
    }

    private static void DestroySpawnedGroundReplacement(ItemDrop? replacement)
    {
        if (replacement == null)
        {
            return;
        }

        if (IsOwnedGroundDrop(replacement))
        {
            replacement.m_nview.Destroy();
            return;
        }

        if (HasValidGroundView(replacement))
        {
            WarnReplacementOnce(
                "ground-rollback-owner",
                "A spoilage replacement changed owner during rollback and could not be removed locally.");
        }
    }

    internal static int CalculateReplacementAmount(int sourceAmount)
    {
        // Stack capacities are intentionally irrelevant here. Spoilage is a
        // one-for-one transformation: 50 source items become 50 replacements,
        // including a deliberate 50/20 over-stack when the target is smaller.
        return Math.Max(0, sourceAmount);
    }

    private static void LogFirstGroundExpiration(int sourceAmount, int replacementStackCount)
    {
        if (_loggedFirstGroundExpiration)
        {
            return;
        }

        _loggedFirstGroundExpiration = true;
        FineDiningPlugin.Log.LogInfo(
            "Ground spoilage runtime expired a stack of " + sourceAmount +
            " item(s); created " + replacementStackCount + " replacement stack(s).");
    }

    private static int InsertReplacementStack(
        Inventory inventory,
        ItemDrop.ItemData item,
        int amount,
        Vector2i requestedPosition)
    {
        if (amount <= 0)
        {
            return 0;
        }

        item.m_stack = amount;
        int before = item.m_stack;
        inventory.AddItem(item, amount, requestedPosition.x, requestedPosition.y);
        return Math.Max(0, before - item.m_stack);
    }

    private static ItemDrop.ItemData CreateReplacement(
        ItemDrop.ItemData template,
        GameObject prefab,
        int sourceWorldLevel)
    {
        ItemDrop.ItemData replacement = template.Clone();
        replacement.m_dropPrefab = prefab;
        replacement.m_worldLevel = sourceWorldLevel;
        replacement.m_equipped = false;
        replacement.m_customData.Remove(ExpiryDataKey);
        FreshnessRuntime.ClearTrackedMetadata(replacement);
        return replacement;
    }

    private static GameObject? ResolveItemPrefab(ObjectDB? objectDb, string prefabName)
    {
        if (objectDb == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        GameObject? direct = objectDb.GetItemPrefab(prefabName);
        if (direct != null &&
            string.Equals(
                FoodIdentity.NormalizePrefabName(direct.name),
                prefabName,
                StringComparison.OrdinalIgnoreCase))
        {
            return direct;
        }

        return objectDb.m_items?.FirstOrDefault(prefab =>
            prefab != null &&
            string.Equals(FoodIdentity.NormalizePrefabName(prefab.name), prefabName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsObjectDatabaseReady(ObjectDB? objectDb)
    {
        return objectDb != null && objectDb.m_items != null && objectDb.m_items.Count > 0;
    }

    private static bool CanStackReplacement(ItemDrop.ItemData candidate, ItemDrop.ItemData template)
    {
        return candidate.m_shared.m_name == template.m_shared.m_name &&
               candidate.m_quality == template.m_quality &&
               candidate.m_worldLevel == template.m_worldLevel &&
               candidate.m_stack < candidate.m_shared.m_maxStackSize &&
               CustomDataEqual(candidate.m_customData, template.m_customData);
    }

    private static bool CustomDataEqual(Dictionary<string, string> left, Dictionary<string, string> right)
    {
        int leftCount = left.Count - CountStackMetadataKeys(left);
        int rightCount = right.Count - CountStackMetadataKeys(right);
        return leftCount == rightCount && left.All(pair =>
            IsStackMetadataKey(pair.Key) || right.TryGetValue(pair.Key, out string value) && value == pair.Value);
    }

    private static int CountStackMetadataKeys(Dictionary<string, string> values)
    {
        int count = values.ContainsKey(ExpiryDataKey) ? 1 : 0;
        count += values.ContainsKey(FreshnessRuntime.AssignedLifetimeDataKey) ? 1 : 0;
        return count;
    }

    private static bool IsStackMetadataKey(string key) =>
        key == ExpiryDataKey ||
        key == FreshnessRuntime.AssignedLifetimeDataKey;

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

    private static bool IsValidClockValue(long clockValue)
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

    private static long AddTicksSaturating(long left, long right)
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

    private static bool ApplyEarlierClock(
        ItemDrop.ItemData target,
        long sourceClockValue,
        long nowTicks,
        bool destinationPaused)
    {
        if (!IsValidClockValue(sourceClockValue))
        {
            return false;
        }

        long composed = TryGetExpiryTicks(target, out long targetClockValue)
            ? ComposeClockValues(targetClockValue, sourceClockValue, nowTicks, destinationPaused)
            : ReencodeClockValue(sourceClockValue, nowTicks, destinationPaused);
        return SetClockValue(target, composed);
    }

    private static bool ApplyEarlierRemaining(
        ItemDrop.ItemData target,
        long sourceRemainingTicks,
        bool destinationPaused)
    {
        if (sourceRemainingTicks < 0L)
        {
            return false;
        }

        long nowTicks = TryGetWorldTicks(out long worldTicks) ? worldTicks : 0L;
        long sourceClockValue = EncodeClockValue(nowTicks, sourceRemainingTicks, destinationPaused);
        return ApplyEarlierClock(target, sourceClockValue, nowTicks, destinationPaused);
    }

    private static long ReencodeClockValue(long clockValue, long nowTicks, bool paused)
    {
        return TryDecodeClockValue(clockValue, nowTicks, out long remainingTicks, out _)
            ? EncodeClockValue(nowTicks, remainingTicks, paused && remainingTicks > 0L)
            : clockValue;
    }

    private static bool SetClockValue(ItemDrop.ItemData target, long clockValue)
    {
        if (!IsValidClockValue(clockValue))
        {
            return false;
        }

        string value = clockValue.ToString(CultureInfo.InvariantCulture);
        if (target.m_customData.TryGetValue(ExpiryDataKey, out string current) && current == value)
        {
            return false;
        }

        target.m_customData[ExpiryDataKey] = value;
        return true;
    }

    private static void WarnReplacementOnce(string key, string message)
    {
        if (LoggedReplacementWarnings.Add(key ?? ""))
        {
            FineDiningPlugin.Log.LogWarning(message);
        }
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
}
