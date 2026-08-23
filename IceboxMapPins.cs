using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FineDining;

/// <summary>
/// Opt-in, client-local map pins. The server broadcasts only a position-free
/// invalidation signal. Each enabled client then requests a full snapshot that
/// is authenticated from its routed-RPC sender and contains only its account.
/// </summary>
internal static class IceboxMapPins
{
    private const string RequestPinsRpc = "FineDining_RequestIceboxPins";
    private const string ReceiveSnapshotRpc = "FineDining_ReceiveIceboxPins";
    private const string InvalidatePinsRpc = "FineDining_InvalidateIceboxPins";
    private const int MaxSnapshotEntries = 8192;

    private static readonly TimeSpan RequestRetryInterval = TimeSpan.FromSeconds(3d);
    private static readonly TimeSpan MinimumServerRequestInterval = TimeSpan.FromMilliseconds(250d);
    private static readonly TimeSpan ServerRequestHistoryLifetime = TimeSpan.FromSeconds(30d);

    private static readonly Dictionary<ZDOID, IceboxPinSnapshotEntry> LocalSnapshot = new();
    private static readonly Dictionary<ZDOID, Minimap.PinData> LocalPins = new();
    private static readonly Dictionary<long, DateTime> LastServerRequestUtc = new();
    private static readonly List<long> ScratchPeerIds = new();

