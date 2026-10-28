# Development checks

Run these commands from the repository root on Windows. Use the .NET SDK,
the .NET Framework 4.8 targeting pack, and PowerShell 7 (`pwsh`).
The project resolves original Valheim and BepInEx references from the local game install;
the smoke scripts accept explicit paths when the install differs from their defaults.
Do not publicize game inputs. Jotunn is not a compile or required runtime dependency.
The FineDining 1.1.5 Valheim 1.0.20 compatibility patch on 2026-10-15 was checked against original
client 25909357 and dedicated-server 25909431 DLLs. `FermenterCheatedSmoke.ps1` extracts
the built bonus predicate IL, substitutes only Unity lookup and delayed/bypass inputs,
and compares 24 combinations with original `CheatUtils.IsCheated(ZDO)`. Both roles
passed; the previous release DLL failed for revision 1 with ordinary input and bypass off.
The final DLL also passed Check-GameApi for both roles and IntegrationSmoke. These are
isolated managed/metadata checks, not actual item spawning or multiplayer validation.
The 1.1.4 build and automated checks on 2026-10-09 use the installed Valheim
1.0.17 client build 25730771. A matching 1.0.17 dedicated-server DLL and actual
game/multiplayer execution have not been checked for this release.
The 1.1.3 release checks on 2026-09-27 use Valheim 1.0.16 client build 25527674
and the matching dedicated-server build 25527701. These are build and automated
check inputs, not a claim of actual game or multiplayer execution.

