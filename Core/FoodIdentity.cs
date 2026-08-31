using UnityEngine;

namespace FineDining;

internal enum FoodStatAxis
{
    None,
    Health,
    Stamina,
    Eitr
}

internal static class FoodIdentity
{
    internal static string GetCanonicalPrefabName(ItemDrop.ItemData? item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        if (item.m_dropPrefab != null)
        {
            return NormalizePrefabName(item.m_dropPrefab.name);
        }

        ObjectDB? objectDb = ObjectDB.instance;
        if (objectDb != null && item.m_shared != null &&
            objectDb.TryGetItemPrefab(item.m_shared, out GameObject prefab) && prefab != null)
        {
            return NormalizePrefabName(prefab.name);
        }

        return string.Empty;
    }

    internal static string GetCanonicalPrefabName(ItemDrop? itemDrop)
    {
        if (itemDrop == null)
        {
            return string.Empty;
        }

        string canonicalName = GetCanonicalPrefabName(itemDrop.m_itemData);
        return canonicalName.Length > 0
            ? canonicalName
            : NormalizePrefabName(itemDrop.gameObject?.name);
    }

    internal static string GetCanonicalPrefabName(Player.Food? food)
    {
        if (food == null)
        {
            return string.Empty;
        }

        // Vanilla saves Food.m_name as the prefab name and resolves it through
        // ObjectDB on load. Prefer that stable identity over a mutable snapshot.
        string savedPrefabName = NormalizePrefabName(food.m_name);
        return savedPrefabName.Length > 0
            ? savedPrefabName
            : GetCanonicalPrefabName(food.m_item);
    }

    // Regeneration and status effects are effects, not food identity. Diet,
    // Chef's Choice, and freshness therefore share this H/S/E-only boundary.
    internal static bool IsDirectlyEdible(ItemDrop.ItemData? item)
    {
        ItemDrop.ItemData.SharedData? shared = item?.m_shared;
        return shared != null &&
               shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable &&
               HasChefFoodStats(
                   shared.m_food,
                   shared.m_foodStamina,
                   shared.m_foodEitr);
    }

    internal static bool IsDirectlyEdible(Player.Food? food) =>
        IsDirectlyEdible(food?.m_item);

    internal static bool TryGetFoodStatAxis(
        float health,
        float stamina,
        float eitr,
        out FoodStatAxis axis)
    {
        axis = FoodStatAxis.None;
        if (!HasChefFoodStats(health, stamina, eitr))
        {
            return false;
        }

        // Eitr identity is structural: any positive Eitr makes this an Eitr food.
        // Non-Eitr foods use Stamina for ties and Health only when Health is larger.
        if (eitr > 0f)
        {
            axis = FoodStatAxis.Eitr;
            return true;
        }

        axis = health <= stamina
            ? FoodStatAxis.Stamina
            : FoodStatAxis.Health;
        return true;
    }

    internal static bool TryGetFoodStatAxis(
        ItemDrop.ItemData? item,
        out FoodStatAxis axis)
    {
        ItemDrop.ItemData.SharedData? shared = item?.m_shared;
        if (shared == null || !IsDirectlyEdible(item))
        {
            axis = FoodStatAxis.None;
            return false;
        }

        return TryGetFoodStatAxis(
            shared.m_food,
            shared.m_foodStamina,
            shared.m_foodEitr,
            out axis);
    }

    internal static bool HasChefFoodStats(
        float health,
        float stamina,
        float eitr) =>
        health > 0f || stamina > 0f || eitr > 0f;

    /// <summary>
    /// Feast routing accepts stat-bearing food and drink-like linked results.
    /// This is intentionally broader than direct edible classification and is
    /// used only while following structural Feast links.
    /// </summary>
    internal static bool LooksLikeFeastRoutingFood(
        ItemDrop.ItemData.SharedData? shared) =>
        shared != null &&
        (HasChefFoodStats(shared.m_food, shared.m_foodStamina, shared.m_foodEitr) ||
         shared.m_isDrink);

    internal static string NormalizePrefabName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string normalized = name!.Trim();
        const string cloneSuffix = "(Clone)";
        return normalized.EndsWith(cloneSuffix, System.StringComparison.OrdinalIgnoreCase)
            ? normalized.Substring(0, normalized.Length - cloneSuffix.Length).TrimEnd()
            : normalized;
    }
}
