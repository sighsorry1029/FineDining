using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

internal static class StationInputResolver
{
    private const float CandidateCacheSeconds = 0.2f;

    // Fermenter.Status is private in the runtime game assembly.
    private enum FermenterStatus
    {
        Empty,
        Fermenting,
        Exposed,
        Ready
    }

    private static readonly MethodInfo? CookingStationIsFireLitMethod =
        AccessTools.Method(typeof(CookingStation), "IsFireLit");
    private static readonly MethodInfo? CookingStationGetFreeSlotMethod =
        AccessTools.Method(typeof(CookingStation), "GetFreeSlot");
    private static readonly MethodInfo? CookingStationGetFuelMethod =
        AccessTools.Method(typeof(CookingStation), "GetFuel");
    private static readonly MethodInfo? SmelterGetFuelMethod =
        AccessTools.Method(typeof(Smelter), "GetFuel");
    private static readonly MethodInfo? SmelterGetQueueSizeMethod =
        AccessTools.Method(typeof(Smelter), "GetQueueSize");
    private static readonly MethodInfo? FermenterGetStatusMethod =
        AccessTools.Method(typeof(Fermenter), "GetStatus");
    private static readonly FieldInfo? FermenterHasRoofField =
        AccessTools.Field(typeof(Fermenter), "m_hasRoof");
    private static readonly FieldInfo? FermenterExposedField =
        AccessTools.Field(typeof(Fermenter), "m_exposed");
    private static readonly Dictionary<string, float> LastDiagnosticLogTimes = new();

    private static Component? _cachedStation;
    private static string _cachedMode = string.Empty;
    private static int _cachedMax;
    private static float _cacheExpiresAt;
    private static IReadOnlyList<StationHintCandidate> _cachedCandidates =
        Array.Empty<StationHintCandidate>();

    internal static void Reset()
    {
        _cachedStation = null;
        _cachedMode = string.Empty;
        _cachedMax = 0;
        _cacheExpiresAt = 0f;
        _cachedCandidates = Array.Empty<StationHintCandidate>();
        LastDiagnosticLogTimes.Clear();
    }

    internal static void ShowCookingStation(CookingStation station, Switch? switchRef)
    {
        if (!CanShow() || station == null)
        {
            return;
        }

        int max = StationModule.CookingStationMax.Value;
        if (max <= 0)
        {
            return;
        }

        List<ItemDrop> inputs = new();
        string mode;
        Switch? addFuelSwitch = station.m_addFuelSwitch;
        Switch? addFoodSwitch = station.m_addFoodSwitch;
        if (switchRef != null && switchRef == addFuelSwitch)
        {
            mode = "CookingFuel";
            ItemDrop? fuelItem = station.m_fuelItem;
            int maxFuel = station.m_maxFuel;
            if (fuelItem == null
                || !station.m_useFuel
                || maxFuel <= 0
                || InvokeFloat(CookingStationGetFuelMethod, station, maxFuel) > maxFuel - 1
                || !IsValidItem(fuelItem))
            {
                return;
            }

            inputs.Add(fuelItem);
            LogGate("CookingStation", station, "fuel", "accepted gate");
        }
        else if (switchRef == null || switchRef == addFoodSwitch)
        {
            mode = "CookingFood";
            bool fireBlocked = station.m_requireFire
                               && !InvokeBool(CookingStationIsFireLitMethod, station);
            bool slotBlocked = InvokeInt(CookingStationGetFreeSlotMethod, station, -1) == -1;
            if (fireBlocked || slotBlocked)
            {
                LogGate(
                    "CookingStation",
                    station,
                    "food",
                    $"input blocked fire={fireBlocked} slotsFull={slotBlocked}");
            }
            else if (station.m_conversion != null)
            {
                inputs.AddRange(station.m_conversion
                    .Where(conversion => conversion != null && IsValidItem(conversion.m_from))
                    .Select(conversion => conversion.m_from));
            }

            if (StationModule.Diagnostics.Value)
            {
                LogGate(
                    "CookingStation",
                    station,
                    "food",
                    $"accepted gate rawInputs={inputs.Count}");
            }
        }
        else
        {
            LogGate(
                "CookingStation",
                station,
                "unknown switch",
                "switch did not match add food/fuel");
            return;
        }

        IReadOnlyList<StationHintCandidate> inputCandidates = GetCachedOrBuild(
            "CookingStation",
            mode,
            station,
            inputs,
            max);
        IReadOnlyList<StationHintCandidate> progressCandidates =
            CookingProgressResolver.GetCandidates(station);
        StationHintUi.ShowCookingStation(progressCandidates, inputCandidates);
    }

