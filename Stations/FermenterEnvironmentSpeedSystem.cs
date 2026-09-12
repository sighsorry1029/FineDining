using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

internal static class FermenterEnvironmentSpeedSystem
{
    private const float VanillaMinimumCover = 0.7f;
    internal const float MaximumDepthMeters = 8f;
    private const float RateEpsilon = 0.001f;
    private const float HeightmapEdgeEpsilon = 0.001f;
    private const double CheckpointSeconds = 10d;

    // FineDining never mutates the vanilla fermentation start timestamp. It is a
    // stable batch token shared with the Diet completion request path.
    private const string BatchTokenKey = "FineDining_FermenterEnv_BatchTokenV1";
    private const string AccumulatedBonusTicksKey = "FineDining_FermenterEnv_AccumulatedBonusTicksV1";
    private const string LastCheckpointTicksKey = "FineDining_FermenterEnv_LastCheckpointTicksV1";
    private const string BonusRateKey = "FineDining_FermenterEnv_BonusRateV1";

    private static readonly FieldInfo? FermenterHasRoofField =
        AccessTools.Field(typeof(Fermenter), "m_hasRoof");
    private static readonly FieldInfo? FermenterExposedField =
        AccessTools.Field(typeof(Fermenter), "m_exposed");
    private static readonly FieldInfo? HeightmapBuildDataField =
        AccessTools.Field(typeof(Heightmap), "m_buildData");

    private sealed class RuntimeState
    {
        internal bool CoverKnown;
        internal float Cover;
        internal bool UnderRoof;
        internal bool Tracked;
        internal bool DepthCached;
        internal Vector3 DepthPosition;
        internal float DepthMeters;
    }

    internal readonly struct EnvironmentStatus
    {
        internal EnvironmentStatus(
            float cover,
            bool canApplyBonus,
            float coverMultiplier,
            bool depthKnown,
            float depthMeters,
            float depthMultiplier)
        {
            Cover = cover;
            CanApplyBonus = canApplyBonus;
            CoverMultiplier = coverMultiplier;
            DepthKnown = depthKnown;
            DepthMeters = depthMeters;
            DepthMultiplier = depthMultiplier;
        }

        internal float Cover { get; }
        internal bool CanApplyBonus { get; }
        internal float CoverMultiplier { get; }
        internal bool DepthKnown { get; }
        internal float DepthMeters { get; }
        internal float DepthMultiplier { get; }
    }

    private static ConditionalWeakTable<Fermenter, RuntimeState> _runtimeStates = new();
    private static readonly List<WeakReference<Fermenter>> TrackedFermenters = new();

    [ThreadStatic]
    private static Fermenter? _coverUpdateTarget;

    internal static void RegisterLoaded(Fermenter fermenter)
    {
        if (!StationModule.IsInitialized)
        {
            return;
        }

        if (TrackedFermenters.Count > 0 && TrackedFermenters.Count % 64 == 0)
        {
            PruneDeadTrackedFermenters();
        }

        RuntimeState state = GetRuntimeState(fermenter);
        if (!state.Tracked)
        {
            state.Tracked = true;
            TrackedFermenters.Add(new WeakReference<Fermenter>(fermenter));
        }
    }

    internal static Fermenter? BeginCoverCapture(Fermenter fermenter)
    {
        Fermenter? previous = _coverUpdateTarget;
        _coverUpdateTarget = StationModule.IsInitialized
                             && !StationModule.IsFermenterBonusExcluded(fermenter)
            ? fermenter
            : null;
        return previous;
    }

    internal static void EndCoverCapture(Fermenter? previous)
    {
        _coverUpdateTarget = previous;
    }

    internal static void CaptureCover(float cover, bool underRoof)
    {
        Fermenter? fermenter = _coverUpdateTarget;
        if (fermenter == null || !IsFinite(cover))
        {
            return;
        }

        RuntimeState state = GetRuntimeState(fermenter);
        state.CoverKnown = true;
        state.Cover = Mathf.Clamp01(cover);
        state.UnderRoof = underRoof;
    }

