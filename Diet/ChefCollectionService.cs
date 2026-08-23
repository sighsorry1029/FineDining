using System.Collections.Generic;
using UnityEngine;

namespace FineDining;

internal static class ChefCollectionService
{
    private static readonly object RandomLock = new();
    private static readonly System.Random RandomSource = new();

    internal static bool EnsureChefCollection(Player? player, PlayerFoodStateData state)
    {
        if (player == null || ObjectDB.instance == null)
        {
            return false;
        }

        List<string> eligible = GetKnownFoodKeys(player);
        HashSet<string> eligibleSet = new(eligible);
        bool changed = false;

        int originalChefCount = state.Chef.Count;
        int originalQueueCount = state.ChefQueue.Count;
        state.Chef.RemoveAll(entry =>
            entry == null || string.IsNullOrWhiteSpace(entry.Key) || !eligibleSet.Contains(entry.Key));
        state.ChefQueue.RemoveAll(key =>
            string.IsNullOrWhiteSpace(key) || !eligibleSet.Contains(key));
        changed |= state.Chef.Count != originalChefCount || state.ChefQueue.Count != originalQueueCount;

        HashSet<string> currentKeys = new();
        for (int index = state.Chef.Count - 1; index >= 0; index--)
        {
            if (!currentKeys.Add(state.Chef[index].Key))
            {
                state.Chef.RemoveAt(index);
                changed = true;
            }
        }

        int desiredCount = Mathf.Min(DietConfig.GetChefCollectionSize(), eligible.Count);
        while (state.Chef.Count < desiredCount)
        {
            if (!TryTakeNextKey(eligible, state, currentKeys, out string nextKey))
            {
                break;
            }

            state.Chef.Add(new ChefEntryData
            {
                Key = nextKey,
                Multiplier = RollChefMultiplier()
            });
            currentKeys.Add(nextKey);
            changed = true;
        }

        while (state.Chef.Count > desiredCount)
        {
            state.Chef.RemoveAt(state.Chef.Count - 1);
            changed = true;
        }

        return changed;
    }

    internal static bool TryConsumeChefEntry(
        Player player,
        PlayerFoodStateData state,
        string key,
        out float multiplier)
    {
        EnsureChefCollection(player, state);
        for (int index = 0; index < state.Chef.Count; index++)
        {
            ChefEntryData entry = state.Chef[index];
            if (entry.Key != key)
            {
                continue;
            }

            multiplier = entry.Multiplier;
            state.Chef.RemoveAt(index);
            EnsureChefCollection(player, state);
            return true;
        }

        multiplier = 1f;
        return false;
    }

    internal static ChefEntryData? GetEntry(PlayerFoodStateData state, string key)
    {
        foreach (ChefEntryData entry in state.Chef)
        {
            if (entry.Key == key)
            {
                return entry;
            }
        }

        return null;
    }

    internal static void RerollAll(Player? player)
    {
        if (player == null || ObjectDB.instance == null)
        {
            return;
        }

        PlayerFoodStateData state = FoodStateStore.GetState(player);
        state.Chef.Clear();
        state.ChefQueue.Clear();
        EnsureChefCollection(player, state);
        FoodStateStore.SaveState(player, state);
    }

    private static bool TryTakeNextKey(
        List<string> eligible,
        PlayerFoodStateData state,
        HashSet<string> currentKeys,
        out string nextKey)
    {
        while (true)
        {
            if (state.ChefQueue.Count == 0)
            {
                RebuildQueue(eligible, state, currentKeys);
            }

            if (state.ChefQueue.Count == 0)
            {
                nextKey = string.Empty;
                return false;
            }

            nextKey = state.ChefQueue[0];
            state.ChefQueue.RemoveAt(0);
            if (!currentKeys.Contains(nextKey))
            {
                return true;
            }
        }
    }

    private static void RebuildQueue(
        List<string> eligible,
        PlayerFoodStateData state,
        HashSet<string> currentKeys)
    {
        List<string> queue = new();
        foreach (string key in eligible)
        {
            if (!currentKeys.Contains(key))
            {
                queue.Add(key);
            }
        }

        Shuffle(queue);
        state.ChefQueue = queue;
    }

    private static List<string> GetKnownFoodKeys(Player player)
    {
        List<string> keys = new();
        HashSet<string> seen = new();
        foreach (GameObject prefab in ObjectDB.instance.m_items)
        {
            if (prefab == null)
            {
                continue;
            }

            ItemDrop.ItemData? item = prefab.GetComponent<ItemDrop>()?.m_itemData;
            if (item == null || !FoodIdentity.IsDietConsumable(item))
            {
                continue;
            }

            if (!player.IsRecipeKnown(item.m_shared.m_name) &&
                !player.IsKnownMaterial(item.m_shared.m_name))
            {
                continue;
            }

            string key = FoodIdentity.GetCanonicalPrefabName(item);
            if (string.IsNullOrWhiteSpace(key))
            {
                key = FoodIdentity.NormalizePrefabName(prefab.name);
            }
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
            {
                keys.Add(key);
            }
        }

        keys.Sort(System.StringComparer.Ordinal);
        return keys;
    }

    private static float RollChefMultiplier()
    {
        float minimum = DietConfig.GetChefMultiplierMin();
        float maximum = DietConfig.GetChefMultiplierMax();
        if (minimum >= maximum)
        {
            return minimum;
        }

        lock (RandomLock)
        {
            return minimum + (float)RandomSource.NextDouble() * (maximum - minimum);
        }
    }

    private static void Shuffle(List<string> list)
    {
        lock (RandomLock)
        {
            for (int index = list.Count - 1; index > 0; index--)
            {
                int swapIndex = RandomSource.Next(index + 1);
                (list[index], list[swapIndex]) = (list[swapIndex], list[index]);
            }
        }
    }
}