    internal static void ShowSmelter(Smelter smelter, Switch? switchRef)
    {
        if (!CanShow() || smelter == null)
        {
            return;
        }

        bool isWindmill = smelter.m_windmill != null;
        string componentName = isWindmill ? "Windmill" : "Smelter";
        int max = isWindmill
            ? StationModule.WindmillMax.Value
            : StationModule.SmelterMax.Value;
        if (max <= 0)
        {
            return;
        }

        List<ItemDrop> inputs = new();
        string mode;
        Switch? addWoodSwitch = smelter.m_addWoodSwitch;
        Switch? addOreSwitch = smelter.m_addOreSwitch;
        if (switchRef != null && switchRef == addWoodSwitch)
        {
            mode = componentName + "Fuel";
            ItemDrop? fuelItem = smelter.m_fuelItem;
            int maxFuel = smelter.m_maxFuel;
            if (fuelItem == null
                || maxFuel <= 0
                || InvokeFloat(SmelterGetFuelMethod, smelter, maxFuel) > maxFuel - 1
                || !IsValidItem(fuelItem))
            {
                return;
            }

            inputs.Add(fuelItem);
            LogGate(componentName, smelter, "fuel", "accepted gate");
        }
        else if (switchRef != null && switchRef == addOreSwitch)
        {
            mode = componentName + "Input";
            int maxOre = smelter.m_maxOre;
            if (maxOre <= 0
                || InvokeInt(SmelterGetQueueSizeMethod, smelter, maxOre) >= maxOre)
            {
                LogGate(componentName, smelter, "input", "blocked by queue gate");
                return;
            }

            if (smelter.m_conversion != null)
            {
                inputs.AddRange(smelter.m_conversion
                    .Where(conversion => conversion != null && IsValidItem(conversion.m_from))
                    .Select(conversion => conversion.m_from));
            }

            if (StationModule.Diagnostics.Value)
            {
                LogGate(
                    componentName,
                    smelter,
                    "input",
                    $"accepted gate rawInputs={inputs.Count}");
            }
        }
        else
        {
            LogGate(
                componentName,
                smelter,
                "unknown switch",
                "switch did not match add input/fuel");
            return;
        }

        ShowFor(componentName, mode, smelter, inputs, max);
    }

    internal static void ShowFermenter(Fermenter fermenter)
    {
        if (!CanShow() || fermenter == null)
        {
            return;
        }

        int max = StationModule.FermenterMax.Value;
        bool diagnostics = StationModule.Diagnostics.Value;
        if (max <= 0 && !diagnostics)
        {
            return;
        }

        FermenterStatus status = GetFermenterStatus(fermenter);
        bool hasRoof = ReadBool(FermenterHasRoofField, fermenter);
        bool exposed = ReadBool(FermenterExposedField, fermenter);
        if (max <= 0 || status != FermenterStatus.Empty || !hasRoof || exposed)
        {
            if (diagnostics)
            {
                LogGate(
                    "Fermenter",
                    fermenter,
                    "input",
                    $"blocked gate max={max} status={status} hasRoof={hasRoof} exposed={exposed}");
            }

            return;
        }

        if (!PrivateArea.CheckAccess(fermenter.transform.position, 0f, false))
        {
            LogGate("Fermenter", fermenter, "input", "blocked by private area");
            return;
        }

        List<ItemDrop> inputs = fermenter.m_conversion == null
            ? new List<ItemDrop>()
            : fermenter.m_conversion
                .Where(conversion => conversion != null && IsValidItem(conversion.m_from))
                .Select(conversion => conversion.m_from)
                .ToList();
        if (diagnostics)
        {
            LogGate(
                "Fermenter",
                fermenter,
                "input",
                $"accepted gate rawInputs={inputs.Count}");
        }

        ShowFor("Fermenter", "FermenterInput", fermenter, inputs, max);
    }

