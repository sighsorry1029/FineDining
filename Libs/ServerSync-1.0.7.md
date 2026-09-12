# Pinned ServerSync input

FineDining vendors the shared `valheim-1.0.7-r1` ServerSync build in
`Libs/ServerSync.dll` and merges it into FineDining.dll with ILRepack. It is not
installed as a separate plugin. Builds do not fetch a moving global reference.

- SHA-256: `B4DD786997F4E90D770F09EF3E9D64154754FE7E8EDFB4841795751895B35846`
- Assembly version: `1.0.0.0`; file version: `1.0.0.1`.
- Upstream commit: `c57c2aa54e07cdcc7630d6068699ea781622323e`.
- License: MIT-0; see `THIRD-PARTY-NOTICES.txt`.
- Replaces SHA-256 `166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60`.
- Reproducible source, baseline, build inputs, manifest, API comparison and tests:
  `C:/Users/blizz/.codex/references/valheim/integrations/serversync/versions/valheim-1.0.7-r1/`.

This is a local compatibility build, not a new official upstream release.
It compiles against original Valheim 1.0.7 DLLs, handles the now-constant
`ZRoutedRpc.Everybody`, and uses `ZNet.IsAdmin(string)` for the same admin policy.
Its existing PeerInfo buffer also queues PlayerList, HistoricalPlayerList,
AdminList and NetTime until receiver registration, retaining FIFO order and
the VersionMatch boundary. Control messages still pass through.

Public/protected API, assembly identity, RPC names and wire format remain the
baseline contracts. FineDining's ConfigSync identity, version policy, config
keys and CustomSyncedValue formats are unchanged. This DLL does not support the
old game runtime through a compatibility fallback.

The shared reference reports original client/server builds, API comparisons,
and 19 isolated checks per target. Those are shared-library evidence, not an
in-game FineDining test. FineDining's merged assembly is separately checked
against both original game targets. Steam/PlayFab connection, large config
sync, admin changes, reconnect and multiple embedded ServerSync mods still need
actual game verification; see `Docs/Valheim-1.0.7.md`.
