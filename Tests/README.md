# Development checks

Run these commands from the repository root on Windows. Use the .NET SDK,
the .NET Framework 4.8 targeting pack, and PowerShell 7 (`pwsh`).
The project resolves Valheim and BepInEx references from the local game install;
the smoke scripts accept explicit paths when the install differs from their defaults.

```powershell
dotnet build FineDining.sln -c Debug -p:DeployToGame=true
pwsh -NoProfile -File Tests/IntegrationSmoke.ps1
pwsh -NoProfile -File Tests/AzuEpiCompatibilitySmoke.ps1
```

The AzuEPI check needs `LocalReferences/AzuExtendedPlayerInventory.dll` or
`-AzuEpiAssemblyPath`. It relaunches under Windows PowerShell for the installed
Harmony/.NET Framework IL reader. The integration check requires PowerShell 7;
Windows PowerShell 5.1 misreads its UTF-8 Korean test literals.

Debug deployment copies only the final merged FineDining.dll into the game's
BepInEx/plugins folder. Use `DeployToGame=false` when explicitly skipping local
deployment, and compare source and destination SHA-256 hashes after deployment.

Only after an explicit release request, build and check the release packages:

```powershell
dotnet build FineDining.sln -c Release -p:DeployToGame=false
pwsh -NoProfile -File Tests/IntegrationSmoke.ps1 -AssemblyPath bin/Release/FineDining.dll -ThunderstoreZipPath Thunderstore/FineDining_v1.0.6.zip -NexusZipPath Nexus/FineDining_v1.0.6.zip
pwsh -NoProfile -File Tests/AzuEpiCompatibilitySmoke.ps1 -AssemblyPath bin/Release/FineDining.dll
```

Use the ZIP names for the version being built. Release builds refresh the
manifest and produce both packages. Mod Release Manager 2.1.0 can automatically
upload new ZIPs from registered watch folders. Check the project's manifest name,
watch folder, team, target sites, and Hexium install location before packaging.
For packaging only, use an unwatched output folder or disable that project's
automatic upload; globally pausing the queue does not isolate the package.

The integration smoke checks managed calculations, persistence contracts,
Harmony metadata, source-level safeguards, and optional package contents. The
AzuEPI smoke checks the actual target IL and the injected merge call. Neither
starts Unity or proves multiplayer behavior.

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
