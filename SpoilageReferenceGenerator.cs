using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace FineDining;

internal enum SpoilageReferenceSection
{
    FarmingHarvest,
    CookingStationInput,
    CookingStationOutput,
    FermentedFood,
    FeastMaterial,
    FeastResult,
    Fish,
    OtherEdible,
    OverrideEnabled,
    OverrideDisabled
}

internal sealed class SpoilageReferenceEntry
{
    internal SpoilageReferenceEntry(
        string prefabName,
        string ownerName,
        SpoilageReferenceSection section,
        double lifetimeHours,
        string replacementPrefab)
    {
        PrefabName = FoodClassifier.CleanPrefabName(prefabName);
        OwnerName = FoodPrefabOwnerResolver.NormalizeOwnerName(ownerName);
        Section = section;
        LifetimeHours = lifetimeHours <= 0d ? 0d : lifetimeHours;
        ReplacementPrefab = FoodClassifier.CleanPrefabName(replacementPrefab);
    }

    internal string PrefabName { get; }
    internal string OwnerName { get; }
    internal SpoilageReferenceSection Section { get; }
    internal double LifetimeHours { get; }
    internal string ReplacementPrefab { get; }
}

internal static class SpoilageReferenceGenerator
{
    internal const string ReferenceFileName = "FineDining.reference.yml";

    private const float ReadyRetrySeconds = 1f;
    private const float FailureRetrySeconds = 5f;
    private const float ExistenceCheckSeconds = 5f;
    private const int OwnerResolutionRetryCount = 3;
    private static readonly (SpoilageReferenceSection Section, string Label)[] SectionOrder =
    {
        (SpoilageReferenceSection.FarmingHarvest, "automatic: farmingHarvest"),
        (SpoilageReferenceSection.FeastMaterial, "automatic: feastMaterial"),
        (SpoilageReferenceSection.FeastResult, "automatic: feastResult"),
        (SpoilageReferenceSection.FermentedFood, "automatic: fermentedFood"),
        (SpoilageReferenceSection.CookingStationOutput, "automatic: cookingStationOutput"),
        (SpoilageReferenceSection.CookingStationInput, "automatic: cookingStationInput"),
        (SpoilageReferenceSection.Fish, "automatic: fish"),
        (SpoilageReferenceSection.OtherEdible, "automatic: otherEdible"),
        (SpoilageReferenceSection.OverrideEnabled, "exact overrides: enabled"),
        (SpoilageReferenceSection.OverrideDisabled, "exact overrides: disabled")
    };

    private static bool _dirty = true;
    private static bool _failureLogged;
    private static float _nextAttemptAt;
    private static float _nextExistenceCheckAt;
    private static int _ownerResolutionRetriesRemaining = OwnerResolutionRetryCount;

    private static string ReferenceFilePath =>
        Path.Combine(SpoilagePolicy.ConfigDirectoryPath, ReferenceFileName);

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
            if (!SpoilagePolicy.IsRuntimeReferenceAuthority || File.Exists(ReferenceFilePath))
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
        if (!SpoilagePolicy.IsRuntimeReferenceAuthority ||
            !SpoilagePolicy.IsReady ||
            !FoodClassifier.IsReady ||
            !_dirty)
        {
            return;
        }

        try
        {
            List<SpoilageReferenceEntry> entries = CaptureReferenceEntries();
            string content = BuildReferenceContent(entries);
            bool wrote = WriteTextIfChanged(ReferenceFilePath, content);
            bool hasUnknownOwner = entries.Any(entry => entry.OwnerName.Equals(
                FoodPrefabOwnerResolver.UnknownOwnerName,
                StringComparison.OrdinalIgnoreCase));
            if (hasUnknownOwner && _ownerResolutionRetriesRemaining > 0)
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
            if (wrote)
            {
                FineDiningPlugin.Log.LogInfo(
                    "Updated generated spoilage reference with " + entries.Count +
                    " classified prefab(s): " + ReferenceFilePath);
            }
        }
        catch (Exception exception)
        {
            _nextAttemptAt = Time.realtimeSinceStartup + FailureRetrySeconds;
            if (!_failureLogged)
            {
                _failureLogged = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not generate " + ReferenceFilePath + "; FineDining will retry: " +
                    exception.GetBaseException().Message);
            }
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