    private static bool CanShow() =>
        StationModule.CanShowDisplay && Player.m_localPlayer != null;

    private static void ShowFor(
        string componentName,
        string mode,
        Component station,
        IEnumerable<ItemDrop> inputs,
        int max)
    {
        IReadOnlyList<StationHintCandidate> candidates = GetCachedOrBuild(
            componentName,
            mode,
            station,
            inputs,
            max);
        if (candidates.Count > 0)
        {
            StationHintUi.Show(candidates);
        }
    }

    private static IReadOnlyList<StationHintCandidate> GetCachedOrBuild(
        string componentName,
        string mode,
        Component station,
        IEnumerable<ItemDrop> inputs,
        int max)
    {
        if (_cachedStation == station
            && _cachedMode == mode
            && _cachedMax == max
            && Time.unscaledTime < _cacheExpiresAt)
        {
            return _cachedCandidates;
        }

        CandidateBuildResult build = BuildCandidates(station, inputs, max);
        _cachedStation = station;
        _cachedMode = mode;
        _cachedMax = max;
        _cacheExpiresAt = Time.unscaledTime + CandidateCacheSeconds;
        _cachedCandidates = build.Candidates;

        if (StationModule.Diagnostics.Value)
        {
            LogGate(
                componentName,
                station,
                "build",
                $"raw={build.Raw}, invalid={build.Invalid}, duplicate={build.Duplicate}, "
                + $"unknown={build.Unknown}, azuBlocked={build.BlockedByAzuCraftyBoxes}, "
                + $"unavailable={build.Unavailable}, player={build.PlayerAvailable}, "
                + $"nearbyOnly={build.NearbyOnlyAvailable}, shown={build.Candidates.Count}");
        }

        return build.Candidates;
    }

    private static CandidateBuildResult BuildCandidates(
        Component station,
        IEnumerable<ItemDrop> inputs,
        int max)
    {
        Player player = Player.m_localPlayer;
        Inventory inventory = player.GetInventory();
        string stationPrefab = Utils.GetPrefabName(station.gameObject);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<StationHintCandidate> playerCandidates = new(max);
        List<StationHintCandidate> nearbyOnlyCandidates = new(max);
        List<StationHintCandidate> result = new(max);
        CandidateBuildResult build = new(result);
        AzuCraftyBoxesCompatibility.NearbyContainerQuery? nearbyQuery = null;
        bool nearbyQueryResolved = false;

        foreach (ItemDrop input in inputs)
        {
            build.Raw++;
            if (!TryReadItem(input, out string prefabName, out string sharedName, out Sprite? icon))
            {
                build.Invalid++;
                continue;
            }

            if (!seen.Add(prefabName))
            {
                build.Duplicate++;
                continue;
            }

            if (!IsKnown(player, prefabName, sharedName))
            {
                build.Unknown++;
                continue;
            }

            int playerAvailable = inventory.CountItems(sharedName, -1, true);
            if (playerAvailable <= 0)
            {
                if (!AzuCraftyBoxesCompatibility.CanItemBePulled(stationPrefab, prefabName))
                {
                    build.BlockedByAzuCraftyBoxes++;
                    continue;
                }

                if (!nearbyQueryResolved)
                {
                    nearbyQueryResolved = true;
                    nearbyQuery = AzuCraftyBoxesCompatibility.CreateNearbyQuery(
                        station,
                        StationModule.NearbyContainerRange.Value);
                }

                int nearbyAvailable = nearbyQuery?.CountAvailable(prefabName, sharedName) ?? 0;
                if (nearbyAvailable <= 0)
                {
                    build.Unavailable++;
                    continue;
                }
            }

            string displayName = Localization.instance != null
                ? Localization.instance.Localize(sharedName)
                : sharedName;
            StationHintCandidate candidate = new(displayName, icon);
            if (playerAvailable > 0)
            {
                build.PlayerAvailable++;
                playerCandidates.Add(candidate);
            }
            else
            {
                build.NearbyOnlyAvailable++;
                nearbyOnlyCandidates.Add(candidate);
            }
        }

        AppendUpToMax(result, playerCandidates, max);
        AppendUpToMax(result, nearbyOnlyCandidates, max);
        return build;
    }