    internal static void CheckpointOwner(Fermenter fermenter, bool force)
    {
        if (!StationModule.IsInitialized
            || !TryGetZdo(fermenter, requireOwner: true, out ZDO? zdo))
        {
            return;
        }

        int content = zdo!.GetInt(ZDOVars.s_content);
        long batchToken = zdo.GetLong(ZDOVars.s_startTime, 0L);
        if (content == 0 || batchToken <= 0L)
        {
            if (HasState(zdo))
            {
                ClearState(zdo);
            }

            return;
        }

        long nowTicks = GetCurrentTicks();
        if (nowTicks <= 0L)
        {
            return;
        }

        bool hasValidState = TryReadValidState(
            zdo,
            batchToken,
            out StateSnapshot snapshot);
        bool excluded = StationModule.IsFermenterBonusExcluded(fermenter);
        RuntimeState runtimeState = GetRuntimeState(fermenter);
        float currentBonusRate = excluded
            ? 0f
            : hasValidState && !runtimeState.CoverKnown
            ? snapshot.BonusRate
            : GetCurrentBonusRate(fermenter);

        if (!hasValidState)
        {
            if (excluded)
            {
                if (HasState(zdo))
                {
                    ClearState(zdo);
                }

                return;
            }

            WriteState(zdo, batchToken, 0L, nowTicks, currentBonusRate);
            return;
        }

        long pendingBonusTicks = ProjectPendingBonusTicks(
            fermenter,
            snapshot,
            nowTicks);
        long accumulatedBonusTicks = SafeAddAndClamp(
            snapshot.AccumulatedBonusTicks,
            pendingBonusTicks,
            GetMaximumTotalBonusTicks(fermenter, snapshot.AccumulatedBonusTicks));
        bool ratesChanged = Math.Abs(snapshot.BonusRate - currentBonusRate) > RateEpsilon;
        bool intervalElapsed = SecondsBetween(snapshot.LastCheckpointTicks, nowTicks)
                               >= CheckpointSeconds;
        bool completionReached = IsFinite(fermenter.m_fermentationDuration)
                                 && fermenter.m_fermentationDuration >= 0f
                                 && GetVanillaElapsed(zdo)
                                 + TimeSpan.FromTicks(accumulatedBonusTicks).TotalSeconds
                                 > fermenter.m_fermentationDuration;
        if (!force && !ratesChanged && !intervalElapsed && !completionReached)
        {
            return;
        }

        WriteState(
            zdo,
            batchToken,
            accumulatedBonusTicks,
            Math.Max(snapshot.LastCheckpointTicks, nowTicks),
            currentBonusRate);
    }

    internal static void CheckpointAllOwners()
    {
        for (int index = TrackedFermenters.Count - 1; index >= 0; index--)
        {
            WeakReference<Fermenter> reference = TrackedFermenters[index];
            if (!reference.TryGetTarget(out Fermenter fermenter)
                || fermenter == null)
            {
                TrackedFermenters.RemoveAt(index);
                continue;
            }

            CheckpointOwner(fermenter, force: true);
        }
    }

    internal static void ResetRuntime()
    {
        _coverUpdateTarget = null;
        TrackedFermenters.Clear();
        _runtimeStates = new ConditionalWeakTable<Fermenter, RuntimeState>();
    }

    internal static void CheckpointForView(ZNetView view)
    {
        if (!StationModule.IsInitialized || view == null)
        {
            return;
        }

        Fermenter? fermenter = view.GetComponent<Fermenter>()
                                ?? view.GetComponentInChildren<Fermenter>();
        if (fermenter != null)
        {
            CheckpointOwner(fermenter, force: true);
        }
    }

    internal static long GetBatchToken(Fermenter fermenter)
    {
        return TryGetZdo(fermenter, requireOwner: false, out ZDO? zdo)
            ? zdo!.GetLong(ZDOVars.s_startTime, 0L)
            : 0L;
    }

