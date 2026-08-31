using System;
using System.Collections.Generic;
using UnityEngine;

namespace FineDining;

internal static class ChefCollectionService
{
    private static readonly object RandomLock = new();
    private static readonly System.Random RandomSource = new();

    internal static bool EnsureChefCollection(Player? player, PlayerFoodStateData state) =>
        EnsureChefCollection(player, state, excludedRefillKey: null);

    private static bool EnsureChefCollection(
        Player? player,
        PlayerFoodStateData state,
        string? excludedRefillKey)
    {
        if (player == null || ObjectDB.instance == null)
        {
            return false;
        }

        ChefFoodTierCatalog.Tick();
        if (!ChefFoodTierCatalog.IsReady)
        {
            return false;
        }

        Dictionary<string, FoodStatAxis> foodAxesByKey =
            new(StringComparer.Ordinal);
        List<ChefCandidate> eligible = GetKnownFoodCandidates(
            player,
            foodAxesByKey);
        RecentFoodComposition recentFoodComposition =
            GetRecentFoodComposition(state, foodAxesByKey);
        HashSet<string> eligibleSet = new(StringComparer.Ordinal);
        foreach (ChefCandidate candidate in eligible)
        {
            eligibleSet.Add(candidate.Key);
        }

        bool changed = false;
        int originalChefCount = state.Chef.Count;
        state.Chef.RemoveAll(entry =>
            entry == null || string.IsNullOrWhiteSpace(entry.Key) || !eligibleSet.Contains(entry.Key));
        changed |= state.Chef.Count != originalChefCount;

        HashSet<string> currentKeys = new(StringComparer.Ordinal);
        for (int index = state.Chef.Count - 1; index >= 0; index--)
        {
            if (!currentKeys.Add(state.Chef[index].Key))
            {
                state.Chef.RemoveAt(index);
                changed = true;
            }
        }

        int desiredCount = Mathf.Min(DietConfig.GetChefCollectionSize(), eligible.Count);
        float cookingFactor = ChefChoiceMath.ClampCookingFactor(
            player.GetSkillFactor(Skills.SkillType.Cooking));
        while (state.Chef.Count < desiredCount)
        {
            if (!TrySelectWeightedCandidate(
                    eligible,
                    currentKeys,
                    excludedRefillKey,
                    cookingFactor,
                    recentFoodComposition,
                    out ChefCandidate next))
            {
                // When every eligible food is active except the entry that was
                // just removed, allow it back rather than leaving a slot empty.
                if (string.IsNullOrWhiteSpace(excludedRefillKey) ||
                    !TrySelectWeightedCandidate(
                        eligible,
                        currentKeys,
                        excludedKey: null,
                        cookingFactor,
                        recentFoodComposition,
                        out next))
                {
                    break;
                }
            }

            state.Chef.Add(new ChefEntryData
            {
                Key = next.Key,
                Multiplier = RollChefMultiplier(cookingFactor)
            });
            currentKeys.Add(next.Key);
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
            return true;
        }

        multiplier = 1f;
        return false;
    }

    internal static bool RefillAfterConsumption(
        Player player,
        PlayerFoodStateData state,
        string consumedKey) =>
        EnsureChefCollection(player, state, consumedKey);

    internal static ChefEntryData? GetEntry(PlayerFoodStateData state, string key)
    {
        if (SpoilagePolicy.IsChefChoiceBlacklisted(key))
        {
            return null;
        }

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
        EnsureChefCollection(player, state);
        FoodStateStore.SaveState(player, state);
    }

    internal static void RotateOldest(Player? player, int count)
    {
        if (player == null || ObjectDB.instance == null || count <= 0)
        {
            return;
        }

        PlayerFoodStateData state = FoodStateStore.GetState(player);
        bool changed = EnsureChefCollection(player, state);
        for (int index = 0; index < count && state.Chef.Count > 0; index++)
        {
            string oldestKey = state.Chef[0].Key;
            state.Chef.RemoveAt(0);
            changed = true;
            changed |= EnsureChefCollection(player, state, oldestKey);
        }

        if (changed)
        {
            FoodStateStore.SaveState(player, state);
        }
    }

