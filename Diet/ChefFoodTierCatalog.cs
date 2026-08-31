using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FineDining;

internal sealed class ChefFoodTierInfo
{
    internal ChefFoodTierInfo(
        string prefabName,
        int tier,
        int tierCount,
        string tierName,
        string fallbackReason,
        IEnumerable<string>? sources)
    {
        PrefabName = FoodIdentity.NormalizePrefabName(prefabName);
        Tier = tier;
        TierCount = Math.Max(1, tierCount);
        TierName = tierName ?? "";
        FallbackReason = fallbackReason ?? "";
        Sources = (sources ?? Enumerable.Empty<string>())
            .Select(FoodIdentity.NormalizePrefabName)
            .Where(source => source.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source, StringComparer.Ordinal)
            .ToArray();
    }

    internal string PrefabName { get; }
    internal int Tier { get; }
    internal int TierCount { get; }
    internal string TierName { get; }
    internal string FallbackReason { get; }
    internal IReadOnlyList<string> Sources { get; }
    internal string ItemNameToken { get; private set; } = "";
    internal FoodStatAxis Axis { get; private set; }
    internal bool IsResolved => Tier >= 0;
    internal bool IsAllTier => !IsResolved;

    internal float NormalizedTier =>
        !IsResolved || TierCount <= 1
            ? 0f
            : Mathf.Clamp01(Tier / (float)(TierCount - 1));

    internal ChefFoodTierInfo WithFoodIdentity(
        string? itemNameToken,
        FoodStatAxis axis)
    {
        ItemNameToken = itemNameToken ?? "";
        Axis = axis;
        return this;
    }
}

/// <summary>
/// Resolves Chef's Choice foods against FineDining's server-authoritative
/// resource-map snapshot. Production dependencies are collected from recipes,
/// CookingStation conversions, and Fermenter conversions, then resolved as a
/// monotonic fixed point so cycles cannot recurse indefinitely.
/// </summary>
internal static class ChefFoodTierCatalog
{
    internal const int AllTier = -1;
    internal const string NoRecipeReason = "no_recipe_or_conversion";
    internal const string UnmappedSourcesReason = "unmapped_sources";
    internal const string CyclicProductionReason = "cyclic_production_path";

    private const float RefreshIntervalSeconds = 1f;

    private static Dictionary<string, ChefFoodTierInfo> _byPrefab =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _dirty = true;
    private static float _nextRefreshAt;
    private static bool _failureLogged;
    private static int _builtResourceMapVersion = -1;

    internal static bool IsReady { get; private set; }
    internal static int Version { get; private set; }

