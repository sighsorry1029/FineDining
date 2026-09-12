using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using UnityEngine;

namespace FineDining;

internal sealed class FoodPrefabOwnerSnapshot
{
    private readonly Dictionary<string, string> _owners;

    internal FoodPrefabOwnerSnapshot(Dictionary<string, string> owners)
    {
        _owners = owners;
    }

    internal string GetOwnerName(string? prefabName)
    {
        string normalized = FoodIdentity.NormalizePrefabName(prefabName);
        return normalized.Length > 0 && _owners.TryGetValue(normalized, out string ownerName)
            ? ownerName
            : FoodPrefabOwnerResolver.UnknownOwnerName;
    }
}

internal static class FoodPrefabOwnerResolver
{
    internal const string VanillaOwnerName = "Valheim";
    internal const string UnknownOwnerName = "Unknown / Untracked";

    private const int MinimumHeuristicTokenLength = 5;
    private static readonly HashSet<string> VanillaPrefabNames =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _vanillaCatalogLoaded;
    private static bool _vanillaCatalogWarningLogged;

    private sealed class PluginSnapshot
    {
        internal string OwnerName { get; set; } = UnknownOwnerName;
        internal string PluginName { get; set; } = "";
        internal string PluginGuid { get; set; } = "";
        internal string AssemblyName { get; set; } = "";
        internal string[] ResourceNames { get; set; } = Array.Empty<string>();
    }

    internal static FoodPrefabOwnerSnapshot GetSnapshot(IEnumerable<string> prefabNames)
    {
        List<string> targets = (prefabNames ?? Enumerable.Empty<string>())
            .Select(FoodIdentity.NormalizePrefabName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();
        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0)
        {
            return new FoodPrefabOwnerSnapshot(owners);
        }

        try
        {
            HashSet<string> targetSet = new(targets, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> jotunnOwners = CollectJotunnOwners(targetSet);
            EnsureVanillaCatalogLoaded();
            Dictionary<string, HashSet<string>> bundleOwners =
                CollectAssetBundleOwners(targetSet, BuildPluginSnapshots());

            foreach (string target in targets)
            {
                string ownerName = ResolveMappedOwner(target, jotunnOwners);
                if (ownerName.Length == 0 && IsVanillaPrefab(target))
                {
                    ownerName = VanillaOwnerName;
                }

                if (ownerName.Length == 0)
                {
                    ownerName = ResolveUniqueBundleOwner(target, bundleOwners);
                }

                owners[target] = ownerName.Length > 0
                    ? NormalizeOwnerName(ownerName)
                    : UnknownOwnerName;
            }
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not resolve one or more food prefab owners; unresolved entries will be grouped under '" +
                UnknownOwnerName + "': " + exception.GetBaseException().Message);
            foreach (string target in targets)
            {
                owners[target] = UnknownOwnerName;
            }
        }

        return new FoodPrefabOwnerSnapshot(owners);
    }

