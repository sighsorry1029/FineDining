using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

// A single authoritative clock per world. The ZDO is a save checkpoint, not an
// authority channel: after world load only server memory supplies clock values.
internal static class WorldSpoilageClock
{
    internal const string PrefabName = "FineDining_SpoilageClock";
    private const string PayloadKey = "sighsorry.FineDining.Clock";
    private const string RequestRpc = "FineDining_ClockRequest_v1";
    private const string SnapshotRpc = "FineDining_ClockSnapshot_v1";
    private static readonly int PrefabHash = PrefabName.GetStableHashCode();
    private sealed class Connection
    {
        internal float LastRequest = -10f;
        internal bool Subscribed;
    }
    private static readonly Dictionary<ZRpc, Connection> Connections = new();
    private static readonly List<ZRpc> StaleConnections = new();
    private static readonly HashSet<ZNetScene> Scenes = new();
    private static readonly HashSet<string> Warnings = new(StringComparer.Ordinal);
    private static GameObject? _root;
    private static GameObject? _prefab;
    private static ZDOMan? _manager;
    private static ZDOID _checkpoint;
    private static SpoilageTimeline _time;
    private static bool _loaded;
    private static bool _ready;
    private static bool _failed;
    private static string _session = "";
    private static long _revision;
    private static float _nextTick;
    private static float _nextRequest;
    internal static bool Initialized { get; private set; }
    internal static bool Frozen => _ready && _time.Frozen;

    internal static void Initialize()
    {
        Initialized = true;
        ZNet.WorldSaveStarted += FlushCheckpoint;
    }

    internal static void ResetSession()
    {
        foreach (ZRpc rpc in Connections.Keys)
        {
            rpc.Unregister(RequestRpc);
            rpc.Unregister(SnapshotRpc);
        }
        Connections.Clear();
        StaleConnections.Clear();
        _manager = null;
        _checkpoint = ZDOID.None;
        _time = default;
        _loaded = _ready = _failed = false;
        _session = Guid.NewGuid().ToString("N");
        _revision = 0;
        _nextTick = _nextRequest = 0;
        Warnings.Clear();
    }

    internal static void Shutdown()
    {
        FlushCheckpoint();
        ZNet.WorldSaveStarted -= FlushCheckpoint;
        ResetSession();
        foreach (ZNetScene scene in Scenes)
        {
            if (scene == null || _prefab == null) continue;
            scene.m_prefabs.Remove(_prefab);
            if (ReferenceEquals(scene.GetPrefab(PrefabHash), _prefab)) scene.NamedPrefabs().Remove(PrefabHash);
        }
        Scenes.Clear();
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = _prefab = null;
        Initialized = false;
    }

    internal static void RegisterPrefab(ZNetScene scene)
    {
        if (!Initialized) return;
        if (_prefab == null)
        {
            _root = new GameObject("FineDining clock prefabs");
            _root.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _prefab = new GameObject(PrefabName);
            _prefab.SetActive(false);
            _prefab.transform.SetParent(_root.transform, false);
            ZNetView view = _prefab.AddComponent<ZNetView>();
            view.m_persistent = true;
            view.m_distant = false;
            view.m_type = ZDO.ObjectType.Default;
            _prefab.SetActive(true); // The inactive parent prevents a premature ZNetView.Awake.
        }
        GameObject existing = scene.GetPrefab(PrefabHash);
        if (existing != null && !ReferenceEquals(existing, _prefab))
        {
            Fail("clock-prefab", "The spoilage clock prefab hash is already registered by another prefab.");
            return;
        }
        scene.NamedPrefabs()[PrefabHash] = _prefab;
        if (!scene.m_prefabs.Contains(_prefab)) scene.m_prefabs.Add(_prefab);
        Scenes.RemoveWhere(s => s == null);
        Scenes.Add(scene);
    }