    internal static void Tick()
    {
        if (!ChefResourceMapPolicy.TryGetSnapshot(out ChefResourceMapSnapshot resourceMap))
        {
            IsReady = false;
            return;
        }

        int resourceMapVersion = ChefResourceMapPolicy.Version;
        if (_builtResourceMapVersion != resourceMapVersion)
        {
            _dirty = true;
            _nextRefreshAt = 0f;
            IsReady = false;
        }

        float now = Time.realtimeSinceStartup;
        if (now < _nextRefreshAt)
        {
            return;
        }

        _nextRefreshAt = now + RefreshIntervalSeconds;
        if (!_dirty)
        {
            return;
        }

        ObjectDB? objectDb = ObjectDB.instance;
        ZNetScene? scene = ZNetScene.instance;
        if (objectDb?.m_items == null || objectDb.m_recipes == null ||
            scene?.m_namedPrefabs == null || scene.m_prefabs == null ||
            scene.m_nonNetViewPrefabs == null)
        {
            return;
        }

        try
        {
            Dictionary<string, ChefFoodTierInfo> rebuilt = BuildCatalog(
                objectDb,
                scene,
                resourceMap);
            if (!ChefResourceMapPolicy.TryGetSnapshot(
                    out ChefResourceMapSnapshot currentMap)
                || !ReferenceEquals(resourceMap, currentMap)
                || resourceMapVersion != ChefResourceMapPolicy.Version)
            {
                _dirty = true;
                _nextRefreshAt = 0f;
                IsReady = false;
                return;
            }

            bool changed = !CatalogEquals(_byPrefab, rebuilt);
            _byPrefab = rebuilt;
            _dirty = false;
            IsReady = true;
            _builtResourceMapVersion = resourceMapVersion;
            _failureLogged = false;
            if (changed)
            {
                Version++;
                ChefTierReferenceGenerator.Invalidate();
            }
        }
        catch (Exception exception)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not build the Chef food tier catalog; FineDining will retry: " +
                    exception.GetBaseException().Message);
            }
        }
    }

    internal static void Invalidate()
    {
        _dirty = true;
        _nextRefreshAt = 0f;
        IsReady = false;
    }

    internal static void Reset()
    {
        _byPrefab = new Dictionary<string, ChefFoodTierInfo>(StringComparer.OrdinalIgnoreCase);
        _dirty = true;
        _nextRefreshAt = 0f;
        _failureLogged = false;
        _builtResourceMapVersion = -1;
        IsReady = false;
        Version = 0;
    }

    internal static IReadOnlyList<ChefFoodTierInfo> GetSnapshot() =>
        !IsReady
            ? Array.Empty<ChefFoodTierInfo>()
            : _byPrefab.Values
            .OrderBy(info => info.PrefabName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(info => info.PrefabName, StringComparer.Ordinal)
            .ToArray();

    private static Dictionary<string, ChefFoodTierInfo> BuildCatalog(
        ObjectDB objectDb,
        ZNetScene scene,
        ChefResourceMapSnapshot resourceMap)
    {
        Dictionary<string, FoodNode> nodes = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject prefab in objectDb.m_items)
        {
            RegisterItem(
                nodes,
                prefab != null ? prefab.GetComponent<ItemDrop>() : null,
                resourceMap);
        }

        foreach (Recipe recipe in objectDb.m_recipes)
        {
            if (recipe?.m_item == null)
            {
                continue;
            }

            FoodNode? output = RegisterItem(nodes, recipe.m_item, resourceMap);
            if (output == null || recipe.m_resources == null)
            {
                continue;
            }

            foreach (Piece.Requirement requirement in recipe.m_resources)
            {
                if (requirement?.m_resItem == null || requirement.m_amount <= 0)
                {
                    continue;
                }

                FoodNode? source = RegisterItem(
                    nodes,
                    requirement.m_resItem,
                    resourceMap);
                if (source != null)
                {
                    output.Dependencies.Add(source.PrefabName);
                }
            }
        }

        AddFeastProductionPaths(nodes, objectDb, resourceMap);

        foreach (GameObject prefab in CollectScenePrefabs(scene))
        {
            if (prefab == null)
            {
                continue;
            }

            CookingStation[] stations;
            try
            {
                stations = prefab.GetComponentsInChildren<CookingStation>(true);
            }
            catch
            {
                stations = Array.Empty<CookingStation>();
            }

            foreach (CookingStation station in stations)
            {
                if (station?.m_conversion == null)
                {
                    continue;
                }

                foreach (CookingStation.ItemConversion conversion in station.m_conversion)
                {
                    RegisterConversion(
                        nodes,
                        conversion?.m_from,
                        conversion?.m_to,
                        resourceMap);
                }
            }

            Fermenter[] fermenters;
            try
            {
                fermenters = prefab.GetComponentsInChildren<Fermenter>(true);
            }
            catch
            {
                fermenters = Array.Empty<Fermenter>();
            }

            foreach (Fermenter fermenter in fermenters)
            {
                if (fermenter?.m_conversion == null)
                {
                    continue;
                }

                foreach (Fermenter.ItemConversion conversion in fermenter.m_conversion)
                {
                    RegisterConversion(
                        nodes,
                        conversion?.m_from,
                        conversion?.m_to,
                        resourceMap);
                }
            }
        }

        foreach (FoodNode node in nodes.Values)
        {
            node.Tier = node.DirectTier;
        }

        // Tier values only increase, so at most node-count passes are required
        // even when production paths contain cycles.
        for (int pass = 0; pass < nodes.Count; pass++)
        {
            bool changed = false;
            foreach (FoodNode node in nodes.Values)
            {
                int best = node.Tier;
                foreach (string dependency in node.Dependencies)
                {
                    if (nodes.TryGetValue(dependency, out FoodNode source))
                    {
                        best = Math.Max(best, source.Tier);
                    }
                }

                if (best <= node.Tier)
                {
                    continue;
                }

                node.Tier = best;
                changed = true;
            }

            if (!changed)
            {
                break;
            }
        }

        Dictionary<string, ChefFoodTierInfo> result =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (FoodNode node in nodes.Values)
        {
            if (node.Item?.m_itemData == null ||
                !FoodIdentity.TryGetFoodStatAxis(
                    node.Item.m_itemData,
                    out FoodStatAxis axis))
            {
                continue;
            }

            string itemNameToken = node.Item.m_itemData.m_shared.m_name;

            if (node.Tier >= 0)
            {
                result[node.PrefabName] = new ChefFoodTierInfo(
                    node.PrefabName,
                    node.Tier,
                    resourceMap.TierCount,
                    resourceMap.GetTierName(node.Tier),
                    "",
                    Array.Empty<string>())
                    .WithFoodIdentity(itemNameToken, axis);
                continue;
            }

            ResolveFallback(node, nodes, out string reason, out IReadOnlyList<string> sources);
            result[node.PrefabName] = new ChefFoodTierInfo(
                node.PrefabName,
                AllTier,
                resourceMap.TierCount,
                "AllTier",
                reason,
                sources)
                .WithFoodIdentity(itemNameToken, axis);
        }

        return result;
    }

    private static void RegisterConversion(
        Dictionary<string, FoodNode> nodes,
        ItemDrop? from,
        ItemDrop? to,
        ChefResourceMapSnapshot resourceMap)
    {
        FoodNode? source = RegisterItem(nodes, from, resourceMap);
        FoodNode? output = RegisterItem(nodes, to, resourceMap);
        if (source != null && output != null)
        {
            output.Dependencies.Add(source.PrefabName);
        }
    }

    private static void AddFeastProductionPaths(
        Dictionary<string, FoodNode> nodes,
        ObjectDB objectDb,
        ChefResourceMapSnapshot resourceMap)
    {
        HashSet<string> feastResults = new(StringComparer.OrdinalIgnoreCase);
        List<(ItemDrop Source, ItemDrop Result)> links = new();
        foreach (GameObject prefab in objectDb.m_items)
        {
            ItemDrop? source = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (source?.m_itemData?.m_shared == null)
            {
                continue;
            }

            Feast? feast = source.GetComponent<Feast>();
            ItemDrop? result = feast?.m_foodItem;
            ItemDrop.ItemData.SharedData? resultShared = result?.m_itemData?.m_shared;
            if (result == null || resultShared == null ||
                FoodIdentity.GetCanonicalPrefabName(source).Equals(
                    FoodIdentity.GetCanonicalPrefabName(result),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool resultLooksFeastLike = result.GetComponent<Feast>() != null ||
                                        resultShared.m_itemType ==
                                            ItemDrop.ItemData.ItemType.Consumable ||
                                        FoodIdentity.LooksLikeFeastRoutingFood(resultShared);
            bool sourceIsMaterial = source.m_itemData.m_shared.m_itemType ==
                                    ItemDrop.ItemData.ItemType.Material;
            bool sourceIsNonFoodRouter =
                source.m_itemData.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable &&
                !FoodIdentity.LooksLikeFeastRoutingFood(source.m_itemData.m_shared);
            if (resultLooksFeastLike && (sourceIsMaterial || sourceIsNonFoodRouter))
            {
                links.Add((source, result));
                feastResults.Add(FoodIdentity.GetCanonicalPrefabName(result));
            }
        }

        foreach ((ItemDrop source, ItemDrop result) in links)
        {
            RegisterConversion(nodes, source, result, resourceMap);
        }

        // Some feast material prefabs route through appendToolTip instead of
        // Feast.m_foodItem. Limit that broad link to targets already proven to
        // be feast results so unrelated tooltip proxies do not become recipes.
        foreach (GameObject prefab in objectDb.m_items)
        {
            ItemDrop? source = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            ItemDrop.ItemData.SharedData? shared = source?.m_itemData?.m_shared;
            ItemDrop? result = shared?.m_appendToolTip;
            if (source == null || result?.m_itemData?.m_shared == null ||
                shared!.m_itemType != ItemDrop.ItemData.ItemType.Material)
            {
                continue;
            }

            string resultName = FoodIdentity.GetCanonicalPrefabName(result);
            if (feastResults.Contains(resultName) || result.GetComponent<Feast>() != null)
            {
                RegisterConversion(nodes, source, result, resourceMap);
            }
        }
    }

    private static IReadOnlyList<GameObject> CollectScenePrefabs(ZNetScene scene)
    {
        List<GameObject> result = new();
        HashSet<int> seen = new();
        AddScenePrefabs(scene.m_namedPrefabs.Values, result, seen);
        AddScenePrefabs(scene.m_prefabs, result, seen);
        AddScenePrefabs(scene.m_nonNetViewPrefabs, result, seen);
        return result;
    }

    private static void AddScenePrefabs(
        IEnumerable<GameObject> source,
        ICollection<GameObject> target,
        ISet<int> seen)
    {
        foreach (GameObject prefab in source)
        {
            if (prefab != null && seen.Add(prefab.GetInstanceID()))
            {
                target.Add(prefab);
            }
        }
    }

    private static FoodNode? RegisterItem(
        Dictionary<string, FoodNode> nodes,
        ItemDrop? item,
        ChefResourceMapSnapshot resourceMap)
    {
        if (item?.m_itemData?.m_shared == null)
        {
            return null;
        }

        string prefabName = FoodIdentity.GetCanonicalPrefabName(item);
        if (prefabName.Length == 0)
        {
            return null;
        }

        if (!nodes.TryGetValue(prefabName, out FoodNode node))
        {
            node = new FoodNode(prefabName);
            nodes.Add(prefabName, node);
        }

        node.Item ??= item;
        node.DirectTier = Math.Max(
            node.DirectTier,
            GetDirectTier(item, resourceMap));
        return node;
    }

    private static int GetDirectTier(
        ItemDrop item,
        ChefResourceMapSnapshot resourceMap)
    {
        int tier = AllTier;
        foreach (string token in GetLocaleIndependentTokens(item))
        {
            if (resourceMap.TryGetResourceTier(token, out int mapped))
            {
                tier = Math.Max(tier, mapped);
            }
        }

        return tier;
    }

    private static IEnumerable<string> GetLocaleIndependentTokens(ItemDrop item)
    {
        string canonicalPrefab = FoodIdentity.GetCanonicalPrefabName(item);
        string componentPrefab = item.gameObject != null
            ? FoodIdentity.NormalizePrefabName(item.gameObject.name)
            : "";
        string sharedToken = item.m_itemData?.m_shared?.m_name ?? "";
        foreach (string value in new[] { canonicalPrefab, componentPrefab, sharedToken })
        {
            string normalized = ChefResourceMapPolicy.NormalizeResourceToken(value);
            if (normalized.Length > 0)
            {
                yield return normalized;
            }
        }
    }

    private static void ResolveFallback(
        FoodNode root,
        IReadOnlyDictionary<string, FoodNode> nodes,
        out string reason,
        out IReadOnlyList<string> sources)
    {
        if (root.Dependencies.Count == 0)
        {
            reason = NoRecipeReason;
            sources = Array.Empty<string>();
            return;
        }

        HashSet<string> leaves = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> cycleMembers = new(StringComparer.OrdinalIgnoreCase);
        CollectUnresolvedSources(
            root.PrefabName,
            root.PrefabName,
            nodes,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            leaves,
            cycleMembers);

        if (leaves.Count > 0)
        {
            reason = UnmappedSourcesReason;
            sources = leaves
                .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(source => source, StringComparer.Ordinal)
                .ToArray();
            return;
        }

        reason = cycleMembers.Count > 0
            ? CyclicProductionReason
            : UnmappedSourcesReason;
        sources = cycleMembers
            .Where(source => !source.Equals(root.PrefabName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source, StringComparer.Ordinal)
            .ToArray();
    }

    private static void CollectUnresolvedSources(
        string current,
        string root,
        IReadOnlyDictionary<string, FoodNode> nodes,
        HashSet<string> path,
        HashSet<string> leaves,
        HashSet<string> cycleMembers)
    {
        if (!path.Add(current))
        {
            cycleMembers.Add(current);
            return;
        }

        if (!nodes.TryGetValue(current, out FoodNode node) || node.Dependencies.Count == 0)
        {
            if (!current.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                leaves.Add(current);
            }

            path.Remove(current);
            return;
        }

        foreach (string dependency in node.Dependencies)
        {
            if (path.Contains(dependency))
            {
                cycleMembers.Add(dependency);
                cycleMembers.Add(current);
                continue;
            }

            CollectUnresolvedSources(
                dependency,
                root,
                nodes,
                path,
                leaves,
                cycleMembers);
        }

        path.Remove(current);
    }

    private static bool CatalogEquals(
        IReadOnlyDictionary<string, ChefFoodTierInfo> left,
        IReadOnlyDictionary<string, ChefFoodTierInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, ChefFoodTierInfo> pair in left)
        {
            if (!right.TryGetValue(pair.Key, out ChefFoodTierInfo other) ||
                !pair.Value.PrefabName.Equals(other.PrefabName, StringComparison.Ordinal) ||
                pair.Value.Tier != other.Tier ||
                pair.Value.TierCount != other.TierCount ||
                !pair.Value.TierName.Equals(other.TierName, StringComparison.Ordinal) ||
                !pair.Value.FallbackReason.Equals(other.FallbackReason, StringComparison.Ordinal) ||
                !pair.Value.ItemNameToken.Equals(other.ItemNameToken, StringComparison.Ordinal) ||
                pair.Value.Axis != other.Axis ||
                !pair.Value.Sources.SequenceEqual(other.Sources, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class FoodNode
    {
        internal FoodNode(string prefabName)
        {
            PrefabName = prefabName;
        }

        internal string PrefabName { get; }
        internal ItemDrop? Item { get; set; }
        internal int DirectTier { get; set; } = AllTier;
        internal int Tier { get; set; } = AllTier;
        internal HashSet<string> Dependencies { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

}
