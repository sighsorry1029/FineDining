# Development checks

Run these commands from the repository root on Windows. Use the .NET SDK,
the .NET Framework 4.8 targeting pack, and PowerShell 7 (`pwsh`).
The project resolves original Valheim and BepInEx references from the local game install;
the smoke scripts accept explicit paths when the install differs from their defaults.
Do not publicize game inputs. Jotunn is not a compile or required runtime dependency.
The 1.1.4 build and automated checks on 2026-10-09 use the installed Valheim
1.0.17 client build 25730771. A matching 1.0.17 dedicated-server DLL and actual
game/multiplayer execution have not been checked for this release.
The 1.1.3 release checks on 2026-09-27 use Valheim 1.0.16 client build 25527674
and the matching dedicated-server build 25527701. These are build and automated
check inputs, not a claim of actual game or multiplayer execution.

```powershell
dotnet build FineDining.sln -c Debug -p:DeployToGame=true
pwsh -NoProfile -File Tests/IntegrationSmoke.ps1
pwsh -NoProfile -File Tests/CookingStationPlanStoreSmoke.ps1
pwsh -NoProfile -File Tests/CookingProductionTranspilerSmoke.ps1
pwsh -NoProfile -File Tests/Check-GameApi.ps1 -CecilPath "$env:USERPROFILE/.nuget/packages/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll" -PluginDll bin/Debug/FineDining.dll -ManagedPath "C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed" -BepInExCore "C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core"
```

CookingStationPlanStoreSmoke exercises the built compact plan store against original
game DLLs in an isolated PowerShell process. It covers all auto/prepaid/bonus flag
combinations (including zero), exact prefab names, unchanged writes and revisions,
received-array caching, ownership/pooled-object cache invalidation, slot reuse,
explicit empty-state receipt, malformed/unsupported payloads and untouched v1 keys.
The 28/23-slot repeated-fill cases use one byte-array key and return to a three-byte
empty block. The bounded format uses 16-bit slot indices, record counts and UTF-8
name byte lengths, a one-byte flag field, and a maximum payload of 1 MiB.

This check uses actual ZDO.Set/Deserialize with nonpersistent ZDO fixtures and
minimal in-process ZNet/ZDOMan globals. It does not initialize Unity, alter a live
world, or execute the native full Serialize path, RPC transport, sector save
dirtiness or item spawning. The native receive envelope is synthetic; its plan
payload is produced by the mod. The new storage has no v1 reader or migration.
In-game validation must include emptying every old station before updating all
peers, cook/save/restart/collect, partial auto-eject failure, owner transfer,
disconnect/reconnect, zone unload/reload and station slot-count changes. Check both
host and dedicated-server roles. Existing v1 keys and their warnings are expected
to remain; old format rollback is not a conversion of the new saved plans.

Repeat Check-GameApi with the dedicated-server original Managed path matching
the client build. The 1.1.3 release check uses
`C:/Users/blizz/.codex/references/valheim/snapshots/dedicated-server-b25527701-windows-x64-20260925T211211Z-depot-restored/original/valheim_server_Data/Managed`.
The old 1.0.7 snapshot does not match these binaries: its
`PlayerProfile.s_bypassCheatChecks` field predates the getter used by 1.0.16.
This checks direct access, explicit Harmony contracts and selected cached
private/reflection bindings; it does not execute patches or Unity.
For Icebox quotas it also verifies the original Start/ServerLoadWorld call paths
to both save loaders and WorldSetup, and requires FineDining's hook at their
common completion point. This catches a Load-only hook that misses chunked saves
and new worlds; it does not simulate reading save files or running a server.

CookingProductionTranspilerSmoke reads the unmodified original DoCrafting IL
with Cecil and invokes the built FineDining TryInject method. It verifies one
insertion, three rejected drift cases without mutation, and emits the patched
bonus region to execute five single/batch/zero/fallback cases. Repeat with
`-ManagedPath` pointing to the dedicated-server original Managed directory.
The helper result, random source and two GUI fields are test stubs; full
crafting, Unity/Mono and composition with other mods are not executed.

This isolated PowerShell 7 check uses test-only NuGet cache dependencies:
HarmonyX 2.16.0, MonoMod.RuntimeDetour 25.3.3, MonoMod.Utils 25.0.11,
MonoMod.Core 1.3.3, MonoMod.Backports 1.1.2, MonoMod.ILHelpers 1.1.0 and
Mono.Cecil 0.11.6 (`lib/netstandard2.0`). These must be restored to the local
NuGet cache to run the script. They are not project runtime dependencies or
game replacements: FineDining still builds with the installed Harmony 2.9.
Desktop .NET Framework cannot read this game's DoCrafting method body because
of default interface methods, and installed Harmony 2.9 does not initialize on
the current desktop Core runtime. The isolated matcher/fragment test avoids
both limitations without modifying any game DLL.

