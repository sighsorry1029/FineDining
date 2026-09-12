# Development checks

Run these commands from the repository root on Windows. Use the .NET SDK,
the .NET Framework 4.8 targeting pack, and PowerShell 7 (`pwsh`).
The project resolves original Valheim 1.0.7 and BepInEx references from the local game install;
the smoke scripts accept explicit paths when the install differs from their defaults.
Do not publicize game inputs. Jotunn is not a compile or required runtime dependency.

```powershell
dotnet build FineDining.sln -c Debug -p:DeployToGame=true
pwsh -NoProfile -File Tests/IntegrationSmoke.ps1
pwsh -NoProfile -File Tests/CookingProductionTranspilerSmoke.ps1
pwsh -NoProfile -File Tests/Check-GameApi.ps1 -CecilPath "$env:USERPROFILE/.nuget/packages/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll" -PluginDll bin/Debug/FineDining.dll -ManagedPath "C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed" -BepInExCore "C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core"
```

Repeat Check-GameApi with the dedicated-server original Managed path. The
reviewed snapshot is `C:/Users/blizz/.codex/references/valheim/snapshots/dedicated-server-b25185644-windows-x64-20260909T131109Z/original/valheim_server_Data/Managed`.
This checks direct access, explicit Harmony contracts and selected cached
private/reflection bindings; it does not execute patches or Unity.

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
pwsh -NoProfile -File Tests/IntegrationSmoke.ps1 -AssemblyPath bin/Release/FineDining.dll -ThunderstoreZipPath Thunderstore/FineDining_v1.0.10.zip -NexusZipPath Nexus/FineDining_v1.0.10.zip
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

- Station inputs, fuel/full gates, progress rows, automatic ejection hints,
  language/scale changes, HUD recreation, and AzuCraftyBoxes present/absent.
- Chef lists after loading, recipe/material discovery, policy changes, language
  changes, and HUD recreation; re-eating and vanilla-effect replacement order.
- Icebox recipe changes, hammer requirements, storage shrinking from 20 to 4
  rows with items in the lower rows, and save/reconnect.
- Client, host, and dedicated-server sessions, including ownership changes.

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