    internal static int GetOwnerSortBucket(string? ownerName)
    {
        string normalized = NormalizeOwnerName(ownerName);
        if (normalized.Equals(VanillaOwnerName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return normalized.Equals(UnknownOwnerName, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }

    internal static string NormalizeOwnerName(string? ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            return UnknownOwnerName;
        }

        StringBuilder builder = new();
        foreach (char character in ownerName!)
        {
            if (character == '\r' || character == '\n')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        string normalized = builder.ToString().Trim();
        return normalized.Length > 0 ? normalized : UnknownOwnerName;
    }

    private static Dictionary<string, string> CollectJotunnOwners(HashSet<string> targets)
    {
        Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> lookupCandidates = new(
            targets.SelectMany(EnumerateLookupCandidates),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var entry in JotunnCompatibility.GetOwners())
            {
                AddJotunnOwner(entry.Prefab, entry.SourceMod, lookupCandidates, owners);
            }
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogDebug(
                "Could not enumerate all Jotunn registry owners: " +
                exception.GetBaseException().Message);
        }

        return owners;
    }

    private static void AddJotunnOwner(
        GameObject? prefab,
        BepInEx.BepInPlugin? sourceMod,
        HashSet<string> lookupCandidates,
        Dictionary<string, string> owners)
    {
        string prefabName = FoodIdentity.NormalizePrefabName(prefab?.name);
        if (prefabName.Length == 0 ||
            !lookupCandidates.Contains(prefabName) ||
            owners.ContainsKey(prefabName))
        {
            return;
        }

        string pluginGuid = (sourceMod?.GUID ?? "").Trim();
        if (pluginGuid.Length > 0 &&
            Chainloader.PluginInfos.TryGetValue(pluginGuid, out var pluginInfo))
        {
            owners[prefabName] = NormalizeOwnerName(
                string.IsNullOrWhiteSpace(pluginInfo.Metadata.Name)
                    ? pluginInfo.Metadata.GUID
                    : pluginInfo.Metadata.Name);
            return;
        }

        string pluginName = (sourceMod?.Name ?? "").Trim();
        string ownerName = NormalizeOwnerName(pluginName.Length > 0 ? pluginName : pluginGuid);
        if (!ownerName.Equals(UnknownOwnerName, StringComparison.OrdinalIgnoreCase))
        {
            owners[prefabName] = ownerName;
        }
    }

    private static Dictionary<string, HashSet<string>> CollectAssetBundleOwners(
        HashSet<string> targets,
        List<PluginSnapshot> plugins)
    {
        Dictionary<string, HashSet<string>> ownersByPrefab =
            new(StringComparer.OrdinalIgnoreCase);
        AssetBundle[] bundles;
        try
        {
            bundles = AssetBundle.GetAllLoadedAssetBundles()
                .Where(bundle => bundle != null)
                .OrderBy(bundle => bundle.name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return ownersByPrefab;
        }

        foreach (AssetBundle bundle in bundles)
        {
            string ownerName = ResolveBundleOwner(bundle.name ?? "", plugins);
            if (ownerName.Length == 0)
            {
                continue;
            }

            string[] assetNames;
            try
            {
                assetNames = bundle.GetAllAssetNames();
            }
            catch
            {
                continue;
            }

            foreach (string assetName in assetNames)
            {
                if (!assetName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefabName = FoodIdentity.NormalizePrefabName(
                    Path.GetFileNameWithoutExtension(assetName));
                if (prefabName.Length == 0 || !targets.Contains(prefabName))
                {
                    continue;
                }

                if (!ownersByPrefab.TryGetValue(prefabName, out HashSet<string> candidateOwners))
                {
                    candidateOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ownersByPrefab.Add(prefabName, candidateOwners);
                }

                candidateOwners.Add(ownerName);
            }
        }

        return ownersByPrefab;
    }

    private static string ResolveBundleOwner(string bundleName, List<PluginSnapshot> plugins)
    {
        string normalizedBundleName = (bundleName ?? "").Trim();
        if (normalizedBundleName.Length == 0)
        {
            return "";
        }

        string[] exactOwners = plugins
            .Where(plugin => plugin.ResourceNames.Any(resourceName =>
                IsBundleResourceMatch(resourceName, normalizedBundleName)))
            .Select(plugin => plugin.OwnerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (exactOwners.Length > 0)
        {
            // An exact but ambiguous resource match is stronger evidence than
            // any name-token heuristic. Leave it unresolved instead of letting
            // the fallback guess a single owner.
            return exactOwners.Length == 1 ? exactOwners[0] : "";
        }

        string bundleToken = NormalizeToken(Path.GetFileNameWithoutExtension(normalizedBundleName));
        if (bundleToken.Length < MinimumHeuristicTokenLength)
        {
            return "";
        }

        string[] heuristicOwners = plugins
            .Where(plugin =>
                IsTokenMatch(bundleToken, NormalizeToken(plugin.PluginName)) ||
                IsTokenMatch(bundleToken, NormalizeToken(plugin.PluginGuid)) ||
                IsTokenMatch(bundleToken, NormalizeToken(plugin.AssemblyName)))
            .Select(plugin => plugin.OwnerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return heuristicOwners.Length == 1 ? heuristicOwners[0] : "";
    }

    private static bool IsBundleResourceMatch(string? resourceName, string bundleName)
    {
        string resource = (resourceName ?? "").Trim();
        return resource.Length > 0 &&
               (resource.Equals(bundleName, StringComparison.OrdinalIgnoreCase) ||
                resource.EndsWith("." + bundleName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTokenMatch(string bundleToken, string pluginToken)
    {
        return pluginToken.Length >= MinimumHeuristicTokenLength &&
               (bundleToken.IndexOf(pluginToken, StringComparison.OrdinalIgnoreCase) >= 0 ||
                pluginToken.IndexOf(bundleToken, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string NormalizeToken(string value)
    {
        StringBuilder builder = new();
        foreach (char character in value ?? "")
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static List<PluginSnapshot> BuildPluginSnapshots()
    {
        List<PluginSnapshot> plugins = new();
        foreach (var pluginInfo in Chainloader.PluginInfos.Values)
        {
            string pluginName = (pluginInfo.Metadata.Name ?? "").Trim();
            string pluginGuid = (pluginInfo.Metadata.GUID ?? "").Trim();
            string assemblyName = "";
            string[] resources = Array.Empty<string>();
            try
            {
                Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
                assemblyName = assembly?.GetName().Name ?? "";
                resources = assembly?.GetManifestResourceNames() ?? Array.Empty<string>();
            }
            catch
            {
                // A partially initialized plugin is not a fatal owner lookup failure.
            }

            plugins.Add(new PluginSnapshot
            {
                OwnerName = NormalizeOwnerName(pluginName.Length > 0 ? pluginName : pluginGuid),
                PluginName = pluginName,
                PluginGuid = pluginGuid,
                AssemblyName = assemblyName,
                ResourceNames = resources
            });
        }

        return plugins;
    }

    private static string ResolveMappedOwner(
        string prefabName,
        IReadOnlyDictionary<string, string> mappings)
    {
        foreach (string candidate in EnumerateLookupCandidates(prefabName))
        {
            if (mappings.TryGetValue(candidate, out string ownerName) && ownerName.Length > 0)
            {
                return ownerName;
            }
        }

        return "";
    }

    private static string ResolveUniqueBundleOwner(
        string prefabName,
        IReadOnlyDictionary<string, HashSet<string>> mappings)
    {
        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string lookupName in EnumerateLookupCandidates(prefabName))
        {
            if (mappings.TryGetValue(lookupName, out HashSet<string> owners))
            {
                candidates.UnionWith(owners);
            }
        }

        return candidates.Count == 1 ? candidates.First() : "";
    }

    private static bool IsVanillaPrefab(string prefabName)
    {
        return EnumerateLookupCandidates(prefabName).Any(VanillaPrefabNames.Contains);
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string prefabName)
    {
        string normalized = FoodIdentity.NormalizePrefabName(prefabName);
        if (normalized.Length == 0)
        {
            yield break;
        }

        yield return normalized;
        int aliasSeparator = normalized.IndexOf(':');
        if (aliasSeparator > 0)
        {
            yield return normalized.Substring(0, aliasSeparator);
        }
    }

    private static void EnsureVanillaCatalogLoaded()
    {
        if (_vanillaCatalogLoaded)
        {
            return;
        }

        string manifestDirectory = Path.Combine(
            Application.dataPath,
            "StreamingAssets",
            "SoftRef");
        string[] manifestPaths =
        {
            Path.Combine(manifestDirectory, "manifest"),
            Path.Combine(manifestDirectory, "manifest_extended")
        };
        int filesFound = 0;
        int filesRead = 0;
        const string marker = "path in bundle:";
        foreach (string path in manifestPaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            filesFound++;
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    int markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (markerIndex < 0)
                    {
                        continue;
                    }

                    string assetPath = line.Substring(markerIndex + marker.Length).Trim();
                    if (!assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string prefabName = FoodIdentity.NormalizePrefabName(
                        Path.GetFileNameWithoutExtension(assetPath));
                    if (prefabName.Length > 0)
                    {
                        VanillaPrefabNames.Add(prefabName);
                    }
                }

                filesRead++;
            }
            catch (Exception exception)
            {
                WarnVanillaCatalogOnce(
                    "Could not read vanilla prefab manifest '" + path + "': " +
                    exception.GetBaseException().Message);
            }
        }

        _vanillaCatalogLoaded = filesRead > 0;
        if (filesFound == 0)
        {
            WarnVanillaCatalogOnce(
                "Vanilla prefab manifests were not found under '" + manifestDirectory +
                "'; vanilla reference entries may be grouped under '" + UnknownOwnerName + "'.");
        }
    }

    private static void WarnVanillaCatalogOnce(string message)
    {
        if (_vanillaCatalogWarningLogged)
        {
            return;
        }

        _vanillaCatalogWarningLogged = true;
        FineDiningPlugin.Log.LogWarning(message);
    }
}
