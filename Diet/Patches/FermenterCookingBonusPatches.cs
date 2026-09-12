using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace FineDining;

internal static class FermenterCookingBonusSystem
{
    private const string RequestTapRpc = "FineDining_Fermenter_RequestTap";
    private const string TapCompletedRpc = "FineDining_Fermenter_TapCompleted";
    internal const float CookingExperienceOnAdd = 0.4f;
    private const float CookingExperienceOnCollect = 0.6f;
    private const float RequestTimeoutSeconds = 10f;
    private const float InteractionDistanceTolerance = 2f;

    private const BindingFlags InstanceMemberFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo? GetStatusMethod =
        typeof(Fermenter).GetMethod(
            "GetStatus",
            InstanceMemberFlags,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
    private static readonly MethodInfo? RpcTapMethod =
        typeof(Fermenter).GetMethod(
            "RPC_Tap",
            InstanceMemberFlags,
            binder: null,
            new[] { typeof(long) },
            modifiers: null);
    private static readonly FieldInfo? DelayedTapItemField =
        typeof(Fermenter).GetField("m_delayedTapItem", InstanceMemberFlags);
    private static readonly FieldInfo? DelayedTapCheatedField =
        typeof(Fermenter).GetField("m_delayedTapItemCheated", InstanceMemberFlags);

    private static ConditionalWeakTable<Fermenter, ClientTapRequest> ClientRequests = new();
    private static ConditionalWeakTable<Fermenter, OwnerTapContext> OwnerTapContexts = new();

    private static int _nextRequestId = 1;
    private static bool _contractWarningLogged;
    private static bool _requestWarningLogged;
    private static bool _completionWarningLogged;

    internal static void ResetRuntime()
    {
        ClientRequests = new ConditionalWeakTable<Fermenter, ClientTapRequest>();
        OwnerTapContexts = new ConditionalWeakTable<Fermenter, OwnerTapContext>();
        _nextRequestId = 1;
        _contractWarningLogged = false;
        _requestWarningLogged = false;
        _completionWarningLogged = false;
    }

    internal static void RegisterRpcs(Fermenter fermenter)
    {
        if ((UnityObject)(object)fermenter == null || !HasRuntimeContract())
        {
            return;
        }

        ZNetView nview = GetNView(fermenter);
        if ((UnityObject)(object)nview == null || !nview.IsValid())
        {
            return;
        }

        nview.Unregister(RequestTapRpc);
        nview.Unregister(TapCompletedRpc);
        nview.Register<int, long, long, int>(
            RequestTapRpc,
            (sender, requestId, playerId, batchTicks, skillPermille) =>
                HandleTapRequest(
                    fermenter,
                    sender,
                    requestId,
                    playerId,
                    batchTicks,
                    skillPermille));
        nview.Register<int, long, int, bool>(
            TapCompletedRpc,
            (sender, requestId, batchTicks, bonusCount, completed) =>
                HandleTapCompleted(
                    fermenter,
                    sender,
                    requestId,
                    batchTicks,
                    bonusCount,
                    completed));
    }

    internal static void RequestTap(
        ZNetView nview,
        string vanillaRpcName,
        object[] vanillaParameters,
        Humanoid user,
        Fermenter fermenter)
    {
        if ((UnityObject)(object)nview == null)
        {
            return;
        }

        if ((UnityObject)(object)fermenter == null
            || user is not Player player
            || (UnityObject)(object)player == null
            || !HasRuntimeContract())
        {
            nview.InvokeRPC(vanillaRpcName, vanillaParameters);
            return;
        }

        if (StationModule.IsFermenterBonusExcluded(fermenter))
        {
            ClientRequests.Remove(fermenter);
            nview.InvokeRPC(vanillaRpcName, vanillaParameters);
            return;
        }

        ZDO zdo = nview.GetZDO();
        if (zdo == null || !IsReady(fermenter))
        {
            nview.InvokeRPC(vanillaRpcName, vanillaParameters);
            return;
        }

        if (ClientRequests.TryGetValue(fermenter, out ClientTapRequest pending))
        {
            if (Time.unscaledTime - pending.StartedAt < RequestTimeoutSeconds)
            {
                return;
            }

            ClientRequests.Remove(fermenter);
        }

        long owner = zdo.GetOwner();
        long batchTicks = FermenterEnvironmentSpeedSystem.GetBatchToken(fermenter);
        if (owner == 0L || batchTicks == 0L)
        {
            nview.InvokeRPC(vanillaRpcName, vanillaParameters);
            return;
        }

        float skillFactor = player.GetSkillFactor(Skills.SkillType.Cooking);
        if (float.IsNaN(skillFactor) || skillFactor <= 0f)
        {
            skillFactor = 0f;
        }
        else
        {
            skillFactor = Mathf.Min(1f, skillFactor);
        }

        int requestId = NextRequestId();
        int skillPermille = Mathf.RoundToInt(skillFactor * 1000f);
        ClientRequests.Add(
            fermenter,
            new ClientTapRequest(
                requestId,
                player.GetPlayerID(),
                owner,
                batchTicks,
                Time.unscaledTime));

        try
        {
            nview.InvokeRPC(
                owner,
                RequestTapRpc,
                requestId,
                player.GetPlayerID(),
                batchTicks,
                skillPermille);
        }
        catch (Exception exception)
        {
            ClientRequests.Remove(fermenter);
            LogRequestFailure(exception);
            nview.InvokeRPC(vanillaRpcName, vanillaParameters);
        }
    }

    internal static OwnerTapContext? TakeOwnerTapContext(Fermenter fermenter)
    {
        if (!OwnerTapContexts.TryGetValue(fermenter, out OwnerTapContext context))
        {
            return null;
        }

        OwnerTapContexts.Remove(fermenter);
        if (StationModule.IsFermenterBonusExcluded(fermenter))
        {
            RejectTapRequest(
                fermenter,
                context.Sender,
                context.RequestId,
                context.BatchTicks);
            return null;
        }

        return context;
    }

    internal static void CompleteDelayedTap(
        Fermenter fermenter,
        OwnerTapContext? context)
    {
        if (context == null)
        {
            return;
        }

        if (StationModule.IsFermenterBonusExcluded(fermenter))
        {
            RejectTapRequest(
                fermenter,
                context.Sender,
                context.RequestId,
                context.BatchTicks);
            return;
        }

        int spawnedBonusItems = 0;
        try
        {
            Fermenter.ItemConversion? conversion = GetItemConversion(
                fermenter,
                GetDelayedTapItem(fermenter));
            if (conversion == null
                || (UnityObject)(object)conversion.m_to == null
                || conversion.m_producedItems <= 0)
            {
                SendTapResponse(
                    fermenter,
                    context.Sender,
                    context.RequestId,
                    context.BatchTicks,
                    spawnedBonusItems,
                    completed: false);
                return;
            }

            if (conversion.m_producedItems != context.BaseItemCount
                || conversion.m_to.gameObject.name != context.OutputPrefabName)
            {
                SendTapResponse(
                    fermenter,
                    context.Sender,
                    context.RequestId,
                    context.BatchTicks,
                    spawnedBonusItems,
                    completed: true);
                return;
            }

            int bonusToSpawn = Math.Min(
                Math.Max(0, context.BonusItemCount),
                context.BaseItemCount);
            Vector3 spawnPosition = (UnityObject)(object)fermenter.m_outputPoint != null
                ? fermenter.m_outputPoint.position + Vector3.up * 0.3f
                : fermenter.transform.position + Vector3.up;
            for (int item = 0; item < bonusToSpawn; item++)
            {
                ItemDrop.OnCreateNew(
                    UnityObject.Instantiate<ItemDrop>(
                        conversion.m_to,
                        spawnPosition,
                        Quaternion.identity),
                    ((DelayedTapCheatedField!.GetValue(fermenter) is true) || GetNView(fermenter).GetZDO().GetBool(ZDOVars.s_cheated))
                    && !PlayerProfile.s_bypassCheatChecks);
                spawnedBonusItems++;
            }
        }
        catch (Exception exception)
        {
            LogCompletionFailure(exception);
        }

        SendTapResponse(
            fermenter,
            context.Sender,
            context.RequestId,
            context.BatchTicks,
            spawnedBonusItems,
            completed: true);
    }

    private static void SendTapResponse(
        Fermenter fermenter,
        long recipient,
        int requestId,
        long batchTicks,
        int spawnedBonusItems,
        bool completed)
    {
        try
        {
            ZNetView nview = GetNView(fermenter);
            if ((UnityObject)(object)nview != null
                && nview.IsValid()
                && recipient != 0L)
            {
                nview.InvokeRPC(
                    recipient,
                    TapCompletedRpc,
                    requestId,
                    batchTicks,
                    Math.Max(0, spawnedBonusItems),
                    completed);
            }
        }
        catch (Exception exception)
        {
            LogCompletionFailure(exception);
        }
    }

    private static void HandleTapRequest(
        Fermenter fermenter,
        long sender,
        int requestId,
        long playerId,
        long batchTicks,
        int skillPermille)
    {
        if (sender == 0L || requestId <= 0)
        {
            return;
        }

        if ((UnityObject)(object)fermenter == null)
        {
            return;
        }

        ZNetView nview = GetNView(fermenter);
        if (skillPermille < 0
            || skillPermille > 1000
            || (UnityObject)(object)nview == null
            || !nview.IsValid()
            || !nview.IsOwner()
            || !HasRuntimeContract()
            || !IsReady(fermenter)
            || OwnerTapContexts.TryGetValue(fermenter, out _))
        {
            RejectTapRequest(fermenter, sender, requestId, batchTicks);
            return;
        }

        ZDO zdo = nview.GetZDO();
        if (zdo == null
            || batchTicks == 0L
            || FermenterEnvironmentSpeedSystem.GetBatchToken(fermenter) != batchTicks)
        {
            RejectTapRequest(fermenter, sender, requestId, batchTicks);
            return;
        }

        Player? requester = FindRequestingPlayer(sender, playerId);
        if (requester is null
            || (UnityObject)requester == null
            || requester.IsDead()
            || requester.IsTeleporting()
            || !IsWithinInteractionDistance(requester, fermenter))
        {
            RejectTapRequest(fermenter, sender, requestId, batchTicks);
            return;
        }

        if (StationModule.IsFermenterBonusExcluded(fermenter))
        {
            HandleExcludedTapRequest(
                fermenter,
                sender,
                requestId,
                batchTicks);
            return;
        }

        int content = GetContent(nview);
        Fermenter.ItemConversion? conversion = GetItemConversion(fermenter, content);
        if (content == 0
            || conversion == null
            || (UnityObject)(object)conversion.m_to == null
            || conversion.m_producedItems <= 0)
        {
            RejectTapRequest(fermenter, sender, requestId, batchTicks);
            return;
        }

        int baseItemCount = conversion.m_producedItems;
        string outputPrefabName = conversion.m_to.gameObject.name;
        int bonusItemCount = 0;
        if (conversion.m_to.m_itemData.m_shared.m_maxStackSize > 1)
        {
            bonusItemCount = CookingProductionBonusSystem.RollConfiguredBonusItems(
                outputPrefabName,
                baseItemCount,
                skillPermille / 1000f,
                DietConfig.GetFermenterOutputBonusChanceAtMaxCookingPercent());
        }

        OwnerTapContexts.Add(
            fermenter,
            new OwnerTapContext(
                sender,
                requestId,
                batchTicks,
                outputPrefabName,
                baseItemCount,
                bonusItemCount));

        bool delayedTapWasAlreadyScheduled = HasScheduledDelayedTap(fermenter);
        try
        {
            RpcTapMethod!.Invoke(fermenter, new object[] { sender });
            if (GetContent(nview) != 0
                && !TryRecoverScheduledTap(
                    fermenter,
                    batchTicks,
                    delayedTapWasAlreadyScheduled))
            {
                RejectOwnerTap(
                    fermenter,
                    sender,
                    requestId,
                    batchTicks);
            }
        }
        catch (Exception exception)
        {
            LogRequestFailure(exception);
            if (!TryRecoverScheduledTap(
                    fermenter,
                    batchTicks,
                    delayedTapWasAlreadyScheduled))
            {
                RejectOwnerTap(
                    fermenter,
                    sender,
                    requestId,
                    batchTicks);
            }
        }
    }

    private static void HandleExcludedTapRequest(
        Fermenter fermenter,
        long sender,
        int requestId,
        long batchTicks)
    {
        if (HasScheduledDelayedTap(fermenter))
        {
            RejectTapRequest(fermenter, sender, requestId, batchTicks);
            return;
        }

        try
        {
            RpcTapMethod!.Invoke(fermenter, new object[] { sender });
        }
        catch (Exception exception)
        {
            LogRequestFailure(exception);
        }

        // An outdated client may still use the custom request RPC after this
        // fermenter becomes excluded. Let the validated tap follow the vanilla
        // path, but explicitly terminate the custom request without bonus or XP.
        RejectTapRequest(fermenter, sender, requestId, batchTicks);
    }

    private static void RejectOwnerTap(
        Fermenter fermenter,
        long recipient,
        int requestId,
        long batchTicks)
    {
        OwnerTapContexts.Remove(fermenter);
        RejectTapRequest(fermenter, recipient, requestId, batchTicks);
    }

    private static void RejectTapRequest(
        Fermenter fermenter,
        long recipient,
        int requestId,
        long batchTicks)
    {
        SendTapResponse(
            fermenter,
            recipient,
            requestId,
            batchTicks,
            0,
            completed: false);
    }

    private static bool HasScheduledDelayedTap(Fermenter fermenter)
    {
        try
        {
            return fermenter.IsInvoking("DelayedTap");
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRecoverScheduledTap(
        Fermenter fermenter,
        long batchTicks,
        bool delayedTapWasAlreadyScheduled)
    {
        if (delayedTapWasAlreadyScheduled || !HasScheduledDelayedTap(fermenter))
        {
            return false;
        }

        try
        {
            ZNetView nview = GetNView(fermenter);
            if ((UnityObject)(object)nview == null)
            {
                return false;
            }

            ZDO zdo = nview.GetZDO();
            if (zdo == null)
            {
                return false;
            }

            long currentBatchTicks = FermenterEnvironmentSpeedSystem.GetBatchToken(fermenter);
            if (currentBatchTicks == 0L)
            {
                if (GetContent(nview) != 0)
                {
                    zdo.Set(ZDOVars.s_content, 0);
                    zdo.Set(ZDOVars.s_cheatedQueued, false);
                }

                FermenterEnvironmentSpeedSystem.NotifyBatchCleared(fermenter);
                return true;
            }

            if (currentBatchTicks != batchTicks)
            {
                return false;
            }

            zdo.Set(ZDOVars.s_content, 0);
            zdo.Set(ZDOVars.s_cheatedQueued, false);
            FermenterEnvironmentSpeedSystem.NotifyBatchCleared(fermenter);
            return true;
        }
        catch (Exception exception)
        {
            LogRequestFailure(exception);
            return false;
        }
    }

    internal static void RejectDelayedTap(
        Fermenter fermenter,
        OwnerTapContext? context)
    {
        if (context != null)
        {
            RejectTapRequest(
                fermenter,
                context.Sender,
                context.RequestId,
                context.BatchTicks);
        }
    }

    private static void HandleTapCompleted(
        Fermenter fermenter,
        long sender,
        int requestId,
        long batchTicks,
        int bonusItemCount,
        bool completed)
    {
        if (!ClientRequests.TryGetValue(fermenter, out ClientTapRequest pending)
            || pending.RequestId != requestId
            || pending.BatchTicks != batchTicks
            || pending.Owner != sender)
        {
            return;
        }

        ClientRequests.Remove(fermenter);
        if (!completed
            || StationModule.IsFermenterBonusExcluded(fermenter))
        {
            return;
        }

        Player player = Player.m_localPlayer;
        if ((UnityObject)(object)player == null || player.GetPlayerID() != pending.PlayerId)
        {
            return;
        }

        player.RaiseSkill(
            Skills.SkillType.Cooking,
            CookingExperienceOnCollect);
        if (bonusItemCount <= 0)
        {
            return;
        }

        Vector3 effectPosition = (UnityObject)(object)fermenter.m_outputPoint != null
            ? fermenter.m_outputPoint.position + Vector3.up
            : fermenter.transform.position + Vector3.up;
        CookingProductionBonusSystem.ShowBonusEffect(
            effectPosition,
            bonusItemCount);
    }

    private static Player? FindRequestingPlayer(long sender, long playerId)
    {
        foreach (Player player in Player.GetAllPlayers())
        {
            if ((UnityObject)(object)player != null
                && player.GetPlayerID() == playerId
                && player.GetOwner() == sender)
            {
                return player;
            }
        }

        return null;
    }

    private static bool IsWithinInteractionDistance(
        Player player,
        Fermenter fermenter)
    {
        float maximumDistance = Mathf.Max(
            1f,
            player.m_maxInteractDistance + InteractionDistanceTolerance);
        return (player.transform.position - fermenter.transform.position).sqrMagnitude
               <= maximumDistance * maximumDistance;
    }

    private static ZNetView GetNView(Fermenter fermenter)
    {
        return fermenter.GetComponent<ZNetView>();
    }

    private static int GetContent(ZNetView nview)
    {
        ZDO zdo = nview.GetZDO();
        return zdo == null ? 0 : zdo.GetInt(ZDOVars.s_content);
    }

    private static bool IsReady(Fermenter fermenter)
    {
        try
        {
            object? status = GetStatusMethod?.Invoke(fermenter, null);
            return string.Equals(status?.ToString(), "Ready", StringComparison.Ordinal);
        }
        catch (Exception exception)
        {
            LogRequestFailure(exception);
            return false;
        }
    }

    private static int GetDelayedTapItem(Fermenter fermenter)
    {
        try
        {
            return DelayedTapItemField?.GetValue(fermenter) is int hash ? hash : 0;
        }
        catch (Exception exception)
        {
            LogCompletionFailure(exception);
            return 0;
        }
    }

    private static Fermenter.ItemConversion? GetItemConversion(
        Fermenter fermenter,
        int itemHash)
    {
        if (fermenter.m_conversion == null)
        {
            return null;
        }

        foreach (Fermenter.ItemConversion conversion in fermenter.m_conversion)
        {
            if (conversion?.m_from != null
                && conversion.m_from.gameObject.name.GetStableHashCode() == itemHash)
            {
                return conversion;
            }
        }

        return null;
    }

    private static bool HasRuntimeContract()
    {
        if (GetStatusMethod != null
            && RpcTapMethod != null
            && DelayedTapItemField?.FieldType == typeof(int)
            && DelayedTapCheatedField?.FieldType == typeof(bool))
        {
            return true;
        }

        if (!_contractWarningLogged)
        {
            _contractWarningLogged = true;
            FineDiningPlugin.Log.LogWarning(
                "Fermenter Cooking bonus compatibility is unavailable because the expected private game members were not found; vanilla tapping remains active.");
        }

        return false;
    }

    private static int NextRequestId()
    {
        if (_nextRequestId <= 0 || _nextRequestId == int.MaxValue)
        {
            _nextRequestId = 1;
        }

        return _nextRequestId++;
    }

    private static void LogRequestFailure(Exception exception)
    {
        if (_requestWarningLogged)
        {
            return;
        }

        _requestWarningLogged = true;
        FineDiningPlugin.Log.LogWarning(
            $"Fermenter Cooking bonus request failed; vanilla output remains available ({exception}).");
    }

    private static void LogCompletionFailure(Exception exception)
    {
        if (_completionWarningLogged)
        {
            return;
        }

        _completionWarningLogged = true;
        FineDiningPlugin.Log.LogWarning(
            $"Fermenter Cooking bonus completion failed ({exception}).");
    }

    internal sealed class OwnerTapContext
    {
        internal OwnerTapContext(
            long sender,
            int requestId,
            long batchTicks,
            string outputPrefabName,
            int baseItemCount,
            int bonusItemCount)
        {
            Sender = sender;
            RequestId = requestId;
            BatchTicks = batchTicks;
            OutputPrefabName = outputPrefabName;
            BaseItemCount = baseItemCount;
            BonusItemCount = bonusItemCount;
        }

        internal long Sender { get; }
        internal int RequestId { get; }
        internal long BatchTicks { get; }
        internal string OutputPrefabName { get; }
        internal int BaseItemCount { get; }
        internal int BonusItemCount { get; }
    }

    private sealed class ClientTapRequest
    {
        internal ClientTapRequest(
            int requestId,
            long playerId,
            long owner,
            long batchTicks,
            float startedAt)
        {
            RequestId = requestId;
            PlayerId = playerId;
            Owner = owner;
            BatchTicks = batchTicks;
            StartedAt = startedAt;
        }

        internal int RequestId { get; }
        internal long PlayerId { get; }
        internal long Owner { get; }
        internal long BatchTicks { get; }
        internal float StartedAt { get; }
    }
}

[HarmonyPatch(typeof(Fermenter), "AddItem")]
internal static class FermenterCookingExperienceAddItemPatch
{
    [HarmonyPostfix]
    private static void Postfix(
        Fermenter __instance,
        Humanoid user,
        bool __result)
    {
        if (!__result
            || StationModule.IsFermenterBonusExcluded(__instance)
            || user is not Player player
            || (UnityObject)(object)player == null
            || (UnityObject)(object)Player.m_localPlayer == null
            || player != Player.m_localPlayer)
        {
            return;
        }

        player.RaiseSkill(
            Skills.SkillType.Cooking,
            FermenterCookingBonusSystem.CookingExperienceOnAdd);
    }
}

[HarmonyPatch(typeof(Fermenter), "Awake")]
internal static class FermenterCookingBonusRpcPatch
{
    [HarmonyPostfix]
    private static void Postfix(Fermenter __instance)
    {
        FermenterCookingBonusSystem.RegisterRpcs(__instance);
    }
}

[HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Interact))]
[HarmonyPriority(Priority.Last)]
internal static class FermenterCookingBonusInteractPatch
{
    private static readonly MethodInfo InvokeRpcMethod = AccessTools.Method(
        typeof(ZNetView),
        nameof(ZNetView.InvokeRPC),
        new[] { typeof(string), typeof(object[]) })!;

