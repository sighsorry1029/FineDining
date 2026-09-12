using System;
using System.Collections.Generic;
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
    private static readonly MethodInfo? SmelterGetQueueSizeMethod =
        AccessTools.Method(typeof(Smelter), "GetQueueSize");
    private static readonly MethodInfo? FermenterGetStatusMethod =
        AccessTools.Method(typeof(Fermenter), "GetStatus");
    private static readonly FieldInfo? FermenterHasRoofField =
        AccessTools.Field(typeof(Fermenter), "m_hasRoof");
    private static readonly FieldInfo? FermenterExposedField =
        AccessTools.Field(typeof(Fermenter), "m_exposed");

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
    }

    internal static void ShowCookingStation(CookingStation station, Switch? switchRef)
    {
        if (!CanShow() || station == null)
        {
            return;
        }

        int max = StationModule.GetHintLimit(
            StationModule.CookingStationRows.Value);
        if (max <= 0)
        {
            return;
        }

        string mode;
        bool includeInputs = true;
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
        }
        else if (switchRef == null || switchRef == addFoodSwitch)
        {
            mode = "CookingFood";
            bool fireBlocked = station.m_requireFire
                               && !InvokeBool(CookingStationIsFireLitMethod, station);
            bool slotBlocked = InvokeInt(CookingStationGetFreeSlotMethod, station, -1) == -1;
            includeInputs = !fireBlocked && !slotBlocked;
        }
        else
        {
            return;
        }

        IReadOnlyList<StationHintCandidate> progressCandidates =
            CookingProgressResolver.GetCandidates(station, max);
        int progressRows =
            (progressCandidates.Count + StationModule.HintColumns - 1)
            / StationModule.HintColumns;
        int inputLimit = Math.Max(
            0,
            max - progressRows * StationModule.HintColumns);
        IReadOnlyList<StationHintCandidate> inputCandidates = includeInputs && inputLimit > 0
            ? GetCachedOrBuild(
                mode,
                station,
                inputLimit)
            : Array.Empty<StationHintCandidate>();
        StationHintUi.ShowCookingStation(progressCandidates, inputCandidates);
    }

    internal static void ShowSmelter(Smelter smelter, Switch? switchRef)
    {
        if (!CanShow() || smelter == null)
        {
            return;
        }

        bool isWindmill = smelter.m_windmill != null;
        int max = StationModule.GetHintLimit(
            isWindmill
                ? StationModule.WindmillRows.Value
                : StationModule.SmelterRows.Value);
        if (max <= 0)
        {
            return;
        }

        string mode;
        Switch? addWoodSwitch = smelter.m_addWoodSwitch;
        Switch? addOreSwitch = smelter.m_addOreSwitch;
        if (switchRef != null && switchRef == addWoodSwitch)
        {
            return;
        }
        else if (switchRef != null && switchRef == addOreSwitch)
        {
            mode = isWindmill ? "WindmillInput" : "SmelterInput";
            int maxOre = smelter.m_maxOre;
            if (maxOre <= 0
                || InvokeInt(SmelterGetQueueSizeMethod, smelter, maxOre) >= maxOre)
            {
                return;
            }
        }
        else
        {
            return;
        }

        ShowFor(mode, smelter, max);
    }

    internal static void ShowFermenter(Fermenter fermenter)
    {
        if (!CanShow() || fermenter == null)
        {
            return;
        }

        int max = StationModule.GetHintLimit(
            StationModule.FermenterRows.Value);
        if (max <= 0)
        {
            return;
        }

        FermenterStatus status = GetFermenterStatus(fermenter);
        bool hasRoof = ReadBool(FermenterHasRoofField, fermenter);
        bool exposed = ReadBool(FermenterExposedField, fermenter);
        if (status != FermenterStatus.Empty || !hasRoof || exposed)
        {
            return;
        }

        if (!PrivateArea.CheckAccess(fermenter.transform.position, 0f, false))
        {
            return;
        }

        ShowFor("FermenterInput", fermenter, max);
    }

    private static bool CanShow() =>
        StationModule.IsInitialized && Player.m_localPlayer != null;

    private static void ShowFor(
        string mode,
        Component station,
        int max)
    {
        IReadOnlyList<StationHintCandidate> candidates = GetCachedOrBuild(
            mode,
            station,
            max);
        if (candidates.Count > 0)
        {
            StationHintUi.Show(candidates);
        }
    }

    private static IReadOnlyList<StationHintCandidate> GetCachedOrBuild(
        string mode,
        Component station,
        int max)
    {
        if (_cachedStation == station
            && _cachedMode == mode
            && _cachedMax == max
            && Time.unscaledTime < _cacheExpiresAt)
        {
            return _cachedCandidates;
        }

        // Keep per-frame availability checks in the Show methods, but only
        // snapshot conversion inputs when the existing candidate cache expires.
        IEnumerable<ItemDrop> inputs = GetInputs(station, mode);
        IReadOnlyList<StationHintCandidate> candidates = BuildCandidates(station, inputs, max);
        _cachedStation = station;
        _cachedMode = mode;
        _cachedMax = max;
        _cacheExpiresAt = Time.unscaledTime + CandidateCacheSeconds;
        _cachedCandidates = candidates;
        return candidates;
    }

    private static IReadOnlyList<ItemDrop> GetInputs(Component station, string mode)
    {
        List<ItemDrop> inputs = new();
        switch (station)
        {
            case CookingStation cookingStation:
                if (mode == "CookingFuel")
                {
                    if (IsValidItem(cookingStation.m_fuelItem))
                    {
                        inputs.Add(cookingStation.m_fuelItem);
                    }
                }
                else if (cookingStation.m_conversion != null)
                {
                    foreach (CookingStation.ItemConversion conversion in cookingStation.m_conversion)
                    {
                        if (conversion != null && IsValidItem(conversion.m_from))
                        {
                            inputs.Add(conversion.m_from);
                        }
                    }
                }

                break;
            case Smelter smelter when smelter.m_conversion != null:
                foreach (Smelter.ItemConversion conversion in smelter.m_conversion)
                {
                    if (conversion != null && IsValidItem(conversion.m_from))
                    {
                        inputs.Add(conversion.m_from);
                    }
                }

                break;
            case Fermenter fermenter when fermenter.m_conversion != null:
                foreach (Fermenter.ItemConversion conversion in fermenter.m_conversion)
                {
                    if (conversion != null && IsValidItem(conversion.m_from))
                    {
                        inputs.Add(conversion.m_from);
                    }
                }

                break;
        }

        return inputs;
    }

    private static IReadOnlyList<StationHintCandidate> BuildCandidates(
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
        AzuCraftyBoxesCompatibility.NearbyContainerQuery? nearbyQuery = null;
        bool nearbyQueryResolved = false;

        foreach (ItemDrop input in inputs)
        {
            if (!TryReadItem(input, out string prefabName, out string sharedName, out Sprite? icon))
            {
                continue;
            }

            if (!seen.Add(prefabName))
            {
                continue;
            }

            if (!IsKnown(player, prefabName, sharedName))
            {
                continue;
            }

            int playerAvailable = inventory.CountItems(sharedName, -1, true);
            if (playerAvailable <= 0)
            {
                if (!AzuCraftyBoxesCompatibility.CanItemBePulled(stationPrefab, prefabName))
                {
                    continue;
                }

                if (!nearbyQueryResolved)
                {
                    nearbyQueryResolved = true;
                    nearbyQuery = AzuCraftyBoxesCompatibility.CreateNearbyQuery(station);
                }

                int nearbyAvailable = nearbyQuery?.CountAvailable(prefabName, sharedName) ?? 0;
                if (nearbyAvailable <= 0)
                {
                    continue;
                }
            }

            string displayName = Localization.instance != null
                ? Localization.instance.Localize(sharedName)
                : sharedName;
            StationHintCandidate candidate = new(displayName, icon);
            if (playerAvailable > 0)
            {
                playerCandidates.Add(candidate);
            }
            else
            {
                nearbyOnlyCandidates.Add(candidate);
            }
        }

        AppendUpToMax(result, playerCandidates, max);
        AppendUpToMax(result, nearbyOnlyCandidates, max);
        return result;
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

}
