namespace FineDining;

internal static class FoodKeys
{
    internal static string GetKey(ItemDrop.ItemData? item) =>
        FoodIdentity.GetCanonicalPrefabName(item);

    internal static string GetKey(Player.Food? food) =>
        FoodIdentity.GetCanonicalPrefabName(food);

    // Diet keeps the original regen-only consumable preview/Chef behavior.
    // Spoilage's direct-food rule intentionally remains limited to the main
    // health, stamina, and eitr stats used by vanilla food classification.
    internal static bool IsConsumableFood(ItemDrop.ItemData? item) =>
        FoodIdentity.IsDirectlyEdible(item) ||
        item?.m_shared is
        {
            m_itemType: ItemDrop.ItemData.ItemType.Consumable,
            m_foodRegen: > 0f
        };
}