    internal static string BuildReferenceContent(IEnumerable<SpoilageReferenceEntry> sourceEntries)
    {
        List<SpoilageReferenceEntry> entries = NormalizeFinalEntries(sourceEntries).ToList();
        StringBuilder builder = new();
        builder.Append("# Generated by FineDining ")
            .Append(FineDiningPlugin.ModVersion)
            .AppendLine(". This file is overwritten automatically.");
        builder.AppendLine("# It is a local lookup only; it is not loaded as configuration or synchronized to clients.");
        builder.AppendLine("# Copy selected '- Prefab, hours[, replacement]' rows under 'overrides:' in FineDining.yml.");
        builder.AppendLine("# Classification is primary; prefab owner is the secondary comment section.");

        foreach ((SpoilageReferenceSection section, string label) in SectionOrder)
        {
            builder.AppendLine();
            builder.Append("# ===== ").Append(label).AppendLine(" =====");
            List<SpoilageReferenceEntry> sectionEntries = entries
                .Where(entry => entry.Section == section)
                .OrderBy(entry => FoodPrefabOwnerResolver.GetOwnerSortBucket(entry.OwnerName))
                .ThenBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.OwnerName, StringComparer.Ordinal)
                .ThenBy(entry => entry.PrefabName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.PrefabName, StringComparer.Ordinal)
                .ToList();
            if (sectionEntries.Count == 0)
            {
                builder.AppendLine("# (none)");
                continue;
            }

            foreach (IGrouping<string, SpoilageReferenceEntry> ownerSection in sectionEntries
                         .GroupBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append("# ----- ")
                    .Append(FoodPrefabOwnerResolver.NormalizeOwnerName(ownerSection.Key))
                    .AppendLine(" -----");
                foreach (SpoilageReferenceEntry entry in ownerSection)
                {
                    builder.Append("- ")
                        .Append(FormatYamlScalar(FormatCompactOverride(entry)))
                        .AppendLine();
                }
            }
        }

        if (entries.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("[]");
        }