    internal static void WorldLoaded()
    {
        if (!Initialized || ZNet.instance == null || !ZNet.instance.IsServer() ||
            ZNet.m_loadError || ZDOMan.instance == null || _failed || _loaded) return;
        try
        {
            if (!SpoilageClock.TryGetRawWorldTicks(out long raw)) return;
            _manager = ZDOMan.instance;
            List<ZDO> records = new();
            int index = 0;
            while (!_manager.GetAllZDOsWithPrefabIterative(PrefabName, records, ref index)) { }
            HashSet<ZDOID> unique = new();
            ZDO? found = null;
            foreach (ZDO record in records)
                if (unique.Add(record.m_uid)) found = record;
            if (unique.Count > 1) throw new InvalidOperationException("Multiple distinct spoilage clock checkpoints exist.");
            long world = ZNet.instance.GetWorldUID();
            if (world == 0) throw new InvalidOperationException("The world identifier is not available.");
            if (found == null)
                _time = new SpoilageTimeline(world, Guid.NewGuid().ToString("N"), raw);
            else
            {
                if (!SpoilageTimeline.TryParse(found.GetString(PayloadKey, ""), out _time) || _time.WorldUid != world)
                    throw new InvalidOperationException("The saved spoilage clock is invalid or belongs to another world.");
                _checkpoint = found.m_uid;
            }
            _loaded = true;
            // Provider initialization may finish in ZoneSystem.Start after this hook.
            RefreshSeason();
            FlushCheckpoint();
        }
        catch (Exception error) { Fail("clock-load", error.GetBaseException().Message); }
    }

    internal static bool TryGetContext(out SpoilageTimeline context)
    {
        context = default;
        if (!Initialized || !_ready || _failed || ZNet.instance == null) return false;
        if (ZNet.instance.IsServer() && !Advance()) return false;
        context = _time;
        return true;
    }

    private static bool Advance()
    {
        if (!_loaded || _failed || !SpoilageClock.TryGetRawWorldTicks(out long raw)) return false;
        if (_time.Advance(raw)) return true;
        Fail("clock-range", "The world spoilage clock exceeded its representable range.");
        return false;
    }

    internal static void RefreshSeason()
    {
        if (!Initialized || !_loaded || _failed || ZNet.instance == null || !ZNet.instance.IsServer()) return;
        if (!SeasonalPreservation.TryGetWinter(out bool winter))
        {
            _ready = false;
            return;
        }
        if (!Advance()) return;
        bool changed = !_ready || _time.Frozen != winter;
        _time.Frozen = winter;
        _ready = true;
        if (changed)
        {
            // Settle the previous phase before publishing the accepted new phase.
            Publish();
            DecayRuntime.InvalidateAll();
        }
    }

    internal static void Tick()
    {
        if (!Initialized || ZNet.instance == null || Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 1f;
        // Also discover a peer when another plugin suppresses the connection postfix.
        foreach (ZNetPeer peer in ZNet.instance.GetPeers()) Bind(peer);
        StaleConnections.Clear();
        foreach (ZRpc rpc in Connections.Keys)
            if (FindPeer(rpc) == null) StaleConnections.Add(rpc);
        foreach (ZRpc rpc in StaleConnections)
        {
            rpc.Unregister(RequestRpc);
            rpc.Unregister(SnapshotRpc);
            Connections.Remove(rpc);
        }
        if (ZNet.instance.IsServer())
        {
            RefreshSeason();
            if (_ready && Advance()) Publish();
            return;
        }
        if (!_ready && Time.unscaledTime >= _nextRequest && FineDiningPlugin.ConfigSync.InitialSyncDone)
        {
            _nextRequest = Time.unscaledTime + 2f;
            ZRpc? server = ZNet.instance.GetServerRPC();
            if (server == null) return;
            ZPackage request = new();
            request.Write(1);
            server.Invoke(RequestRpc, request);
        }
    }

    internal static void Bind(ZNetPeer peer)
    {
        if (!Initialized || peer?.m_rpc == null || Connections.ContainsKey(peer.m_rpc)) return;
        Connections.Add(peer.m_rpc, new Connection());
        peer.m_rpc.Register<ZPackage>(RequestRpc, ReceiveRequest);
        peer.m_rpc.Register<ZPackage>(SnapshotRpc, ReceiveSnapshot);
    }

    private static ZNetPeer? FindPeer(ZRpc rpc)
    {
        if (ZNet.instance == null) return null;
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            if (ReferenceEquals(peer.m_rpc, rpc)) return peer;
        return null;
    }

    private static void ReceiveRequest(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || !_ready ||
            FindPeer(rpc)?.IsReady() != true || !Connections.TryGetValue(rpc, out Connection connection) ||
            Time.unscaledTime - connection.LastRequest < 1f) return;
        connection.LastRequest = Time.unscaledTime;
        try
        {
            if (package.Size() != sizeof(int) || package.ReadInt() != 1) return;
            connection.Subscribed = true;
            if (Advance()) rpc.Invoke(SnapshotRpc, Snapshot());
        }
        catch (Exception error) { Warn("request", "Rejected a malformed clock request: " + error.Message); }
    }