```powershell
dotnet build FineDining.sln -c Debug -p:DeployToGame=true
pwsh -NoProfile -File Tests/IntegrationSmoke.ps1
pwsh -NoProfile -File Tests/FermenterCheatedSmoke.ps1
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

IntegrationSmoke also checks the auto-eject guarantee-level setting (0 disables,
default 100), the 25/50 linear curves and roll boundaries, overcook eligibility,
server-sync registration, and configured Cooking tooltip text. The plan-store
format and completion path remain independent of later threshold changes.
In-game checks should cover client/owner agreement at 0/25/50/100, manual versus
auto collection experience, a live tooltip update, and cooking a stored plan
after changing the setting. When configuration synchronization overlaps an insertion
RPC, the client and owner can observe different thresholds: the existing protocol
independently predicts prepaid experience on the client and decides the plan on
the owner. This patch does not make that transition atomic; the experience outcome
during such a transition still needs multiplayer validation.

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

### Diet-state persistence (2026-10-21)

After the Debug plugin build, run:

```powershell
pwsh -NoProfile -File Tests/FoodStateStoreSmoke/Run.ps1 -MonoHost <MonoHost.exe>
```

Use the same audited original-game Mono host described below for the PoV check.
This runner builds only its test executable and loads the final merged plugin,
original game assemblies and original Mono runtime. It leaves character files
and game DLLs unchanged. Generated input hashes, `mono.log` and a synthetic
`diet-state.json` stay in the ignored test build directory.

The regression check executes FoodStateStore's actual serializer/deserializer
and compares all populated Recent/Chef/Active fields and order, exact float bits,
Unicode/escaping, empty lists, missing optional breakdown fields, unknown fields,
invalid payloads and repeated roundtrips under three cultures. The scalar-only
payload found in all five supplied server character files remains readable;
already missing history cannot be reconstructed. A separate PowerShell JSON
reader validates the emitted format independently of the YAML-based reader.

The lifecycle fixture executes actual SaveState/GetState, cache invalidation,
RecentHistoryService and the existing OnDeathPostfix. A fresh Player receives
customData through original ZPackage string encoding. Reconnection preserves
all lists and continues consumption counts; clearing vanilla active foods then
calling the death postfix preserves Recent/Chef while removing Active effects.
Unity object identity/animator hashing and minimal BepInEx initialization are
test fixtures. No original DLL is publicized or rewritten.

This is an isolated Mono/managed check, not a live game, complete Player.Save/Load,
`.fch` file, transport or dedicated-server session. In game, apply the patched DLL
to the actual client profile and server, restart, eat multiple foods (including
a repeat), and compare `fd:printstate` before death, after respawn, and after
logout/login. Recent keys/order/counts and Chef choices/multipliers must survive;
Active effects disappear on death as usual. Also verify a newly saved character
contains all three JSON lists. Keep a copy of the character before testing.

### PoV custom food cards (2026-10-19)

Build the Debug plugin first, then run the new original-Mono model check:

```powershell
pwsh -NoProfile -File Tests/PoVTooltipSmoke/Run.ps1 -MonoHost <MonoHost.exe> -PathOfValheimanDll <original-5.0.10.dll> -OptionalDependencyDirectory <Jotunn-directory>
```

`MonoHost` is the existing audited host from InventorySlots' `build/PathOfValheimanSmoke`
(built by its `Run-MonoSmoke.ps1`, using `InventoryActions/build/CompatibilitySmoke/MonoHost.cpp`).
It loads the installed game's original Mono runtime and performs normal runtime cleanup.
The script builds only the test executable, records input hashes, and neither publicizes
nor rewrites original DLLs. PoV 5.0.10 input SHA-256:
`252B60914EFC9F402A977A8C235E28BCB363D7BB7CEEBDF5F22A228518282E74`.

The checks bind original private PoV model/Core fields and its food tooltip prefix,
install/remove the actual model postfix, and exercise the built FineDining food preview.
They cover decimals, missing/zero food rows, raw-food card classification, other-mod
rows, unchanged duration, repeated previews, item/save-data preservation, minimum
freshness wording, snack regeneration, no local player, and display-change comparison.
The existing desktop PoV smoke also checks the four gameplay patches against 5.0.10.

Fixtures substitute Unity object identity, animator hash, color parsing and localization,
plus progression getters and managed player state. A supplied model replaces PoV's
native-dependent BuildCore body only for the installed-postfix execution check. These
are not card rendering, full consumption, BepInEx startup or multiplayer tests.
In game, check inventory/chest hover and pinned cards, freshness countdown/paused
storage/minimum freshness, raw food and snacks, live diet/progression changes, and
Alt-held wiki links. Also check PoV layout Off, PoV absent and reconnect/scene teardown.

Pre-release 1.1.6 Debug verification on 2026-10-19: build had zero warnings/errors; the original Mono
runner passed 23 checks, exited 0 and completed Mono cleanup. IntegrationSmoke and
the existing PoV compatibility smoke passed. Check-GameApi passed 1,991 direct
references, 82 explicit Harmony targets and 37 selected private bindings. Final
merged Debug DLL and Steam plugins copy both have SHA-256
`523AA890E794C7822BB13B2946F5E396921B4FD885144872546F916D7C9CB109`.
No Release package, version change or actual game/multiplayer run was performed.

Release 1.1.7 verification on 2026-10-19: Debug and Release built with zero warnings/errors.
The final Release DLL passed the same 23 original-Mono checks (exit 0, cleanup
completed), IntegrationSmoke including both ZIPs, the existing PoV compatibility
smoke and Check-GameApi with the counts above. Thunderstore/Nexus packaged files
match their release sources. Release DLL SHA-256:
`4B5DC2DDFBB9B2BEB38F6DDCD34F6B8F374819D3525A233C1BC2AFB9CBC5C818`.
The Debug DLL and Steam plugins copy both have SHA-256
`C986381C0A40102902800A8A961E2F3D8FCD1EF82920F238E40587FDC766E262`.
Actual game and multiplayer checks remain outstanding.

PoV 5.3.2 card compatibility on 2026-10-25: the supplied original DLL has SHA-256
`83BD50F23D9C301FE469B5FBE2C430FD35C928D7A2CCFD99AF5A1040558C9D72`.
Its `owners` weak table now stores an internal Owner wrapper instead of ItemData,
which reproduced the 1.1.8 missing-field warning in the original Mono test.
FineDining now binds the renderer's private `OwnerOf(UITooltip)` method once and
reads the current item through that cached delegate. The exact direct-ItemData
table fallback remains for older PoV builds without that method; no version or
whole-DLL hash gate was added. Older original DLLs were unavailable for this retest.

The expanded original-Mono runner passes 30 checks, including actual card-patch
installation and PoV's own registration hook with null/unbound owners, tooltip
reuse, multiple owners, and null registration. Existing food/freshness model checks
still pass. The gameplay compatibility smoke also passes against 5.3.2, including
food/snack/cooking IL patterns and fermenter handling. Debug build, IntegrationSmoke
and Check-GameApi pass; the final DLL is copied to Steam plugins with a matching
SHA-256. These checks use the unchanged original Valheim 1.0.20 client DLL.
No actual UI rendering, Alt-held interaction or multiplayer was run. The one-second
refresh interval and pinned-card guard remain unchanged; verify them in the target
Gale profile after replacing its DLL and restarting the game.

Before the 1.1.9 release, the follow-up recheck found that the supplied `asdfasdf` Gale profile still had
the unpatched 1.1.8 Release DLL (`4986C59D...D0D94E6B`), while the existing Debug
build already used `OwnerOf`. No further gameplay or card logic change was needed.
After the checks passed again, a Debug build updated both Steam plugins and that
profile through `DeployToGame=true` / the existing second-copy MSBuild target.
Both deployed files match the final DLL SHA-256
`90F87D08DC5AC72F8E9E6DC566E104D007FBD6678A050C413E604174A5458513`.
The previous profile DLL is backed up under ignored `obj/local-backups/pov-532`.
Valheim was running during deployment; a full restart is required to load the
replacement. That Debug-only step retained version 1.1.8 and generated no Release package.

Release 1.1.9 on 2026-10-25 passed IntegrationSmoke (including both ZIPs),
Check-GameApi, the PoV gameplay compatibility smoke and all 30 original-Mono card
checks. Debug deployment to Steam matched its build output. The Release DLL has
SHA-256 `897D0B165D150ADA9B9CF1C5A80DEEE9E5B929CD3CABEB40A4A03B855F7CF2D0`;
Thunderstore and Nexus ZIPs contain that exact DLL. Plugin, assembly and manifest
versions match, and the package has no external game or optional-mod DLLs.
Actual game/UI/multiplayer validation remains outstanding.

Only after an explicit release request, build and check the release packages:

```powershell
dotnet build FineDining.sln -c Release -p:DeployToGame=false
pwsh -NoProfile -File Tests/IntegrationSmoke.ps1 -AssemblyPath bin/Release/FineDining.dll -ThunderstoreZipPath Thunderstore/FineDining_v1.1.8.zip -NexusZipPath Nexus/FineDining_v1.1.8.zip
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