    internal static void NotifyBatchStartedOrReset(Fermenter fermenter)
    {
        if (!StationModule.IsInitialized
            || !TryGetZdo(fermenter, requireOwner: true, out ZDO? zdo))
        {
            return;
        }

        int content = zdo!.GetInt(ZDOVars.s_content);
        long batchToken = zdo.GetLong(ZDOVars.s_startTime, 0L);
        if (content == 0 || batchToken <= 0L)
        {
            ClearState(zdo);
            return;
        }

        if (TryReadValidState(zdo, batchToken, out _))
        {
            CheckpointOwner(fermenter, force: true);
            return;
        }

        if (StationModule.IsFermenterBonusExcluded(fermenter))
        {
            if (HasState(zdo))
            {
                ClearState(zdo);
            }

            return;
        }

        long nowTicks = GetCurrentTicks();
        if (nowTicks > 0L)
        {
            WriteState(
                zdo,
                batchToken,
                0L,
                nowTicks,
                GetCurrentBonusRate(fermenter));
        }
    }

    internal static void NotifyBatchCleared(Fermenter fermenter)
    {
        if (StationModule.IsInitialized
            && TryGetZdo(fermenter, requireOwner: true, out ZDO? zdo))
        {
            ClearState(zdo!);
        }
    }

    internal static bool HasContent(Fermenter fermenter)
    {
        return TryGetZdo(fermenter, requireOwner: false, out ZDO? zdo)
               && zdo!.GetInt(ZDOVars.s_content) != 0;
    }

    internal static double ProjectEffectiveElapsed(Fermenter fermenter, double vanillaElapsed)
    {
        if (!StationModule.IsInitialized
            || vanillaElapsed < 0d
            || !TryGetZdo(fermenter, requireOwner: false, out ZDO? zdo)
            || ZNet.instance == null)
        {
            return vanillaElapsed;
        }

        long batchToken = zdo!.GetLong(ZDOVars.s_startTime, 0L);
        if (batchToken <= 0L
            || zdo.GetInt(ZDOVars.s_content) == 0
            || !TryReadValidState(zdo, batchToken, out StateSnapshot snapshot))
        {
            return vanillaElapsed;
        }

        long pendingBonusTicks = ProjectPendingBonusTicks(
            fermenter,
            snapshot,
            GetCurrentTicks());
        long projectedBonusTicks = SafeAddAndClamp(
            snapshot.AccumulatedBonusTicks,
            pendingBonusTicks,
            GetMaximumTotalBonusTicks(fermenter, snapshot.AccumulatedBonusTicks));
        double projected = vanillaElapsed
                           + TimeSpan.FromTicks(projectedBonusTicks).TotalSeconds;
        return double.IsNaN(projected) || double.IsInfinity(projected)
            ? vanillaElapsed
            : Math.Max(vanillaElapsed, projected);
    }

    internal static bool TryGetRemainingSeconds(
        Fermenter fermenter,
        out double remainingSeconds,
        out float speedMultiplier)
    {
        remainingSeconds = 0d;
        speedMultiplier = 1f;
        if (!StationModule.IsInitialized
            || fermenter.m_fermentationDuration <= 0f
            || !TryGetZdo(fermenter, requireOwner: false, out ZDO? zdo)
            || zdo!.GetInt(ZDOVars.s_content) == 0)
        {
            return false;
        }

        double elapsed = GetVanillaElapsed(zdo);
        if (elapsed < 0d)
        {
            return false;
        }

        elapsed = ProjectEffectiveElapsed(fermenter, elapsed);
        bool excluded = StationModule.IsFermenterBonusExcluded(fermenter);
        RuntimeState runtimeState = GetRuntimeState(fermenter);
        float bonusRate = 0f;
        long batchToken = zdo.GetLong(ZDOVars.s_startTime, 0L);
        if (TryReadValidState(zdo, batchToken, out StateSnapshot snapshot))
        {
            bonusRate = excluded ? 0f : snapshot.BonusRate;
            if (!excluded && IsOwner(fermenter) && runtimeState.CoverKnown)
            {
                bonusRate = GetCurrentBonusRate(fermenter);
            }
        }

        speedMultiplier = Math.Max(1f, 1f + bonusRate);
        if (elapsed > fermenter.m_fermentationDuration)
        {
            remainingSeconds = 0d;
            return true;
        }

        if (excluded)
        {
            remainingSeconds = Math.Max(0d, fermenter.m_fermentationDuration - elapsed);
            return true;
        }

        GetCoverState(fermenter, runtimeState, out bool hasRoof, out bool exposed);
        if (!hasRoof || exposed)
        {
            return false;
        }

        remainingSeconds = Math.Max(
            0d,
            (fermenter.m_fermentationDuration - elapsed) / speedMultiplier);
        return true;
    }

