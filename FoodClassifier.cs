using System;
using System.Collections.Generic;
using UnityEngine;

namespace FineDining;

internal static class FoodClassifier
{
    private static readonly StringComparer PrefabComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly HashSet<string> FarmingHarvestPrefabs = new(PrefabComparer);
    private static readonly HashSet<string> CookingStationInputPrefabs = new(PrefabComparer);
    private static readonly HashSet<string> CookingStationOutputPrefabs = new(PrefabComparer);
    private static readonly HashSet<string> FermentedFoodPrefabs = new(PrefabComparer);
    private static readonly HashSet<string> FeastMaterialPrefabs = new(PrefabComparer);
    private static readonly HashSet<string> FeastResultPrefabs = new(PrefabComparer);
    private static readonly HashSet<string> FishPrefabs = new(PrefabComparer);

    private static bool _cacheReady;
    private static int _cachedObjectDbId = -1;
    private static int _cachedZNetSceneId = -1;
    private static int _cachedItemCount = -1;
    private static int _cachedNamedPrefabCount = -1;
    private static int _cachedNetPrefabCount = -1;
    private static int _cachedNonNetPrefabCount = -1;

    internal static void Invalidate()
    {
        _cacheReady = false;
        _cachedObjectDbId = -1;
        _cachedZNetSceneId = -1;
        _cachedItemCount = -1;
        _cachedNamedPrefabCount = -1;
        _cachedNetPrefabCount = -1;
        _cachedNonNetPrefabCount = -1;
        ClearClassificationSets();
        SpoilageReferenceGenerator.Invalidate();
    }

    internal static bool IsReady => EnsureCache();

    internal static bool TryClassify(ItemDrop.ItemData? item, out SpoilageGroup group)
    {
        group = SpoilageGroup.OtherEdible;
        if (item?.m_shared == null || !EnsureCache())
        {
            return false;
        }

        string prefabName = GetPrefabName(item);
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        return TrySelectGroup(
            FarmingHarvestPrefabs.Contains(prefabName),
            CookingStationInputPrefabs.Contains(prefabName),
            CookingStationOutputPrefabs.Contains(prefabName),
            FermentedFoodPrefabs.Contains(prefabName),
            FeastMaterialPrefabs.Contains(prefabName),
            FeastResultPrefabs.Contains(prefabName),
            FishPrefabs.Contains(prefabName),
            IsEdible(item),
            out group);
    }

    internal static bool TrySelectGroup(
        bool farmingHarvest,
        bool cookingStationInput,
        bool cookingStationOutput,
        bool fermentedFood,
        bool feastMaterial,
        bool feastResult,
        bool fish,
        bool edible,
        out SpoilageGroup group)
    {
        group = SpoilageGroup.OtherEdible;

        // Exact overrides are resolved by SpoilagePolicy before this method.
        // Keep automatic overlap priority explicit. Structural feast,
        // fermenter, and fish relationships are intentionally narrower than
        // direct edibility and therefore win before the broad edible fallback.
        if (farmingHarvest)
        {
            group = SpoilageGroup.FarmingHarvest;
        }
        else if (feastMaterial)
        {
            group = SpoilageGroup.FeastMaterial;
        }
        else if (feastResult)
        {
            group = SpoilageGroup.FeastResult;
        }
        else if (fermentedFood)
        {
            group = SpoilageGroup.FermentedFood;
        }
        else if (cookingStationOutput || edible && cookingStationInput)
        {
            group = SpoilageGroup.CookingStationOutput;
        }
        else if (cookingStationInput)
        {
            group = SpoilageGroup.CookingStationInput;
        }
        else if (fish)
        {
            group = SpoilageGroup.Fish;
        }
        else if (!edible)
        {
            return false;
        }

        return true;
    }

    internal static bool IsEdible(ItemDrop.ItemData? item) =>
        FoodIdentity.IsDirectlyEdible(item);

    private static bool HasDirectFoodStats(ItemDrop.ItemData.SharedData shared)
    {
        return shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f;
    }

    private static bool LooksLikeFeastRoutingFood(ItemDrop.ItemData.SharedData shared)
    {
        // Feaster-compatible routing also accepts drink-like linked results.
        // This helper never participates in runtime edible classification.
        return HasDirectFoodStats(shared) || shared.m_isDrink;
    }

    private static string GetPrefabName(ItemDrop.ItemData? item) =>
        FoodIdentity.GetCanonicalPrefabName(item);

    private static string CleanPrefabName(string? name) =>
        FoodIdentity.NormalizePrefabName(name);

