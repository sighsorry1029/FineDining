using System;
using System.Collections.Generic;
using UnityEngine;

namespace FineDining;

internal static class CookingProgressResolver
{
    // CookingStation.Status is private in the runtime game assembly.
    private enum CookingStatus
    {
        NotDone,
        Done,
        Burnt
    }

    internal static IReadOnlyList<StationHintCandidate> GetCandidates(
        CookingStation station,
        int maxCandidates)
    {
        if (station.m_slots == null
            || station.m_slots.Length == 0
            || maxCandidates <= 0
            || !TryGetZdo(station, out ZDO? zdo))
        {
            return Array.Empty<StationHintCandidate>();
        }

        List<StationHintCandidate> candidates = new(
            Math.Min(station.m_slots.Length, maxCandidates));
        for (int slot = 0; slot < station.m_slots.Length; slot++)
        {
            string itemName = zdo!.GetString("slot" + slot);
            if (string.IsNullOrEmpty(itemName))
            {
                continue;
            }

            CookingStatus status = (CookingStatus)zdo.GetInt("slotstatus" + slot);
            if (status == CookingStatus.Burnt && itemName == "Coal")
            {
                continue;
            }

            CookingStation.ItemConversion? conversion = FindConversion(station, itemName);
            float cookedTime = zdo.GetFloat("slot" + slot);
            ItemDrop? item;
            string timerText;

            if (status == CookingStatus.NotDone)
            {
                item = FindItemPrefab(itemName) ?? conversion?.m_from;
                timerText = conversion != null && conversion.m_cookTime > 0f
                    ? StationText.FormatSeconds(
                        Math.Max(0d, conversion.m_cookTime - cookedTime),
                        keepAtLeastOneSecond: true)
                    : string.Empty;
            }
            else if (status == CookingStatus.Done)
            {
                item = FindItemPrefab(itemName) ?? conversion?.m_to;
                timerText = conversion != null
                            && conversion.m_cookTime > 0f
                            && station.m_overCookedItem != null
                    ? StationText.FormatSeconds(
                        Math.Max(0d, conversion.m_cookTime * 2d - cookedTime),
                        keepAtLeastOneSecond: true)
                    : StationText.FormatSeconds(0d);
            }
            else
            {
                item = FindItemPrefab(itemName) ?? station.m_overCookedItem;
                timerText = StationText.FormatSeconds(0d);
            }

            bool showAutoEject = !string.IsNullOrEmpty(timerText)
                                  && status != CookingStatus.Burnt
                                  && CookingStationAutoPopSystem.ShouldShowAutoEject(
                                      station,
                                      slot,
                                      itemName,
                                      conversion?.m_to != null
                                          ? Utils.GetPrefabName(conversion.m_to.gameObject)
                                          : string.Empty,
                                      outputPhase: status == CookingStatus.Done);
            timerText = StationText.ColorizeTimer(timerText);
            StationHintCandidate? candidate = CreateCandidate(
                item,
                timerText,
                showAutoEject ? StationText.AutoEjectLabel : string.Empty);
            if (candidate != null)
            {
                candidates.Add(candidate);
                if (candidates.Count >= maxCandidates)
                {
                    break;
                }
            }
        }

        return candidates;
    }

    private static CookingStation.ItemConversion? FindConversion(
        CookingStation station,
        string itemName)
    {
        if (station.m_conversion == null)
        {
            return null;
        }

        foreach (CookingStation.ItemConversion conversion in station.m_conversion)
        {
            if (conversion != null
                && (MatchesPrefab(conversion.m_from, itemName)
                    || MatchesPrefab(conversion.m_to, itemName)))
            {
                return conversion;
            }
        }

        return null;
    }

    private static bool MatchesPrefab(ItemDrop? item, string prefabName) =>
        item != null && Utils.GetPrefabName(item.gameObject) == prefabName;

    private static ItemDrop? FindItemPrefab(string prefabName)
    {
        GameObject? prefab = ObjectDB.instance != null
            ? ObjectDB.instance.GetItemPrefab(prefabName)
            : null;
        if (prefab == null && ZNetScene.instance != null)
        {
            prefab = ZNetScene.instance.GetPrefab(prefabName);
        }

        return prefab != null ? prefab.GetComponent<ItemDrop>() : null;
    }

    private static StationHintCandidate? CreateCandidate(
        ItemDrop? item,
        string timerText,
        string secondaryStatusText)
    {
        if (item == null || item.m_itemData == null || item.m_itemData.m_shared == null)
        {
            return null;
        }

        string sharedName = item.m_itemData.m_shared.m_name;
        if (string.IsNullOrWhiteSpace(sharedName))
        {
            return null;
        }

        Sprite? icon = null;
        Sprite[] icons = item.m_itemData.m_shared.m_icons;
        if (icons != null && icons.Length > 0)
        {
            icon = icons[0];
        }

        string displayName = Localization.instance != null
            ? Localization.instance.Localize(sharedName)
            : sharedName;
        return new StationHintCandidate(
            displayName,
            icon,
            timerText,
            secondaryStatusText);
    }

    private static bool TryGetZdo(Component component, out ZDO? zdo)
    {
        ZNetView? view = component.GetComponent<ZNetView>()
                         ?? component.GetComponentInParent<ZNetView>();
        zdo = view != null && view.IsValid() ? view.GetZDO() : null;
        return zdo != null;
    }
}