    internal static EnvironmentStatus GetEnvironmentStatus(Fermenter fermenter)
    {
        if (StationModule.IsFermenterBonusExcluded(fermenter))
        {
            return new EnvironmentStatus(
                0f,
                canApplyBonus: false,
                coverMultiplier: 1f,
                depthKnown: false,
                depthMeters: 0f,
                depthMultiplier: 1f);
        }

        RuntimeState state = GetRuntimeState(fermenter);
        GetCoverState(fermenter, state, out bool underRoof, out bool exposed);
        float cover = state.CoverKnown
            ? state.Cover
            : exposed ? 0f : VanillaMinimumCover;
        cover = Mathf.Clamp01(cover);

        float coverMaximum = StationModule.IsInitialized
            ? SanitizeMultiplier(StationModule.FermenterCoverMaxSpeedMultiplier.Value)
            : 1f;
        float coverFactor = Mathf.InverseLerp(VanillaMinimumCover, 1f, cover);
        float coverMultiplier = Mathf.Lerp(1f, coverMaximum, coverFactor);

        bool depthKnown = TryGetDepthMeters(fermenter, state, out float depthMeters);
        float depthMultiplier = 1f;
        if (depthKnown)
        {
            float depthFactor = Mathf.Clamp01(depthMeters / MaximumDepthMeters);
            float depthMaximum = StationModule.IsInitialized
                ? SanitizeMultiplier(StationModule.FermenterDepthMaxSpeedMultiplier.Value)
                : 1f;
            depthMultiplier = Mathf.Lerp(1f, depthMaximum, depthFactor);
        }

        return new EnvironmentStatus(
            cover,
            underRoof && cover >= VanillaMinimumCover,
            coverMultiplier,
            depthKnown,
            depthMeters,
            depthMultiplier);
    }

    internal static bool IsOwner(Fermenter fermenter)
    {
        ZNetView? view = GetView(fermenter);
        return view != null && view.IsValid() && view.IsOwner();
    }

    private static float GetCurrentBonusRate(Fermenter fermenter)
    {
        if (StationModule.IsFermenterBonusExcluded(fermenter))
        {
            return 0f;
        }

        EnvironmentStatus environment = GetEnvironmentStatus(fermenter);
        if (!environment.CanApplyBonus)
        {
            return 0f;
        }

        float multiplier = environment.CoverMultiplier * environment.DepthMultiplier;
        return IsFinite(multiplier) ? Math.Max(0f, multiplier - 1f) : 0f;
    }

    private static bool TryGetDepthMeters(
        Fermenter fermenter,
        RuntimeState state,
        out float depthMeters)
    {
        Vector3 position = fermenter.transform.position;
        if (state.DepthCached
            && (state.DepthPosition - position).sqrMagnitude <= 0.0001f)
        {
            depthMeters = state.DepthMeters;
            return true;
        }

        if (!TryGetOriginalTerrainBaselineY(position, out float baselineY))
        {
            depthMeters = 0f;
            return false;
        }

        depthMeters = baselineY - position.y;
        if (!IsFinite(depthMeters))
        {
            depthMeters = 0f;
            return false;
        }

        depthMeters = Mathf.Max(0f, depthMeters);
        state.DepthCached = true;
        state.DepthPosition = position;
        state.DepthMeters = depthMeters;
        return true;
    }

