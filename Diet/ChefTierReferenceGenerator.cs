using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace FineDining;

/// <summary>
/// Writes the complete Chef food catalog as a lookup document. Unassigned foods
/// retain their diagnostic details, while resolved foods are grouped by
/// ResourceMap tier and food-stat category. Prefab ownership remains
/// presentation-only metadata and therefore appears only as comment headers.
/// </summary>
internal static class ChefTierReferenceGenerator
{
    internal const string ReferenceFileName = "FoodTier.reference.yml";

    private const float ReadyRetrySeconds = 1f;
    private const float FailureRetrySeconds = 5f;
    private const float ExistenceCheckSeconds = 5f;
    private const int OwnerResolutionRetryCount = 3;

    private static bool _dirty = true;
    private static bool _failureLogged;
    private static float _nextAttemptAt;
    private static float _nextExistenceCheckAt;
    private static int _ownerResolutionRetriesRemaining = OwnerResolutionRetryCount;

    private static string ReferenceFilePath =>
        Path.Combine(FineDiningPlugin.ConfigDirectoryPath, ReferenceFileName);

    internal static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (!_dirty)
        {
            if (now < _nextExistenceCheckAt)
            {
                return;
            }

            _nextExistenceCheckAt = now + ExistenceCheckSeconds;
            if (!FineDiningPlugin.IsRuntimeReferenceAuthority || File.Exists(ReferenceFilePath))
            {
                return;
            }

            _dirty = true;
            _ownerResolutionRetriesRemaining = OwnerResolutionRetryCount;
        }

        if (now < _nextAttemptAt)
        {
            return;
        }

        _nextAttemptAt = now + ReadyRetrySeconds;
        if (!FineDiningPlugin.IsRuntimeReferenceAuthority ||
            !ChefFoodTierCatalog.IsReady ||
            !_dirty)
        {
            return;
        }

        if (!TryGenerateCurrentReference(
                out ReferenceGeneration result,
                out string error))
        {
            _nextAttemptAt = Time.realtimeSinceStartup + FailureRetrySeconds;
            if (!_failureLogged)
            {
                _failureLogged = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not generate " + ReferenceFilePath +
                    "; FineDining will retry: " + error);
            }

