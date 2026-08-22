# FineDining

FineDining adds persistent, server-authoritative spoilage timers, eight structural food groups, and an Icebox whose contents stay preserved. Timers are stored on each stack, shown in the upper-left of inventory and container slots, and preserved through Valheim's normal character, container, and world serialization.

Author: **sighsorry**
Plugin GUID: `sighsorry.FineDining`

## Features

- Eight automatic groups cover cultivated harvests, CookingStation conversions, Fermenter outputs, Feast materials/results, fish, and other directly edible items.
- Group replacements are fixed in code: produce becomes `FineDining_RottenProduce`; prepared, fermented, and Feast food becomes `FineDining_RottenFood`; CookingStation inputs/outputs and fish become `RottenMeat`. The two generated rotten items are zero-stat consumables that apply five- and ten-second variants of Valheim's Puke effect.
- Exact prefab overrides can force an item into spoilage, change its lifetime or replacement, or disable spoilage with `0` hours.
- The generated `FineDining_Icebox` is an 8x4-by-default, 1000-health chest whose contents always pause. Its server-synchronized row count and recipe are configurable, and it uses the `antifreezegland` material.
- Configured no-spoil biomes pause player inventories, ordinary containers, and timestamped world ItemDrops, including loose stacks and placed food Pieces. The synchronized default is `Mountain, DeepNorth`, with optional Expand World Data biome support.
- Remaining time is always shown. Slot values use rounded-up hours, then rounded-up minutes below one hour, with `1m` as the smallest visible value; paused values are blue and carry Valheim's Frost icon.
- A directly edible item's health, stamina, eitr, and health-regeneration values decrease linearly from `x1.00` to a server-configurable minimum (`x0.75` by default) as that item's own spoilage clock runs down. Its tooltip shows the adjusted vanilla values rounded to at most one decimal and the freshness multiplier.
- Every holder uses the same signed world clock, so running and cold-preserved stacks retain the shorter effective remaining time through placement, recovery, and merges.
- Placing food with systems such as Feaster transfers the earliest deadline from the stack(s) Valheim will actually consume. Every timestamped world ItemDrop, loose or placed, shows its remaining or cold-paused time on hover; directly edible food also shows its freshness multiplier. Timerless natural drops remain unchanged. Removing a Piece transfers its deadline back to recovered food.
- Ingredient freshness is never inherited by a crafted result. General Recipes, CookingStation conversions, and Fermenter conversions therefore begin with the result item's own fresh lifetime when FineDining first timestamps that result.
- Unloaded and restarted warm objects catch up when loaded; preserved Iceboxes and objects in no-spoil biomes retain a frozen remaining duration without background scanning.
- Icebox placement is limited by a server-only SteamID policy. Optional client map pins show only Iceboxes owned by the current account and default to off.
- Loose food changes into its spoiled prefab at the spoilage deadline without restarting Valheim's original one-hour ground cleanup age. Once that threshold has passed, vanilla deletion is attempted before replacement.
- YAML gameplay rules, the minimum food-effect multiplier, and the no-spoil biome config are synchronized from the server. FineDining is required on the server and every client.

## Configuration

Spoilage lifetimes and exact prefab overrides are read from:

```text
BepInEx/config/FineDining/FineDining.yml
```

The file is created with these defaults when it does not exist:

```yaml
version: 1
lifetimes:
  farmingHarvest: 100
  cookingStationInput: 75
  cookingStationOutput: 50
  fermentedFood: 25
  feastMaterial: 125
  feastResult: 125
  fish: 75
  otherEdible: 25
overrides: []
```

All lifetime values are world-active hours in the range `0..5040`. Zero disables that group. A positive value shorter than one second is clamped to one second; for example, `0.01` hours is 36 seconds. There is no `defaults` or `spoiledPrefabs` YAML field: automatic group replacements are fixed as follows.

| Group | Default hours | Fixed replacement |
|---|---:|---|
| `farmingHarvest` | 100 | `FineDining_RottenProduce` |
| `cookingStationInput` | 75 | `RottenMeat` |
| `cookingStationOutput` | 50 | `RottenMeat` |
| `fermentedFood` | 25 | `FineDining_RottenFood` |
| `feastMaterial` | 125 | `FineDining_RottenFood` |
| `feastResult` | 125 | `FineDining_RottenFood` |
| `fish` | 75 | `RottenMeat` |
| `otherEdible` | 25 | `FineDining_RottenFood` |