    private static bool TryGetOriginalTerrainBaselineY(
        Vector3 worldPosition,
        out float baselineY)
    {
        baselineY = 0f;
        if (!IsFinite(worldPosition.x)
            || !IsFinite(worldPosition.y)
            || !IsFinite(worldPosition.z))
        {
            return false;
        }

        Heightmap heightmap = Heightmap.FindHeightmap(worldPosition);
        if (heightmap == null || heightmap.IsDistantLod)
        {
            return false;
        }

        int width = heightmap.m_width;
        float scale = heightmap.m_scale;
        HeightmapBuilder.HMBuildData? buildData = ReadField<HeightmapBuilder.HMBuildData>(
            HeightmapBuildDataField,
            heightmap);
        if (width <= 0
            || !IsFinite(scale)
            || scale <= 0f
            || buildData == null
            || buildData.m_baseHeights == null
            || buildData.m_width != width
            || buildData.m_center != heightmap.transform.position
            || !Mathf.Approximately(buildData.m_scale, scale))
        {
            return false;
        }

        long strideLong = (long)width + 1L;
        long expectedCount = strideLong * strideLong;
        if (expectedCount > int.MaxValue
            || buildData.m_baseHeights.Count != (int)expectedCount)
        {
            return false;
        }

        Vector3 localPoint = heightmap.transform.InverseTransformPoint(worldPosition);
        float halfWidth = width * scale * 0.5f;
        float gridX = (localPoint.x + halfWidth) / scale;
        float gridZ = (localPoint.z + halfWidth) / scale;
        if (!IsFinite(halfWidth)
            || !IsFinite(gridX)
            || !IsFinite(gridZ)
            || gridX < -HeightmapEdgeEpsilon
            || gridZ < -HeightmapEdgeEpsilon
            || gridX > width + HeightmapEdgeEpsilon
            || gridZ > width + HeightmapEdgeEpsilon)
        {
            return false;
        }

        gridX = Mathf.Clamp(gridX, 0f, width);
        gridZ = Mathf.Clamp(gridZ, 0f, width);
        int cellX = Mathf.Min(Mathf.FloorToInt(gridX), width - 1);
        int cellZ = Mathf.Min(Mathf.FloorToInt(gridZ), width - 1);
        float factorX = gridX - cellX;
        float factorZ = gridZ - cellZ;

        int stride = width + 1;
        int row0 = cellZ * stride;
        int row1 = (cellZ + 1) * stride;
        float height00 = buildData.m_baseHeights[row0 + cellX];
        float height10 = buildData.m_baseHeights[row0 + cellX + 1];
        float height01 = buildData.m_baseHeights[row1 + cellX];
        float height11 = buildData.m_baseHeights[row1 + cellX + 1];
        if (!IsFinite(height00)
            || !IsFinite(height10)
            || !IsFinite(height01)
            || !IsFinite(height11))
        {
            return false;
        }

        float localHeight = factorX + factorZ <= 1f
            ? height00
              + (height10 - height00) * factorX
              + (height01 - height00) * factorZ
            : height11
              + (height01 - height11) * (1f - factorX)
              + (height10 - height11) * (1f - factorZ);
        float sampleLocalX = gridX * scale - halfWidth;
        float sampleLocalZ = gridZ * scale - halfWidth;
        float worldY = heightmap.transform.TransformPoint(
            new Vector3(sampleLocalX, localHeight, sampleLocalZ)).y;
        if (!IsFinite(worldY))
        {
            return false;
        }

        baselineY = worldY;
        return true;
    }

