# Changelog

## 1.0.0

- Initial FineDining release under `sighsorry.FineDining`.
- Integrates persistent spoilage, freshness-scaled food stats, Iceboxes, expanded food slots, diminishing returns, Chef's Choice, Full Straight, Cooking production bonuses, and station hover guidance in one plugin.
- Uses one Harmony owner, one ConfigSync lock, one localization service, and one active-food multiplier pipeline.
- Adds owner-authoritative fermenter cover/depth acceleration without writing vanilla `ZDOVars.s_startTime`.
- Uses only new FineDining config, state, RPC, ZDO, localization, and prefab identifiers; no legacy migration is included.
