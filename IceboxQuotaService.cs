using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FineDining;

internal readonly struct IceboxPinSnapshotEntry
{
    internal IceboxPinSnapshotEntry(ZDOID zdoId, Vector3 position)
    {
        ZdoId = zdoId;
        Position = position;
    }

    internal ZDOID ZdoId { get; }
    internal Vector3 Position { get; }
}

/// <summary>
/// Server-authoritative Icebox account ownership and placement quota.
///
/// Existing boxes discovered during ZDOMan.Load are grandfathered: lowering a
/// limit never deletes world data.  Only boxes first observed after the initial
/// world scan are validated against the current count.  Placement notices carry
/// only a ZDOID; identity is resolved from the RPC sender and the server's peer
/// and player ZDO data.
/// </summary>
internal static class IceboxQuotaService
{
    private const string PlacementNoticeRpc = "FineDining_IceboxPlacementNotice";
    private const string PlacementRejectedRpc = "FineDining_IceboxPlacementRejected";
    internal const string PlacementLimitMessageToken = "$finedining_icebox_limit_reached";
    private const int MaxPendingPerSender = 64;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30d);
    private static readonly TimeSpan PlacementSettleDelay = TimeSpan.FromMilliseconds(250d);
    private static readonly TimeSpan PendingReplicationTimeout = TimeSpan.FromSeconds(20d);

    private sealed class IndexedIcebox
    {
        internal IndexedIcebox(ZDOID zdoId, long creatorPlayerId, string ownerAccountId, Vector3 position)
        {
            ZdoId = zdoId;
            CreatorPlayerId = creatorPlayerId;
            OwnerAccountId = ownerAccountId;
            Position = position;
        }

        internal ZDOID ZdoId;
        internal long CreatorPlayerId;
        internal string OwnerAccountId;
        internal Vector3 Position;
    }

    private sealed class PendingPlacement
    {
        internal PendingPlacement(
            long senderUid,
            DateTime firstSeenUtc,
            bool refundEligible,
            bool hasPlacementNotice)
        {
            SenderUid = senderUid;
            FirstSeenUtc = firstSeenUtc;
            RefundEligible = refundEligible;
            HasPlacementNotice = hasPlacementNotice;
        }

        internal long SenderUid;
        internal DateTime FirstSeenUtc;
        // Honest-client quality-of-life hint for local NoCostCheat/FreeBuild.
        // The server still validates the sender, ZDO, creator, and global free-build
        // state, but cannot independently observe client-authoritative consumption.
        internal bool RefundEligible;
        internal bool HasPlacementNotice;
    }

    private static readonly Dictionary<ZDOID, IndexedIcebox> Indexed = new();
    private static readonly Dictionary<string, int> CountsByAccount = new(StringComparer.Ordinal);
    private static readonly Dictionary<ZDOID, PendingPlacement> Pending = new();
    private static readonly HashSet<ZDOID> LocallyNotifiedPlacements = new();
    private static readonly HashSet<ZDOID> RejectedPlacements = new();
    private static readonly List<ZDO> ScanBuffer = new();
    private static readonly HashSet<ZDOID> ScanSeenIds = new();
    private static readonly HashSet<ZDOID> ReconcileBaselineIds = new();
    private static readonly List<ZDOID> ScratchIds = new();

    private static ZDOMan? _trackedZdoMan;
    private static ZRoutedRpc? _boundRoutedRpc;
    private static DateTime _nextScanUtc = DateTime.MinValue;
    private static int _reconcileScanIndex;
    private static bool _reconcileScanInProgress;
    private static bool _worldScanComplete;
    private static int _revision;
    private static bool _initialized;

    private static readonly FieldInfo? PlayerInfoUserInfoField =
        typeof(ZNet.PlayerInfo).GetField("m_userInfo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    internal static int Revision => _revision;
    internal static bool IsAuthoritativeIndexReady =>
        _worldScanComplete && ZNet.instance != null && ZNet.instance.IsServer();

    internal static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ResetSession();
    }

    internal static void Shutdown()
    {
        ResetSession();
        _initialized = false;
    }

    internal static void ResetSession()
    {
        if (_trackedZdoMan != null)
        {
            _trackedZdoMan.m_onZDODestroyed -= HandleZdoDestroyed;
            _trackedZdoMan = null;
        }

        _boundRoutedRpc = null;
        _nextScanUtc = DateTime.MinValue;
        _reconcileScanIndex = 0;
        _reconcileScanInProgress = false;
        _worldScanComplete = false;
        _revision = 0;
        Indexed.Clear();
        CountsByAccount.Clear();
        Pending.Clear();
        LocallyNotifiedPlacements.Clear();
        RejectedPlacements.Clear();
        ScanBuffer.Clear();
        ScanSeenIds.Clear();
        ReconcileBaselineIds.Clear();
        ScratchIds.Clear();
    }

    internal static void EnsureRpcBindings()
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || ReferenceEquals(_boundRoutedRpc, routedRpc))
        {
            return;
        }

        routedRpc.Register<ZPackage>(PlacementNoticeRpc, HandlePlacementNoticeRpc);
        routedRpc.Register<ZPackage>(PlacementRejectedRpc, HandlePlacementRejectedRpc);
        _boundRoutedRpc = routedRpc;
    }

    internal static void Tick()
    {
        if (!_initialized)
        {
            return;
        }

        EnsureRpcBindings();
        if (ZNet.instance == null || !ZNet.instance.IsServer() || !_worldScanComplete)
        {
            return;
        }

        ZDOMan? zdoMan = ZDOMan.instance;
        if (zdoMan == null)
        {
            return;
        }

        EnsureTrackedZdoMan(zdoMan);
        DateTime nowUtc = DateTime.UtcNow;
        if (!_reconcileScanInProgress && nowUtc >= _nextScanUtc)
        {
            BeginReconcileScan();
        }

        if (_reconcileScanInProgress)
        {
            AdvanceReconcileScan(zdoMan, nowUtc);
        }

        ProcessPendingPlacements(nowUtc);
    }

    internal static void OnAuthoritativeWorldLoaded(ZDOMan zdoMan)
    {
        if (zdoMan == null || ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        if (_trackedZdoMan != null && !ReferenceEquals(_trackedZdoMan, zdoMan))
        {
            _trackedZdoMan.m_onZDODestroyed -= HandleZdoDestroyed;
        }

        Indexed.Clear();
        CountsByAccount.Clear();
        Pending.Clear();
        RejectedPlacements.Clear();
        _revision = 0;
        EnsureTrackedZdoMan(zdoMan);

        PrepareScan(zdoMan);
        for (int index = 0; index < ScanBuffer.Count; index++)
        {
            ZDO zdo = ScanBuffer[index];
            if (!IceboxSubsystem.IsIcebox(zdo))
            {
                continue;
            }

            AddGrandfatheredIcebox(zdo);
        }

        _worldScanComplete = true;
        _reconcileScanIndex = 0;
        _reconcileScanInProgress = false;
        _nextScanUtc = DateTime.UtcNow.Add(ScanInterval);
        BumpRevision();
        FineDiningPlugin.Log.LogInfo(
            $"Indexed {Indexed.Count} existing Icebox(es); existing boxes are grandfathered against placement limits.");
    }

    internal static void NotifyLocallyPlacedIcebox(Piece piece)
    {
        if (!IceboxSubsystem.IsIcebox(piece))
        {
            return;
        }

        Player? localPlayer = Player.m_localPlayer;
        ZNetView? view = piece.m_nview;
        if (localPlayer == null ||
            view == null ||
            !view.IsValid() ||
            !view.IsOwner() ||
            piece.GetCreator() == 0L ||
            piece.GetCreator() != localPlayer.GetPlayerID())
        {
            return;
        }

        ZDO zdo = view.GetZDO();
        if (!IceboxSubsystem.IsIcebox(zdo) || !LocallyNotifiedPlacements.Add(zdo.m_uid))
        {
            return;
        }

        EnsureRpcBindings();
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null)
        {
            LocallyNotifiedPlacements.Remove(zdo.m_uid);
            return;
        }

        ZPackage package = new();
        package.Write(zdo.m_uid);
        bool freeBuild = localPlayer.NoCostCheat() ||
                         (ZoneSystem.instance != null &&
                          ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey()));
        package.Write(!freeBuild);
        routedRpc.InvokeRoutedRPC(routedRpc.GetServerPeerID(), PlacementNoticeRpc, package);
    }

    internal static IReadOnlyList<IceboxPinSnapshotEntry> GetEntriesForAccount(string accountId)
    {
        string canonical = IceboxSubsystem.NormalizeAccountId(accountId);
        if (canonical.Length == 0 || Indexed.Count == 0)
        {
            return Array.Empty<IceboxPinSnapshotEntry>();
        }

        List<IceboxPinSnapshotEntry> result = new();
        foreach (IndexedIcebox entry in Indexed.Values)
        {
            if (!AccountIdsEqual(entry.OwnerAccountId, canonical))
            {
                continue;
            }

            result.Add(new IceboxPinSnapshotEntry(entry.ZdoId, entry.Position));
        }

        result.Sort(static (left, right) =>
        {
            int user = left.ZdoId.UserID.CompareTo(right.ZdoId.UserID);
            return user != 0 ? user : left.ZdoId.ID.CompareTo(right.ZdoId.ID);
        });
        return result;
    }

    internal static bool TryResolveSenderIdentity(long senderUid, out long playerId, out string accountId)
    {
        playerId = 0L;
        accountId = string.Empty;
        if (senderUid == 0L || ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return false;
        }

        Player? localPlayer = Player.m_localPlayer;
        if (localPlayer != null && localPlayer.GetOwner() == senderUid)
        {
            playerId = localPlayer.GetPlayerID();
            accountId = ResolveLocalAccountId();
            if (playerId != 0L && accountId.Length > 0)
            {
                return true;
            }
        }

        ZNetPeer? peer = ZNet.instance.GetPeer(senderUid);
        if (peer == null || peer.m_characterID.IsNone())
        {
            return false;
        }

        ZDO? playerZdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
        playerId = playerZdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
        accountId = ResolveAccountIdForCharacter(peer.m_characterID);
        return playerId != 0L && accountId.Length > 0;
    }

    internal static bool TryResolveOnlineCreatorIdentity(
        long creatorPlayerId,
        out long senderUid,
        out string accountId)
    {
        senderUid = 0L;
        accountId = string.Empty;
        if (creatorPlayerId == 0L || ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return false;
        }

        Player? localPlayer = Player.m_localPlayer;
        if (localPlayer != null && localPlayer.GetPlayerID() == creatorPlayerId)
        {
            senderUid = localPlayer.GetOwner();
            accountId = ResolveLocalAccountId();
            if (senderUid != 0L && accountId.Length > 0)
            {
                return true;
            }
        }

        List<ZNet.PlayerInfo>? players = ZNet.instance.m_players;
        if (players == null)
        {
            return false;
        }

        for (int index = 0; index < players.Count; index++)
        {
            ZNet.PlayerInfo playerInfo = players[index];
            ZDO? playerZdo = ZDOMan.instance?.GetZDO(playerInfo.m_characterID);
            long candidatePlayerId = playerZdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L;
            if (candidatePlayerId != creatorPlayerId)
            {
                continue;
            }

            accountId = NormalizePlayerInfoAccount(playerInfo);
            ZNetPeer? peer = FindPeerByCharacter(playerInfo.m_characterID);
            senderUid = peer?.m_uid ?? playerInfo.m_characterID.UserID;
            return senderUid != 0L && accountId.Length > 0;
        }

        return false;
    }

    internal static string ResolveLocalAccountId()
    {
        ZNet? znet = ZNet.instance;
        if (znet == null)
        {
            return string.Empty;
        }

        ZDOID characterId = znet.m_characterID;
        List<ZNet.PlayerInfo>? players = znet.m_players;
        if (players != null)
        {
            for (int index = 0; index < players.Count; index++)
            {
                if (players[index].m_characterID == characterId)
                {
                    return NormalizePlayerInfoAccount(players[index]);
                }
            }
        }

        Player? localPlayer = Player.m_localPlayer;
        if (localPlayer != null)
        {
            for (int index = 0; players != null && index < players.Count; index++)
            {
                ZDO? playerZdo = ZDOMan.instance?.GetZDO(players[index].m_characterID);
                if ((playerZdo?.GetLong(ZDOVars.s_playerID, 0L) ?? 0L) == localPlayer.GetPlayerID())
                {
                    return NormalizePlayerInfoAccount(players[index]);
                }
            }
        }

        return string.Empty;
    }

    internal static bool IsAuthoritativeServerSender(long senderUid)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        return senderUid != 0L && routedRpc != null && senderUid == routedRpc.GetServerPeerID();
    }

    private static void HandlePlacementNoticeRpc(long senderUid, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || package == null)
        {
            return;
        }

        ZDOID zdoId;
        bool refundEligible;
        try
        {
            zdoId = package.ReadZDOID();
            refundEligible = package.ReadBool();
        }
        catch
        {
            return;
        }

        if (zdoId.IsNone() || zdoId.UserID != senderUid || Indexed.ContainsKey(zdoId) || RejectedPlacements.Contains(zdoId))
        {
            return;
        }

        int pendingForSender = 0;
        foreach (PendingPlacement pending in Pending.Values)
        {
            if (pending.SenderUid == senderUid)
            {
                pendingForSender++;
            }
        }

        if (pendingForSender >= MaxPendingPerSender)
        {
            return;
        }

        if (Pending.TryGetValue(zdoId, out PendingPlacement? existing))
        {
            if (existing.SenderUid == 0L)
            {
                existing.SenderUid = senderUid;
            }
            if (!existing.HasPlacementNotice)
            {
                // A periodic ZDO scan can win the race with the authenticated
                // placement notice.  In that case the notice supplies the
                // previously unknown refund disposition.
                existing.RefundEligible = refundEligible;
                existing.HasPlacementNotice = true;
            }
            else
            {
                // Repeated notices may suppress their own refund, but can never
                // upgrade a nocost placement into a refundable one.
                existing.RefundEligible &= refundEligible;
            }
            return;
        }

        Pending[zdoId] = new PendingPlacement(
            senderUid,
            DateTime.UtcNow,
            refundEligible,
            hasPlacementNotice: true);
    }

    private static void HandlePlacementRejectedRpc(long senderUid, ZPackage package)
    {
        if (!IsAuthoritativeServerSender(senderUid) || package == null)
        {
            return;
        }

        int limit;
        try
        {
            limit = package.ReadInt();
        }
        catch
        {
            return;
        }

        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        player.Message(
            MessageHud.MessageType.Center,
            FormatPlacementLimitMessage(limit));
    }

    private static void ProcessPendingPlacements(DateTime nowUtc)
    {
        if (Pending.Count == 0)
        {
            return;
        }

        ScratchIds.Clear();
        ScratchIds.AddRange(Pending.Keys);
        for (int index = 0; index < ScratchIds.Count; index++)
        {
            ZDOID zdoId = ScratchIds[index];
            if (!Pending.TryGetValue(zdoId, out PendingPlacement? pending))
            {
                continue;
            }

            // Player.PlacePiece returns to the caller before vanilla consumes
            // the requirements.  In particular, a local-host routed RPC can be
            // delivered in the same frame as Piece.SetCreator.  Never refund a
            // rejected placement until that consumption path has completed.
            if (nowUtc - pending.FirstSeenUtc < PlacementSettleDelay)
            {
                continue;
            }

            if (Indexed.ContainsKey(zdoId) || RejectedPlacements.Contains(zdoId))
            {
                Pending.Remove(zdoId);
                continue;
            }

            ZDO? zdo = ZDOMan.instance?.GetZDO(zdoId);
            if (zdo == null || !zdo.IsValid())
            {
                if (nowUtc - pending.FirstSeenUtc >= PendingReplicationTimeout)
                {
                    Pending.Remove(zdoId);
                }
                continue;
            }

            if (!IceboxSubsystem.IsIcebox(zdo))
            {
                Pending.Remove(zdoId);
                continue;
            }

            long creatorPlayerId = zdo.GetLong(ZDOVars.s_creator, 0L);
            long senderUid = pending.SenderUid;
            long authoritativePlayerId;
            string authoritativeAccountId;
            bool identityResolved;
            if (senderUid != 0L)
            {
                identityResolved = TryResolveSenderIdentity(
                    senderUid,
                    out authoritativePlayerId,
                    out authoritativeAccountId);
            }
            else
            {
                identityResolved = TryResolveOnlineCreatorIdentity(
                    creatorPlayerId,
                    out senderUid,
                    out authoritativeAccountId);
                authoritativePlayerId = creatorPlayerId;
            }

            if (!identityResolved || creatorPlayerId == 0L)
            {
                if (nowUtc - pending.FirstSeenUtc < PendingReplicationTimeout)
                {
                    continue;
                }

                RejectPlacement(zdo, senderUid, 0, showMessage: false, pending.RefundEligible);
                continue;
            }

            long currentOwnerUid = zdo.GetOwner();
            long serverSessionUid = ZDOMan.GetSessionID();
            bool ownerIsPlausible = currentOwnerUid == senderUid ||
                                    currentOwnerUid == serverSessionUid;
            if (creatorPlayerId != authoritativePlayerId || !ownerIsPlausible)
            {
                RejectPlacement(zdo, senderUid, 0, showMessage: false, pending.RefundEligible);
                continue;
            }

            string accountId = IceboxSubsystem.NormalizeAccountId(authoritativeAccountId);
            AttributeUnresolvedGrandfatheredIceboxes(creatorPlayerId, accountId);
            int limit = IceboxLimitPolicy.Current.GetLimit(accountId);
            int currentCount = CountsByAccount.TryGetValue(accountId, out int indexedCount) ? indexedCount : 0;
            if (limit == 0 || (limit > 0 && currentCount >= limit))
            {
                RejectPlacement(zdo, senderUid, limit, showMessage: true, pending.RefundEligible);
                continue;
            }

            if (!string.Equals(
                    zdo.GetString(IceboxSubsystem.OwnerAccountIdKey, string.Empty),
                    accountId,
                    StringComparison.Ordinal))
            {
                zdo.Set(IceboxSubsystem.OwnerAccountIdKey, accountId);
                ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
            }

            Pending.Remove(zdoId);
            IndexedIcebox accepted = new(zdoId, creatorPlayerId, accountId, zdo.GetPosition());
            AddIndexed(accepted, updateRevision: true);
        }
    }

    private static void AddGrandfatheredIcebox(ZDO zdo)
    {
        long creatorPlayerId = zdo.GetLong(ZDOVars.s_creator, 0L);
        string accountId = IceboxSubsystem.NormalizeAccountId(
            zdo.GetString(IceboxSubsystem.OwnerAccountIdKey, string.Empty));
        if (accountId.Length == 0 &&
            TryResolveOnlineCreatorIdentity(creatorPlayerId, out _, out string resolvedAccountId))
        {
            accountId = IceboxSubsystem.NormalizeAccountId(resolvedAccountId);
            zdo.Set(IceboxSubsystem.OwnerAccountIdKey, accountId);
            ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
        }

        AddIndexed(
            new IndexedIcebox(zdo.m_uid, creatorPlayerId, accountId, zdo.GetPosition()),
            updateRevision: false);
    }

    private static void AddIndexed(IndexedIcebox entry, bool updateRevision)
    {
        if (Indexed.ContainsKey(entry.ZdoId))
        {
            return;
        }

        Indexed[entry.ZdoId] = entry;
        IncrementAccountCount(entry.OwnerAccountId);
        if (updateRevision)
        {
            BumpRevision();
        }
    }

    private static void RemoveIndexed(ZDOID zdoId)
    {
        if (!Indexed.TryGetValue(zdoId, out IndexedIcebox? entry))
        {
            Pending.Remove(zdoId);
            return;
        }

        Indexed.Remove(zdoId);

        DecrementAccountCount(entry.OwnerAccountId);
        Pending.Remove(zdoId);
        BumpRevision();
    }

    private static void BeginReconcileScan()
    {
        ScanBuffer.Clear();
        ReconcileBaselineIds.Clear();
        foreach (ZDOID indexedId in Indexed.Keys)
        {
            ReconcileBaselineIds.Add(indexedId);
        }
        _reconcileScanIndex = 0;
        _reconcileScanInProgress = true;
    }

    private static void AdvanceReconcileScan(ZDOMan zdoMan, DateTime nowUtc)
    {
        // Valheim's iterative scan stops after a bounded number of populated
        // sectors. Advance it once per frame instead of draining every sector
        // in one Update, so even very large worlds avoid a periodic hitch.
        if (!zdoMan.GetAllZDOsWithPrefabIterative(
                IceboxSubsystem.PrefabName,
                ScanBuffer,
                ref _reconcileScanIndex))
        {
            return;
        }

        _reconcileScanInProgress = false;
        _reconcileScanIndex = 0;
        _nextScanUtc = nowUtc.Add(ScanInterval);
        ReconcileAuthoritativeIndex(nowUtc);
    }

    private static void ReconcileAuthoritativeIndex(DateTime nowUtc)
    {
        ScanSeenIds.Clear();
        for (int index = 0; index < ScanBuffer.Count; index++)
        {
            ZDO zdo = ScanBuffer[index];
            if (!IceboxSubsystem.IsIcebox(zdo))
            {
                continue;
            }

            ScanSeenIds.Add(zdo.m_uid);
            if (Indexed.TryGetValue(zdo.m_uid, out IndexedIcebox? indexed))
            {
                RepairIndexedMetadata(zdo, indexed);
                Vector3 position = zdo.GetPosition();
                if (indexed.Position != position)
                {
                    indexed.Position = position;
                    BumpRevision();
                }
                continue;
            }

            if (!RejectedPlacements.Contains(zdo.m_uid) && !Pending.ContainsKey(zdo.m_uid))
            {
                long creatorPlayerId = zdo.GetLong(ZDOVars.s_creator, 0L);
                long senderUid = 0L;
                _ = TryResolveOnlineCreatorIdentity(creatorPlayerId, out senderUid, out _);
                Pending[zdo.m_uid] = new PendingPlacement(
                    senderUid,
                    nowUtc,
                    refundEligible: false,
                    hasPlacementNotice: false);
            }
        }

        ScratchIds.Clear();
        foreach (ZDOID indexedId in ReconcileBaselineIds)
        {
            if (!ScanSeenIds.Contains(indexedId))
            {
                ScratchIds.Add(indexedId);
            }
        }

        for (int index = 0; index < ScratchIds.Count; index++)
        {
            RemoveIndexed(ScratchIds[index]);
        }

        ReconcileBaselineIds.Clear();
        PruneRejectedPlacements();
    }

    private static void RepairIndexedMetadata(ZDO zdo, IndexedIcebox indexed)
    {
        if (indexed.OwnerAccountId.Length > 0)
        {
            string storedAccountId = IceboxSubsystem.NormalizeAccountId(
                zdo.GetString(IceboxSubsystem.OwnerAccountIdKey, string.Empty));
            if (!AccountIdsEqual(storedAccountId, indexed.OwnerAccountId))
            {
                // Once accepted, the server-side index is authoritative.  A
                // peer that temporarily owns the container ZDO must not be able
                // to rewrite the account used after the next world restart.
                zdo.Set(IceboxSubsystem.OwnerAccountIdKey, indexed.OwnerAccountId);
                ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
            }
            return;
        }

        long creatorPlayerId = zdo.GetLong(ZDOVars.s_creator, indexed.CreatorPlayerId);
        if (!TryResolveOnlineCreatorIdentity(creatorPlayerId, out _, out string accountId))
        {
            return;
        }

        string canonical = IceboxSubsystem.NormalizeAccountId(accountId);
        if (canonical.Length == 0)
        {
            return;
        }

        indexed.CreatorPlayerId = creatorPlayerId;
        indexed.OwnerAccountId = canonical;
        IncrementAccountCount(canonical);
        zdo.Set(IceboxSubsystem.OwnerAccountIdKey, canonical);
        ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
        BumpRevision();
    }

    private static void AttributeUnresolvedGrandfatheredIceboxes(
        long creatorPlayerId,
        string accountId)
    {
        string canonical = IceboxSubsystem.NormalizeAccountId(accountId);
        if (creatorPlayerId == 0L || canonical.Length == 0)
        {
            return;
        }

        // A saved pre-FineDining Icebox has only Valheim's character creator
        // id until that character next appears online. Resolve every matching
        // grandfathered box before evaluating a new placement so the first box
        // built immediately after login cannot slip through a positive quota.
        foreach (IndexedIcebox indexed in Indexed.Values)
        {
            if (indexed.CreatorPlayerId != creatorPlayerId ||
                indexed.OwnerAccountId.Length != 0)
            {
                continue;
            }

            ZDO? zdo = ZDOMan.instance?.GetZDO(indexed.ZdoId);
            if (zdo == null || !zdo.IsValid() || !IceboxSubsystem.IsIcebox(zdo))
            {
                continue;
            }

            indexed.OwnerAccountId = canonical;
            IncrementAccountCount(canonical);
            zdo.Set(IceboxSubsystem.OwnerAccountIdKey, canonical);
            ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
            BumpRevision();
        }
    }

    private static void RejectPlacement(
        ZDO zdo,
        long receiverUid,
        int limit,
        bool showMessage,
        bool refundEligible)
    {
        ZDOID zdoId = zdo.m_uid;
        Pending.Remove(zdoId);
        RemoveIndexed(zdoId);
        if (!RejectedPlacements.Add(zdoId))
        {
            return;
        }

        SendPlacementRejected(receiverUid, limit, showMessage);
        if (refundEligible && !IsGlobalFreeBuildEnabled())
        {
            RefundPlacementOnce(zdo);
        }

        if (!zdo.IsValid())
        {
            return;
        }

        zdo.SetOwner(ZDOMan.GetSessionID());
        GameObject? instance = ZNetScene.instance?.FindInstance(zdoId);
        if (instance != null && ZNetScene.instance != null)
        {
            ZNetScene.instance.Destroy(instance);
        }
        else
        {
            ZDOMan.instance?.DestroyZDO(zdo);
        }
    }

    private static void RefundPlacementOnce(ZDO zdo)
    {
        if (!zdo.IsValid() || zdo.GetBool(IceboxSubsystem.LimitRefundProcessedKey, false))
        {
            return;
        }

        zdo.Set(IceboxSubsystem.LimitRefundProcessedKey, true);
        Piece.Requirement[] requirements = ResolveCurrentRequirements(zdo);
        Vector3 position = zdo.GetPosition() + Vector3.up;
        for (int requirementIndex = 0; requirementIndex < requirements.Length; requirementIndex++)
        {
            Piece.Requirement requirement = requirements[requirementIndex];
            ItemDrop? itemDrop = requirement.m_resItem;
            if (itemDrop == null || !requirement.m_recover)
            {
                continue;
            }

            int remaining = requirement.GetAmount(1);
            int maxStack = Math.Max(1, itemDrop.m_itemData.m_shared.m_maxStackSize);
            while (remaining > 0)
            {
                int amount = Math.Min(remaining, maxStack);
                remaining -= amount;
                ItemDrop.ItemData item = itemDrop.m_itemData.Clone();
                item.m_dropPrefab = itemDrop.gameObject;
                item.m_stack = amount;
                ItemDrop.DropItem(
                    item,
                    amount,
                    position,
                    Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            }
        }
    }

    private static Piece.Requirement[] ResolveCurrentRequirements(ZDO zdo)
    {
        string storedRecipe = zdo.GetString(IceboxSubsystem.PlacedRecipeKey, string.Empty);
        ObjectDB? objectDb = ObjectDB.instance;
        if (storedRecipe.Length == 0 || objectDb == null)
        {
            // Only a newly placed Icebox with a valid recipe snapshot is
            // eligible for a placement-limit refund. There is deliberately no
            // fallback for snapshot-less or legacy instances.
            return Array.Empty<Piece.Requirement>();
        }

        if (!GeneratedPrefabRegistry.TryCreateIceboxRequirements(
                objectDb,
                storedRecipe,
                out Piece.Requirement[] storedRequirements) ||
            !GeneratedPrefabRegistry.TryCreateIceboxRequirements(
                objectDb,
                IceboxSubsystem.Recipe,
                out Piece.Requirement[] configuredRequirements))
        {
            return Array.Empty<Piece.Requirement>();
        }

        string storedCanonical =
            GeneratedPrefabRegistry.SerializeIceboxRequirements(storedRequirements);
        string configuredCanonical =
            GeneratedPrefabRegistry.SerializeIceboxRequirements(configuredRequirements);
        if (!string.Equals(storedCanonical, configuredCanonical, StringComparison.Ordinal))
        {
            FineDiningPlugin.Log.LogWarning(
                $"Skipped an Icebox placement refund because its stored recipe " +
                $"'{storedCanonical}' did not match the server recipe '{configuredCanonical}'.");
            return Array.Empty<Piece.Requirement>();
        }

        return storedRequirements;
    }

    private static void SendPlacementRejected(long receiverUid, int limit, bool showMessage)
    {
        if (!showMessage || receiverUid == 0L)
        {
            return;
        }

        Player? localPlayer = Player.m_localPlayer;
        if (localPlayer != null && localPlayer.GetOwner() == receiverUid)
        {
            localPlayer.Message(
                MessageHud.MessageType.Center,
                FormatPlacementLimitMessage(limit));
            return;
        }

        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null)
        {
            return;
        }

        ZPackage package = new();
        package.Write(limit);
        routedRpc.InvokeRoutedRPC(receiverUid, PlacementRejectedRpc, package);
    }

    private static void EnsureTrackedZdoMan(ZDOMan zdoMan)
    {
        if (ReferenceEquals(_trackedZdoMan, zdoMan))
        {
            return;
        }

        if (_trackedZdoMan != null)
        {
            _trackedZdoMan.m_onZDODestroyed -= HandleZdoDestroyed;
        }

        _trackedZdoMan = zdoMan;
        _trackedZdoMan.m_onZDODestroyed += HandleZdoDestroyed;
    }

    private static void HandleZdoDestroyed(ZDO zdo)
    {
        if (zdo == null)
        {
            return;
        }

        RemoveIndexed(zdo.m_uid);
        Pending.Remove(zdo.m_uid);
        LocallyNotifiedPlacements.Remove(zdo.m_uid);
        RejectedPlacements.Remove(zdo.m_uid);
    }

    private static void PruneRejectedPlacements()
    {
        if (RejectedPlacements.Count == 0)
        {
            return;
        }

        ScratchIds.Clear();
        foreach (ZDOID zdoId in RejectedPlacements)
        {
            ZDO? zdo = ZDOMan.instance?.GetZDO(zdoId);
            if (zdo == null || !zdo.IsValid() || !IceboxSubsystem.IsIcebox(zdo))
            {
                ScratchIds.Add(zdoId);
            }
        }

        for (int index = 0; index < ScratchIds.Count; index++)
        {
            RejectedPlacements.Remove(ScratchIds[index]);
        }
    }

    private static void PrepareScan(ZDOMan zdoMan)
    {
        ScanBuffer.Clear();
        int scanIndex = 0;
        while (!zdoMan.GetAllZDOsWithPrefabIterative(
                   IceboxSubsystem.PrefabName,
                   ScanBuffer,
                   ref scanIndex))
        {
        }
    }

    private static string ResolveAccountIdForCharacter(ZDOID characterId)
    {
        List<ZNet.PlayerInfo>? players = ZNet.instance?.m_players;
        if (players == null)
        {
            return string.Empty;
        }

        for (int index = 0; index < players.Count; index++)
        {
            if (players[index].m_characterID == characterId)
            {
                return NormalizePlayerInfoAccount(players[index]);
            }
        }

        return string.Empty;
    }

    private static string NormalizePlayerInfoAccount(ZNet.PlayerInfo playerInfo)
    {
        try
        {
            // Access the platform id through reflection so FineDining does not
            // gain a hard reference to Splatform merely to stringify an id that
            // Valheim already stores in ZNet.PlayerInfo.
            object boxedPlayerInfo = playerInfo;
            object? userInfo = PlayerInfoUserInfoField?.GetValue(boxedPlayerInfo);
            if (userInfo == null)
            {
                return string.Empty;
            }

            FieldInfo? idField = userInfo.GetType().GetField(
                "m_id",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object? platformId = idField?.GetValue(userInfo);
            return IceboxSubsystem.NormalizeAccountId(platformId?.ToString());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ZNetPeer? FindPeerByCharacter(ZDOID characterId)
    {
        List<ZNetPeer>? peers = ZNet.instance?.GetPeers();
        if (peers == null)
        {
            return null;
        }

        for (int index = 0; index < peers.Count; index++)
        {
            if (peers[index].m_characterID == characterId)
            {
                return peers[index];
            }
        }

        return null;
    }

    private static void IncrementAccountCount(string accountId)
    {
        string canonical = IceboxSubsystem.NormalizeAccountId(accountId);
        if (canonical.Length == 0)
        {
            return;
        }

        CountsByAccount[canonical] = CountsByAccount.TryGetValue(canonical, out int current)
            ? current + 1
            : 1;
    }

    private static void DecrementAccountCount(string accountId)
    {
        string canonical = IceboxSubsystem.NormalizeAccountId(accountId);
        if (canonical.Length == 0 || !CountsByAccount.TryGetValue(canonical, out int current))
        {
            return;
        }

        if (current <= 1)
        {
            CountsByAccount.Remove(canonical);
        }
        else
        {
            CountsByAccount[canonical] = current - 1;
        }
    }

    private static bool AccountIdsEqual(string left, string right)
    {
        return string.Equals(
            IceboxSubsystem.NormalizeAccountId(left),
            IceboxSubsystem.NormalizeAccountId(right),
            StringComparison.Ordinal);
    }

    private static void BumpRevision()
    {
        _revision = _revision == int.MaxValue ? 1 : _revision + 1;
    }

    private static bool IsGlobalFreeBuildEnabled()
    {
        ZoneSystem? zoneSystem = ZoneSystem.instance;
        if (zoneSystem == null)
        {
            return false;
        }

        // AllPiecesUnlocked affects build-menu visibility only; vanilla still
        // consumes requirements.  NoBuildCost is the authoritative world key
        // that suppresses Player.ConsumeResources for a normal Piece.
        return zoneSystem.GetGlobalKey(GlobalKeys.NoBuildCost);
    }

    internal static string FormatPlacementLimitMessage(int limit)
    {
        string limitText = limit < 0 ? "-1" : limit.ToString();
        string localized = Localization.instance != null
            ? Localization.instance.Localize(PlacementLimitMessageToken)
            : PlacementLimitMessageToken;
        if (string.IsNullOrWhiteSpace(localized) ||
            string.Equals(localized, PlacementLimitMessageToken, StringComparison.Ordinal))
        {
            return $"Icebox placement limit reached (maximum {limitText}).";
        }

        return localized.Replace("{0}", limitText);
    }
}