    private static long ProjectPendingBonusTicks(
        Fermenter fermenter,
        StateSnapshot snapshot,
        long nowTicks)
    {
        if (StationModule.IsFermenterBonusExcluded(fermenter)
            || nowTicks <= snapshot.LastCheckpointTicks
            || snapshot.BonusRate <= 0f)
        {
            return 0L;
        }

        double bonusTicks = (nowTicks - snapshot.LastCheckpointTicks)
                            * (double)snapshot.BonusRate;
        long maximumTotalBonusTicks = GetMaximumTotalBonusTicks(
            fermenter,
            snapshot.AccumulatedBonusTicks);
        long remainingCapacity = Math.Max(
            0L,
            maximumTotalBonusTicks - snapshot.AccumulatedBonusTicks);
        if (double.IsNaN(bonusTicks) || bonusTicks <= 0d || remainingCapacity <= 0L)
        {
            return 0L;
        }

        return bonusTicks >= remainingCapacity
            ? remainingCapacity
            : Math.Max(0L, (long)Math.Round(bonusTicks));
    }

    private static long GetMaximumTotalBonusTicks(
        Fermenter fermenter,
        long accumulatedBonusTicks)
    {
        double maximumSeconds = IsFinite(fermenter.m_fermentationDuration)
            ? Math.Max(0d, fermenter.m_fermentationDuration + 1d)
            : 0d;
        double ticks = maximumSeconds * TimeSpan.TicksPerSecond;
        long durationLimit = ticks >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(ticks);
        return Math.Max(Math.Max(0L, accumulatedBonusTicks), durationLimit);
    }

    private static long SafeAddAndClamp(long left, long right, long maximum)
    {
        left = Math.Max(0L, left);
        maximum = Math.Max(left, maximum);
        right = Math.Max(0L, right);
        return right > maximum - left ? maximum : left + right;
    }

    private static bool TryReadValidState(
        ZDO zdo,
        long batchToken,
        out StateSnapshot snapshot)
    {
        snapshot = new StateSnapshot(
            zdo.GetLong(BatchTokenKey, 0L),
            zdo.GetLong(AccumulatedBonusTicksKey, 0L),
            zdo.GetLong(LastCheckpointTicksKey, 0L),
            ReadBonusRate(zdo));
        return batchToken > 0L
               && snapshot.BatchToken == batchToken
               && snapshot.AccumulatedBonusTicks >= 0L
               && snapshot.LastCheckpointTicks > 0L;
    }

    private static void WriteState(
        ZDO zdo,
        long batchToken,
        long accumulatedBonusTicks,
        long lastCheckpointTicks,
        float bonusRate)
    {
        zdo.Set(BatchTokenKey, Math.Max(0L, batchToken));
        zdo.Set(AccumulatedBonusTicksKey, Math.Max(0L, accumulatedBonusTicks));
        zdo.Set(LastCheckpointTicksKey, Math.Max(0L, lastCheckpointTicks));
        zdo.Set(BonusRateKey, IsFinite(bonusRate) ? Math.Max(0f, bonusRate) : 0f);
    }

    private static void ClearState(ZDO zdo)
    {
        WriteState(zdo, 0L, 0L, 0L, 0f);
    }

    private static bool HasState(ZDO zdo)
    {
        return zdo.GetLong(BatchTokenKey, 0L) > 0L
               || zdo.GetLong(AccumulatedBonusTicksKey, 0L) > 0L
               || zdo.GetLong(LastCheckpointTicksKey, 0L) > 0L
               || ReadBonusRate(zdo) > 0f;
    }

    private static bool TryGetZdo(
        Fermenter fermenter,
        bool requireOwner,
        out ZDO? zdo)
    {
        ZNetView? view = GetView(fermenter);
        zdo = view != null
              && view.IsValid()
              && (!requireOwner || view.IsOwner())
            ? view.GetZDO()
            : null;
        return zdo != null;
    }

    private static ZNetView? GetView(Fermenter fermenter) =>
        fermenter != null ? fermenter.GetComponent<ZNetView>() : null;

