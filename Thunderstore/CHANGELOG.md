# Changelog

## 1.1.11

- Added the server-synchronized `Auto Eject Guaranteed Cooking Level` setting, accepting 0-100 with a default of 100. Auto-eject chance scales linearly up to the chosen level: 25 or 50 guarantees ejection at that level; 0 disables it for newly inserted food.
- Preserved existing CookingStation plans, prepaid collection experience, production bonuses, and the requirement for a separate overcook stage. No saved-data format or required dependency changes.
- Updated English and Korean Cooking guidance to show the configured level or disabled state, refreshing an open tooltip when the setting changes. Added regression checks for every guarantee level from 1 through 100, probability boundaries, disabled behavior, and configured tooltip text.
- Known limitation: changing this setting while insertion RPCs are in flight can leave the inserting client and station owner using different values for the prepaid-experience decision. That transition is not atomic; live game and multiplayer validation remain outstanding.

## 1.1.10

- Fixed foods with different spoilage timers being unable to stack with Path of Valheiman's metadata checks, verified against PoV 5.3.6. Supported paths include inventory transfers, full-inventory pickup checks, drag previews, and PoV's own sorting.
- Preserved the shorter remaining lifetime, assigned freshness basis, and minimum-freshness state when merging. PoV sorting now composes food metadata alongside its direct quantity transfers, including partial stacks.
- Preserved other item metadata and existing stack restrictions. Added isolated rollback for unsupported PoV contracts, tracked PoV's loose-food merges across world levels, and completed metadata after transfers interrupted by an exception. No new settings, required dependencies, or save migrations.
- Added 46 original-Mono stack regression checks covering transfers, sorting, malformed data, nested cancellation, cleanup, and simulated InventorySlots/InventoryActions patch ordering. Actual gameplay, full mod combinations, and multiplayer save/reconnect still require in-game validation.

## 1.1.9

- Fixed Path of Valheiman 5.3.2 food card compatibility failing with an `ItemTooltip.owners` warning after PoV changed the table's value type. FineDining now uses PoV's own cached item lookup, retaining the earlier direct-table path when that lookup is unavailable.
- Preserved adjusted food values, freshness text, tooltip reuse, the one-second refresh limit and Alt-held card interactions. No configuration keys, saved-data formats or required dependencies changed; a card compatibility failure still leaves food gameplay active.
- Verified installation and item lookup with the original PoV DLL and Valheim Mono, including reused and unbound tooltips, and rechecked the existing food/cooking integration. Actual card rendering and combined multiplayer remain unverified.

## 1.1.8

- Fixed diet-state serialization omitting recent food history, consumption counts, Chef's Choice selections, and active food modifiers from character saves, which could reset records after death or reconnecting.
- Kept the existing saved-data key and v3 JSON format while preserving food order and exact float multipliers. Death still clears active food effects without clearing recent history or Chef's Choice. Records already missing from older saves cannot be recovered.
- Added original-game Mono regression checks for populated lists, new-player restoration, continued consumption counts, the death save hook, numeric precision, and malformed data, plus independent JSON validation. These are isolated checks; live dedicated-server death and reconnect testing remains outstanding.

## 1.1.7

- Fixed Path of Valheiman 5.0.10's custom item cards showing unadjusted food stats and omitting FineDining's freshness information. Cards now reuse the ordinary tooltip's food preview and spoilage text while retaining PoV's artwork and interactions; InventorySlots is not required.
- Preserved decimal food values, handled missing rows for PoV's edible raw foods, and removed regeneration rows for snacks with no healing. PoV's food multipliers and FineDining's effects are applied once without modifying the hovered item or consuming food.
- Refresh visible cards only when displayed values or text change, checking at most once per second and deferring refresh while PoV's Alt-held card is in use. A tooltip compatibility failure leaves the existing food and cooking integration active.
- Added original-Mono tooltip regression checks and rechecked the existing PoV integration against 5.0.10. Actual UI rendering and combined multiplayer still require in-game validation.

## 1.1.6

- Added optional Path of Valheiman integration, checked against 5.0.4. FineDining's slot scale, Chef's Choice, diminishing returns, Full Course and freshness now compose with PoV's food values and food-derived masteries. Item previews include PoV nourishment and cooking stamps without rolling Hearty Meal.
- Preserved PoV raw-food views after character loading and its separate snack handling. Snacks receive freshness scaling without consuming normal food slots, Chef choices or recent-food history, or awarding FineDining's normal-food experience.
- Connected FineDining's food-regeneration loop to PoV's interval, remainder and healing-packet calculations so faster ticks do not multiply total recovery by themselves.
- Routed PoV batch-cooking ingredients through the existing validated cooking-plan path while preserving item removal, RPC payloads and cheated flags. Fermenter completion now recognizes PoV's exact temporary extra bottle and runs before its queued-batch promotion, preserving existing bonus output and metadata.
- Kept PoV's skill-progression freeze, global multiplier, No Decay, Hearty Meal rolls and extra-production stacking policies. Added no required dependency, config key or save migration. Added original-DLL/isolated regression checks; combined Unity gameplay and multiplayer still require in-game validation.

## 1.1.5

- Updated bonus fermenter output to use Valheim 1.0.20's current cheated-state revision, matching vanilla handling of the fermenter's saved state.
- Preserved ingredient flags, bypass behavior, output quantities, and ownership checks. Added checks against the original client and dedicated-server game code for the changed flag calculation.

## 1.1.4

- Changed new CookingStation plans to one compact saved block per station, preserving fixed bonuses, auto-eject decisions, and prepaid collection experience. Empty plans are explicitly synchronized, and unchanged plans avoid repeated writes and decoding.
- Added bounded payload validation, ownership-aware writes, cache invalidation, and regression checks for slot reuse, empty-state receipt, malformed data, and repeated use of 28-slot and 23-slot stations.
- Changed the default Spoilage.yml overrides to Raspberry and Mushroom at `96, keep`, DeerMeat and NeckTail at `48, keep`, and Honey at `0`. Removed the Blueberries override so it follows its automatic group rule in newly created policies. Existing YAML files and assigned spoilage timers are not rewritten.

**Before updating:** finish and collect all food from every CookingStation under the old version, including modded stations and stations in unloaded areas, then update the server and all clients to 1.1.4 together. There is no reader or migration for old per-slot cooking plans: food left inside loses its previous auto-eject decision, fixed bonus, and prepaid collection-experience record. Old saved keys are left untouched, so existing save warnings can remain. Empty stations under the new version before reverting; replacing the DLL does not convert saved plans.

## 1.1.3

- Fixed non-food CookingStation conversions, including Frost Foundry armor, weapons, shields, and unfinished casts, being automatically assigned spoilage timers and RottenMeat replacements.
- Cooking inputs and outputs now require a conversion path to directly edible food. Raw food, dough, and intermediate ingredients across CookingStation/Fermenter chains remain supported, and exact prefab overrides remain authoritative.
- Added regression checks for Deep North equipment exclusions, food-processing chains, unrelated branches, conversion cycles, case-insensitive matching, and rebuilding classification after food endpoints change.

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
