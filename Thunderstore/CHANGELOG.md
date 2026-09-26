# Changelog

## 1.1.2

- Added independent, server-synchronized `Full Course Enabled`, `Diminishing Returns Enabled`, and `Chef Choice Enabled` settings, all enabled by default. Full Course changes apply to the current diet on the next food-stat update; Chef and diminishing changes apply to newly eaten foods while preserving already-eaten effects.
- Disabling Chef's Choice hides its guidance and pauses list consumption, refills, expiry/Puke rotation, and admin rerolls. Saved selections and shared consumption history are retained for re-enabling.
- Added client-only `Show Recent Food Row` and `Show Chef Choice Row` settings, both enabled by default. Hiding a row dismisses its hover without stopping food effects, history, Chef updates, or item/eaten-food tooltips. The Chef row moves up when the recent-food row is hidden.
- Added regression checks for all eight gameplay-toggle combinations with both HUD rows hidden, retained food effects and Chef state, and conditional Cooking guidance.
- Updated the packaged BepInEx dependency to `5.4.2351`.

## 1.1.1

- Fixed Icebox placement limits remaining inactive on dedicated servers and hosts when loading chunked saves or creating a new world. Quota initialization now runs after the common server world-loading step, covering old saves, chunked saves, and worlds without a save file.
- Prevented failed loads and repeated initialization from replacing the accepted Icebox baseline. Existing boxes remain intact and count toward the limit; new placements retain the existing rejection and refund behavior.
- Added regression checks for the Icebox initialization hook and the original client/server world-loading paths.

## 1.1.0

- Added the server-synchronized, live `Spoilage Mode` setting. `FollowYaml` is the default and preserves existing rules; `Off` stops new timers and expiry processing, hides spoilage UI, and removes freshness penalties from food eaten while Off; `StatDecreaseOnly` follows YAML lifetimes and exclusions but keeps expired items at minimum freshness instead of replacing them.
- Mode changes preserve saved spoilage data. `Off` does not freeze world time, so existing running deadlines can expire when re-enabled. Already-eaten food effects, rotten replacements, and permanent minimum-freshness markers are not reset by switching modes.
- Preserved inherited timers and permanent minimum-freshness markers when placing and recovering tracked food, including while spoilage is Off. The generated spoilage reference continues to describe the underlying YAML rules independently of the selected mode.
- Added regression checks for mode defaults, YAML rule handling, Off UI and freshness suppression, and inherited spoilage state.
- Reorganized related README screenshots into two-column groups and replaced static images with smaller WebP previews linked to their full-resolution originals. Animated GIFs remain unchanged.

## 1.0.10

- Changed the default `Maximum Food Slots` from 9 to 4 for newly created configurations. Existing local and server configuration values remain unchanged, and the supported range stays at 3 through 9.
- Fixed cooking-station input hints briefly reusing a cached available or unavailable state after the fire or free-slot condition changed. Transient availability is now checked on every hover update without caching an empty result.
- Restored configured Icebox dimensions when `Container.Load` exits with an exception, while preserving the original load error for Harmony and other mods.
- Centralized freshness lifetime composition across vanilla and optional inventory integrations, and reused FineDining's shared config directory for Icebox policy loading without changing data keys or file locations.

## 1.0.8

- Fixed repeated Unity errors when station hint cards cloned Valheim 1.0 inventory elements containing `TouchRaycastPadding`. Inherited root behaviours are now disabled without breaking their required `Image` component relationship.

## 1.0.7

- Updated FineDining for Valheim 1.0.7, including the changed crafting, food, HUD, fermenter, and inventory contracts used by its Harmony patches and runtime integrations.
- Removed the hard Jotunn dependency. FineDining now registers its generated rotten foods, Icebox, icons, and effects itself; Jotunn remains an optional compatibility integration when another installed mod uses it.
- Updated the bundled ServerSync compatibility build for Valheim 1.0.7 while preserving configuration locking, version checks, and synchronization identifiers.
- Fixed Cooking bonus-output patch detection for Valheim 1.0.7. Per-item bonuses once again apply to single and batch crafting while retaining the guarded vanilla fallback if the target method changes again.
- Added a Deep North tier and its confirmed food ingredients to the default `ResourceMap.yml`. Existing server and single-player maps remain unchanged and must merge the new section manually to adopt it.
- Updated AzuExtendedPlayerInventory compatibility validation for 2.4.10 and retained safe spoilage metadata recovery without adding a hard dependency.
- Tightened lifecycle cleanup, private game-member access, item provenance handling, and hot-path UI/station work while preserving saved food data, network ownership checks, and external configuration formats.

## 1.0.6