### Optional Path of Valheiman integration

PoV stack regression (2026-10-28):

```powershell
pwsh -NoProfile -File Tests/PoVStackSmoke/Run.ps1 -MonoHost "<x64 MonoHost.exe>" -PathOfValheimanDll "<original PathOfValheiman.dll>" -OptionalDependencyDirectory "<Jotunn directory>"
```

Inputs: PoV 5.3.6 SHA-256
`EA9CE4ACC6EB85C903EC415BAE66D23A1EA112F6C82B846FACD847BAB3E72416`,
original installed Valheim 1.0.20 `assembly_valheim.dll` SHA-256
`94998885B103D6E5F5480123EAEE0A093E82AF2DDEEEE37536C15BF555F24F3A`,
and the game's Mono runtime/BepInEx HarmonyX. No input DLL is publicized or rewritten.
The harness installs original PoV stack callbacks and production FineDining
tracking/adapter patches. It executes original Inventory AddItem overloads,
CanAddItem, drag preview and PoV Sort, with authority/loading/time/environment,
native Unity identity, UI selection and change-notification boundaries replaced
by fixtures. It covers partial transfers, clocks/keep markers, malformed and
foreign metadata, world-level differences, scoped permissions, exceptions,
atomic sort rejection and cleanup. InventorySlots/InventoryActions owner-order
checks use synthetic rejecting prefixes under their real owner IDs; they are
not a full execution of those plugins.

The adapter checks relevant members, registered callbacks and sort/comparison
IL structure, not a DLL hash/version allowlist. This bounded check is not proof
of semantic equivalence for arbitrary future code. Bindings last until plugin
shutdown; live DLL replacement or removing the owning tracking patches while
playing is unsupported. There is no reflection discovery or UI rebuild added
to the drag-preview path.

Still required in-game: pickup with a full bag, partial drag/shift moves,
player/chest/Icebox transfers, PoV sorting with mixed food ages and keep markers,
all spoilage modes, real IS/IA together and separately, save/reconnect and
client/host/dedicated ownership changes. Verify counts and earlier expiry after
reloading. The isolated fixtures do not establish network or live UI behavior.

```powershell
pwsh -NoProfile -File Tests/PathOfValheimanCompatibilitySmoke.ps1 -PathOfValheimanDll "<path to original PathOfValheiman.dll>"
```

The 2026-10-18 check uses PoV 5.0.4, SHA-256
`36E45B65B44667AD596446CAC2AE989041669948C11710B05185E4FE16697DB4`,
and original Valheim 1.0.20 client 25909357 / dedicated-server 25909431 inputs.
The PoV DLL is a test input, not a project reference or distributed dependency.
The script uses the same test-only desktop HarmonyX dependencies listed above.
It binds the original private signatures, installs/removes all four adapter
patches in isolation, reads original IL and runs the emitted food-return,
snack-value and cooking-packet fragments. It covers both food return paths,
zero/fractional/unit/bonus scales, unchanged ingredient and cheated payloads,
six rejected IL drift cases, Harmony cancellation/results and patch-method
wrapping, strict temporary fermenter counts, nested/failed-prefix cleanup,
no-PoV defaults and PoV's original recovery cadence math.

Only the fragments execute: diet/freshness inputs, native prefab/name lookup
and RPC transport are substituted. Neither plugin startup nor a Unity world
is run, and desktop HarmonyX success does not establish Mono/game execution.
Before treating combined multiplayer behavior as verified, check:

- FineDining alone and with PoV: normal/stale food, Chef/DR toggles, Full Course
  activation/expiry, food-derived masteries, No Decay, Hearty Meal and item hover.
- Raw food across save/reconnect; snacks at their cap without extra food slots,
  Chef/history changes, duplicate experience or repeated inventory consumption.
- Faster/slower recovery with status-effect modifiers and unchanged total recovery.
- Batch cooking, auto-eject and combined bonus counts on host/dedicated owners,
  including save/restart, owner transfer, disconnect and full/partly filled stations.
- Fermenter extra output and PoV queue promotion: expected counts and output
  metadata, including failed/cancelled taps and repeated requests.