The optional `pwsh -NoProfile -File Tests/AzuEpiCompatibilitySmoke.ps1` needs a
compatible `LocalReferences/AzuExtendedPlayerInventory.dll` or an explicit
`-AzuEpiAssemblyPath`. On 2026-09-10 the local test reference was updated from
2.4.7 to the supplied **2.4.10** DLL, SHA-256
`639AD639EF4237F9DFE403C91A754397FDCA0319A2AA69C1D31EF406A3E9CBFE`.
LocalReferences is git-ignored and is neither a compile dependency nor a
packaged DLL. Restore that verified input locally when reproducing the check.
The default command passes the original-game preflight and actual Harmony IL
test: 302 original instructions, 307 output instructions, one matched merge
and one injected expiry call. The script relaunches under Windows PowerShell
for the installed Harmony IL reader. This particular target loads successfully;
other game targets can still encounter the CLR default-interface-method limit.
The integration check requires PowerShell 7;
Windows PowerShell 5.1 misreads its UTF-8 Korean test literals.

Debug deployment copies only the final merged FineDining.dll into the game's
BepInEx/plugins folder. Use `DeployToGame=false` when explicitly skipping local
deployment, and compare source and destination SHA-256 hashes after deployment.

Only after an explicit release request, build and check the release packages:

```powershell
dotnet build FineDining.sln -c Release -p:DeployToGame=false
pwsh -NoProfile -File Tests/IntegrationSmoke.ps1 -AssemblyPath bin/Release/FineDining.dll -ThunderstoreZipPath Thunderstore/FineDining_v1.1.4.zip -NexusZipPath Nexus/FineDining_v1.1.4.zip
pwsh -NoProfile -File Tests/CookingStationPlanStoreSmoke.ps1 -AssemblyPath bin/Release/FineDining.dll
pwsh -NoProfile -File Tests/AzuEpiCompatibilitySmoke.ps1 -AssemblyPath bin/Release/FineDining.dll
```

Use the ZIP names for the version being built. Release builds refresh the
manifest and produce both packages. Mod Release Manager 2.1.0 can automatically
upload new ZIPs from registered watch folders. Ordinary Release builds leave
that existing automation alone; uploader inspection or changes require a
separate request. For packaging only or upload-prohibited work, use a separate
output folder outside existing watch folders. Globally pausing the queue does
not isolate a package. Debug checks must not generate release ZIPs.

The integration smoke checks managed calculations, persistence contracts,
Harmony metadata, source-level safeguards, and optional package contents. The
AzuEPI smoke checks the actual target IL and the injected merge call. Neither
starts Unity or proves multiplayer behavior.

IntegrationSmoke also checks the soft Jotunn boundary and executes replacement
cloning/provenance cases without loading Jotunn. Standalone execution of the
game's other transpilers is not a substitute for Unity/Mono: attempted desktop
CLR execution was blocked by runtime compatibility. Verify actual patch
application and optional patch composition inside Valheim. Current evidence,
ServerSync pin and the client/host/server checklist are recorded in
`Docs/Valheim-1.0.7.md`.

Cooking tooltip geometry checks call the built DLL with Unity `Rect` and
`Vector2` values. They cover row alignment, the left-side gap, all four viewport
edges, negative canvas origins, and uniform scaling in narrow or short
viewports. They do not verify text measurement, scroll masks, or input behavior
inside Unity.

For UI or state changes, also check in Valheim:

- Change all three diet feature toggles live on the server. Full Course must
  disappear and stop scaling the current diet on the next food-stat update;
  Chef/diminishing changes apply to new meals, preserving already eaten effects
  and their hover details. Chef Off hides its row, item/Puke/Cooking guidance,
  disables its penalty exemption, and pauses the saved list (including expiry,
  Puke, and admin rerolls). Re-enabling resumes that list. Shared history keeps
  updating; both recent/Chef rows disappear when both features are off. Check
  toggles while a hover is open, save/reconnect while off, and re-enable after
  recipe discovery or a blacklist change. The integration test runs all eight
  combinations against the built food rules with both local rows hidden and
  checks retained snapshots/list, history, and Cooking text without Unity or
  ServerSync transport execution.