The compact override format is:

```yaml
overrides:
  - RawMeat, 100, RottenMeat
  - DeerMeat, 0
  - CustomFood, 0.01, CustomRottenFood
```

- Field 1 is an exact item prefab name, compared case-insensitively.
- Field 2 is `0..5040` hours. A positive value force-includes the prefab; `0` disables it and clears an existing timer when an authoritative peer next processes the stack.
- Field 3 is optional. When omitted, an automatically classified item uses its fixed group replacement; an otherwise unclassified force-included item uses `RottenMeat`.
- Duplicate prefab overrides and invalid values reject the entire reload, leaving the last-known-good rules active.

The food-stat floor is a server-synchronized BepInEx setting:

```ini
[01 - Food Effects]
Minimum Food Effect Multiplier = 0.75
```

Its range is `0..1`. It controls the multiplier at zero freshness; the remaining linear contribution is always calculated as `1 - minimum`, so fully fresh food remains `x1.00`. A value of `1` disables freshness-based stat reduction without disabling spoilage.

`Expire Action`, `Food Scope`, and `Show Timer` are not configurable. They are fixed to replacement, direct-scope classification, and enabled timer display. Legacy `.cfg` settings and old lifetime keys are not read or migrated.

Only the server or single-player world reads the local YAML. Multiplayer clients ignore their local file and wait for the synchronized server rules before creating, removing, or expiring timers. File reloads are validated and committed atomically.

Changing a positive lifetime affects newly timestamped stacks. Existing stacks keep their running deadline or paused remaining duration. Disabling a group or prefab removes its existing clock when the authoritative player, container owner, or ground-item owner processes it.

### No-spoil biomes

The synchronized BepInEx setting is stored in `BepInEx/config/sighsorry.FineDining.cfg`:

```ini
[00 - Server]
Lock Configuration = true

[02 - Preservation]
No Spoil Biomes = Mountain, DeepNorth
```

`Lock Configuration` defaults to `true`, making synchronized gameplay settings server-authoritative while retaining ServerSync's administrator exemption. `No Spoil Biomes` accepts comma- or semicolon-separated, case-insensitive biome identifiers. For an Expand World Data custom biome, enter the canonical value of that biome's `biome:` field, not its display name. Unknown names stay configured and begin matching if their content mod loads later.

### Icebox and placement limits

FineDining owns three stable generated prefab IDs and registers them throughout the game-data lifecycle:

- `FineDining_Icebox`: cloned from `piece_chest`; 8 columns by 4 rows by default, 1000 health, and `antifreezegland` visuals.
- `FineDining_RottenProduce`: produce replacement cloned from `Resin`, with `LoxMeatRotten` visuals; eating one applies `FineDining_PukeRottenProduce` for 5 seconds.
- `FineDining_RottenFood`: prepared-food replacement cloned from `BreadDough`, with `LoxMeatRotten` visuals; eating one applies `FineDining_PukeRottenFood` for 10 seconds.

Icebox storage and construction use synchronized BepInEx settings:

```ini
[03 - Icebox]
Storage Rows = 4
Recipe = FineWood:10,Iron:2
```

`Storage Rows` accepts `4..20`; columns are always fixed at 8. If the setting is lowered while a higher row still contains an item, that occupied row remains visible and usable until it is cleared, preventing hidden slots or item loss. `Recipe` uses comma-separated, case-sensitive `ItemPrefab:Amount` entries. Amounts must be positive integers. If any entry is malformed or unresolved, the Icebox is removed from the Hammer menu on every peer until the whole recipe becomes valid, so a partial recipe is never applied.

Each newly placed Icebox stores its exact construction recipe in its ZDO. Later recipe changes therefore affect new construction only, while dismantling uses the materials recorded for that Icebox. A placement-limit rejection refunds that snapshot only when it exactly matches the server's current canonical recipe; a missing, invalid, or mismatched snapshot is not refunded. There is intentionally no migration rule for Iceboxes that do not contain this snapshot.

