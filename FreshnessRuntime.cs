using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using ServerSync;
using UnityEngine;
using ItemData = ItemDrop.ItemData;

namespace FineDining;

internal readonly struct AssignedLifetimeSnapshot
{
    internal AssignedLifetimeSnapshot(string? rawValue, long value, bool valid)
    {
        RawValue = rawValue;
        Value = value;
        Valid = valid;
    }

    internal string? RawValue { get; }
    internal long Value { get; }
    internal bool Valid { get; }
}

internal static class FreshnessRuntime
{
    internal const string AssignedLifetimeDataKey =
        "sighsorry.FineDining.AssignedLifetimeTicks";
    internal const float DefaultMinimumFoodMultiplier = 0.75f;

    private static ConfigEntry<float>? _minimumFoodMultiplier;

    internal static float MinimumFoodMultiplier =>
        Mathf.Clamp01(_minimumFoodMultiplier?.Value ?? DefaultMinimumFoodMultiplier);

    internal static void Initialize(ConfigFile config, ConfigSync configSync)
    {
        Shutdown();
        _minimumFoodMultiplier = BindMinimumFoodMultiplier(config);
        SyncedConfigEntry<float> syncedEntry = configSync.AddConfigEntry(_minimumFoodMultiplier);
        syncedEntry.SynchronizedConfig = true;
    }

    internal static ConfigEntry<float> BindMinimumFoodMultiplier(ConfigFile config) =>
        config.Bind(
            ConfigPresentation.Spoilage.Name,
            "Stale Food Minimum Multiplier",
            DefaultMinimumFoodMultiplier,
            ConfigPresentation.Synced(
                "Minimum multiplier applied to a directly edible item's health, stamina, eitr, " +
                "and health regeneration at zero freshness. Intermediate freshness is interpolated " +
                "linearly between this value and 1.",
                ConfigPresentation.Spoilage,
                500,
                new AcceptableValueRange<float>(0f, 1f)));

    internal static void Shutdown()
    {
        _minimumFoodMultiplier = null;
    }

    internal static float GetFoodStatMultiplier(ItemData? item)
    {
        if (!SpoilagePolicy.IsEnabled || !FoodIdentity.IsDirectlyEdible(item))
        {
            return 1f;
        }

        float ratio = TryGetFreshnessRatio(item, out float currentRatio)
            ? currentRatio
            : 1f;
        return CalculateFoodStatMultiplier(ratio);
    }

    internal static float CalculateFoodStatMultiplier(float freshnessRatio) =>
        CalculateFoodStatMultiplierForMinimum(freshnessRatio, MinimumFoodMultiplier);

    internal static float CalculateFoodStatMultiplierForMinimum(
        float freshnessRatio,
        float minimumMultiplier)
    {
        float minimum = ClampRatio(minimumMultiplier);
        return Mathf.Clamp(
            minimum + (1f - minimum) * ClampRatio(freshnessRatio),
            minimum,
            1f);
    }

    internal static bool TryGetFreshnessRatio(ItemData? item, out float ratio)
    {
        ratio = 1f;
        if (!SpoilagePolicy.IsEnabled || item == null)
        {
            return false;
        }

        if (SpoilageClock.IsSpoiled(item))
        {
            ratio = 0f;
            return true;
        }

        if (
            !SpoilageClock.TryGetWorldTicks(out long nowTicks) ||
            !SpoilageClock.TryGetSpoilageClock(
                item,
                nowTicks,
                out long remainingTicks,
                out _))
        {
            return false;
        }

        long assignedLifetime = TryGetAssignedLifetime(item, out long persistedLifetime)
            ? persistedLifetime
            : ResolveRuleLifetime(item, remainingTicks);
        ratio = ClampRatio(remainingTicks / (double)assignedLifetime);
        return true;
    }

    internal static bool EnsureTrackedMetadata(ItemData item, long assignedLifetimeTicks)
    {
        if (item == null)
        {
            return false;
        }

        item.m_customData ??= new Dictionary<string, string>();
        if (TryGetAssignedLifetime(item, out _))
        {
            return false;
        }

        item.m_customData[AssignedLifetimeDataKey] = NormalizeLifetime(assignedLifetimeTicks)
            .ToString(CultureInfo.InvariantCulture);
        return true;
    }

    internal static bool ClearTrackedMetadata(ItemData? item) =>
        item?.m_customData != null && item.m_customData.Remove(AssignedLifetimeDataKey);

