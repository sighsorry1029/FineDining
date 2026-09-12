# FineDining: Valheim 1.0.7 compatibility and optional Jotunn

Implementation record, 2026-09-10. Released as mod version **1.0.7**. This is a
Debug patch, not a released package or an assertion of completed game testing.

## Scope and reference inputs

The single `FineDining.sln` / `FineDining.csproj` plugin targets .NET Framework
4.8. `FineDiningPlugin.Awake` initializes config, content, diet and station
modules and installs Harmony patches; `OnDestroy` detaches optional events,
shuts down modules and removes owned resources/patches.

The project now compiles against the original installed game assemblies,
without publicized references or a Jotunn assembly/package reference. The
reviewed game is Valheim 1.0.7, Windows client Steam build **25185596** and
dedicated server build **25185644**. Original reference snapshots are under
`C:/Users/blizz/.codex/references/valheim/snapshots/`:

- `client-b25185596-windows-x64-20260909T131109Z`
- `dedicated-server-b25185644-windows-x64-20260909T131109Z`

The native registration and temporary icon rendering approach was compared
with the local FeedLikeGrandma 1.0.7 working implementation. That project was
not changed. FineDining retains its own collision checks, replacement readiness,
recipe policy, identities and lifetime ownership instead of copying a manager.
The earlier review is in the global comparison folder
`comparisons/0.221.12--1.0.7-windows-x64/finedining-1.0.6-review-20260909/`.

## Implemented changes

### Jotunn dependency boundary

`GeneratedPrefabRegistry` owns an inactive persistent root, two rotten item
prefabs, the Icebox prefab and two cloned Puke effects. ObjectDB Awake,
CopyOtherDB, UpdateRegisters and ZNetScene Awake trigger registration. Repeated
registration reuses the owned objects and updates ObjectDB's item list, hash
and SharedData dictionaries, status effects, ZNetScene's prefab lists/hash
dictionary and the Hammer build table. Conflicting foreign names/hashes are
not overwritten. Missing prerequisites can retry at a later content event.

`EnsureGeneratedReplacementAvailable` requires the exact owned prefab and
effect in every live registry. Incomplete generated content retains the
expired original item for retry. Invalid Icebox recipes hide new construction
while keeping the network prefab for existing saved boxes. Shutdown removes
only owned entries and destroys only owned objects, sprites and textures.

`GeneratedIconRenderer` renders the three fixed icons outside the frame loop.
It strips gameplay components on an inactive clone before activation, restores
the active render target, cleans up temporary objects, and skips headless
rendering. Materials are borrowed; one material search per content notification
handles missing assets without retaining all loaded materials. The Full Course
PNG uses cached Unity image decoding and explicit texture ownership.

`JotunnCompatibility` uses a BepInEx soft dependency and cached reflection only
when Jotunn is present. It observes other mods' ModRegistry ownership and item/
prefab registration events, detaching events at shutdown. Vanilla, bundle and
Unknown ownership fallbacks remain. FineDining can load without Jotunn, but
installing this patch does not repair an incompatible Jotunn used by other mods.

**External discovery change:** FineDining's native content is no longer added
to Jotunn's CustomItem/CustomPiece/CustomPrefab collections. Other mods that
enumerate only those collections will not discover FineDining's content there.
Game prefab names, stable hashes, ObjectDB/ZNetScene lookup, FineDining's public
API and saved identifiers remain. No second Jotunn registration owner is added.

### Required game API adaptations

These are compatibility fixes in addition to the dependency restructuring:

- Cached private accessors in `GameAccess` replace publicized direct accesses;
  public inventory/container accessors are used where equivalent.
- Harmony signatures follow 1.0.7's tooltip, positioned Inventory.AddItem and
  ItemDrop.OnCreateNew arguments. Inventory overlays use InventoryElement and
  tooltips use ZInput pointer coordinates while retaining placement behavior.
- Fermenter content and delayed output use integer prefab hashes. Cooking
  station add requests and output calls preserve the new cheated boolean.
  Fermenter bonus output follows vanilla cheated/bypass handling. Existing
  sender, ownership, batch/token, duplicate and recovery checks remain.
- Spoilage replacements carry source world level and cheated provenance;
  manual replacement stacking does not merge different provenance. The custom
  EatFood path records the new per-food statistic alongside the existing total.
- The pinned shared ServerSync 1.0.7 build replaces the old embedded binary.
  See `Libs/ServerSync-1.0.7.md` for hash, source and handshake corrections.