    private static ZPackage Snapshot()
    {
        ZPackage package = new();
        package.Write(1);
        package.Write(_session);
        package.Write(++_revision);
        package.Write(_time.Serialize());
        return package;
    }

    private static void Publish()
    {
        if (!_ready || _failed || ZNet.instance == null || !ZNet.instance.IsServer()) return;
        ZPackage? package = null;
        foreach (var pair in Connections)
        {
            if (!pair.Value.Subscribed || FindPeer(pair.Key)?.IsReady() != true) continue;
            package ??= Snapshot();
            try { pair.Key.Invoke(SnapshotRpc, package); }
            catch (Exception error) { Warn("snapshot-send", "Clock snapshot delivery will retry: " + error.Message); }
        }
    }

    private static void ReceiveSnapshot(ZRpc rpc, ZPackage package)
    {
        // Check the actual server transport, not a claimed sender UID in a routed packet.
        if (ZNet.instance == null || ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC())) return;
        try
        {
            if (package.Size() > 512 || package.ReadInt() != 1) return;
            string session = package.ReadString();
            long revision = package.ReadLong();
            if (!SpoilageTimeline.IsEpoch(session) || revision <= 0 ||
                !SpoilageTimeline.TryParse(package.ReadString(), out SpoilageTimeline snapshot) ||
                snapshot.WorldUid != ZNet.instance.GetWorldUID() || package.GetPos() != package.Size()) return;
            if (_ready && (session != _session || revision <= _revision || snapshot.Epoch != _time.Epoch)) return;
            bool changed = !_ready || _time.Frozen != snapshot.Frozen || snapshot.ClockTicks < _time.ClockTicks;
            _time = snapshot;
            _session = session;
            _revision = revision;
            _ready = true;
            if (changed) DecayRuntime.InvalidateAll();
        }
        catch (Exception error) { Warn("snapshot", "Rejected a malformed clock snapshot: " + error.Message); }
    }

    private static void FlushCheckpoint()
    {
        if (!Initialized || !_loaded || _failed || _manager == null || ZNet.instance == null ||
            !ZNet.instance.IsServer() || !Advance()) return;
        try
        {
            ZDO? zdo = _manager.GetZDO(_checkpoint);
            if (zdo == null)
            {
                zdo = _manager.CreateNewZDO(Vector3.zero, PrefabHash);
                _checkpoint = zdo.m_uid;
            }
            zdo.Persistent = true;
            zdo.SetPrefab(PrefabHash);
            zdo.SetType(ZDO.ObjectType.Default);
            zdo.SetDistant(false);
            zdo.SetPosition(Vector3.zero);
            zdo.SetOwner(ZDOMan.GetSessionID());
            zdo.Set(PayloadKey, _time.Serialize());
            _manager.SetDirtySector(zdo);
        }
        catch (Exception error) { Fail("clock-save", "Could not checkpoint the spoilage clock: " + error.Message); }
    }

    private static void Fail(string key, string reason)
    {
        _failed = true;
        _ready = false;
        Warn(key, reason + " Seasonal clock processing is deferred; game saving remains available.");
    }
    private static void Warn(string key, string message)
    {
        if (Warnings.Add(key)) FineDiningPlugin.Log.LogWarning(message);
    }

    internal static void ReportMissingHistory() => Warn("item-history",
        "An item refers to a different spoilage-clock history for this world. Its timer was kept unchanged; " +
        "restore the matching world/character backup to recover that history. No fresh lifetime was granted.");
}

[HarmonyPatch(typeof(ZNet), "Awake")]
internal static class SpoilageTimelineWorldAwakePatch
{
    private static void Prefix() => WorldSpoilageClock.ResetSession();
}
[HarmonyPatch(typeof(ZNet), "ServerLoadWorld")]
internal static class SpoilageTimelineWorldLoadedPatch
{
    private static void Postfix() => WorldSpoilageClock.WorldLoaded();
}
[HarmonyPatch(typeof(ZNet), "OnNewConnection", typeof(ZNetPeer))]
internal static class SpoilageTimelinePeerPatch
{
    private static void Postfix(ZNetPeer peer) => WorldSpoilageClock.Bind(peer);
}
[HarmonyPatch(typeof(ZNet), "OnDestroy")]
internal static class SpoilageTimelineWorldDestroyedPatch
{
    private static void Postfix() => WorldSpoilageClock.ResetSession();
}
[HarmonyPatch(typeof(ZNetScene), "Awake")]
internal static class SpoilageTimelinePrefabPatch
{
    private static void Postfix(ZNetScene __instance) => WorldSpoilageClock.RegisterPrefab(__instance);
}