    private static bool EnsureCache()
    {
        ObjectDB objectDb = ObjectDB.instance;
        ZNetScene scene = ZNetScene.instance;
        if (!IsDatabaseReady(objectDb, scene))
        {
            return false;
        }

        int objectDbId = objectDb.GetInstanceID();
        int sceneId = scene.GetInstanceID();
        int itemCount = objectDb.m_items.Count;
        int namedPrefabCount = scene.m_namedPrefabs.Count;
        int netPrefabCount = scene.m_prefabs.Count;
        int nonNetPrefabCount = scene.m_nonNetViewPrefabs.Count;

        if (_cacheReady &&
            _cachedObjectDbId == objectDbId &&
            _cachedZNetSceneId == sceneId &&
            _cachedItemCount == itemCount &&
            _cachedNamedPrefabCount == namedPrefabCount &&
            _cachedNetPrefabCount == netPrefabCount &&
            _cachedNonNetPrefabCount == nonNetPrefabCount)
        {
            return true;
        }

        bool rebuiltExistingCache = _cacheReady;
        try
        {
            BuildCache(objectDb, scene);
            _cachedObjectDbId = objectDbId;
            _cachedZNetSceneId = sceneId;
            _cachedItemCount = itemCount;
            _cachedNamedPrefabCount = namedPrefabCount;
            _cachedNetPrefabCount = netPrefabCount;
            _cachedNonNetPrefabCount = nonNetPrefabCount;
            _cacheReady = true;
            if (rebuiltExistingCache)
            {
                // Mods can append ObjectDB or scene prefabs after the first
                // successful scan. Keep both active timers and the generated
                // lookup in step with the newly rebuilt classification snapshot.
                DecayRuntime.InvalidateAll();
                SpoilageReferenceGenerator.Invalidate();
            }

            return true;
        }
        catch (Exception exception)
        {
            Invalidate();
            FineDiningPlugin.Log.LogWarning("Failed to build food classification cache: " + exception);
            return false;
        }
    }

    private static bool IsDatabaseReady(ObjectDB? objectDb, ZNetScene? scene)
    {
        return objectDb != null && objectDb.m_items != null &&
               scene != null && scene.m_namedPrefabs != null && scene.m_prefabs != null &&
               scene.m_nonNetViewPrefabs != null &&
               objectDb.m_items.Count > 0 &&
               scene.m_namedPrefabs.Count + scene.m_prefabs.Count + scene.m_nonNetViewPrefabs.Count > 0;
    }

    private static void BuildCache(ObjectDB objectDb, ZNetScene scene)
    {
        List<GameObject> scenePrefabs = CollectScenePrefabs(scene);
        HashSet<string> cultivatedRootNames = AddGrownPrefabRoots(scenePrefabs);

        HashSet<string> farmingHarvests = BuildFarmingHarvestPrefabSet(
            scenePrefabs,
            cultivatedRootNames);
        HashSet<string> cookingInputs = new(PrefabComparer);
        HashSet<string> cookingOutputs = new(PrefabComparer);
        AddCookingStationConversions(scenePrefabs, cookingInputs, cookingOutputs);
        HashSet<string> fermentedFoods = BuildFermentedFoodPrefabSet(scenePrefabs);
        BuildFeastPrefabSets(objectDb, out HashSet<string> feastMaterials, out HashSet<string> feastResults);
        HashSet<string> fishPrefabs = BuildFishPrefabSet(scenePrefabs);

        ReplaceContents(FarmingHarvestPrefabs, farmingHarvests);
        ReplaceContents(CookingStationInputPrefabs, cookingInputs);
        ReplaceContents(CookingStationOutputPrefabs, cookingOutputs);
        ReplaceContents(FermentedFoodPrefabs, fermentedFoods);
        ReplaceContents(FeastMaterialPrefabs, feastMaterials);
        ReplaceContents(FeastResultPrefabs, feastResults);
        ReplaceContents(FishPrefabs, fishPrefabs);
    }

    private static List<GameObject> CollectScenePrefabs(ZNetScene scene)
    {
        List<GameObject> prefabs = new();
        HashSet<int> seenInstanceIds = new();

        AddScenePrefabs(scene.m_namedPrefabs.Values, prefabs, seenInstanceIds);
        AddScenePrefabs(scene.m_prefabs, prefabs, seenInstanceIds);
        AddScenePrefabs(scene.m_nonNetViewPrefabs, prefabs, seenInstanceIds);
        return prefabs;
    }

