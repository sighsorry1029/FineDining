# FineDining

FineDining is a single server-synchronized Valheim mod that combines persistent food spoilage, freshness-scaled food effects, an expanded diet system, Cooking-skill production bonuses, and station hover guidance.

- Author: `sighsorry`
- Plugin GUID: `sighsorry.FineDining`
- Version: `1.0.0`
- Required on the server and every client

## Clean installation

Install BepInEx for Valheim and place `FineDining.dll` in `BepInEx/plugins` on every peer.

FineDining is a clean integration rather than a compatibility shell. Remove the standalone BeingSpoiled, GourmetsDiet, and InputHoverHints DLLs before installing it. BepInEx incompatibility declarations prevent the combined mod from loading beside those plugins.

There is intentionally no migration or legacy reader. Old config files, spoilage metadata, diet state, RPC names, generated prefabs, and placed custom pieces are not imported. Back up a world before replacing an existing setup; remove or recover old custom pieces while their originating mod is still installed.

## Spoilage and preservation

Every tracked stack has an absolute world-time deadline and an assigned lifetime. This keeps timers progressing across container unloads, unloaded zones, dedicated-server restarts, and player reconnects. Preserved items encode their remaining duration instead, so they resume without losing frozen time.

Default automatic groups:

| Group | Lifetime | Replacement |
|---|---:|---|
| Farming harvest | 100 h | `FineDining_RottenProduce` |
| CookingStation input | 75 h | `RottenMeat` |
| CookingStation output | 50 h | `RottenMeat` |
| Fermented edible output | 25 h | `FineDining_RottenFood` |
| Feast material | 125 h | `FineDining_RottenFood` |
| Placed Feast result | 125 h | `FineDining_RottenFood` |
| Fish | 75 h | `RottenMeat` |
| Other directly edible item | 25 h | `FineDining_RottenFood` |

Classification deliberately avoids a general ingredient graph. Plant-grown outputs are included even when not edible; other Pickable outputs must be directly edible; CookingStation inputs and outputs use their structural rules; remaining items must be direct consumables with positive health, stamina, or eitr. Cultivated prefab names ending in `Seed` or `Seeds` are excluded.

`FineDining.yml` supports exact overrides in one compact list:

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
overrides:
  - Dandelion, 100
  - Thistle, 100, FineDining_RottenProduce
  - SomePrefab, 0
