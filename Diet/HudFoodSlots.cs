using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FineDining;

internal static class HudFoodSlots
{
    internal static void EnsureFoodSlots(Hud hud)
    {
        int currentSlots = hud.m_foodBars.Length;
        int targetSlots = DietConfig.GetMaxFoodSlots();
        if (targetSlots <= currentSlots)
        {
            return;
        }

        if (currentSlots == 0
            || hud.m_foodIcons.Length < currentSlots
            || hud.m_foodTime.Length < currentSlots)
        {
            return;
        }

        int lastIndex = currentSlots - 1;
        Image firstIcon = hud.m_foodIcons[0];
        TMP_Text firstTime = hud.m_foodTime[0];
        Image lastBarImage = hud.m_foodBars[lastIndex];
        Image lastIcon = hud.m_foodIcons[lastIndex];
        TMP_Text lastTime = hud.m_foodTime[lastIndex];
        if (firstIcon == null || firstTime == null || lastBarImage == null || lastIcon == null || lastTime == null)
        {
            return;
        }

        RectTransform? iconRoot = firstIcon.transform.parent as RectTransform;
        RectTransform? lastBar = lastBarImage.transform as RectTransform;
        RectTransform? lastIconContainer = lastIcon.transform.parent as RectTransform;
        if (iconRoot == null
            || lastBar == null
            || lastIconContainer == null
            || !TryBuildPath(iconRoot, firstIcon.transform, out List<int> iconPath)
            || !TryBuildPath(iconRoot, firstTime.transform, out List<int> timePath)
            || FollowPath(lastIconContainer, iconPath) != lastIcon.transform
            || FollowPath(lastIconContainer, timePath) != lastTime.transform)
        {
            return;
        }

        Vector3 barOffset = new(0f, 138f, 0f);
        Vector3 iconOffset = new(0f, 138f, 0f);
        if (currentSlots >= 2)
        {
            Image previousBarImage = hud.m_foodBars[lastIndex - 1];
            Image previousIcon = hud.m_foodIcons[lastIndex - 1];
            if (previousBarImage == null
                || previousIcon == null
                || previousBarImage.transform is not RectTransform previousBar
                || previousIcon.transform.parent is not RectTransform previousIconContainer)
            {
                return;
            }

            barOffset = lastBar.localPosition - previousBar.localPosition;
            iconOffset = lastIconContainer.localPosition - previousIconContainer.localPosition;
        }

        Array.Resize(ref hud.m_foodBars, targetSlots);
        Array.Resize(ref hud.m_foodIcons, targetSlots);
        Array.Resize(ref hud.m_foodTime, targetSlots);

        RectTransform currentBar = lastBar;
        RectTransform currentIconContainer = lastIconContainer;
        for (int index = currentSlots; index < targetSlots; index++)
        {
            GameObject barObject = UnityEngine.Object.Instantiate(currentBar.gameObject, currentBar.parent);
            RectTransform barRect = barObject.GetComponent<RectTransform>();
            barRect.localPosition = currentBar.localPosition + barOffset;
            currentBar = barRect;
            hud.m_foodBars[index] = barObject.GetComponent<Image>();
            hud.m_foodBars[index].gameObject.SetActive(false);

            GameObject iconObject = UnityEngine.Object.Instantiate(currentIconContainer.gameObject, currentIconContainer.parent);
            RectTransform iconRect = iconObject.GetComponent<RectTransform>();
            iconRect.localPosition = currentIconContainer.localPosition + iconOffset;
            currentIconContainer = iconRect;

            Transform iconTransform = FollowPath(iconRect, iconPath)!;
            Transform timeTransform = FollowPath(iconRect, timePath)!;
            hud.m_foodIcons[index] = iconTransform.GetComponent<Image>();
            hud.m_foodTime[index] = timeTransform.GetComponent<TMP_Text>();

            hud.m_foodIcons[index].gameObject.SetActive(false);
            hud.m_foodTime[index].gameObject.SetActive(false);
        }
    }

    internal static void LimitVisibleSlots(Hud hud, Player player)
    {
        PlayerFoodStateData state = FoodStateStore.GetState(player);
        int visibleSlots = FoodSlotProgression.GetCurrentSlots(player, state);
        for (int index = 0; index < hud.m_foodBars.Length; index++)
        {
            bool visible = index < visibleSlots;
            if (hud.m_foodBars[index] != null && !visible)
            {
                hud.m_foodBars[index].gameObject.SetActive(false);
            }

            if (index < hud.m_foodIcons.Length && hud.m_foodIcons[index] != null)
            {
                Transform parent = hud.m_foodIcons[index].transform.parent;
                if (parent != null)
                {
                    parent.gameObject.SetActive(visible);
                }
            }

            if (index < hud.m_foodTime.Length && hud.m_foodTime[index] != null && !visible)
            {
                hud.m_foodTime[index].gameObject.SetActive(false);
            }
        }
    }

    internal static void UpdateTooltips(Hud hud, Player player)
    {
        List<Player.Food> foods = player.GetFoods();
        for (int index = 0; index < hud.m_foodIcons.Length; index++)
        {
            Image icon = hud.m_foodIcons[index];
            if (icon == null)
            {
                continue;
            }

            UITooltip? tooltip = HudFoodPanels.GetOrCreateTooltip(icon.gameObject, hud);
            if (tooltip == null)
            {
                continue;
            }

            bool hasFood = index < foods.Count && foods[index]?.m_item?.m_shared != null;
            string foodName = hasFood
                ? Localization.instance.Localize(foods[index].m_item.m_shared.m_name)
                : string.Empty;
            tooltip.Set(
                string.Empty,
                HudFoodPanels.FormatFoodNameForTooltip(foodName));
            HudFoodPanels.UpdateTooltipHover(icon, tooltip, hasFood && icon.isActiveAndEnabled);
        }
    }

    private static bool TryBuildPath(Transform root, Transform child, out List<int> path)
    {
        path = new List<int>();
        Transform current = child;
        while (current != null && current != root)
        {
            path.Add(current.GetSiblingIndex());
            current = current.parent;
        }

        if (current != root)
        {
            path.Clear();
            return false;
        }

        path.Reverse();
        return true;
    }

    private static Transform? FollowPath(Transform root, List<int> path)
    {
        Transform current = root;
        foreach (int siblingIndex in path)
        {
            if (siblingIndex < 0 || siblingIndex >= current.childCount)
            {
                return null;
            }

            current = current.GetChild(siblingIndex);
        }

        return current;
    }
}