    private static void AddScenePrefabs(
        IEnumerable<GameObject> source,
        ICollection<GameObject> target,
        ISet<int> seenInstanceIds)
    {
        foreach (GameObject prefab in source)
        {
            if (prefab == null)
            {
                continue;
            }

            int instanceId = prefab.GetInstanceID();
            if (seenInstanceIds.Add(instanceId))
            {
                target.Add(prefab);
            }
        }
    }

    private static HashSet<string> AddGrownPrefabRoots(List<GameObject> scanRoots)
    {
        HashSet<string> cultivatedRootNames = new(PrefabComparer);
        HashSet<int> scannedRootIds = new();
        HashSet<int> knownRootIds = new();
        foreach (GameObject root in scanRoots)
        {
            if (root != null)
            {
                knownRootIds.Add(root.GetInstanceID());
            }
        }

        for (int rootIndex = 0; rootIndex < scanRoots.Count; rootIndex++)
        {
            GameObject root = scanRoots[rootIndex];
            if (root == null || !scannedRootIds.Add(root.GetInstanceID()))
            {
                continue;
            }

            try
            {
                foreach (Plant plant in root.GetComponentsInChildren<Plant>(true))
                {
                    if (plant?.m_grownPrefabs == null)
                    {
                        continue;
                    }

                    foreach (GameObject grownPrefab in plant.m_grownPrefabs)
                    {
                        if (grownPrefab == null)
                        {
                            continue;
                        }

                        int grownId = grownPrefab.GetInstanceID();
                        string grownName = CleanPrefabName(grownPrefab.name);
                        if (grownName.Length > 0)
                        {
                            cultivatedRootNames.Add(grownName);
                        }

                        if (knownRootIds.Add(grownId))
                        {
                            scanRoots.Add(grownPrefab);
                        }
                    }
                }
            }
            catch
            {
                // Ignore only the malformed mod prefab currently being inspected.
            }
        }

        return cultivatedRootNames;
    }

    private static HashSet<string> BuildFarmingHarvestPrefabSet(
        IEnumerable<GameObject> scanRoots,
        ISet<string> cultivatedRootNames)
    {
        HashSet<string> harvested = new(PrefabComparer);
        foreach (GameObject root in scanRoots)
        {
            if (root == null)
            {
                continue;
            }

            bool cultivatedRoot = cultivatedRootNames.Contains(CleanPrefabName(root.name));
            try
            {
                foreach (Pickable pickable in root.GetComponentsInChildren<Pickable>(true))
                {
                    if (pickable == null)
                    {
                        continue;
                    }

                    AddPickableOutputs(pickable, harvested, cultivatedRoot);
                }
            }
            catch
            {
                // Ignore only the malformed mod prefab currently being inspected.
            }
        }

        return harvested;
    }

    internal static bool ShouldIncludePickableOutput(
        bool cultivatedRoot,
        bool directlyEdible,
        bool hasSeedPrefabSuffix)
    {
        return directlyEdible || cultivatedRoot && !hasSeedPrefabSuffix;
    }

