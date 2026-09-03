# FineDining

FineDining is a server-synchronized Valheim food overhaul focused on food storage, diet variety, and the Cooking skill.

## Showcase

### Spoilage and preservation

Food can spoil in containers, as loose drops, and when placed for display. Remaining time stays visible where the item is used.

![](https://i.ibb.co/ksxvZryc/spoilinchest.gif)

![](https://i.ibb.co/9XNdTJp/itemdropspoil.gif)

![](https://i.ibb.co/bgsRhwFQ/feasterplacedspoil.gif)

Expired food normally becomes its configured rotten result. A group rule or exact override can instead keep the original prefab at minimum freshness. Cold biomes and the Icebox pause active spoilage timers.

![](https://i.ibb.co/fzY9VG7S/rottenfood.png)

![](https://i.ibb.co/kVKHbGV3/feastcanspoil.png)

![](https://i.ibb.co/YTh1R93Z/feastnospoilatmountain.png)

![](https://i.ibb.co/C34FSch2/foodinmountains.png)

![](https://i.ibb.co/rR4g5J1D/foodinicebox.png)

### Diet, Chef's Choice, and Full Course

Recent meals influence Chef's Choice. Natural food expiry or a food removed by Puke advances the list.

![](https://i.ibb.co/ksDTH1SS/recenthistory.png)

![](https://i.ibb.co/yF5f6v6b/chefchoice.png)

![](https://i.ibb.co/rRx97QGV/chefchoicereroll.gif)

Tooltips show Chef, diminishing, and stale multipliers. Filling every unlocked food slot activates Full Course.

![](https://i.ibb.co/qLG1Dz2r/chefchoiceandstailinitemtooltip.png)

![](https://i.ibb.co/ZRvgVVrw/dminishandstale.png)

![](https://i.ibb.co/LDwvdf4m/fullcourse.png)

At higher Cooking levels, the most likely Chef multiplier moves toward the configured peak.

![](https://i.ibb.co/wFPLCLs5/chefmultiplierpeak.png)

### Cooking and station hints

Cooking skill can auto-eject finished food, while hover hints expose valid inputs and processing state for vanilla and modded stations.

![](https://i.ibb.co/HLhn4dBv/autoeject.gif)

![](https://i.ibb.co/60D56PXj/cookingstationhover.png)

![](https://i.ibb.co/67vCs4tN/moddedcookingstationhover.png)

Cover and depth speed fermentation. Compatible custom Fermenters receive the same clear hover information.

![](https://i.ibb.co/KxyHz6HD/fermenterhover.png)

![](https://i.ibb.co/pryCYT85/fermentingdeeperandclosed.png)

![](https://i.ibb.co/KcQGq5Cp/moddedfermenterhover.png)

Production buildings and supported ValheimCuisine stations expose compact hover information.

![](https://i.ibb.co/ycrv35Zt/windmillhover.png)

![](https://i.ibb.co/PssKNRwh/smelterhover.png)

![](https://i.ibb.co/hRhPr8vX/smelterhover2.png)

![](https://i.ibb.co/rfm7KMxB/freydicollectorhover.png)

![](https://i.ibb.co/4H314Dq/grimphyboxhover.png)

## Features

- Configurable spoilage, freshness-scaled food stats, and an Icebox.
- Three- to nine-slot diets with diminishing returns, Chef's Choice, and Full Course.
- Cooking experience, production bonuses, auto-eject, faster fermentation, and station hints.

## Spoilage

Ordinary food starts its spoilage clock when it first enters a player inventory. Untouched food in location loot and ordinary containers remains fresh until that first pickup. Placed food starts from its placement time.

An active clock uses Valheim's network world time. It continues in ordinary containers and unloaded zones while that clock is running, and it survives restarts without resetting. Time does not advance while the server is offline, while an empty dedicated server pauses world time, or while the item is preserved.

Spoilage pauses in:

- An Icebox.
- Biomes listed in `No-Spoil Biomes`; the default is `Mountain, DeepNorth`.
- Water, for world items classified as Fish.

The default `Spoilage.yml` policy is:

```yaml
version: 1
lifetimes:
  farmingHarvest: 96
  cookingStationInput: 48
  cookingStationOutput: 72
  unfermentedFood: 72
  fermentedFood: 96
  feastMaterial: 96, keep
  feastResult: 72, keep
  fish: 48
  otherEdible: 48
chefChoiceBlacklist: []
overrides: []
```

Group lifetime values use `<hours>[, keep]`. Valid hours are `0..720`; a number alone uses the group's fixed rotten replacement, while `keep` preserves the original prefab and stack and permanently moves it to minimum freshness. Both `0` and `0, keep` disable spoilage; the latter retains the action for when you enable it again. Positive fractions are supported, with one second as the minimum internal lifetime. By default, `feastMaterial` and `feastResult` use `keep`. `unfermentedFood` covers Fermenter inputs whose conversion path eventually reaches directly edible food, including paths that continue through a CookingStation.

Existing `Spoilage.yml` files are not overwritten. Changed lifetimes apply to newly assigned timers; existing timers keep their saved lifetime and expiry.

Exact prefab rules use this compact format:

```yaml
overrides:
  - ModdedFood, 100
  - ModdedProduce, 72, FineDining_RottenProduce
  - PreservedIdentityFood, 72, keep
  - DecorativeFood, 0
  - DisabledPreservedFood, 0, keep
```

The optional third value is either a replacement prefab or the reserved, case-insensitive keyword `keep`. With a positive lifetime and `keep`, the timer runs normally, but expiry preserves the original prefab and stack and records a permanent minimum-freshness state instead of creating a replacement. The item stays at the configured minimum freshness effect, displays `Minimum freshness reached` (`Min` in slots), and does not receive another timer after moves or restarts. Because its prefab identity is unchanged, other systems still recognize it as the original item. A `0` lifetime, with or without `keep`, disables spoilage without applying this state. Disabling spoilage does not restore items that already reached permanent minimum freshness. A zero-hour override cannot specify a replacement prefab.

When the third override value is omitted, a classified item inherits its group's action while an unclassified force-include uses `RottenMeat`. By default, farming harvests become `FineDining_RottenProduce`; CookingStation inputs, CookingStation outputs, and Fish become `RottenMeat`; unfermented food, fermented food, and other edible food become `FineDining_RottenFood`; feast materials and feast results keep their original prefab.

`FineDining_RottenProduce` and `FineDining_RottenFood` cannot spoil again. Eating them applies Puke for five and ten seconds respectively.

Timers appear in inventory and container slots, item tooltips, loose ItemDrop hover text, and placed-food hover text. Merging timed stacks keeps the lower remaining lifetime; merging with a minimum-freshness `keep` stack applies that state to the resulting stack. Vanilla loose-item cleanup remains active.

### Freshness

An edible item's own freshness scales its health, stamina, eitr, and health regeneration from `x1.00` to the configured minimum, `x0.75` by default. The scale is captured when the food is eaten.

Ingredient freshness is not inherited by recipes, CookingStation output, Fermenter output, or feast output. Food duration and consume status effects are unchanged.

## Diet and Chef's Choice

`Maximum Food Slots` accepts any integer from `3` through `9` and defaults to `9`. Slots unlock as the player learns directly edible Health/Stamina/Eitr foods, stopping at the configured maximum:

```text
Known foods  0-6  7-9  10-12  13-15  16-18  19-21  22+
Food slots     3    4      5      6      7      8      9
```

Newly earned slots take effect when an active food is removed or replaced, or while no food is active.

`Food Stat Scale` defaults to `0.9` and controls the total base strength of a full unlocked diet relative to three equivalent vanilla foods. Each food's Health, Stamina, Eitr, and health regeneration uses `3 * Food Stat Scale / currently unlocked slots` before other effects. The default per-food multipliers are `0.9`, `0.675`, `0.54`, `0.45`, about `0.385714`, `0.3375`, and `0.3` for three through nine unlocked slots. Empty slots do not increase the remaining foods' multipliers. Player base stats and external player-stat bonuses are unchanged.

- The recent-food history tracks seven unique foods by default.
- The fourth and later consumption receives the default `x0.75` diminishing multiplier.
- Chef's Choice contains seven foods with multipliers between `x1.10` and `x1.50` by default.
- Higher Cooking skill favors higher food tiers and stronger Chef multipliers.
- Recent Health/Stamina/Eitr proportions influence new Chef choices by `70%` at the default setting.
- A naturally expired food or a food actually removed by Puke advances Chef's Choice once from the oldest entry.
- Full Course works at every stage from three through nine unlocked slots and defaults to `x1.20` while every unlocked slot is filled with directly edible food. With the default Food Stat Scale, this gives `0.9 * 1.2 = 1.08` of three equivalent vanilla foods before other effects. Even with a nine-slot maximum, filling the first three unlocked slots activates Full Course.

Hover an eaten food to see its name and **Extra effect ×N** above the recent-food row, connected by a white arrow. This replaces the cursor-following name tooltip. The total excludes the base slot scale and vanilla time-based stat decay. The second line lists non-neutral Full Course, Chef, freshness, and diminishing multipliers; when none apply, only **Extra effect ×1.00** is shown beside the food name. Chef, freshness, and diminishing values are saved when eating, while Full Course reflects the current diet. Foods already active without a saved breakdown show only the combined multiplier until eaten again.

Chef's Choice only uses consumables with positive health, stamina, or eitr. Exact exclusions belong in `chefChoiceBlacklist` in `Spoilage.yml`.

`ResourceMap.yml` defines Chef food tiers from lowest to highest. The server or single-player host creates, hot-reloads, and synchronizes it. Connected clients ignore their local copy, and invalid edits keep the last valid map.

## Cooking and stations

Successfully eating a directly edible Health/Stamina/Eitr food grants `0.15` Cooking experience by default. Setting `Cooking Experience per Food Eaten` to `0` disables it.

At Cooking level 100:

- Normal recipes and CookingStation outputs have a default `25%` bonus-item chance, configurable from `0%` to `25%`.
- Fermenter outputs use a separate default of `20%`, also configurable from `0%` to `25%`.
- Lower Cooking levels scale both chances linearly.

`Production Bonus Excluded Output Prefabs` disables the bonus for matching output items across recipes, CookingStations, and Fermenters.

CookingStation auto-eject is available only when the conversion has a separate burnt or coal stage. Its chance equals the inserting player's Cooking level, and the result is decided when the item is inserted.

Fermenters can run faster with cover and depth. Full cover and eight meters of depth each provide up to `x2` speed by default, and the two multipliers combine. Prefabs listed in `Fermenter Bonus Excluded Prefabs` keep native timing and output; FineDining acceleration, output bonuses, and insertion/collection Cooking experience are disabled for them.

Hover hints show available inputs and processing time for CookingStations, Smelters, Windmills, and Fermenters. Icon scale and rows are client-side settings. Optional AzuCraftyBoxes integration includes nearby container contents, and optional ValheimCuisine integration adds Grimpy Box and Freydis information.

## Icebox

`FineDining_Icebox` is found in the Hammer's Misc tab and always pauses spoilage inside it.

- Size: eight columns and four rows by default; rows are configurable from `4` to `20`.
- Recipe: `TrophySGolem:1,Obsidian:8,Crystal:16,Silver:32`.
- Map and minimap pins: enabled by default for Iceboxes owned by the local account.
- Placement limit: two per Steam account by default, configured through the server-synchronized `Icebox Default Placement Limit` setting.

Use `-1` for unlimited placement, `0` to deny placement, or a positive number for a maximum count. Exact Steam64 overrides are configured in the server-only `Icebox.yml` and take priority over the synchronized default.

## Configuration

The main configuration file is `BepInEx/config/sighsorry.FineDining.cfg`.

Server configuration locking is enabled by default. Config descriptions identify every entry as server-synchronized or client-only.

| Section | Scope | Contents |
|---|---|---|
| `1 - General` | Server-synchronized | Configuration lock, Cooking bonuses, and fermentation |
| `2 - Client` | Client-only | Station icon scale and row counts |
| `3 - Diet` | Server-synchronized | Food slots, stat scales, history, and diminishing returns |
| `4 - Chef Choice` | Server-synchronized | Choice count, multipliers, tier weighting, and food-type preference |
| `5 - Spoilage` | Mixed | Preservation, stale-food minimum, Icebox default limit, recipe, size, and local pins |

Additional files are created under `BepInEx/config/FineDining/`:

| File | Purpose |
|---|---|
| `Spoilage.yml` | Server-authoritative lifetimes, expiry results, overrides, and Chef blacklist |
| `ResourceMap.yml` | Server-authoritative Chef food tiers |
| `Icebox.yml` | Server-only per-Steam64 Icebox limit overrides |
| `Spoilage.reference.yml` | Generated spoilage classification reference; not configuration |
| `FoodTier.reference.yml` | Generated Chef tier and food-type reference; not configuration |

Invalid YAML keeps the last valid policy. Files from older names are not read or migrated; the former `Icebox.yml` `defaultLimit` field is unsupported.

### Admin commands

| Command | Action |
|---|---|
| `fd:rerollchef` | Reroll the local player's Chef's Choice list |
| `fd:clearrecent` | Clear the local player's recent-food history |
| `fd:printstate` | Print the local player's FineDining diet state |

## Compatibility

- InventorySlots `1.3.7` or newer: spoilage-aware stack merging.
- AzuExtendedPlayerInventory: hidden-slot merge compatibility.
- AzuCraftyBoxes: nearby-container station hints using its configured range.
- ValheimCuisine: Grimpy Box and Freydis hover information.
- Expand World Data: custom biome names in `No-Spoil Biomes`.
- Feaster-style placed food: spoilage metadata and hover information.

FineDining performs spoilage work only for authoritative loaded inventories, containers, and world items. Unloaded progress is timestamp-based rather than continuously simulated, and dedicated servers do not require a local HUD.