```

Hours accept `0..5040`. Zero disables a group or exact prefab. Positive values below one second are clamped to one second internally. Invalid YAML is rejected atomically and the last valid policy remains active.

Timers appear in inventory and container slots, item tooltips, loose ItemDrop hover text, and placed food/Feast hover text. Running clocks are gold. Cold-paused clocks are blue and use the Frost icon. The display rounds up to hours and switches to minutes below one hour, with one minute as its minimum text.

Vanilla loose-item cleanup is retained. Timestamped world stacks that survive vanilla cleanup conditions can spoil in place. Stack replacement never creates multiple ground stacks from one source stack.

## Freshness and diet effects

Only a directly edible item's own remaining lifetime affects its stats. Ingredient freshness is never inherited by recipes, CookingStation output, Fermenter output, or Feast output.

Freshness scales health, stamina, eitr, and health regeneration linearly from `x1.00` to the configured minimum, `x0.75` by default. Food duration and consume status effects are unchanged. The multiplier is captured when the food is eaten.

Diet scaling is composed once in this order:

```text
(3 / configured slots) × slot constant
× (diminishing factor OR Chef's Choice multiplier)
× this item's freshness at consumption
× dynamic Full Straight multiplier
```

Defaults:

- Nine active food slots.
- Seven unique recent foods are tracked.
- The fourth and later repeat uses a one-time `x0.75` diminishing factor.
- Seven rotating Chef's Choice foods receive a persistent random `x1.00..x2.00` multiplier and ignore diminishing for that consumption.
- Filling every configured slot activates Full Straight `x1.20` dynamically.
- A full bar may replace its most depleted re-eatable food.
- Eating a Puke item while full rerolls Chef's Choice.

The combined consumption scale is stored once in `sighsorry.FineDining.DietState`, so active foods keep the exact diet and freshness effect across character save/load. Tooltip health, stamina, eitr, and regeneration values are rounded to one decimal for display without changing calculation precision.

## Cooking and fermentation

Cooking-skill production bonuses apply to Cooking recipes, CookingStation output, and Fermenter output. The synchronized percentage defaults to 100% of Valheim's normal Cooking bonus chance. Exact or `*` wildcard output-prefab exclusions are supported.

Fermenter speed can increase with cover and depth below the original terrain surface. At defaults, full cover contributes up to `x2`, eight meters of depth contributes up to `x2`, and the two multiply. FineDining stores accumulated bonus work in four owner-authoritative ZDO values and never rewrites vanilla `ZDOVars.s_startTime`; that timestamp remains the stable batch token used by the Cooking bonus RPC. The last authoritative rate also accounts for unloaded time when the fermenter is loaded again.

Station hover guidance shows:

- Available CookingStation, Smelter, Windmill, and Fermenter inputs.
- Cooking/overcooking progress for occupied cooking slots.
- Smelter, Windmill, and Fermenter remaining time.
- Fermenter cover, depth, and effective rate.
- Nearby AzuCraftyBoxes contents when its optional API is available.

Display settings are client-local. Fermenter cover/depth speed settings are synchronized gameplay settings.

## Icebox

`FineDining_Icebox` is cloned from `piece_chest`, uses the `antifreezegland` material, has 1000 health, and always preserves its contents.

- Eight fixed columns.
- Four rows by default, configurable from 4 through 20.
- Default synchronized recipe: `FineWood:10,Iron:2`.
- Recipe changes are all-or-nothing and placed boxes retain a refund snapshot.
- Optional owner-only map/minimap pins are off by default and client-local.
- Server-only per-Steam64 placement limits default to two boxes; `-1` is unlimited and `0` denies placement.

The generated `FineDining_RottenProduce` and `FineDining_RottenFood` items are restart-safe ObjectDB/ZNetScene content. They have zero food stats and cannot spoil again. Eating them applies separate five-second and ten-second Puke effects.

## Configuration

The main file is `BepInEx/config/sighsorry.FineDining.cfg`.

| Sections | Scope | Purpose |
|---|---|---|
| `00 - Server` | synchronized lock | Server configuration lock |
| `01 - Food Effects` | synchronized | Minimum freshness multiplier |
| `02 - Preservation` | synchronized | No-spoil biomes; default `Mountain, DeepNorth` |
| `03 - Icebox` | mixed | Rows, recipe, local map pins |
| `10..13 - Diet` | synchronized | Slots, diminishing, Chef's Choice, Cooking bonus |
| `20..23 - Station` | client-local | Hints, layout, nearby range, diagnostics |
| `24 - Fermentation Environment` | synchronized | Cover and depth maximum multipliers |

Additional files under `BepInEx/config/FineDining/`:

- `FineDining.yml`: authoritative spoilage policy, synchronized to clients.
- `FineDining.icebox-limits.yml`: server-only default and per-Steam64 Icebox limits.
- `FineDining.reference.yml`: generated classification documentation; never read as config.

Custom biome names in `No Spoil Biomes` are resolved through Expand World Data when that optional mod is installed.

## Compatibility and performance

- InventorySlots 1.3.6 or later: signed spoilage clocks and lifetime metadata use its merger API.
- AzuExtendedPlayerInventory: its hidden-slot merge path is pattern-validated at runtime.
- AzuCraftyBoxes: optional nearby-container station input lookup.
- Expand World Data: optional custom no-spoil biome resolution.
- Feaster-style placed foods: timer metadata is bridged between the placed ItemDrop and edible Feast item.

Spoilage reconciliation runs on a one-second schedule and scans only authoritative loaded inventories, containers, and ItemDrops; work is linear in ordinary slot counts. Unloaded progress uses timestamps rather than background object simulation. Fermenter checkpoints are bounded and owner-only, and station candidates use a short hover cache with one nearby-container query per refresh.

Dedicated servers execute spoilage, Icebox quotas, generated content registration, fermenter accumulation, and Cooking bonus validation without requiring a local HUD.