- Cooking skill descriptions now appear to the left of the skills panel, aligned with the hovered row. Long descriptions scale and stay within the screen bounds, with mouse and gamepad support.
- Preserved existing Cooking descriptions and conditional FineDining tips, and added tooltip cleanup when skill rows are reused or the inventory UI is destroyed. SecondaryAttacks is not required.
- Reduced repeated HUD number formatting and station input-list construction while preserving live UI updates and the existing station cache timing.
- Icebox recipe changes no longer trigger an unrelated scene-wide storage-size refresh. Storage row changes retain the existing resize handling.
- Simplified active-food effect updates and moved Chef collection refresh state alongside its reconciliation logic without changing saved food data or selection order.
- Added Cooking tooltip layout regression checks and support for automatically deploying the merged Debug DLL with `DeployToGame=true`.

## 1.0.5

- Added default spoilage overrides: Raspberry, Mushroom, and Blueberries now use `96, keep`, retaining their original items at minimum freshness after 96 hours. Honey now uses `0`, disabling spoilage.
- Existing Spoilage.yml files are not overwritten; add these overrides manually to adopt the new defaults in an existing profile or server.

## 1.0.4

- Maximum Food Slots now accepts every value from 3 through 9. Replaced the separate six- and nine-slot scales with one Food Stat Scale, defaulting to 0.9; each food uses `3 * scale / unlocked slots`, keeping a full diet's base strength consistent throughout progression.
- Full Course now works at every unlocked slot count from 3 through 9 when all slots are filled with eligible foods. Default settings provide 90% of a comparable vanilla diet before Full Course and 108% with it.
- Replaced eaten-food name tooltips with a fixed, localized Net effect summary (종합 효과 in Korean) and a white arrow from the hovered icon. The total excludes slot scaling and vanilla time decay; a second line shows non-neutral Full Course, Chef, freshness, and diminishing multipliers. Consumed factors are saved, while Full Course updates with the current diet.
- Increased every default spoilage lifetime by 24 hours. Groups and exact overrides now accept `0, keep` to disable spoilage without removing the chosen expiry action.
- Added regression coverage for all supported slot counts, saved effect breakdowns, hover formatting, and disabled keep rules.

Existing Spoilage.yml files and assigned timers are not overwritten. Foods eaten before this update show their combined net effect only; eating them again records the detailed breakdown. The old per-slot-profile scale settings are not migrated.

## 1.0.3

- Preserved EpicMMOSystem and other mods' maximum Health, Stamina, and Eitr bonuses by routing final food totals through Valheim's standard calculation. FineDining continues scaling only each food's own stat and health-regeneration contributions.

## 1.0.2

- Added `keep` expiry actions for spoilage groups and exact prefab overrides, preserving the original prefab and stack while permanently setting expired items to minimum freshness. Feast materials and feast results now use `keep` in the default policy.
- Added localized minimum-freshness countdown and endpoint indicators for `keep` items across inventory and container slots, item tooltips, loose drops, and placed-food hover text, including distinct cold-preservation wording. Kept items persist across moves, placement, recovery, and restarts.
- Improved stack compatibility so the minimum-freshness state propagates through vanilla inventory and world stacking, InventorySlots, and AzuExtendedPlayerInventory recovery paths.
- Existing numeric lifetime and override syntax remains valid. Existing `Spoilage.yml` files are not overwritten, so upgraded servers must set `feastMaterial: 72, keep` and `feastResult: 48, keep` manually to adopt the new defaults.

## 1.0.1

- Added progression-aware Chef's Choice: higher Cooking favors higher ResourceMap tiers and stronger multipliers, while recent Health, Stamina, and Eitr food ratios influence future choices.
- Added editable `ResourceMap.yml`, generated `FoodTier.reference.yml` and `Spoilage.reference.yml`, a Chef blacklist, and admin commands for rerolling choices, clearing recent history, and printing state.
- Reworked diet progression with configurable six- or nine-slot limits, known-food slot unlocks, balanced per-slot stat scaling, and the Full Course bonus from six filled slots.
- Chef's Choice now advances when active food expires or Puke removes food; Puke removal order can be newest, oldest, or random.
- Added Cooking experience from eating, separate Cooking and Fermenter bonus-output chances, CookingStation auto-eject for overcookable foods, and bonus feedback effects.
- Added cover- and depth-based fermentation speed, accurate hover timing, and per-prefab exclusions that retain native timing, rewards, and experience.
- Improved spoilage with first-pickup timer activation, persisted clocks for already-timed stored food, water preservation for world Fish, unfermented Fermenter-input classification, safer stack merging and replacement, and revised 24-72 hour defaults.
- Migrated generated rotten foods and the Icebox to Jotunn for reliable registration across restarts; expanded Icebox recipes, storage rows, synchronized placement limits, per-Steam overrides, and owner map pins.
- Expanded station hover information and compatibility for vanilla and modded stations, AzuCraftyBoxes, ValheimCuisine, InventorySlots, and AzuExtendedPlayerInventory.
- Reorganized configuration into five sections and renamed YAML/reference files. Jotunn 2.29.2 is now required, and legacy config or YAML names are intentionally not migrated.

## 1.0.0

- Initial release.