    private static bool TrySelectWeightedCandidate(
        IReadOnlyList<ChefCandidate> eligible,
        ISet<string> currentKeys,
        string? excludedKey,
        float cookingFactor,
        RecentFoodComposition recentFoodComposition,
        out ChefCandidate selected)
    {
        List<ChefCandidate> available = new();
        List<float> baseWeights = new();
        float[] categoryBaseWeights = new float[FoodStatAxisCount];
        foreach (ChefCandidate candidate in eligible)
        {
            if (currentKeys.Contains(candidate.Key) ||
                candidate.Key.Equals(excludedKey, StringComparison.Ordinal))
            {
                continue;
            }

            available.Add(candidate);
            float baseWeight = candidate.GetSelectionWeight(cookingFactor);
            baseWeights.Add(baseWeight);
            categoryBaseWeights[candidate.AxisIndex] += baseWeight;
        }

        if (available.Count == 0)
        {
            selected = null!;
            return false;
        }

        float totalBaseWeight = 0f;
        float availableHistoryShare = 0f;
        for (int axisIndex = 0; axisIndex < FoodStatAxisCount; axisIndex++)
        {
            if (categoryBaseWeights[axisIndex] <= 0f)
            {
                continue;
            }

            totalBaseWeight += categoryBaseWeights[axisIndex];
            availableHistoryShare += recentFoodComposition.GetShare(axisIndex);
        }

        List<float> weights = new(available.Count);
        float preferencePercent = availableHistoryShare > 0f
            ? DietConfig.GetChefRecentFoodPreferencePercent()
            : 0f;
        for (int index = 0; index < available.Count; index++)
        {
            ChefCandidate candidate = available[index];
            float categoryBaseWeight = categoryBaseWeights[candidate.AxisIndex];
            float baseCategoryProbability = totalBaseWeight > 0f
                ? categoryBaseWeight / totalBaseWeight
                : 0f;
            float historyCategoryProbability = availableHistoryShare > 0f
                ? recentFoodComposition.GetShare(candidate.AxisIndex) /
                  availableHistoryShare
                : baseCategoryProbability;
            float categoryProbability = ChefChoiceMath.BlendCategoryProbability(
                baseCategoryProbability,
                historyCategoryProbability,
                preferencePercent);
            weights.Add(categoryBaseWeight > 0f
                ? categoryProbability * baseWeights[index] / categoryBaseWeight
                : 0f);
        }

        float sample;
        lock (RandomLock)
        {
            sample = (float)RandomSource.NextDouble();
        }

        int selectedIndex = ChefChoiceMath.ChooseWeightedIndex(weights, sample);
        if (selectedIndex < 0)
        {
            selected = null!;
            return false;
        }

        selected = available[selectedIndex];
        return true;
    }

    private static List<ChefCandidate> GetKnownFoodCandidates(
        Player player,
        IDictionary<string, FoodStatAxis> foodAxesByKey)
    {
        List<ChefCandidate> candidates = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ChefFoodTierInfo tierInfo in ChefFoodTierCatalog.GetSnapshot())
        {
            string key = tierInfo.PrefabName;
            if (string.IsNullOrWhiteSpace(key) ||
                tierInfo.Axis == FoodStatAxis.None)
            {
                continue;
            }

            foodAxesByKey[key] = tierInfo.Axis;
            if (!FoodSlotProgression.IsKnownFood(player, tierInfo))
            {
                continue;
            }

            if (SpoilagePolicy.IsChefChoiceBlacklisted(key) ||
                !seen.Add(key))
            {
                continue;
            }

            candidates.Add(new ChefCandidate(key, tierInfo, tierInfo.Axis));
        }

        candidates.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Key, right.Key));
        return candidates;
    }

    private static RecentFoodComposition GetRecentFoodComposition(
        PlayerFoodStateData state,
        IReadOnlyDictionary<string, FoodStatAxis> foodAxesByKey)
    {
        int health = 0;
        int stamina = 0;
        int eitr = 0;
        foreach (HistoryEntryData entry in state.Recent)
        {
            if (entry == null ||
                !foodAxesByKey.TryGetValue(entry.Key, out FoodStatAxis axis))
            {
                continue;
            }

            switch (axis)
            {
                case FoodStatAxis.Health:
                    health++;
                    break;
                case FoodStatAxis.Stamina:
                    stamina++;
                    break;
                case FoodStatAxis.Eitr:
                    eitr++;
                    break;
            }
        }

        return new RecentFoodComposition(health, stamina, eitr);
    }

    private static float RollChefMultiplier(float cookingFactor)
    {
        float sample;
        lock (RandomLock)
        {
            sample = (float)RandomSource.NextDouble();
        }

        return ChefChoiceMath.GetChefMultiplier(
            DietConfig.GetChefMultiplierMin(),
            DietConfig.GetChefMultiplierMax(),
            sample,
            cookingFactor,
            DietConfig.GetChefMultiplierModeAtMaxCooking());
    }

    private sealed class ChefCandidate
    {
        internal ChefCandidate(
            string key,
            ChefFoodTierInfo tierInfo,
            FoodStatAxis axis)
        {
            Key = key;
            TierInfo = tierInfo;
            AxisIndex = GetFoodStatAxisIndex(axis);
        }

        internal string Key { get; }
        internal int AxisIndex { get; }
        private ChefFoodTierInfo TierInfo { get; }

        internal float GetSelectionWeight(float cookingFactor) =>
            ChefChoiceMath.GetFoodSelectionWeight(
                cookingFactor,
                TierInfo.NormalizedTier,
                TierInfo.IsResolved,
                DietConfig.GetChefHighTierSelectionStrength());
    }

    private const int FoodStatAxisCount = 3;

    private static int GetFoodStatAxisIndex(FoodStatAxis axis) =>
        axis switch
        {
            FoodStatAxis.Health => 0,
            FoodStatAxis.Stamina => 1,
            FoodStatAxis.Eitr => 2,
            _ => throw new ArgumentOutOfRangeException(
                nameof(axis),
                axis,
                "Chef food must have a classified food-stat axis.")
        };

    private readonly struct RecentFoodComposition
    {
        private readonly int _health;
        private readonly int _stamina;
        private readonly int _eitr;
        private readonly int _total;

        internal RecentFoodComposition(int health, int stamina, int eitr)
        {
            _health = Math.Max(0, health);
            _stamina = Math.Max(0, stamina);
            _eitr = Math.Max(0, eitr);
            _total = _health + _stamina + _eitr;
        }

        internal float GetShare(int axisIndex)
        {
            if (_total <= 0)
            {
                return 0f;
            }

            int count = axisIndex switch
            {
                0 => _health,
                1 => _stamina,
                2 => _eitr,
                _ => 0
            };
            return count / (float)_total;
        }
    }
}