    private static double GetVanillaElapsed(ZDO zdo)
    {
        long startTicks = zdo.GetLong(ZDOVars.s_startTime, 0L);
        if (startTicks <= 0L
            || startTicks > DateTime.MaxValue.Ticks
            || ZNet.instance == null)
        {
            return -1d;
        }

        return (ZNet.instance.GetTime() - new DateTime(startTicks)).TotalSeconds;
    }

    private static void GetCoverState(
        Fermenter fermenter,
        RuntimeState state,
        out bool hasRoof,
        out bool exposed)
    {
        if (state.CoverKnown)
        {
            hasRoof = state.UnderRoof;
            exposed = state.Cover < VanillaMinimumCover;
            return;
        }

        hasRoof = ReadField(FermenterHasRoofField, fermenter, fallback: false);
        exposed = ReadField(FermenterExposedField, fermenter, fallback: true);
    }

    private static bool ReadField(FieldInfo? field, object instance, bool fallback)
    {
        try
        {
            return field?.GetValue(instance) is bool value ? value : fallback;
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogDebug(
                $"Could not read {field?.DeclaringType?.Name}.{field?.Name}: {exception.Message}");
            return fallback;
        }
    }

    private static T? ReadField<T>(FieldInfo? field, object instance)
        where T : class
    {
        try
        {
            return field?.GetValue(instance) as T;
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogDebug(
                $"Could not read {field?.DeclaringType?.Name}.{field?.Name}: {exception.Message}");
            return null;
        }
    }

    private static RuntimeState GetRuntimeState(Fermenter fermenter) =>
        _runtimeStates.GetValue(fermenter, _ => new RuntimeState());

    private static float ReadBonusRate(ZDO zdo)
    {
        float value = zdo.GetFloat(BonusRateKey, 0f);
        return IsFinite(value) ? Math.Max(0f, value) : 0f;
    }

    private static float SanitizeMultiplier(float value) =>
        IsFinite(value) ? Math.Max(1f, value) : 1f;

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    private static long GetCurrentTicks() =>
        ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;

    private static double SecondsBetween(long startTicks, long endTicks) =>
        startTicks > 0L && endTicks > startTicks
            ? TimeSpan.FromTicks(endTicks - startTicks).TotalSeconds
            : 0d;

    private static void PruneDeadTrackedFermenters()
    {
        for (int index = TrackedFermenters.Count - 1; index >= 0; index--)
        {
            WeakReference<Fermenter> reference = TrackedFermenters[index];
            if (!reference.TryGetTarget(out Fermenter fermenter)
                || fermenter == null)
            {
                TrackedFermenters.RemoveAt(index);
            }
        }
    }

    private readonly struct StateSnapshot
    {
        internal StateSnapshot(
            long batchToken,
            long accumulatedBonusTicks,
            long lastCheckpointTicks,
            float bonusRate)
        {
            BatchToken = batchToken;
            AccumulatedBonusTicks = accumulatedBonusTicks;
            LastCheckpointTicks = lastCheckpointTicks;
            BonusRate = bonusRate;
        }

        internal long BatchToken { get; }
        internal long AccumulatedBonusTicks { get; }
        internal long LastCheckpointTicks { get; }
        internal float BonusRate { get; }
    }
}

[HarmonyPatch(typeof(Fermenter), "Awake")]
internal static class StationFermenterEnvironmentAwakePatch
{
    private static void Prefix(Fermenter __instance)
    {
        FermenterEnvironmentSpeedSystem.RegisterLoaded(__instance);
    }

    private static Exception? Finalizer(Exception? __exception, Fermenter __instance)
    {
        if (__exception == null && FermenterEnvironmentSpeedSystem.IsOwner(__instance))
        {
            try
            {
                FermenterEnvironmentSpeedSystem.CheckpointOwner(__instance, force: true);
            }
            catch (Exception exception)
            {
                FineDiningPlugin.Log.LogWarning(
                    $"Could not initialize fermenter environment state: {exception.Message}");
            }
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(Fermenter), "SlowUpdate")]
internal static class StationFermenterEnvironmentSlowUpdatePatch
{
    private static void Postfix(Fermenter __instance)
    {
        if (FermenterEnvironmentSpeedSystem.IsOwner(__instance))
        {
            FermenterEnvironmentSpeedSystem.CheckpointOwner(__instance, force: false);
        }
    }
}

[HarmonyPatch(typeof(Fermenter), "UpdateCover")]
internal static class StationFermenterEnvironmentCoverScopePatch
{
    private static void Prefix(Fermenter __instance, out Fermenter? __state)
    {
        __state = FermenterEnvironmentSpeedSystem.BeginCoverCapture(__instance);
    }