- Toggle `Show Recent Food Row` and `Show Chef Choice Row` independently while
  hovering a row. Hidden rows must immediately dismiss their hover; showing
  them again must display the current state without clearing history or
  rerolling the Chef list. With only Chef shown, it occupies the first row.
  Check item/eaten-food tooltips, Full Course, and Chef refreshes while both
  rows are hidden. Verify that clients can choose different visibility settings
  with server configuration locked, and that the choices survive restart.
- Station inputs, fuel/full gates, progress rows, automatic ejection hints,
  language/scale changes, HUD recreation, and AzuCraftyBoxes present/absent.
- Chef lists after loading, recipe/material discovery, policy changes, language
  changes, and HUD recreation; re-eating and vanilla-effect replacement order.
- Icebox recipe changes, hammer requirements, storage shrinking from 20 to 4
  rows with items in the lower rows, and save/reconnect.
- Client, host, and dedicated-server sessions, including ownership changes.

For Icebox placement limits, start an existing chunked world, an old-format world,
and a new world without a save file. With the default limit of two and empty
overrides, allow the first two placements and reject/refund the third once.
Restart with existing boxes (including an over-limit baseline): count them but
never delete or refund them on load. Also check removal/replacement, reconnect,
duplicate placement notices, and the owner map pins. A repeated completion hook
must not reclassify a pending placement as an existing box; a failed world load
must not initialize the quota index. The game/transport and refund checks still
require actual client/host/dedicated-server execution.

For `Spoilage Mode`, IntegrationSmoke executes the mode overlay for every YAML
rule state/action, binds the default config without writing a file, and verifies
Off freshness/UI suppression, raw metadata retention, and the distinction between
an overdue clock and a permanent spoiled marker during inheritance. These managed
tests do not instantiate Unity inventories or run network synchronization.

For automatic cooking classification, IntegrationSmoke calls the built conversion
classifier with all six Deep North armor conversions plus weapon/shield cases,
dough/raw food, mixed CookingStation/Fermenter chains, dead branches, reachable
and unreachable cycles, case variants, and a rebuild without edible endpoints.
Equipment and unfinished casts must stay outside automatic cooking groups while
food ingredients keep their existing classification priority. This uses graph
fixtures, not a running Unity prefab scan.

In Valheim, check the regenerated `Spoilage.reference.yml` and inventory/ground
tooltips after loading with the patched DLL. With `FollowYaml` and no exact
equipment overrides, Frost Foundry equipment and casts must have no automatic
timer or replacement; old running timers must be cleared on reconciliation.
Verify ordinary cooking and modded multi-stage foods still
spoil, and that explicit positive/zero-hour equipment overrides still work.
Repeat with matching client/server DLLs and after save/reconnect. Already-replaced
items are not restored by the classification fix.

In Valheim, test live server changes between all three modes with new, running,
paused, overdue, and permanently marked items. Check inventory/container slots,
tooltips, ground and Feast hover text, placement/recovery, ordinary/AzuEPI/
InventorySlots merges, and save/reconnect. Off must create no new timer or anchor;
existing running deadlines can be due on re-enable. StatDecreaseOnly must retain
YAML zero-hour exclusions and keep expired items at minimum freshness. Already
eaten effects and permanently marked items must survive mode changes. Repeat on
client/host/dedicated-server setups, including ownership changes and reconnect
while policy synchronization is still pending.

For the Cooking skill tooltip, check in Valheim with Korean and English:

- Hover Cooking with the mouse, then select it with a gamepad. Verify the
  tooltip appears to the left of the skills panel, follows the row's top edge,
  keeps the normal hover delay, and stays outside the scroll mask.
- Scroll Cooking to the top and bottom of the visible list. Move away, close
  the inventory, and reopen it repeatedly; there should be no stale tooltip,
  duplicate panel, or accumulated position offset.
- Check 1920x1080, 1280x720, and a narrow window or ultrawide resolution, at
  small/default/large supported UI scales. The full panel should remain inside
  the screen margins, shrink uniformly when necessary, and show the topic and
  full multiline body without clipping or horizontal scrolling.
- Change language and Cooking-related settings, reopen the skills view, and
  confirm the original skill description and enabled FineDining lines remain
  present exactly once, with readable wrapping and unchanged numbers.
- With SecondaryAttacks installed, move repeatedly between its Sneak and Blood
  Magic skill tooltips, FineDining Cooking, another ordinary skill tooltip, and
  an inventory item tooltip. Repeat with mouse and gamepad. Each tooltip must
  keep its own placement, size, visibility, and text after returning from the
  others. Also check FineDining without SecondaryAttacks and with any UI or
  EpicLoot combination used by the target mod profile.

Compare GC allocations and UI rebuilds in the same scene before making a
performance claim. Preserve per-frame hint visibility when reducing cached work.