Each generated rotten item is cloned and registered independently in the `ObjectDB` list/hash/data indexes and the `ZNetScene` list/name index. Its Puke clone is registered by a distinct stable hash in `ObjectDB.m_StatusEffects` before the item is considered ready. FineDining registers synchronously during `ObjectDB` and `ZNetScene` lifecycle callbacks, retries once after later postfixes have populated their content, and performs a final `Game.Start` checkpoint on clients and dedicated servers. A temporarily unavailable generated replacement or status effect postpones expiry instead of deleting the original stack, then retries when the registries are ready.

Both rotten items remain `Consumable` but have zero health, stamina, eitr, regeneration, and duration stats. They are therefore replacement terminals rather than direct food, receive no freshness multiplier or food-slot fork, and cannot spoil again. Their effects remain real `SE_Puke` clones, preserving Valheim's one-food-per-second removal, icon, visual effects, and other Puke penalties; a shared private category prevents the 5- and 10-second variants from stacking with each other. Mods that intentionally recognize `SE_Puke`, including GourmetsDiet's full-slot reroll behavior, continue to recognize them.

An Icebox pauses every timed stack in its own inventory regardless of biome. Removing a stack resumes it from the preserved remaining duration unless its new holder is also preserved. The prefab IDs remain registered even when a lifetime or feature is disabled, and are restored during `ObjectDB`, `ZNetScene`, and in-process world reload lifecycles so saved Iceboxes and rotten items are not discarded as missing prefabs.

The authoritative server creates and hot-reloads:

```text
BepInEx/config/FineDining/FineDining.icebox-limits.yml
```

```yaml
defaultLimit: 2
overrides:
  "76561198000000000": 6
  "76561198000000001": 0
  "76561198000000002": -1
```

Steam64 IDs must be quoted. `0` denies new placement, `-1` is unlimited, and a positive value is the maximum number owned by that account. The file is server-only and is never synchronized because it contains account IDs. Existing boxes are grandfathered: lowering a limit blocks later placement but never deletes existing world data. A rejected new placement is removed authoritatively and a valid server-matching construction snapshot is refunded once.

Refund eligibility also uses the placing client's local no-cost state because Valheim consumes construction resources client-side. The server independently validates the RPC sender, Icebox ZDO, creator, and global no-build-cost state, but cannot prove client inventory consumption; this follows Valheim's normal honest-client trust boundary. Placement quota enforcement and rejected-piece removal remain server-authoritative.

`Show Icebox Map Pins` is a client-only option under `[03 - Icebox]` and defaults to `false`. When enabled, the world map and minimap show only the current Steam account's Iceboxes, including indexed boxes in unloaded zones.

## Generated reference

After ObjectDB is ready, the source-of-truth server or single-player world automatically generates:

```text
BepInEx/config/FineDining/FineDining.reference.yml
```

This lookup lists every classified prefab with one of the eight runtime groups, configured lifetime, and effective replacement prefab. Entries are grouped by spoilage classification first and owning mod second; owner resolution prefers Jotunn `SourceMod` metadata, then Valheim's vanilla manifest, loaded asset-bundle inference, and finally `Unknown / Untracked`. Copy a row into `FineDining.yml` under `overrides` to customize or disable that prefab. Fixed replacements appear for reference but are not YAML settings.

The reference file is generated documentation only: FineDining never reads it as configuration or synchronizes it to clients. It is rewritten only when the generated content changes, and is recreated if it is deleted while the authoritative world remains active. Entries whose owner is initially unknown receive a small, bounded set of delayed resolution retries for late-loading mods.

## Automatic groups

Rule priority is: exact override, `farmingHarvest`, `feastMaterial`, `feastResult`, `fermentedFood`, `cookingStationOutput`, `cookingStationInput`, `fish`, `otherEdible`, then excluded. A directly edible CookingStation input uses the output group, while an eligible `farmingHarvest` output wins every automatic overlap.

- `farmingHarvest`: each main and extra Pickable output is evaluated independently. Outputs beneath a prefab referenced by `Plant.m_grownPrefabs` are included even when non-edible, except non-edible outputs whose prefab name ends in `Seed` or `Seeds` (case-insensitive). Directly edible outputs always remain included. An output of any other Pickable is included only when that individual output is directly edible.
- `cookingStationInput`: non-edible `CookingStation.m_conversion[*].m_from` items that do not match a higher group.
- `cookingStationOutput`: every `CookingStation.m_conversion[*].m_to`, plus directly edible CookingStation inputs.
- `fermentedFood`: directly edible `Fermenter.m_conversion[*].m_to` outputs.
- `feastMaterial`: material/routing items structurally linked by a `Feast` component or confirmed Feast tooltip link to a distinct Feast result.
- `feastResult`: the placed/edible Feast endpoints discovered from the same explicit links.
- `fish`: inventory endpoints structurally associated with a `Fish` component, excluding items already claimed by a higher group.
- `otherEdible`: remaining `Consumable` items that directly provide health, stamina, or eitr food stats.