    /// <summary>
    /// Copies only the host ItemDrop metadata needed to evaluate a linked Feast
    /// food item. Foreign custom data and placement anchors are never copied.
    /// </summary>
    internal static void CopyFreshnessMetadata(ItemData? source, ItemData? destination)
    {
        if (destination == null)
        {
            return;
        }

        destination.m_customData ??= new Dictionary<string, string>();
        CopyOrRemove(source, destination, SpoilageClock.ExpiryDataKey);
        CopyOrRemove(source, destination, AssignedLifetimeDataKey);
        CopyOrRemove(source, destination, SpoilageClock.SpoiledDataKey);
    }

    internal static AssignedLifetimeSnapshot CaptureAssignedLifetime(ItemData? item)
    {
        string? rawValue = null;
        item?.m_customData?.TryGetValue(AssignedLifetimeDataKey, out rawValue);
        return CaptureAssignedLifetimeValue(rawValue);
    }

    private static AssignedLifetimeSnapshot CaptureAssignedLifetimeValue(string? rawValue)
    {
        bool valid = TryParsePositiveLong(rawValue, out long value);
        return new AssignedLifetimeSnapshot(rawValue, value, valid);
    }

    internal static bool ComposeAssignedLifetime(
        ItemData target,
        AssignedLifetimeSnapshot destination,
        AssignedLifetimeSnapshot source)
    {
        if (target == null ||
            !TryComposeAssignedLifetime(destination, source, out long merged))
        {
            return false;
        }

        target.m_customData ??= new Dictionary<string, string>();
        string encoded = merged.ToString(CultureInfo.InvariantCulture);
        if (target.m_customData.TryGetValue(AssignedLifetimeDataKey, out string current) &&
            string.Equals(current, encoded, StringComparison.Ordinal))
        {
            return false;
        }

        target.m_customData[AssignedLifetimeDataKey] = encoded;
        return true;
    }

    internal static string? ComposeAssignedLifetimeValues(
        string? destinationValue,
        string? sourceValue)
    {
        AssignedLifetimeSnapshot destination =
            CaptureAssignedLifetimeValue(destinationValue);
        AssignedLifetimeSnapshot source = CaptureAssignedLifetimeValue(sourceValue);
        return TryComposeAssignedLifetime(destination, source, out long merged)
            ? merged.ToString(CultureInfo.InvariantCulture)
            : destinationValue;
    }

    internal static bool CanMergeAssignedLifetimeValues(
        string? destinationValue,
        string? sourceValue) =>
        (destinationValue == null || TryParsePositiveLong(destinationValue, out _)) &&
        (sourceValue == null || TryParsePositiveLong(sourceValue, out _));

    internal static bool TryGetAssignedLifetime(ItemData? item, out long lifetime)
    {
        lifetime = 0L;
        return item?.m_customData != null &&
               item.m_customData.TryGetValue(AssignedLifetimeDataKey, out string value) &&
               TryParsePositiveLong(value, out lifetime);
    }

    private static bool TryComposeAssignedLifetime(
        AssignedLifetimeSnapshot destination,
        AssignedLifetimeSnapshot source,
        out long merged)
    {
        merged = 0L;
        if (!source.Valid)
        {
            return false;
        }

        // Preserve unknown future destination formats. A genuinely missing
        // destination inherits the source; two valid values use the larger basis
        // so an earlier expiry cannot make a merged stack appear fresher.
        if (destination.Valid)
        {
            merged = Math.Max(destination.Value, source.Value);
            return true;
        }

        if (destination.RawValue != null)
        {
            return false;
        }

        merged = source.Value;
        return true;
    }

    private static long ResolveRuleLifetime(ItemData item, long fallbackRemaining)
    {
        ResolvedSpoilageRule rule = SpoilagePolicy.Resolve(item);
        return rule.State == SpoilageRuleState.Enabled
            ? NormalizeLifetime(rule.LifetimeTicks)
            : NormalizeLifetime(fallbackRemaining);
    }

    private static void CopyOrRemove(ItemData? source, ItemData destination, string key)
    {
        if (source?.m_customData != null && source.m_customData.TryGetValue(key, out string value))
        {
            destination.m_customData[key] = value;
        }
        else
        {
            destination.m_customData.Remove(key);
        }
    }

    private static bool TryParsePositiveLong(string? value, out long parsed)
    {
        parsed = 0L;
        return value != null &&
               long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) &&
               parsed > 0L &&
               string.Equals(value, parsed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static long NormalizeLifetime(long ticks) =>
        Math.Max(TimeSpan.TicksPerSecond, ticks);

    private static float ClampRatio(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            return 1f;
        }

        return (float)Math.Max(0d, Math.Min(1d, ratio));
    }
}
