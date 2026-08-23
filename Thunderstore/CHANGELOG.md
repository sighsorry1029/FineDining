# Changelog

## 1.0.0

- Initial FineDining release under `sighsorry.FineDining`.
- Integrates persistent spoilage, freshness-scaled food stats, Iceboxes, expanded food slots, diminishing returns, Chef's Choice, Full Straight, Cooking production bonuses, and station hover guidance in one plugin.
- Uses one Harmony owner, one ConfigSync lock, one localization service, and one active-food multiplier pipeline.
- Adds owner-authoritative fermenter cover/depth acceleration without writing vanilla `ZDOVars.s_startTime`.
- Uses only new FineDining config, state, RPC, ZDO, localization, and prefab identifiers; no legacy migration is included.
- Preserves an expired stack's full item count in one replacement stack, including intentional over-stacks such as `50/50 -> 50/20`.
- Keeps re-eaten foods in true newest-first history order and stores the compact Diet state as versioned JSON.
- Packages the same verified README, license, notices, manifest, and DLL across Windows and non-Windows Release builds.