            return;
        }

        if (result.HasUnknownOwner && _ownerResolutionRetriesRemaining > 0)
        {
            _ownerResolutionRetriesRemaining--;
            _dirty = true;
            _nextAttemptAt = now + FailureRetrySeconds;
        }
        else
        {
            _dirty = false;
            _ownerResolutionRetriesRemaining = 0;
            _nextExistenceCheckAt = now + ExistenceCheckSeconds;
        }

        _failureLogged = false;
        if (result.Changed)
        {
            FineDiningPlugin.Log.LogInfo(
                "Updated generated Chef tier reference with " + result.EntryCount +
                " prefab(s), including " + result.UnassignedCount +
                " unassigned: " + ReferenceFilePath);
        }
    }

    internal static void Invalidate()
    {
        ResetGenerationState(resetFailureLog: false);
    }

    internal static void Reset()
    {
        ResetGenerationState(resetFailureLog: true);
    }

    private static bool TryGenerateCurrentReference(
        out ReferenceGeneration result,
        out string error)
    {
        result = default;
        if (!ChefFoodTierCatalog.IsReady)
        {
            error =
                "The synchronized Chef tier catalog is not ready yet. Wait until world loading finishes.";
            return false;
        }

        try
        {
            IReadOnlyList<ChefFoodTierInfo> catalog = ChefFoodTierCatalog.GetSnapshot();
            FoodPrefabOwnerSnapshot owners = FoodPrefabOwnerResolver.GetSnapshot(
                catalog.Select(entry => entry.PrefabName));
            List<ReferenceEntry> entries = catalog
                .Select(entry => new ReferenceEntry(
                    entry,
                    owners.GetOwnerName(entry.PrefabName)))
                .ToList();
            bool changed = SpoilageReferenceGenerator.WriteTextIfChanged(
                ReferenceFilePath,
                BuildReferenceContent(entries));
            result = new ReferenceGeneration(
                changed,
                entries.Any(entry => entry.OwnerName.Equals(
                    FoodPrefabOwnerResolver.UnknownOwnerName,
                    StringComparison.OrdinalIgnoreCase)),
                entries.Count,
                entries.Count(entry => entry.Info.IsAllTier));
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
    }

    internal static string BuildReferenceContent(IEnumerable<ReferenceEntry> sourceEntries)
    {
        List<ReferenceEntry> entries = (sourceEntries ?? Enumerable.Empty<ReferenceEntry>())
            .Where(entry => entry != null && entry.Info.PrefabName.Length > 0)
            .GroupBy(entry => entry.Info.PrefabName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.OwnerName, StringComparer.Ordinal)
                .First())
            .ToList();

        StringBuilder builder = new();
        builder.Append("# Generated by FineDining ")
            .Append(FineDiningPlugin.ModVersion)
            .AppendLine(". This file is overwritten automatically.");
        builder.AppendLine("# It lists every Chef-eligible food by ResourceMap tier and food-stat category.");
        builder.AppendLine("# Unassigned foods remain eligible but use the neutral AllTier selection weight.");
        builder.AppendLine("# Positive Eitr is eitrFood; otherwise Health <= Stamina is staminaFood, else healthFood.");
        builder.AppendLine("# ResourceMap tiers with no Chef-eligible foods are omitted.");
        builder.AppendLine("# This file is diagnostic only; it is not loaded as configuration.");

        List<ReferenceEntry> unassigned = entries
            .Where(entry => entry.Info.IsAllTier)
            .ToList();
        AppendUnassigned(builder, unassigned);

        HashSet<string> outputKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "unassigned"
        };
        foreach (IGrouping<int, ReferenceEntry> tierGroup in entries
                     .Where(entry => entry.Info.IsResolved)
                     .GroupBy(entry => entry.Info.Tier)
                     .OrderBy(group => group.Key))
        {
            AppendTier(
                builder,
                tierGroup,
                GetUniqueTierOutputName(tierGroup, outputKeys));
        }

        return Canonicalize(builder.ToString());
    }

    private static void AppendUnassigned(
        StringBuilder builder,
        IReadOnlyCollection<ReferenceEntry> entries)
    {
        builder.AppendLine();
        if (entries.Count == 0)
        {
            builder.AppendLine("unassigned: []");
            return;
        }

        builder.AppendLine("unassigned:");
        AppendCounts(builder, "  ", entries);
        foreach (IGrouping<string, ReferenceEntry> ownerGroup in OrderOwnerGroups(entries))
        {
            builder.AppendLine();
            builder.Append("  # ----- ")
                .Append(FoodPrefabOwnerResolver.NormalizeOwnerName(ownerGroup.Key))
                .AppendLine(" -----");
            foreach (ReferenceEntry entry in ownerGroup
                         .OrderBy(item => GetAxisSortOrder(item.Info.Axis))
                         .ThenBy(item => item.Info.PrefabName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Info.PrefabName, StringComparer.Ordinal))
            {
                builder.Append("  - prefab: ")
                    .AppendLine(FormatYamlScalar(entry.Info.PrefabName));
                builder.Append("    foodType: ")
                    .AppendLine(GetFoodTypeLabel(entry.Info.Axis));
                builder.Append("    reason: ")
                    .AppendLine(FormatYamlScalar(entry.Info.FallbackReason));
                if (entry.Info.Sources.Count == 0)
                {
                    builder.AppendLine("    sources: []");
                    continue;
                }

                builder.AppendLine("    sources:");
                foreach (string source in entry.Info.Sources)
                {
                    builder.Append("      - ")
                        .AppendLine(FormatYamlScalar(source));
                }
            }
        }
    }

    private static void AppendTier(
        StringBuilder builder,
        IGrouping<int, ReferenceEntry> tierGroup,
        string tierName)
    {
        List<ReferenceEntry> entries = tierGroup.ToList();
        builder.AppendLine();
        builder.Append(FormatYamlScalar(tierName)).AppendLine(":");
        AppendCounts(builder, "  ", entries);

        foreach (FoodStatAxis axis in OrderedFoodAxes)
        {
            List<ReferenceEntry> categoryEntries = entries
                .Where(entry => entry.Info.Axis == axis)
                .ToList();
            if (categoryEntries.Count == 0)
            {
                continue;
            }

            builder.AppendLine();
            builder.Append("  # --- ")
                .Append(GetFoodTypeLabel(axis))
                .AppendLine(" ---");
            foreach (IGrouping<string, ReferenceEntry> ownerGroup in
                     OrderOwnerGroups(categoryEntries))
            {
                builder.Append("  # ----- ")
                    .Append(FoodPrefabOwnerResolver.NormalizeOwnerName(ownerGroup.Key))
                    .AppendLine(" -----");
                foreach (ReferenceEntry entry in ownerGroup
                             .OrderBy(item => item.Info.PrefabName, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(item => item.Info.PrefabName, StringComparer.Ordinal))
                {
                    builder.Append("  - ")
                        .AppendLine(FormatCompactFood(entry.Info.PrefabName, axis));
                }
            }
        }
    }

    private static string GetUniqueTierOutputName(
        IGrouping<int, ReferenceEntry> tierGroup,
        ISet<string> usedNames)
    {
        string baseName = tierGroup
            .Select(entry => entry.Info.TierName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
            "tier" + tierGroup.Key;
        string candidate = baseName;
        int suffix = 0;
        while (!usedNames.Add(candidate))
        {
            suffix++;
            candidate = baseName + " (tier " + tierGroup.Key +
                        (suffix > 1 ? "-" + suffix : "") + ")";
        }

        return candidate;
    }

    private static IEnumerable<IGrouping<string, ReferenceEntry>> OrderOwnerGroups(
        IEnumerable<ReferenceEntry> entries) =>
        entries
            .GroupBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => FoodPrefabOwnerResolver.GetOwnerSortBucket(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal);

    private static void AppendCounts(
        StringBuilder builder,
        string indentation,
        IEnumerable<ReferenceEntry> entries)
    {
        Dictionary<FoodStatAxis, int> counts = OrderedFoodAxes.ToDictionary(
            axis => axis,
            _ => 0);
        foreach (ReferenceEntry entry in entries)
        {
            if (counts.ContainsKey(entry.Info.Axis))
            {
                counts[entry.Info.Axis]++;
            }
        }

        builder.Append(indentation)
            .Append("# counts: healthFood=").Append(counts[FoodStatAxis.Health])
            .Append(", staminaFood=").Append(counts[FoodStatAxis.Stamina])
            .Append(", eitrFood=").Append(counts[FoodStatAxis.Eitr])
            .AppendLine();
    }

    private static string FormatCompactFood(string prefabName, FoodStatAxis axis)
    {
        string value = (prefabName ?? "") + ", " + GetFoodTypeLabel(axis);
        bool safe = !string.IsNullOrWhiteSpace(prefabName) && prefabName.All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.');
        return safe ? value : FormatYamlScalar(value);
    }

    private static int GetAxisSortOrder(FoodStatAxis axis) =>
        axis switch
        {
            FoodStatAxis.Health => 0,
            FoodStatAxis.Stamina => 1,
            FoodStatAxis.Eitr => 2,
            _ => 3
        };

    private static string GetFoodTypeLabel(FoodStatAxis axis) =>
        axis switch
        {
            FoodStatAxis.Health => "healthFood",
            FoodStatAxis.Stamina => "staminaFood",
            FoodStatAxis.Eitr => "eitrFood",
            _ => "unknownFood"
        };

    private static readonly FoodStatAxis[] OrderedFoodAxes =
    {
        FoodStatAxis.Health,
        FoodStatAxis.Stamina,
        FoodStatAxis.Eitr
    };

    private static void ResetGenerationState(bool resetFailureLog)
    {
        _dirty = true;
        if (resetFailureLog)
        {
            _failureLogged = false;
        }

        _nextAttemptAt = 0f;
        _nextExistenceCheckAt = 0f;
        _ownerResolutionRetriesRemaining = OwnerResolutionRetryCount;
    }

    private static string FormatYamlScalar(string value)
    {
        string text = value ?? "";
        bool safe = text.Length > 0 && text.All(character =>
            char.IsLetterOrDigit(character) || character is '_' or '-' or '.');
        if (safe)
        {
            return text;
        }

        return "\"" + text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"") + "\"";
    }

    private static string Canonicalize(string value) =>
        (value ?? "")
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimEnd('\n') + "\n";

    internal sealed class ReferenceEntry
    {
        internal ReferenceEntry(ChefFoodTierInfo info, string ownerName)
        {
            Info = info;
            OwnerName = FoodPrefabOwnerResolver.NormalizeOwnerName(ownerName);
        }

        internal ChefFoodTierInfo Info { get; }
        internal string OwnerName { get; }
    }

    private readonly struct ReferenceGeneration
    {
        internal ReferenceGeneration(
            bool changed,
            bool hasUnknownOwner,
            int entryCount,
            int unassignedCount)
        {
            Changed = changed;
            HasUnknownOwner = hasUnknownOwner;
            EntryCount = entryCount;
            UnassignedCount = unassignedCount;
        }

        internal bool Changed { get; }
        internal bool HasUnknownOwner { get; }
        internal int EntryCount { get; }
        internal int UnassignedCount { get; }
    }
}