    private static Exception? Finalizer(Exception? __exception, Fermenter? __state)
    {
        FermenterEnvironmentSpeedSystem.EndCoverCapture(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Cover), nameof(Cover.GetCoverForPoint))]
internal static class StationFermenterEnvironmentCoverResultPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ref float coverPercentage, ref bool underRoof)
    {
        FermenterEnvironmentSpeedSystem.CaptureCover(coverPercentage, underRoof);
    }
}

[HarmonyPatch(typeof(Fermenter), "GetFermentationTime")]
internal static class StationFermenterEnvironmentElapsedPatch
{
    private static void Postfix(Fermenter __instance, ref double __result)
    {
        __result = FermenterEnvironmentSpeedSystem.ProjectEffectiveElapsed(
            __instance,
            __result);
    }
}

[HarmonyPatch(typeof(Fermenter), "RPC_AddItem")]
internal static class StationFermenterEnvironmentAddPatch
{
    private static void Postfix(Fermenter __instance)
    {
        FermenterEnvironmentSpeedSystem.NotifyBatchStartedOrReset(__instance);
    }
}

[HarmonyPatch(typeof(Fermenter), "ResetFermentationTimer")]
internal static class StationFermenterEnvironmentResetPatch
{
    private static void Postfix(Fermenter __instance)
    {
        FermenterEnvironmentSpeedSystem.NotifyBatchStartedOrReset(__instance);
    }
}

[HarmonyPatch(typeof(Fermenter), "RPC_Tap")]
internal static class StationFermenterEnvironmentTapPatch
{
    private static void Prefix(Fermenter __instance, out bool __state)
    {
        __state = FermenterEnvironmentSpeedSystem.HasContent(__instance);
        if (__state)
        {
            FermenterEnvironmentSpeedSystem.CheckpointOwner(__instance, force: true);
        }
    }

    private static void Postfix(Fermenter __instance, bool __state)
    {
        if (__state && !FermenterEnvironmentSpeedSystem.HasContent(__instance))
        {
            FermenterEnvironmentSpeedSystem.NotifyBatchCleared(__instance);
        }
    }
}

[HarmonyPatch(typeof(Fermenter), "DropAllItems")]
internal static class StationFermenterEnvironmentDropPatch
{
    private static void Prefix(Fermenter __instance, out bool __state)
    {
        __state = FermenterEnvironmentSpeedSystem.HasContent(__instance);
        if (__state)
        {
            FermenterEnvironmentSpeedSystem.CheckpointOwner(__instance, force: true);
        }
    }

    private static void Postfix(Fermenter __instance, bool __state)
    {
        if (__state && !FermenterEnvironmentSpeedSystem.HasContent(__instance))
        {
            FermenterEnvironmentSpeedSystem.NotifyBatchCleared(__instance);
        }
    }
}

[HarmonyPatch(typeof(ZNetView), nameof(ZNetView.ResetZDO))]
internal static class StationFermenterEnvironmentResetZdoPatch
{
    private static void Prefix(ZNetView __instance)
    {
        FermenterEnvironmentSpeedSystem.CheckpointForView(__instance);
    }
}

[HarmonyPatch(typeof(ZNetView), "OnDestroy")]
internal static class StationFermenterEnvironmentViewDestroyedPatch
{
    private static void Prefix(ZNetView __instance)
    {
        FermenterEnvironmentSpeedSystem.CheckpointForView(__instance);
    }
}