    private static void AppendUpToMax(
        List<StationHintCandidate> result,
        List<StationHintCandidate> source,
        int max)
    {
        foreach (StationHintCandidate candidate in source)
        {
            if (result.Count >= max)
            {
                break;
            }

            result.Add(candidate);
        }
    }

    private static bool TryReadItem(
        ItemDrop item,
        out string prefabName,
        out string sharedName,
        out Sprite? icon)
    {
        prefabName = string.Empty;
        sharedName = string.Empty;
        icon = null;
        if (!IsValidItem(item))
        {
            return false;
        }

        prefabName = Utils.GetPrefabName(item.gameObject);
        sharedName = item.m_itemData.m_shared.m_name;
        if (string.IsNullOrWhiteSpace(prefabName) || string.IsNullOrWhiteSpace(sharedName))
        {
            return false;
        }

        Sprite[] icons = item.m_itemData.m_shared.m_icons;
        if (icons != null && icons.Length > 0)
        {
            icon = icons[0];
        }

        return true;
    }

    private static bool IsKnown(Player player, string prefabName, string sharedName) =>
        player.IsKnownMaterial(sharedName)
        || player.IsRecipeKnown(sharedName)
        || player.IsKnownMaterial(prefabName)
        || player.IsRecipeKnown(prefabName);

    private static bool IsValidItem(ItemDrop? item) =>
        item != null && item.m_itemData != null && item.m_itemData.m_shared != null;

    private static FermenterStatus GetFermenterStatus(Fermenter fermenter) =>
        (FermenterStatus)InvokeInt(
            FermenterGetStatusMethod,
            fermenter,
            (int)FermenterStatus.Fermenting);

    private static bool InvokeBool(MethodInfo? method, object instance, bool fallback = false)
    {
        object? value = Invoke(method, instance);
        return value is bool result ? result : fallback;
    }

    private static int InvokeInt(MethodInfo? method, object instance, int fallback)
    {
        object? value = Invoke(method, instance);
        if (value is Enum enumValue)
        {
            return Convert.ToInt32(enumValue);
        }

        return value is int result ? result : fallback;
    }

    private static float InvokeFloat(MethodInfo? method, object instance, float fallback)
    {
        object? value = Invoke(method, instance);
        return value is float result ? result : fallback;
    }

    private static object? Invoke(MethodInfo? method, object instance)
    {
        if (method == null)
        {
            return null;
        }

        try
        {
            return method.Invoke(instance, null);
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogDebug(
                $"Could not invoke {method.DeclaringType?.Name}.{method.Name}: {exception.Message}");
            return null;
        }
    }

    private static bool ReadBool(FieldInfo? field, object instance, bool fallback = false)
    {
        try
        {
            object? value = field?.GetValue(instance);
            return value is bool result ? result : fallback;
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogDebug(
                $"Could not read {field?.DeclaringType?.Name}.{field?.Name}: {exception.Message}");
            return fallback;
        }
    }

    private static void LogGate(
        string componentName,
        Component component,
        string stage,
        string message)
    {
        if (!StationModule.IsInitialized || !StationModule.Diagnostics.Value)
        {
            return;
        }

        string key = $"{componentName}:{component.GetInstanceID()}:{stage}";
        float now = Time.unscaledTime;
        if (LastDiagnosticLogTimes.TryGetValue(key, out float last) && now - last < 1f)
        {
            return;
        }

        if (LastDiagnosticLogTimes.Count >= 256)
        {
            LastDiagnosticLogTimes.Clear();
        }

        LastDiagnosticLogTimes[key] = now;
        FineDiningPlugin.Log.LogInfo(
            $"[Station Diagnostics] {componentName} {stage}: {message}");
    }

    private struct CandidateBuildResult
    {
        internal CandidateBuildResult(List<StationHintCandidate> candidates) : this()
        {
            Candidates = candidates;
        }

        internal List<StationHintCandidate> Candidates { get; }
        internal int Raw { get; set; }
        internal int Invalid { get; set; }
        internal int Duplicate { get; set; }
        internal int Unknown { get; set; }
        internal int BlockedByAzuCraftyBoxes { get; set; }
        internal int Unavailable { get; set; }
        internal int PlayerAvailable { get; set; }
        internal int NearbyOnlyAvailable { get; set; }
    }
}
