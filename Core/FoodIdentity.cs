using UnityEngine;

namespace FineDining;

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

    internal static bool IsDirectlyEdible(ItemDrop.ItemData? item)
    {
        ItemDrop.ItemData.SharedData? shared = item?.m_shared;
        return shared != null &&
               shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable &&
               (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f);
    }

    internal static bool IsDirectlyEdible(Player.Food? food) =>
        IsDirectlyEdible(food?.m_item);

    internal static string NormalizePrefabName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return Utils.GetPrefabName(name!.Trim()).Trim();
    }
}