    private static ZRoutedRpc? _boundRoutedRpc;
    private static Minimap? _boundMinimap;
    private static Minimap? _pinTypeMinimap;
    private static Minimap.PinType _pinType = Minimap.PinType.Icon3;
    private static int _lastRevision;
    private static int _lastBroadcastRevision;
    private static int _nextRequestId;
    private static int _pendingRequestId;
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static DateTime _nextServerRequestPruneUtc = DateTime.MinValue;
    private static bool _snapshotReady;
    private static bool _renderDirty;
    private static bool _initialized;
    private static MethodInfo? _addPinMethod;
    private static object? _defaultPinAuthor;

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
        ClearLocalPins(clearSnapshot: true);
        _boundRoutedRpc = null;
        _boundMinimap = null;
        _pinTypeMinimap = null;
        _pinType = Minimap.PinType.Icon3;
        _lastRevision = 0;
        _lastBroadcastRevision = 0;
        _nextRequestId = 0;
        _pendingRequestId = 0;
        _lastRequestUtc = DateTime.MinValue;
        _nextServerRequestPruneUtc = DateTime.MinValue;
        _snapshotReady = false;
        _renderDirty = false;
        LastServerRequestUtc.Clear();
        ScratchPeerIds.Clear();
    }

    internal static void EnsureRpcBindings()
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || ReferenceEquals(_boundRoutedRpc, routedRpc))
        {
            return;
        }

        routedRpc.Register<ZPackage>(RequestPinsRpc, HandleRequestPinsRpc);
        routedRpc.Register<ZPackage>(ReceiveSnapshotRpc, HandleReceiveSnapshotRpc);
        routedRpc.Register<ZPackage>(InvalidatePinsRpc, HandleInvalidatePinsRpc);
        _boundRoutedRpc = routedRpc;
    }

    internal static void Tick()
    {
        if (!_initialized)
        {
            return;
        }

        EnsureRpcBindings();
        BroadcastAuthoritativeInvalidation();
        if (!IceboxSubsystem.ShowMapPins)
        {
            DisablePins();
            return;
        }

        if (Player.m_localPlayer == null || ZNet.instance == null || Minimap.instance == null)
        {
            return;
        }

        if (ZNet.instance.IsServer())
        {
            RefreshLocalHostSnapshot();
        }
        else
        {
            RefreshRemoteSnapshot();
        }

        if (!ReferenceEquals(_boundMinimap, Minimap.instance))
        {
            ClearLocalPins(clearSnapshot: false);
            _boundMinimap = Minimap.instance;
            _renderDirty = true;
        }

        if (_snapshotReady && (_renderDirty || LocalPins.Count != LocalSnapshot.Count))
        {
            ApplySnapshotToMinimap(_boundMinimap);
            _renderDirty = false;
        }
    }

    internal static void HandleConfigChanged()
    {
        if (!IceboxSubsystem.ShowMapPins)
        {
            DisablePins();
            return;
        }

        InvalidateSnapshot();
    }

    internal static void HandleMinimapDestroyed(Minimap minimap)
    {
        if (!ReferenceEquals(_boundMinimap, minimap))
        {
            return;
        }

        LocalPins.Clear();
        _boundMinimap = null;
        if (ReferenceEquals(_pinTypeMinimap, minimap))
        {
            _pinTypeMinimap = null;
        }
        _renderDirty = true;
    }

    private static void BroadcastAuthoritativeInvalidation()
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !IceboxQuotaService.IsAuthoritativeIndexReady)
        {
            return;
        }

        int revision = IceboxQuotaService.Revision;
        if (revision == _lastBroadcastRevision)
        {
            return;
        }

        // No account id or world position is broadcast. Enabled clients use
        // this signal only to request a newly authenticated full snapshot.
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null)
        {
            return;
        }

        routedRpc.InvokeRoutedRPC(ZRoutedRpc.Everybody, InvalidatePinsRpc, new ZPackage());
        _lastBroadcastRevision = revision;
    }

    private static void RefreshLocalHostSnapshot()
    {
        if (!IceboxQuotaService.IsAuthoritativeIndexReady)
        {
            return;
        }

        int revision = IceboxQuotaService.Revision;
        if (_snapshotReady && revision == _lastRevision)
        {
            return;
        }

        string accountId = IceboxQuotaService.ResolveLocalAccountId();
        if (accountId.Length == 0)
        {
            return;
        }

        ReplaceLocalSnapshot(IceboxQuotaService.GetEntriesForAccount(accountId));
        _lastRevision = revision;
        _snapshotReady = true;
        _pendingRequestId = 0;
        _renderDirty = true;
    }

    private static void RefreshRemoteSnapshot()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (_snapshotReady && _pendingRequestId == 0)
        {
            return;
        }

        if (nowUtc - _lastRequestUtc < RequestRetryInterval)
        {
            return;
        }

        SendRemoteSnapshotRequest();
    }

    private static void SendRemoteSnapshotRequest()
    {
        EnsureRpcBindings();
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || ZNet.instance == null || ZNet.instance.IsServer())
        {
            return;
        }

        int requestId = _nextRequestId == int.MaxValue ? 1 : _nextRequestId + 1;
        _nextRequestId = requestId;
        _pendingRequestId = requestId;
        _lastRequestUtc = DateTime.UtcNow;

        ZPackage package = new();
        package.Write(requestId);
        routedRpc.InvokeRoutedRPC(routedRpc.GetServerPeerID(), RequestPinsRpc, package);
    }

    private static void HandleRequestPinsRpc(long senderUid, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || package == null)
        {
            return;
        }

        int requestId;
        try
        {
            requestId = package.ReadInt();
        }
        catch
        {
            return;
        }

        if (requestId <= 0)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        PruneServerRequestHistory(nowUtc);
        if (LastServerRequestUtc.TryGetValue(senderUid, out DateTime lastRequestUtc) &&
            nowUtc - lastRequestUtc < MinimumServerRequestInterval)
        {
            return;
        }
        LastServerRequestUtc[senderUid] = nowUtc;

        if (!IceboxQuotaService.TryResolveSenderIdentity(senderUid, out _, out string accountId))
        {
            return;
        }

        SendFullSnapshot(senderUid, requestId, accountId);
    }

    private static void SendFullSnapshot(long receiverUid, int requestId, string accountId)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null)
        {
            return;
        }

        IReadOnlyList<IceboxPinSnapshotEntry> entries =
            IceboxQuotaService.GetEntriesForAccount(accountId);
        ZPackage package = new();
        package.Write(requestId);
        package.Write(entries.Count);
        for (int index = 0; index < entries.Count; index++)
        {
            package.Write(entries[index].ZdoId);
            package.Write(entries[index].Position);
        }

        routedRpc.InvokeRoutedRPC(receiverUid, ReceiveSnapshotRpc, package);
    }

    private static void HandleReceiveSnapshotRpc(long senderUid, ZPackage package)
    {
        if (!IceboxQuotaService.IsAuthoritativeServerSender(senderUid) ||
            package == null ||
            !IceboxSubsystem.ShowMapPins)
        {
            return;
        }

        int requestId;
        int count;
        try
        {
            requestId = package.ReadInt();
            count = package.ReadInt();
        }
        catch
        {
            return;
        }

        if (count < 0 || count > MaxSnapshotEntries ||
            _pendingRequestId == 0 || requestId != _pendingRequestId)
        {
            return;
        }

        Dictionary<ZDOID, IceboxPinSnapshotEntry> parsed = new();
        try
        {
            for (int index = 0; index < count; index++)
            {
                ZDOID zdoId = package.ReadZDOID();
                Vector3 position = package.ReadVector3();
                if (!zdoId.IsNone())
                {
                    parsed[zdoId] = new IceboxPinSnapshotEntry(zdoId, position);
                }
            }
        }
        catch
        {
            return;
        }

        LocalSnapshot.Clear();
        foreach (KeyValuePair<ZDOID, IceboxPinSnapshotEntry> entry in parsed)
        {
            LocalSnapshot[entry.Key] = entry.Value;
        }
        _pendingRequestId = 0;
        _snapshotReady = true;
        _renderDirty = true;
    }

    private static void HandleInvalidatePinsRpc(long senderUid, ZPackage package)
    {
        if (!IceboxQuotaService.IsAuthoritativeServerSender(senderUid) ||
            package == null ||
            !IceboxSubsystem.ShowMapPins)
        {
            return;
        }

        InvalidateSnapshot();
    }

    private static void PruneServerRequestHistory(DateTime nowUtc)
    {
        if (nowUtc < _nextServerRequestPruneUtc)
        {
            return;
        }

        _nextServerRequestPruneUtc = nowUtc.Add(ServerRequestHistoryLifetime);
        ScratchPeerIds.Clear();
        foreach (KeyValuePair<long, DateTime> request in LastServerRequestUtc)
        {
            if (nowUtc - request.Value >= ServerRequestHistoryLifetime ||
                ZNet.instance?.GetPeer(request.Key) == null)
            {
                ScratchPeerIds.Add(request.Key);
            }
        }

        for (int index = 0; index < ScratchPeerIds.Count; index++)
        {
            LastServerRequestUtc.Remove(ScratchPeerIds[index]);
        }
    }

    private static void ReplaceLocalSnapshot(IReadOnlyList<IceboxPinSnapshotEntry> entries)
    {
        LocalSnapshot.Clear();
        for (int index = 0; index < entries.Count; index++)
        {
            IceboxPinSnapshotEntry entry = entries[index];
            LocalSnapshot[entry.ZdoId] = entry;
        }
    }

    private static void ApplySnapshotToMinimap(Minimap? minimap)
    {
        if (minimap == null)
        {
            return;
        }

        Sprite? icon = ResolveIceboxIcon();
        EnsureCustomPinType(minimap, icon);
        HashSet<ZDOID> seen = new();
        foreach (IceboxPinSnapshotEntry entry in LocalSnapshot.Values)
        {
            seen.Add(entry.ZdoId);
            bool create = !LocalPins.TryGetValue(entry.ZdoId, out Minimap.PinData? pin) ||
                          pin == null ||
                          minimap.m_pins == null ||
                          !minimap.m_pins.Contains(pin);
            if (create)
            {
                pin = TryAddPin(minimap, entry.Position, _pinType);
                if (pin == null)
                {
                    continue;
                }
                LocalPins[entry.ZdoId] = pin;
            }

            if (pin == null)
            {
                continue;
            }

            pin.m_pos = entry.Position;
            pin.m_doubleSize = true;
            if (icon != null)
            {
                pin.m_icon = icon;
                if (pin.m_iconElement != null)
                {
                    pin.m_iconElement.sprite = icon;
                }
            }
        }

        List<ZDOID> remove = new();
        foreach (ZDOID zdoId in LocalPins.Keys)
        {
            if (!seen.Contains(zdoId))
            {
                remove.Add(zdoId);
            }
        }
        for (int index = 0; index < remove.Count; index++)
        {
            RemoveLocalPin(remove[index]);
        }

        minimap.m_pinUpdateRequired = true;
    }

    private static void EnsureCustomPinType(Minimap minimap, Sprite? icon)
    {
        if (minimap.m_visibleIconTypes == null || minimap.m_icons == null)
        {
            return;
        }

        if (!ReferenceEquals(_pinTypeMinimap, minimap) ||
            (int)_pinType < 0 ||
            (int)_pinType >= minimap.m_visibleIconTypes.Length)
        {
            int index = minimap.m_visibleIconTypes.Length;
            bool[] expanded = new bool[index + 1];
            Array.Copy(minimap.m_visibleIconTypes, expanded, index);
            expanded[index] = true;
            minimap.m_visibleIconTypes = expanded;
            _pinType = (Minimap.PinType)index;
            _pinTypeMinimap = minimap;
            minimap.m_icons.Add(new Minimap.SpriteData
            {
                m_name = _pinType,
                m_icon = icon
            });
            return;
        }

        for (int index = 0; index < minimap.m_icons.Count; index++)
        {
            Minimap.SpriteData data = minimap.m_icons[index];
            if (data.m_name != _pinType)
            {
                continue;
            }

            if (icon != null && data.m_icon != icon)
            {
                data.m_icon = icon;
                minimap.m_icons[index] = data;
            }
            return;
        }

        minimap.m_icons.Add(new Minimap.SpriteData
        {
            m_name = _pinType,
            m_icon = icon
        });
    }

    private static Sprite? ResolveIceboxIcon()
    {
        Sprite? generatedIcon = GeneratedPrefabRegistry.GetIceboxIcon();
        if (generatedIcon != null)
        {
            return generatedIcon;
        }

        GameObject? prefab = ZNetScene.instance?.GetPrefab(IceboxSubsystem.PrefabHash);
        Piece? piece = prefab != null ? prefab.GetComponent<Piece>() : null;
        if (piece?.m_icon != null)
        {
            return piece.m_icon;
        }

        GameObject? chest = ZNetScene.instance?.GetPrefab("piece_chest");
        return chest != null ? chest.GetComponent<Piece>()?.m_icon : null;
    }

    private static void RemoveLocalPin(ZDOID zdoId)
    {
        if (!LocalPins.TryGetValue(zdoId, out Minimap.PinData? pin))
        {
            return;
        }

        LocalPins.Remove(zdoId);
        if (pin == null)
        {
            return;
        }

        _boundMinimap?.RemovePin(pin);
    }

    private static void ClearLocalPins(bool clearSnapshot)
    {
        if (_boundMinimap != null)
        {
            foreach (Minimap.PinData pin in LocalPins.Values)
            {
                if (pin != null)
                {
                    _boundMinimap.RemovePin(pin);
                }
            }
        }

        LocalPins.Clear();
        if (clearSnapshot)
        {
            LocalSnapshot.Clear();
            _lastRevision = 0;
        }
    }

    private static void DisablePins()
    {
        if (LocalPins.Count > 0 || LocalSnapshot.Count > 0)
        {
            ClearLocalPins(clearSnapshot: true);
        }
        else
        {
            _lastRevision = 0;
        }

        InvalidateSnapshot();
        _renderDirty = false;
    }

    private static void InvalidateSnapshot()
    {
        _snapshotReady = false;
        _pendingRequestId = 0;
        _lastRequestUtc = DateTime.MinValue;
    }

    private static Minimap.PinData? TryAddPin(
        Minimap minimap,
        Vector3 position,
        Minimap.PinType pinType)
    {
        try
        {
            if (_addPinMethod == null)
            {
                MethodInfo[] methods = typeof(Minimap).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int index = 0; index < methods.Length; index++)
                {
                    MethodInfo candidate = methods[index];
                    if (candidate.Name == nameof(Minimap.AddPin) && candidate.GetParameters().Length == 7)
                    {
                        _addPinMethod = candidate;
                        ParameterInfo authorParameter = candidate.GetParameters()[6];
                        _defaultPinAuthor = Activator.CreateInstance(authorParameter.ParameterType);
                        break;
                    }
                }
            }

            return _addPinMethod?.Invoke(
                minimap,
                new[]
                {
                    (object)position,
                    pinType,
                    string.Empty,
                    false,
                    false,
                    0L,
                    _defaultPinAuthor!
                }) as Minimap.PinData;
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not add an Icebox minimap pin: " + exception.GetBaseException().Message);
            return null;
        }
    }
}