Being an ingredient of edible food is not sufficient. General Recipe relationships, Fermenter inputs, and `PickableItem` are not followed. The cultivated-seed exclusion is deliberately a suffix rule rather than a substring rule, so names such as `SeedOil` are unaffected; a directly edible item also wins over the exclusion. `m_harvestable` or `m_pickRaiseSkill` alone does not admit a non-edible wild Pickable output; use a positive exact override for such ingredients. A generic `m_appendToolTip` link alone never makes an item spoilable; Feast links are admitted only when the target is already structurally Feast-like. CookingStation fuel and overcooked waste are likewise outside `m_conversion` and are not included automatically.

FineDining uses `Plant.m_grownPrefabs` as its cultivated-harvest signal and does not read Groundwork's `pickables.yml` overrides. Use positive exact overrides for non-edible wild ingredients such as `Dandelion`, `Thistle`, and comparable modded ingredients.

## Freshness effects

Only an edible item's own timer affects that item's food stats. FineDining does not inspect Recipe ingredients or carry a material-quality lineage into crafted, cooked, fermented, or placed results. For configured minimum `m` and current remaining-time ratio `r`, both clamped to `0..1`, the multiplier is:

```text
m + (1 - m) * r
```

With the default `m = 0.75`, this produces `x1.00` when fresh, `x0.875` at half of the item's assigned lifetime, and approaches `x0.75` at expiry. Health, stamina, eitr, and health regeneration use the same multiplier; duration and arbitrary consume status effects are unchanged. Cold preservation freezes both the spoilage timer and the resulting stat multiplier. Changing the setting affects subsequent tooltip and eating calculations; food that is already active keeps the multiplier captured when it was eaten.

FineDining never mutates an item prefab's shared stats. Tooltips and consumed-food state use per-item snapshots, so another stack of the same prefab and other mods' base values are not changed. Tooltip snapshots alone round health, stamina, eitr, and health regeneration to at most one decimal; a positive value that would disappear at `0.0` uses a display-only `0.1` floor. Consumed-food calculations retain full precision. Active food multipliers are preserved through character save/load. With GourmetsDiet installed, the effective value is `current base stat × this item's freshness multiplier × GourmetsDiet multiplier`; the two mods coordinate so freshness is applied once.

Placed Feast implementations often store the spoilage clock on their host `ItemDrop` and the edible stats on `Feast.m_foodItem`. FineDining bridges those two representations for hover text and eating only; this is the placed food's own clock, not ingredient-freshness inheritance.

## Time and persistence

Spoilage and its original lifetime basis are stored in two item-custom-data values under:

```text
sighsorry.FineDining.ExpiryWorldTicks
sighsorry.FineDining.AssignedLifetimeTicks
```

A positive value is a running absolute `ZNet.GetTime()` deadline. A negative value is the remaining duration frozen by an Icebox or configured no-spoil biome. Entering preservation converts a still-live deadline once; leaving converts the frozen duration back to a deadline. An already-due positive clock spoils before it can be frozen, and unchanged paused items are not rewritten each second.

`AssignedLifetimeTicks` records the positive lifetime that was assigned when the stack first received its timer. This keeps its freshness ratio stable if YAML lifetimes are reloaded later. Both values follow Valheim's item custom-data serialization through inventories, containers, world drops, and restarts; disabling spoilage or replacing an expired item removes the basis together with the clock.

The holder's actual world position is classified with `Heightmap` and a `WorldGenerator` fallback, so the same rule works on a dedicated server without consulting a local player's status effects. Fire, shelter, equipment, Frost Resistance, and the visual Cold/Frost effects do not alter preservation. Character saves serialize preserved inventory clocks as running deadlines and restore the live online copy afterward, so logging out in a no-spoil biome does not create indefinite preservation.