    private static readonly MethodInfo RequestTapMethod = AccessTools.Method(
        typeof(FermenterCookingBonusSystem),
        nameof(FermenterCookingBonusSystem.RequestTap))!;

    private static bool _patternWarningLogged;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> codes = new(instructions);
        try
        {
            int tapNameIndex = -1;
            int invokeIndex = -1;
            for (int index = 0; index < codes.Count; index++)
            {
                if (codes[index].opcode == OpCodes.Ldstr
                    && Equals(codes[index].operand, "RPC_Tap"))
                {
                    tapNameIndex = index;
                    break;
                }
            }

            if (tapNameIndex >= 0)
            {
                for (int index = tapNameIndex + 1;
                     index < Math.Min(codes.Count, tapNameIndex + 10);
                     index++)
                {
                    if (codes[index].opcode == OpCodes.Callvirt
                        && Equals(codes[index].operand, InvokeRpcMethod))
                    {
                        invokeIndex = index;
                        break;
                    }
                }
            }

            if (tapNameIndex < 0 || invokeIndex < 0)
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
            codes[invokeIndex + 2].operand = RequestTapMethod;
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
            "Fermenter Cooking bonus request patch was not applied; vanilla tapping remains active"
            + detail
            + ".");
    }
}

[HarmonyPatch(typeof(Fermenter), "DelayedTap")]
internal static class FermenterCookingBonusOutputPatch
{
    [HarmonyPrefix]
    private static void Prefix(
        Fermenter __instance,
        out FermenterCookingBonusSystem.OwnerTapContext? __state)
    {
        __state = FermenterCookingBonusSystem.TakeOwnerTapContext(__instance);
    }

    [HarmonyPostfix]
    private static void Postfix(
        Fermenter __instance,
        ref FermenterCookingBonusSystem.OwnerTapContext? __state,
        bool __runOriginal)
    {
        if (__runOriginal)
        {
            FermenterCookingBonusSystem.CompleteDelayedTap(__instance, __state);
        }
        else
        {
            FermenterCookingBonusSystem.RejectDelayedTap(__instance, __state);
        }

        __state = null;
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(
        Fermenter __instance,
        FermenterCookingBonusSystem.OwnerTapContext? __state,
        Exception? __exception)
    {
        if (__exception != null && __state != null)
        {
            FermenterCookingBonusSystem.RejectDelayedTap(__instance, __state);
        }

        return __exception;
    }
}
