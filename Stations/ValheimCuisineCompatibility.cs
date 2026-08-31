using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace FineDining;

internal static class ValheimCuisineCompatibility
{
    internal const string PluginGuid = "XutzBR.ValheimCuisine";
    internal const string GrimpyConverterTypeName =
        "ValheimCuisine.ValheimCuisinePlugin+GrimpyBoxConverter";
    internal const string FreydisCollectorTypeName =
        "ValheimCuisine.ValheimCuisinePlugin+FreydisCollector";

    private const string GrimpyConversionItemsFieldName =
        "GrimpyBoxConversionItems";
    private const string FreydisSecondsPerUnitFieldName = "m_secPerUnit";
    private const string FreydisMaximumLevelFieldName = "m_maxLevel";
    private const float CandidateCacheSeconds = 0.2f;

    private static Type? _grimpyConverterType;
    private static Type? _freydisCollectorType;
    private static FieldInfo? _grimpyConversionItemsField;
    private static FieldInfo? _freydisSecondsPerUnitField;
    private static FieldInfo? _freydisMaximumLevelField;
    private static bool _initialized;
    private static bool _grimpyBroken;
    private static bool _freydisBroken;

    private static string? _grimpyRecipeSource;
    private static Dictionary<string, GrimpyRecipe> _grimpyRecipes =
        new(StringComparer.Ordinal);
    private static Component? _cachedGrimpyConverter;
    private static float _grimpyCacheExpiresAt;
    private static IReadOnlyList<StationHintCandidate> _cachedGrimpyCandidates =
        Array.Empty<StationHintCandidate>();
    private static int _cachedGrimpyMaxHints;

    internal static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (!Chainloader.PluginInfos.TryGetValue(
                PluginGuid,
                out BepInEx.PluginInfo pluginInfo) ||
            pluginInfo.Instance == null)
        {
            return;
        }

        Assembly assembly = pluginInfo.Instance.GetType().Assembly;
        Type pluginType = pluginInfo.Instance.GetType();
        try
        {
            InitializeGrimpy(assembly, pluginType);
        }
        catch (Exception exception)
        {
            DisableGrimpy(
                "ValheimCuisine Grimpy Box compatibility could not be initialized.",
                exception);
        }

        try
        {
            InitializeFreydis(assembly);
        }
        catch (Exception exception)
        {
            DisableFreydis(
                "ValheimCuisine Freydis compatibility could not be initialized.",
                exception);
        }