        return Canonicalize(builder.ToString());
    }

    internal static bool WriteTextIfChanged(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string normalized = Canonicalize(content ?? "");
        if (File.Exists(path) &&
            string.Equals(File.ReadAllText(path), normalized, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(path, normalized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static List<SpoilageReferenceEntry> CaptureReferenceEntries()
    {
        ObjectDB objectDb = ObjectDB.instance;
        if (objectDb?.m_items == null)
        {
            throw new InvalidOperationException("ObjectDB item prefabs are not ready.");
        }

        Dictionary<string, ItemDrop> itemDrops = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject prefab in objectDb.m_items)
        {
            if (prefab == null)
            {
                continue;
            }

            ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop?.m_itemData?.m_shared == null)
            {
                continue;
            }

            string prefabName = FoodClassifier.CleanPrefabName(prefab.name);
            if (prefabName.Length > 0 && !itemDrops.ContainsKey(prefabName))
            {
                itemDrops.Add(prefabName, itemDrop);
            }
        }

        List<(string PrefabName,
            SpoilageReferenceSection Section,
            double LifetimeHours,
            string ReplacementPrefab)> candidates = new();
        HashSet<string> capturedPrefabs = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ItemDrop> pair in itemDrops)
        {
            ResolvedSpoilageRule rule = SpoilagePolicy.Resolve(pair.Value.m_itemData);
            if (rule.State is not (SpoilageRuleState.Enabled or SpoilageRuleState.Disabled))
            {
                continue;
            }

            if (!SpoilagePolicy.TryGetReferenceLifetimeHours(
                    pair.Key,
                    rule.Group,
                    rule.IsOverride,
                    out double lifetimeHours))
            {
                throw new InvalidOperationException(
                    "Could not resolve normalized lifetime hours for '" + pair.Key + "'.");
            }

            candidates.Add((
                FoodClassifier.CleanPrefabName(pair.Key),
                GetReferenceSection(rule),
                lifetimeHours <= 0d ? 0d : lifetimeHours,
                FoodClassifier.CleanPrefabName(
                    rule.State == SpoilageRuleState.Enabled ? rule.ReplacementPrefab : "")));
            capturedPrefabs.Add(pair.Key);
        }

        if (!SpoilagePolicy.TryGetReferenceOverrides(out List<SpoilagePolicyReferenceOverride> overrides))
        {
            throw new InvalidOperationException("The normalized spoilage policy is not ready.");
        }

        foreach (SpoilagePolicyReferenceOverride itemOverride in overrides)
        {
            if (capturedPrefabs.Contains(itemOverride.PrefabName))
            {
                continue;
            }

            candidates.Add((
                FoodClassifier.CleanPrefabName(itemOverride.PrefabName),
                itemOverride.LifetimeTicks > 0L
                    ? SpoilageReferenceSection.OverrideEnabled
                    : SpoilageReferenceSection.OverrideDisabled,
                itemOverride.Hours <= 0d ? 0d : itemOverride.Hours,
                FoodClassifier.CleanPrefabName(
                    itemOverride.LifetimeTicks > 0L ? itemOverride.ReplacementPrefab : "")));
        }

        FoodPrefabOwnerSnapshot ownerSnapshot = FoodPrefabOwnerResolver.GetSnapshot(
            candidates.Select(candidate => candidate.PrefabName));
        return candidates
            .Select(candidate => new SpoilageReferenceEntry(
                candidate.PrefabName,
                ownerSnapshot.GetOwnerName(candidate.PrefabName),
                candidate.Section,
                candidate.LifetimeHours,
                candidate.ReplacementPrefab))
            .ToList();
    }

    private static IEnumerable<SpoilageReferenceEntry> NormalizeFinalEntries(
        IEnumerable<SpoilageReferenceEntry> sourceEntries)
    {
        return (sourceEntries ?? Enumerable.Empty<SpoilageReferenceEntry>())
            .Where(entry => entry != null && entry.PrefabName.Length > 0)
            .GroupBy(entry => entry.PrefabName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(entry => GetOverridePriority(entry.Section))
                .ThenBy(entry => GetSectionIndex(entry.Section))
                .ThenBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.OwnerName, StringComparer.Ordinal)
                .ThenBy(entry => entry.LifetimeHours)
                .ThenBy(entry => entry.ReplacementPrefab, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ReplacementPrefab, StringComparer.Ordinal)
                .ThenBy(entry => entry.PrefabName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.PrefabName, StringComparer.Ordinal)
                .First());
    }

    private static int GetOverridePriority(SpoilageReferenceSection section)
    {
        return section switch
        {
            SpoilageReferenceSection.OverrideDisabled => 2,
            SpoilageReferenceSection.OverrideEnabled => 1,
            _ => 0
        };
    }

    private static int GetSectionIndex(SpoilageReferenceSection section)
    {
        for (int index = 0; index < SectionOrder.Length; index++)
        {
            if (SectionOrder[index].Section == section)
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static SpoilageReferenceSection GetReferenceSection(ResolvedSpoilageRule rule)
    {
        if (rule.IsOverride)
        {
            return rule.State == SpoilageRuleState.Disabled
                ? SpoilageReferenceSection.OverrideDisabled
                : SpoilageReferenceSection.OverrideEnabled;
        }

        return rule.Group switch
        {
            SpoilageGroup.FarmingHarvest => SpoilageReferenceSection.FarmingHarvest,
            SpoilageGroup.CookingStationInput => SpoilageReferenceSection.CookingStationInput,
            SpoilageGroup.CookingStationOutput => SpoilageReferenceSection.CookingStationOutput,
            SpoilageGroup.FermentedFood => SpoilageReferenceSection.FermentedFood,
            SpoilageGroup.FeastMaterial => SpoilageReferenceSection.FeastMaterial,
            SpoilageGroup.FeastResult => SpoilageReferenceSection.FeastResult,
            SpoilageGroup.Fish => SpoilageReferenceSection.Fish,
            SpoilageGroup.OtherEdible => SpoilageReferenceSection.OtherEdible,
            _ => throw new ArgumentOutOfRangeException(
                nameof(rule.Group),
                rule.Group,
                "Unknown spoilage group.")
        };
    }

    private static string FormatCompactOverride(SpoilageReferenceEntry entry)
    {
        string tuple = entry.PrefabName + ", " + FormatHours(entry.LifetimeHours);
        return entry.LifetimeHours > 0d && entry.ReplacementPrefab.Length > 0
            ? tuple + ", " + entry.ReplacementPrefab
            : tuple;
    }

    private static string FormatHours(double lifetimeHours)
    {
        if (lifetimeHours <= 0d)
        {
            return "0";
        }

        return lifetimeHours.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatYamlScalar(string value)
    {
        bool safe = value.Length > 0 && value.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '_' or '-' or '.' or ',' or ' ');
        if (safe)
        {
            return value;
        }

        return "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"") + "\"";
    }

    private static string Canonicalize(string value)
    {
        return (value ?? "")
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimEnd('\n') + "\n";
    }

}
