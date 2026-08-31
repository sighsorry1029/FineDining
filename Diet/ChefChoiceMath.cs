using System;
using System.Collections.Generic;

namespace FineDining;

internal static class ChefChoiceMath
{
    internal const float MinimumAllowedMultiplier = 1f;
    internal const float MaximumTierSelectionStrength = 20f;

    internal static float ClampCookingFactor(float cookingFactor)
    {
        return ClampUnit(cookingFactor);
    }

    internal static float ClampNormalizedTier(float normalizedTier)
    {
        return ClampUnit(normalizedTier);
    }

    internal static float ClampPercentage(float percentage)
    {
        if (float.IsNaN(percentage) || percentage <= 0f)
        {
            return 0f;
        }

        return float.IsPositiveInfinity(percentage) || percentage >= 100f
            ? 100f
            : percentage;
    }

    internal static float BlendCategoryProbability(
        float baseProbability,
        float historyProbability,
        float percentage)
    {
        float blend = ClampPercentage(percentage) / 100f;
        if (blend <= 0f)
        {
            return baseProbability;
        }

        if (blend >= 1f)
        {
            return historyProbability;
        }

        return baseProbability + (historyProbability - baseProbability) * blend;
    }

    internal static float GetFoodSelectionWeight(
        float cookingFactor,
        float normalizedTier,
        bool tierResolved,
        float highTierSelectionStrength)
    {
        if (!tierResolved)
        {
            return 1f;
        }

        float skill = ClampCookingFactor(cookingFactor);
        float tier = ClampNormalizedTier(normalizedTier);
        float strength = ClampTierSelectionStrength(highTierSelectionStrength);
        return (float)Math.Exp(strength * skill * tier);
    }

    internal static float GetInterpolatedMultiplierMode(
        float minimum,
        float maximum,
        float cookingLevelOneHundredMode,
        float cookingFactor)
    {
        minimum = ClampMultiplierMinimum(minimum);
        maximum = ClampMultiplierMaximum(maximum, minimum);
        float targetMode = ClampMultiplierMode(
            cookingLevelOneHundredMode,
            minimum,
            maximum);
        return minimum +
               (targetMode - minimum) * ClampCookingFactor(cookingFactor);
    }

    internal static float GetTriangularQuantile(
        float uniformSample,
        float normalizedMode)
    {
        double sample = ClampUnit(uniformSample);
        double mode = ClampUnit(normalizedMode);
        double quantile = sample < mode
            ? Math.Sqrt(sample * mode)
            : 1d - Math.Sqrt((1d - sample) * (1d - mode));
        return ClampUnit((float)quantile);
    }

    internal static float GetChefMultiplier(
        float minimum,
        float maximum,
        float uniformSample,
        float cookingFactor,
        float cookingLevelOneHundredMode)
    {
        minimum = ClampMultiplierMinimum(minimum);
        maximum = ClampMultiplierMaximum(maximum, minimum);
        if (minimum >= maximum)
        {
            return minimum;
        }

        double mode = GetInterpolatedMultiplierMode(
            minimum,
            maximum,
            cookingLevelOneHundredMode,
            cookingFactor);
        double normalizedMode = (mode - minimum) / (maximum - minimum);
        double quantile = GetTriangularQuantile(
            uniformSample,
            (float)normalizedMode);
        return (float)((1d - quantile) * minimum + quantile * maximum);
    }

    internal static int ChooseWeightedIndex(
        IReadOnlyList<float>? weights,
        float uniformSample)
    {
        if (weights == null || weights.Count == 0)
        {
            return -1;
        }

        double total = 0d;
        int lastPositive = -1;
        for (int index = 0; index < weights.Count; index++)
        {
            float weight = weights[index];
            if (float.IsNaN(weight) || float.IsInfinity(weight) || weight <= 0f)
            {
                continue;
            }

            total += weight;
            lastPositive = index;
        }

        if (lastPositive < 0 || total <= 0d || double.IsInfinity(total))
        {
            return -1;
        }

        double target = ClampUnit(uniformSample) * total;
        double cumulative = 0d;
        for (int index = 0; index < weights.Count; index++)
        {
            float weight = weights[index];
            if (float.IsNaN(weight) || float.IsInfinity(weight) || weight <= 0f)
            {
                continue;
            }

            cumulative += weight;
            if (target < cumulative)
            {
                return index;
            }
        }

        // A sample of exactly one is outside System.Random.NextDouble's range,
        // but treating it as the final positive bucket makes the pure core safe.
        return lastPositive;
    }

    private static float ClampUnit(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0f;
        }

        return value >= 1f ? 1f : value;
    }

    internal static float ClampMultiplierMinimum(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value)
            ? MinimumAllowedMultiplier
            : Math.Max(MinimumAllowedMultiplier, value);
    }

    internal static float ClampMultiplierMaximum(float value, float minimum)
    {
        minimum = ClampMultiplierMinimum(minimum);
        return float.IsNaN(value) || float.IsInfinity(value)
            ? minimum
            : Math.Max(minimum, value);
    }

    internal static float ClampMultiplierMode(
        float value,
        float minimum,
        float maximum)
    {
        minimum = ClampMultiplierMinimum(minimum);
        maximum = ClampMultiplierMaximum(maximum, minimum);
        if (float.IsNaN(value) || value <= minimum)
        {
            return minimum;
        }

        return float.IsPositiveInfinity(value) || value >= maximum
            ? maximum
            : value;
    }

    private static float ClampTierSelectionStrength(float value)
    {
        if (float.IsNaN(value) || value <= 0f)
        {
            return 0f;
        }

        return float.IsPositiveInfinity(value) || value >= MaximumTierSelectionStrength
            ? MaximumTierSelectionStrength
            : value;
    }
}