        if (!_grimpyBroken || !_freydisBroken)
        {
            FineDiningPlugin.Log.LogInfo(
                "Enabled optional ValheimCuisine " + pluginInfo.Metadata.Version +
                " station-hover compatibility.");
        }
    }

    private static void InitializeGrimpy(Assembly assembly, Type pluginType)
    {
        _grimpyConverterType = ValidateComponentType(
            assembly.GetType(GrimpyConverterTypeName, throwOnError: false));
        _grimpyConversionItemsField = pluginType.GetField(
            GrimpyConversionItemsFieldName,
            BindingFlags.Public | BindingFlags.Static);
        if (_grimpyConverterType != null
            && _grimpyConversionItemsField?.FieldType == typeof(ConfigEntry<string>))
        {
            return;
        }

        _grimpyConverterType = null;
        _grimpyConversionItemsField = null;
        _grimpyBroken = true;
        FineDiningPlugin.Log.LogWarning(
            "ValheimCuisine is installed, but its Grimpy Box conversion contract was not found. " +
            "Grimpy Box hover icons are disabled.");
    }

    private static void InitializeFreydis(Assembly assembly)
    {
        _freydisCollectorType = ValidateComponentType(
            assembly.GetType(FreydisCollectorTypeName, throwOnError: false));
        _freydisSecondsPerUnitField = _freydisCollectorType?.GetField(
            FreydisSecondsPerUnitFieldName,
            BindingFlags.Public | BindingFlags.Instance);
        _freydisMaximumLevelField = _freydisCollectorType?.GetField(
            FreydisMaximumLevelFieldName,
            BindingFlags.Public | BindingFlags.Instance);
        if (_freydisCollectorType != null
            && _freydisSecondsPerUnitField?.FieldType == typeof(float)
            && _freydisMaximumLevelField?.FieldType == typeof(int))
        {
            return;
        }

        _freydisCollectorType = null;
        _freydisSecondsPerUnitField = null;
        _freydisMaximumLevelField = null;
        _freydisBroken = true;
        FineDiningPlugin.Log.LogWarning(
            "ValheimCuisine is installed, but its Freydis collector contract was not found. " +
            "Freydis hover timing is disabled.");
    }

    internal static void Shutdown()
    {
        _grimpyConverterType = null;
        _freydisCollectorType = null;
        _grimpyConversionItemsField = null;
        _freydisSecondsPerUnitField = null;
        _freydisMaximumLevelField = null;
        _initialized = false;
        _grimpyBroken = false;
        _freydisBroken = false;
        _grimpyRecipeSource = null;
        _grimpyRecipes.Clear();
        ResetGrimpyCandidateCache();
    }

    internal static bool TryShow(
        Hud hud,
        GameObject hoverObject,
        Hoverable hoverable,
        Player player)
    {
        if (!_initialized)
        {
            Initialize();
        }

        Component? freydis = FindComponentInParents(
            hoverObject,
            hoverable,
            _freydisCollectorType);
        if (freydis != null)
        {
            ShowFreydis(hud, freydis);
            return true;
        }

        if (hoverable is Container container && _grimpyConverterType != null)
        {
            Component? grimpy = container.GetComponent(_grimpyConverterType);
            if (grimpy != null)
            {
                ShowGrimpy(grimpy, container, player);
                return true;
            }
        }

        return false;
    }

    internal static double CalculateFreydisRemainingSeconds(
        double secondsPerUnit,
        double accumulatedSeconds,
        long lastUpdateTicks,
        long serverNowTicks)
    {
        if (double.IsNaN(secondsPerUnit) ||
            double.IsInfinity(secondsPerUnit) ||
            secondsPerUnit <= 0d)
        {
            return double.NaN;
        }

        if (double.IsNaN(accumulatedSeconds) ||
            double.IsInfinity(accumulatedSeconds) ||
            accumulatedSeconds < 0d)
        {
            accumulatedSeconds = 0d;
        }

        double elapsedSeconds = 0d;
        if (lastUpdateTicks > 0L && serverNowTicks > lastUpdateTicks)
        {
            elapsedSeconds = (serverNowTicks - lastUpdateTicks) /
                             (double)TimeSpan.TicksPerSecond;
        }

        double pendingSeconds = accumulatedSeconds + elapsedSeconds;
        return pendingSeconds >= secondsPerUnit
            ? 0d
            : secondsPerUnit - pendingSeconds;
    }

    internal static bool TryParseGrimpyRecipe(
        string? text,
        out string prefabName,
        out int requiredAmount,
        out int producedAmount)
    {
        prefabName = string.Empty;
        requiredAmount = 0;
        producedAmount = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text!.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        prefabName = parts[0].Trim();
        return prefabName.Length > 0 &&
               int.TryParse(
                   parts[1].Trim(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out requiredAmount) &&
               int.TryParse(
                   parts[2].Trim(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out producedAmount) &&
               requiredAmount > 0 &&
               producedAmount > 0;
    }

    private static Type? ValidateComponentType(Type? type) =>
        type != null && typeof(Component).IsAssignableFrom(type)
            ? type
            : null;

    private static Component? FindComponentInParents(
        GameObject hoverObject,
        Hoverable hoverable,
        Type? componentType)
    {
        if (componentType == null)
        {
            return null;
        }

        if (hoverable is Component hoverComponent &&
            componentType.IsInstanceOfType(hoverComponent))
        {
            return hoverComponent;
        }

        Transform? current = hoverObject.transform;
        while (current != null)
        {
            Component? component = current.GetComponent(componentType);
            if (component != null)
            {
                return component;
            }

            current = current.parent;
        }

        return null;
    }

    private static void ShowGrimpy(
        Component converter,
        Container container,
        Player player)
    {
        if (_grimpyBroken)
        {
            return;
        }

        try
        {
            int maxHints = StationModule.GetHintLimit(
                StationModule.GrimpyBoxRows.Value);
            if (maxHints <= 0)
            {
                return;
            }

            IReadOnlyList<StationHintCandidate> candidates =
                GetGrimpyCandidates(converter, container, player, maxHints);
            if (candidates.Count > 0)
            {
                StationHintUi.Show(candidates);
            }
        }
        catch (Exception exception)
        {
            DisableGrimpy("Grimpy Box hover icons failed.", exception);
        }
    }

    private static IReadOnlyList<StationHintCandidate> GetGrimpyCandidates(
        Component converter,
        Container container,
        Player player,
        int maxHints)
    {
        if (_cachedGrimpyConverter == converter &&
            _cachedGrimpyMaxHints == maxHints &&
            Time.unscaledTime < _grimpyCacheExpiresAt)
        {
            return _cachedGrimpyCandidates;
        }

        RefreshGrimpyRecipes();
        List<StationHintCandidate> candidates = new(maxHints);
        if (_grimpyRecipes.Count > 0)
        {
            Dictionary<string, int> containedAmounts =
                new(StringComparer.Ordinal);
            Dictionary<string, ItemDrop.ItemData> sampleItems =
                new(StringComparer.Ordinal);
            List<string> orderedPrefabs = new();
            HashSet<string> seen = new(StringComparer.Ordinal);

            AddGrimpyItems(
                container.GetInventory()?.GetAllItems(),
                containedAmounts,
                sampleItems,
                orderedPrefabs,
                seen,
                countAsContained: true);
            AddGrimpyItems(
                player.GetInventory()?.GetAllItems(),
                containedAmounts,
                sampleItems,
                orderedPrefabs,
                seen,
                countAsContained: false);

            string ectoplasmName = GetLocalizedItemName("Ectoplasm", "Ectoplasm");
            foreach (string prefabName in orderedPrefabs)
            {
                if (candidates.Count >= maxHints ||
                    !_grimpyRecipes.TryGetValue(prefabName, out GrimpyRecipe recipe) ||
                    !sampleItems.TryGetValue(prefabName, out ItemDrop.ItemData sample))
                {
                    continue;
                }

                int contained = containedAmounts.TryGetValue(prefabName, out int amount)
                    ? amount
                    : 0;
                candidates.Add(new StationHintCandidate(
                    GetLocalizedItemName(sample),
                    GetItemIcon(sample),
                    contained.ToString(CultureInfo.InvariantCulture) + "/" +
                    recipe.RequiredAmount.ToString(CultureInfo.InvariantCulture),
                    FineDiningLocalization.LocalizeOrFallback(
                        "$finedining_station_grimpy_output",
                        "→ $1 ×$2",
                        ectoplasmName,
                        recipe.ProducedAmount.ToString(CultureInfo.InvariantCulture))));
            }
        }

        _cachedGrimpyConverter = converter;
        _cachedGrimpyMaxHints = maxHints;
        _grimpyCacheExpiresAt = Time.unscaledTime + CandidateCacheSeconds;
        _cachedGrimpyCandidates = candidates;
        return candidates;
    }

    private static void AddGrimpyItems(
        IReadOnlyList<ItemDrop.ItemData>? items,
        IDictionary<string, int> containedAmounts,
        IDictionary<string, ItemDrop.ItemData> sampleItems,
        ICollection<string> orderedPrefabs,
        ISet<string> seen,
        bool countAsContained)
    {
        if (items == null)
        {
            return;
        }

        foreach (ItemDrop.ItemData item in items)
        {
            string prefabName = FoodIdentity.GetCanonicalPrefabName(item);
            if (!_grimpyRecipes.ContainsKey(prefabName))
            {
                continue;
            }

            sampleItems[prefabName] = item;
            if (seen.Add(prefabName))
            {
                orderedPrefabs.Add(prefabName);
            }

            if (!countAsContained || item.m_stack <= 0)
            {
                continue;
            }

            long total = containedAmounts.TryGetValue(prefabName, out int current)
                ? (long)current + item.m_stack
                : item.m_stack;
            containedAmounts[prefabName] = (int)Math.Min(int.MaxValue, total);
        }
    }

    private static void RefreshGrimpyRecipes()
    {
        ConfigEntry<string>? entry =
            _grimpyConversionItemsField?.GetValue(null) as ConfigEntry<string>;
        if (entry == null)
        {
            throw new InvalidOperationException(
                "ValheimCuisine GrimpyBoxConversionItems is unavailable.");
        }

        string source = entry.Value ?? string.Empty;
        if (string.Equals(source, _grimpyRecipeSource, StringComparison.Ordinal))
        {
            return;
        }

        Dictionary<string, GrimpyRecipe> recipes =
            new(StringComparer.Ordinal);
        foreach (string part in source.Split(','))
        {
            if (TryParseGrimpyRecipe(
                    part,
                    out string prefabName,
                    out int requiredAmount,
                    out int producedAmount))
            {
                recipes[prefabName] = new GrimpyRecipe(
                    requiredAmount,
                    producedAmount);
            }
        }

        _grimpyRecipeSource = source;
        _grimpyRecipes = recipes;
        ResetGrimpyCandidateCache();
    }

    private static void ShowFreydis(Hud hud, Component collector)
    {
        if (_freydisBroken || hud.m_hoverName == null)
        {
            return;
        }

        try
        {
            object? secondsValue = _freydisSecondsPerUnitField?.GetValue(collector);
            object? maximumValue = _freydisMaximumLevelField?.GetValue(collector);
            if (secondsValue is not float secondsPerUnit ||
                maximumValue is not int maximumLevel ||
                float.IsNaN(secondsPerUnit) ||
                float.IsInfinity(secondsPerUnit) ||
                secondsPerUnit <= 0f ||
                maximumLevel <= 0)
            {
                return;
            }

            ZNetView? view = collector.GetComponent<ZNetView>();
            ZDO? zdo = view?.GetZDO();
            if (zdo == null || ZNet.instance == null)
            {
                return;
            }

            int level = Mathf.Clamp(
                zdo.GetInt(ZDOVars.s_level, 0),
                0,
                maximumLevel);
            string line;
            if (level >= maximumLevel)
            {
                line = FineDiningLocalization.LocalizeOrFallback(
                    "$finedining_station_freydis_full",
                    "<color=#9FE870>Collection storage full $1/$2</color>",
                    level.ToString(CultureInfo.InvariantCulture),
                    maximumLevel.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                long nowTicks = ZNet.instance.GetTime().Ticks;
                double remainingSeconds = CalculateFreydisRemainingSeconds(
                    secondsPerUnit,
                    zdo.GetFloat(ZDOVars.s_product, 0f),
                    zdo.GetLong(ZDOVars.s_lastTime, nowTicks),
                    nowTicks);
                string duration = StationText.FormatDuration(remainingSeconds);
                if (duration.Length == 0)
                {
                    return;
                }

                line = FineDiningLocalization.LocalizeOrFallback(
                    "$finedining_station_freydis_progress",
                    "Stored $1/$2 · next collection $3",
                    level.ToString(CultureInfo.InvariantCulture),
                    maximumLevel.ToString(CultureInfo.InvariantCulture),
                    StationText.ColorizeTimer(duration));
            }

            AppendHoverLine(hud, line);
        }
        catch (Exception exception)
        {
            DisableFreydis("Freydis hover timing failed.", exception);
        }
    }

    private static string GetLocalizedItemName(ItemDrop.ItemData item)
    {
        string sharedName = item.m_shared?.m_name ?? string.Empty;
        return Localization.instance != null && sharedName.Length > 0
            ? Localization.instance.Localize(sharedName)
            : sharedName;
    }

    private static string GetLocalizedItemName(
        string prefabName,
        string fallback)
    {
        ItemDrop.ItemData? item = ObjectDB.instance?
            .GetItemPrefab(prefabName)?
            .GetComponent<ItemDrop>()?
            .m_itemData;
        string localized = item != null
            ? GetLocalizedItemName(item)
            : string.Empty;
        return string.IsNullOrWhiteSpace(localized)
            ? fallback
            : localized;
    }

    private static Sprite? GetItemIcon(ItemDrop.ItemData item)
    {
        Sprite[]? icons = item.m_shared?.m_icons;
        return icons != null && icons.Length > 0
            ? icons[0]
            : null;
    }

    private static void AppendHoverLine(Hud hud, string line)
    {
        string current = hud.m_hoverName.text ?? string.Empty;
        if (ContainsLine(current, line))
        {
            return;
        }

        hud.m_hoverName.text = current.Length == 0
            ? line
            : current + (current.EndsWith("\n", StringComparison.Ordinal) ? string.Empty : "\n") + line;
    }

    private static bool ContainsLine(string text, string line)
    {
        if (string.Equals(text, line, StringComparison.Ordinal))
        {
            return true;
        }

        string prefixedLine = "\n" + line;
        int index = text.IndexOf(prefixedLine, StringComparison.Ordinal);
        return index >= 0 &&
               (index + prefixedLine.Length == text.Length ||
                text[index + prefixedLine.Length] == '\n');
    }

    private static void ResetGrimpyCandidateCache()
    {
        _cachedGrimpyConverter = null;
        _cachedGrimpyMaxHints = 0;
        _grimpyCacheExpiresAt = 0f;
        _cachedGrimpyCandidates = Array.Empty<StationHintCandidate>();
    }

    private static void DisableGrimpy(string message, Exception exception)
    {
        _grimpyBroken = true;
        _grimpyConverterType = null;
        _grimpyConversionItemsField = null;
        _grimpyRecipeSource = null;
        _grimpyRecipes.Clear();
        ResetGrimpyCandidateCache();
        FineDiningPlugin.Log.LogWarning(
            message + " " + exception.GetBaseException().Message);
    }

    private static void DisableFreydis(string message, Exception exception)
    {
        _freydisBroken = true;
        _freydisCollectorType = null;
        _freydisSecondsPerUnitField = null;
        _freydisMaximumLevelField = null;
        FineDiningPlugin.Log.LogWarning(
            message + " " + exception.GetBaseException().Message);
    }

    private sealed class GrimpyRecipe
    {
        internal GrimpyRecipe(int requiredAmount, int producedAmount)
        {
            RequiredAmount = requiredAmount;
            ProducedAmount = producedAmount;
        }

        internal int RequiredAmount { get; }
        internal int ProducedAmount { get; }
    }
}