Config keys, YAML formats, CustomSyncedValue formats, item custom data
keys, public FineDining API and content names are unchanged. The ResourceMap
default data is extended as described below. Manifest now
requires BepInExPack 5.4.2350 and does not require Jotunn. No old-game migration,
release version increment or unrelated mod patch is included. The ResourceMap
selection-weight changes are described below.

### ResourceMap follow-up: DeepNorth food ingredients

On 2026-09-10 the existing eight sections and 132 resource tokens were preserved
and a final DeepNorth section added: MooseMeat, SealBlubber, Kale, Poteitr,
Lingonberry, GlowWorm, Oat, OatFlour, Ice and SpiceDeepNorth. No tier resolver,
parser, sync, save format, multiplier formula or per-frame code was changed.
This follow-up changes Chef selection weights through the existing formula.

The repository default and the existing local host configuration were both
updated. The previous configuration was backed up to
`C:/Users/blizz/.codex/references/valheim/comparisons/0.221.12--1.0.7-windows-x64/finedining-resourcemap-review-20260910/applied-20260910T005514/ResourceMap.before.yml`.
Both updated map files have SHA-256
`CD21A655A63586C78A16D62AF4DF4F7FB0B04467F36DBB37494C5B51A2CC709E`.
Future updates still preserve existing user maps; no automatic merge was added.

The reviewed original-data model predicts 20 previously unassigned North foods
becoming DeepNorth and Pancakes moving from Plains to DeepNorth. PulledBear and
old foods retain their tier indices. This is a static model, not a new in-game
reference generation. Metals, molds, trophies and unrelated resources were not
copied from other mods' maps. Earlier mushroom/neck-tail/fish classification
questions remain separate.

With nine tiers, the existing normalization denominator changes from 7 to 8.
At Cooking 100 and default selection strength 5, AshLands weight changes from
148.41 to 79.44; DeepNorth weight is 148.41. These are candidate weights, not
selection percentages. Valid existing Chef entries and their multipliers are
retained; future replenishment uses the updated weights.

IntegrationSmoke now checks the nine-tier order, existing tier indices, 142
unique normalized tokens and the new resources. The installed YAML also passes
the built parser and equals the embedded map's policy. The subsequent Debug
build/deployment succeeded with zero warnings/errors. Actual hot reload,
FoodTier.reference regeneration, multiplayer synchronization and save/reconnect
remain game checks; the existing generated reference was not manually edited.

## Verification record

### Cooking production matcher follow-up

The 1.0.7 original DoCrafting body resolves GetCurrentCraftingStation before
Recipe.GetAmount and initializes the displayed bonus after requirement checks.
The previous matcher required the reverse call order and treated the station's
next local as the bonus. It rejected the unchanged original body with the
reported anchor warning. API existence/build checks had not covered this pattern.

The matcher now resolves station/result locals independently, identifies the
bonus local from its verified accumulation, and requires one corresponding zero
initialization before the bonus loop. Loop bounds, amount writes, inventory
capacity-check exit, Harmony order and the vanilla fallback remain guarded.
The injection still runs once before the bonus loop and adds the calculated
per-item bonus once; it does not change vanilla behavior for other skills or
excluded recipes. Requirement checks, ingredient consumption, cheat provenance,
upgrading, statistics and network behavior outside that region are unchanged.

CookingProductionTranspilerSmoke reproduced the previous rejection, then passed
on client and dedicated original IL after this change: 859 -> 875 instructions,
one helper call, three rejected malformed patterns, five executed isolated
single/batch/zero/vanilla-fallback cases. It uses Cecil plus test-only HarmonyX
2.16 under PowerShell 7 and stubbed helper/random inputs; the deployed mod still
references installed Harmony 2.9. This proves matcher/fragment behavior, not
actual Unity/Mono detour installation or full crafting with other mods.

After restarting the affected profile, verify absence of the reported warning,
single/batch Cooking outputs and displayed bonus, exclusions/non-Cooking
recipes, full inventory, requirement failures, no-cost/cheated inputs and
RepairRequiresMaterials/other crafting patches. No game session was started
as part of this follow-up.

- **Build:** Debug with `DeployToGame=true`, original installed client DLLs,
  final ILRepack output; zero warnings and errors. Only final FineDining.dll
  is deployed to `Valheim/BepInEx/plugins` and the affected Gale profile
  `asdfadsfadf/BepInEx/plugins/sighsorry-FineDining` via `CopyOutputDLLPath2`.
  Final build and both deployed SHA-256 hashes equal
  `7EF94C4C3147B62F165EBCCC02AB601791D7AB483F92437D9D26049519F02708`
  after the Cooking matcher follow-up. The previous Gale DLL is preserved in
  `cooking-production-fix-20260910T012134/FineDining.gale-before.dll` under the
  global `finedining-1.0.6-review-20260909` comparison directory; its SHA-256 is
  `3818DD4B065488B7DFE4D2FC3EADF0E3D32FBF5C3B0E327C217654B534772533`.
  Installed assembly_valheim, assembly_utils and assembly_guiutils hashes match
  the archived original client inputs.
