# Changelog

## 1.0.2

- Added `keep` expiry actions for spoilage groups and exact prefab overrides, preserving the original prefab and stack while permanently marking expired items spoiled. Feast materials and feast results now use `keep` in the default policy.
- Added localized minimum-freshness countdown and endpoint indicators for `keep` items across inventory and container slots, item tooltips, loose drops, and placed-food hover text, including distinct cold-preservation wording. Kept items persist across moves, placement, recovery, and restarts.
- Improved stack compatibility so spoiled state propagates through vanilla inventory and world stacking, InventorySlots, and AzuExtendedPlayerInventory recovery paths.
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