    internal static bool HasSeedPrefabSuffix(string? prefabName)
    {
        string normalized = CleanPrefabName(prefabName);
        return normalized.EndsWith("Seed", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("Seeds", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectEdibleItemPrefab(GameObject? itemPrefab)
    {
        ItemDrop? itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
        return itemDrop?.m_itemData?.m_shared != null && IsEdible(itemDrop.m_itemData);
    }

    private static void AddPickableOutputs(
        Pickable pickable,
        ISet<string> harvested,
        bool cultivatedRoot)
    {
        AddPickableOutput(harvested, pickable.m_itemPrefab, cultivatedRoot);
        if (pickable.m_extraDrops?.m_drops == null)
        {
            return;
        }

        foreach (DropTable.DropData extraDrop in pickable.m_extraDrops.m_drops)
        {
            AddPickableOutput(harvested, extraDrop.m_item, cultivatedRoot);
        }
    }

    private static void AddPickableOutput(
        ISet<string> harvested,
        GameObject? itemPrefab,
        bool cultivatedRoot)
    {
        bool hasSeedPrefabSuffix = HasSeedPrefabSuffix(itemPrefab?.name);
        bool directlyEdible = IsDirectEdibleItemPrefab(itemPrefab);
        if (ShouldIncludePickableOutput(cultivatedRoot, directlyEdible, hasSeedPrefabSuffix))
        {
            AddGameObjectItemPrefab(harvested, itemPrefab);
        }
    }

    private static void AddCookingStationConversions(
        IEnumerable<GameObject> scanRoots,
        ISet<string> inputs,
        ISet<string> outputs)
    {
        HashSet<int> seenStationIds = new();
        foreach (GameObject root in scanRoots)
        {
            if (root == null)
            {
                continue;
            }

            try
            {
                foreach (CookingStation station in root.GetComponentsInChildren<CookingStation>(true))
                {
                    if (station == null || !seenStationIds.Add(station.GetInstanceID()) ||
                        station.m_conversion == null)
                    {
                        continue;
                    }

                    foreach (CookingStation.ItemConversion conversion in station.m_conversion)
                    {
                        if (conversion == null)
                        {
                            continue;
                        }

                        AddItemDropPrefab(inputs, conversion.m_from);
                        AddItemDropPrefab(outputs, conversion.m_to);
                    }
                }
            }
            catch
            {
                // A broken mod prefab must not prevent other station conversions
                // and vanilla food from being classified.
            }
        }
    }

    private static HashSet<string> BuildFermentedFoodPrefabSet(IEnumerable<GameObject> scanRoots)
    {
        HashSet<string> fermentedFoods = new(PrefabComparer);
        HashSet<int> seenFermenterIds = new();
        foreach (GameObject root in scanRoots)
        {
            if (root == null)
            {
                continue;
            }

            Fermenter[] fermenters;
            try
            {
                fermenters = root.GetComponentsInChildren<Fermenter>(true);
            }
            catch
            {
                // Isolate a malformed mod prefab from every other scan root.
                continue;
            }

            foreach (Fermenter fermenter in fermenters)
            {
                try
                {
                    if (fermenter == null || !seenFermenterIds.Add(fermenter.GetInstanceID()) ||
                        fermenter.m_conversion == null)
                    {
                        continue;
                    }

                    foreach (Fermenter.ItemConversion conversion in fermenter.m_conversion)
                    {
                        try
                        {
                            ItemDrop? output = conversion?.m_to;
                            if (output?.m_itemData?.m_shared != null && IsEdible(output.m_itemData))
                            {
                                AddItemDropPrefab(fermentedFoods, output);
                            }
                        }
                        catch
                        {
                            // One broken conversion must not hide valid outputs on the same station.
                        }
                    }
                }
                catch
                {
                    // Continue with the remaining fermenters supplied by other mods.
                }
            }
        }

        return fermentedFoods;
    }

    private static void BuildFeastPrefabSets(
        ObjectDB objectDb,
        out HashSet<string> feastMaterials,
        out HashSet<string> feastResults)
    {
        Dictionary<string, ItemDrop> itemDropsByPrefab = new(PrefabComparer);
        foreach (GameObject itemPrefab in objectDb.m_items)
        {
            try
            {
                ItemDrop? itemDrop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                string prefabName = itemDrop != null ? CleanPrefabName(itemDrop.gameObject.name) : "";
                if (prefabName.Length > 0 && !itemDropsByPrefab.ContainsKey(prefabName))
                {
                    itemDropsByPrefab.Add(prefabName, itemDrop!);
                }
            }
            catch
            {
                // Ignore only the malformed ObjectDB entry currently being inspected.
            }
        }

        feastMaterials = new HashSet<string>(PrefabComparer);
        feastResults = new HashSet<string>(PrefabComparer);
        foreach (KeyValuePair<string, ItemDrop> entry in itemDropsByPrefab)
        {
            try
            {
                ItemDrop itemDrop = entry.Value;
                ItemDrop.ItemData.SharedData? shared = itemDrop.m_itemData?.m_shared;
                Feast? feast = itemDrop.GetComponent<Feast>();
                if (shared == null || feast == null)
                {
                    continue;
                }

                ItemDrop? linkedFood = feast.m_foodItem;
                string linkedName = GetItemDropPrefabName(linkedFood);
                bool hasDifferentLinkedFood = linkedName.Length > 0 &&
                                              !linkedName.Equals(
                                                  entry.Key,
                                                  StringComparison.OrdinalIgnoreCase);
                ItemDrop.ItemData.SharedData? linkedShared = linkedFood?.m_itemData?.m_shared;
                bool linkedLooksLikeResult = linkedFood != null &&
                                             (linkedFood.GetComponent<Feast>() != null ||
                                              linkedShared != null &&
                                              (linkedShared.m_itemType ==
                                                   ItemDrop.ItemData.ItemType.Consumable ||
                                               LooksLikeFeastRoutingFood(linkedShared)));

                // Match the structural feast routing used by Feaster-compatible
                // placement. The source material is never added to the result set.
                if (shared.m_itemType == ItemDrop.ItemData.ItemType.Material)
                {
                    if (hasDifferentLinkedFood && linkedLooksLikeResult)
                    {
                        feastMaterials.Add(entry.Key);
                        feastResults.Add(linkedName);
                    }

                    continue;
                }

                // Some mods put Feast on a non-material routing prefab and keep
                // the actual consumable/placed result in m_foodItem.
                if (hasDifferentLinkedFood && linkedLooksLikeResult &&
                    shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable &&
                    !LooksLikeFeastRoutingFood(shared))
                {
                    feastMaterials.Add(entry.Key);
                    feastResults.Add(linkedName);
                    continue;
                }

                feastResults.Add(entry.Key);
            }
            catch
            {
                // One broken mod feast must not prevent other results being indexed.
            }
        }

        // appendToolTip is shared by unrelated items, so use it only for a
        // material whose target is already known to be feast-like (or carries
        // Feast itself). This never turns the source material into food.
        foreach (KeyValuePair<string, ItemDrop> entry in itemDropsByPrefab)
        {
            try
            {
                ItemDrop.ItemData.SharedData? shared = entry.Value.m_itemData?.m_shared;
                if (shared == null || shared.m_itemType != ItemDrop.ItemData.ItemType.Material)
                {
                    continue;
                }

                ItemDrop? appendToolTip = shared.m_appendToolTip;
                string tooltipName = GetItemDropPrefabName(appendToolTip);
                if (tooltipName.Length == 0 ||
                    tooltipName.Equals(entry.Key, StringComparison.OrdinalIgnoreCase) ||
                    appendToolTip == null)
                {
                    continue;
                }

                if (feastResults.Contains(tooltipName) || appendToolTip.GetComponent<Feast>() != null)
                {
                    feastMaterials.Add(entry.Key);
                    feastResults.Add(tooltipName);
                }
            }
            catch
            {
                // Isolate malformed tooltip links supplied by individual mods.
            }
        }

    }

    private static HashSet<string> BuildFishPrefabSet(IEnumerable<GameObject> scanRoots)
    {
        HashSet<string> fishPrefabs = new(PrefabComparer);
        HashSet<int> seenFishIds = new();
        foreach (GameObject root in scanRoots)
        {
            if (root == null)
            {
                continue;
            }

            try
            {
                foreach (Fish fish in root.GetComponentsInChildren<Fish>(true))
                {
                    if (fish == null || !seenFishIds.Add(fish.GetInstanceID()))
                    {
                        continue;
                    }

                    // Vanilla fish often use their own prefab as the pickup
                    // item, while mods may route a world fish to a separate
                    // inventory prefab. A mod can also put Fish below the
                    // root ItemDrop, so include all structural endpoints.
                    AddGameObjectItemPrefab(fishPrefabs, fish.gameObject);
                    AddItemDropPrefab(fishPrefabs, fish.GetComponentInParent<ItemDrop>());
                    AddGameObjectItemPrefab(fishPrefabs, fish.m_pickupItem);
                }
            }
            catch
            {
                // Isolate malformed fish prefabs supplied by individual mods.
            }
        }

        return fishPrefabs;
    }

    private static string GetItemDropPrefabName(ItemDrop? itemDrop)
    {
        return itemDrop == null ? "" : CleanPrefabName(itemDrop.gameObject.name);
    }

    private static void AddItemDropPrefab(ISet<string> target, ItemDrop? itemDrop)
    {
        string prefabName = GetItemDropPrefabName(itemDrop);
        if (!string.IsNullOrWhiteSpace(prefabName))
        {
            target.Add(prefabName);
        }
    }

    private static void AddGameObjectItemPrefab(ISet<string> target, GameObject? prefab)
    {
        if (prefab == null || prefab.GetComponent<ItemDrop>() == null)
        {
            return;
        }

        string prefabName = CleanPrefabName(prefab.name);
        if (!string.IsNullOrWhiteSpace(prefabName))
        {
            target.Add(prefabName);
        }
    }

    private static void ReplaceContents(ISet<string> target, IEnumerable<string> source)
    {
        target.Clear();
        foreach (string value in source)
        {
            target.Add(value);
        }
    }

    private static void ClearClassificationSets()
    {
        FarmingHarvestPrefabs.Clear();
        CookingStationInputPrefabs.Clear();
        CookingStationOutputPrefabs.Clear();
        FermentedFoodPrefabs.Clear();
        FeastMaterialPrefabs.Clear();
        FeastResultPrefabs.Clear();
        FishPrefabs.Clear();
    }
}