- **Automatic:** IntegrationSmoke passes managed calculations, persistence,
  geometry, patch metadata and source safeguards. New replacement checks invoke
  built code and original ItemData.Clone for cheated/non-cheated provenance,
  independent custom data and template preservation; no Jotunn assembly loads.
- **Automatic:** Check-GameApi passes against both original client and dedicated
  server: 1,922 direct game references, 82 explicit Harmony target/argument
  contracts, 37 private bindings and selected dynamic/reflection contracts.
  The merged ServerSync handshake message set is checked in the final DLL.
  This verifies metadata/IL, not detour installation or all indirect calls.
- **Optional integration IL test passed:** on 2026-09-10 LocalReferences was
  updated from AzuEPI 2.4.7 to the supplied **2.4.10** DLL, SHA-256
  `639AD639EF4237F9DFE403C91A754397FDCA0319A2AA69C1D31EF406A3E9CBFE`.
  Its recovery postfix calls Changed(bool, bool), resolving the previous old
  input's preflight failure. The default AzuEpiCompatibilitySmoke command
  passes with the unchanged FineDining adapter: 302 original / 307 output IL
  instructions, one matched direct merge and one injected expiry call.
  The target's 50 game-reference instructions also resolve against both
  original client and dedicated-server metadata. This executes the IL reader
  and FineDining Transpiler, not actual in-game detour installation or recovery.
- **Runtime not completed:** running the other game IL transpilers in standalone
  Windows PowerShell hit its .NET Framework default-interface-method loader
  limitation; PowerShell 7 also cannot substitute for the installed Harmony/
  Mono runtime. This is not recorded as a transpiler success or game failure.
  Actual Unity/Mono patch application, rendering and network behavior were not
  executed. No performance improvement is claimed from static checks.

Generated bin/obj output and external library internals beyond the cited
ServerSync/Jotunn contracts are excluded from product-source review. Steam/
PlayFab native transport, third-party patch combinations and all reflection
not explicitly covered by Check-GameApi remain outside automatic verification.

## Required game verification

Use backups of a representative world/profile and the same patched FineDining
on client, host and dedicated server. Test without Jotunn first, then with a
compatible Jotunn and late-registering content mods.

1. Load menu/world, leave and reconnect repeatedly. Verify two rotten prefabs,
   two Puke effects (5/10 seconds), Icebox saved contents and Hammer recipe,
   with no duplicate entries or startup errors. Exercise recipe enable/disable,
   missing resources and foreign name/hash collisions: existing saved items
   must survive and incomplete replacement registration must retain originals.
2. Check both rotten icons, Icebox icon/material and Full Course PNG on client;
   dedicated startup must not create render cameras. Repeat HUD/inventory
   destruction, language and scale changes, skill tooltip hover/controller use
   and SecondaryAttacks transitions. Check for retained resources after reload.
3. Spoil inventory, ground and placed food; exercise full storage and Icebox
   shrinking, mixed cheated/world-level stacks, saves and reloads. Count items
   before/after, including replacement failure and ownership transfer.
4. Cook, auto-pop and ferment with local/remote users. Verify bonus quantity,
   XP, provenance and new statistics. Repeat requests, ownership changes and
   disconnect during delayed output: no loss or duplicate bonus. Confirm
   transpiler success/fallback logs and combine the target optional mods.
5. Connect host/dedicated through Steam and PlayFab with small and fragmented
   configs; verify player/history/admin lists, time, lock/non-admin rejection,
   admin changes, version rejection, interrupted sync and reconnect. Repeat
   alongside other ServerSync-embedding mods. Check config restoration and
   absence of duplicated callbacks.
6. With AzuEPI 2.4.10, validate inventory load/stack spoilage behavior in-game;
   its local IL smoke has passed. Check hidden-slot recovery with full storage,
   partial/full merges, earlier expiry, save/reconnect and cheated provenance.
   AzuEPI's direct recovery merge does not propagate m_cheated; this is an
   external recovery-policy issue, separate from FineDining timer compatibility.
   Updating the local test reference does not install AzuEPI into the game.
   Compare frame allocations, material
   searches and UI rebuild counts in the same scene before claiming speedups.

Build commands and additional UI checks are in `Tests/README.md`. No Release
ZIP, upload, commit or push was performed for this patch.