Valheim's server world clock advances while at least one player is present and pauses when the server is empty or stopped. Server restarts and zone unloads preserve either signed state, but real-world time while the server is inactive does not count.

Only the authoritative peer mutates an inventory, container, ground drop, or placed food Piece. On a dedicated server this can be the server or the client currently owning the ZDO; `IsOwner()` gating prevents duplicate replacement. The placing client records the consumed stack deadline because Valheim keeps player inventories client-authoritative, then the ItemDrop ZDO synchronizes it through the server.

Timestamped ground stacks expire as a whole. One expired source stack produces at most one replacement stack. When the replacement has a smaller maximum stack size, the amount preserves the source stack's occupied fraction and rounds up (`50/50 -> 20/20`, `25/50 -> 10/20`, and every positive stack produces at least one item). A replacement with equal or greater capacity preserves the original count without increasing it. Their hover text shows the same running or cold-paused clock without writing world state. Fresh loose world drops without a FineDining timestamp remain untouched and receive no spoilage hover line until they enter an authoritative inventory. A placed ItemDrop Piece is initialized when its owner observes it, including existing timerless Pieces loaded after installing the mod.

Preserving vanilla cleanup age assumes the configured spoiled prefab also uses `ItemDrop.m_autoDestroy`, as the default `RottenMeat` does. A custom replacement that disables vanilla auto-destruction is retained according to that prefab's own behavior and produces a one-time warning.

Unloaded zones contain no live ItemDrop GameObjects, even on a dedicated server. A running absolute deadline catches up and replacement occurs on the first owner tick after the zone loads (normally within about one second); a preserved negative duration remains frozen through unload and restart. Icebox inventories and their owner metadata live in ZDO data and are re-indexed after world load, so quota and own-map-pin state include unloaded boxes without continuously loading zones.

## Inventory mod compatibility

FineDining works without InventorySlots. When `sighsorry.InventorySlots` is installed, use version `1.3.6` or newer. FineDining registers mergers for its signed clock and assigned-lifetime basis through InventorySlots' metadata API, while InventorySlots also carries a load-order-safe fallback. Stack merges retain the earlier deadline and a conservative lifetime basis, so an older item cannot gain food stats by joining a fresher stack. Its fork icon follows Valheim's direct-food rule.

`AzuExtendedPlayerInventory` is an optional soft dependency. FineDining recognizes its hidden/out-of-bounds slot recovery path and preserves the earlier timer when that path directly merges stacks without calling Valheim's normal `Inventory.AddItem` methods. The compatibility hook is pattern-validated and disables only itself if a future AzuEPI version changes that merge implementation. `ExtraSlots` continues to use the player's main inventory and normal merge paths, so it needs no separate FineDining patch.

## Performance

Pickable, Plant, CookingStation, Fermenter, Feast, and Fish structures are scanned only when ObjectDB/ZNetScene content changes. Recipe graphs, ingredient consumption, and general ingredient closure are not traversed. Freshness arithmetic runs only while rendering a relevant tooltip, eating food, or composing a stack; it adds no inventory-wide per-frame scan. Owner manifests and loaded bundles are inspected only while producing the reference, plus a bounded retry when an owner is initially unresolved; they are not part of timer processing. Normal item classification is a few case-insensitive set lookups. Holder preservation is sampled once per second, but an inventory is serialized only when it changes, reaches a deadline, or crosses a preservation boundary; paused values are not rewritten continuously. Icebox quotas are indexed once from saved ZDOs and normally updated by placement/removal events; a low-frequency reconciliation scan is advanced incrementally across frames to catch missed notices without loading zones or causing a full-world scan hitch. Map-pin clients receive only a position-free invalidation signal and then request their own authenticated snapshot. Unloaded containers, ground items, and Pieces consume no update time.

## Cross-world characters

Valheim's network time is persisted per world. Moving a character carrying timed food between worlds with different world clocks can shift the apparent remaining time. Unloads and restarts within the same world are supported.

## Installation

Install BepInEx for Valheim, then place `FineDining.dll` in `BepInEx/plugins` on the dedicated server and every client. FineDining is a required mod and all peers must run the same `1.0.0` version because it registers networked Icebox and replacement prefabs. Expand World Data is optional; if custom biome identifiers are configured, its biome definition must resolve consistently on every peer that can own spoilage objects.
