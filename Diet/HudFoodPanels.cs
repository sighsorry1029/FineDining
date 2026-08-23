using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FineDining;

internal static class HudFoodPanels
{
    private const string RootName = "FineDining_DietHudRoot";
    private const float FallbackFoodIconSize = 43f;
    private const float SlotSpacing = 1f;

    private static readonly Vector3[] RectCorners = new Vector3[4];
    private static readonly Color DefaultBackground = new(0f, 0f, 0f, 0.45f);
    private static readonly Color FullStraightStarColor = new(1f, 0.78f, 0.16f, 1f);
    private static readonly string FullStraightMultiplierValueText = FoodRules.FullStraightMultiplier.ToString("0.00", CultureInfo.InvariantCulture);
    private static readonly string FullStraightMultiplierText = $"x{FullStraightMultiplierValueText}";
    private static readonly AccessTools.FieldRef<UITooltip> CurrentTooltipField =
        AccessTools.StaticFieldRefAccess<UITooltip>(
            AccessTools.DeclaredField(typeof(UITooltip), "m_current")
            ?? throw new System.MissingFieldException(typeof(UITooltip).FullName, "m_current"));
    private static Hud? _contextOwner;
    private static PanelContext? _context;

    internal static void Update(Hud hud, Player player)
    {
        if (hud == null || player == null)
        {
            return;
        }

        if (hud.m_gpRoot == null)
        {
            ResetAll();
            return;
        }

        PanelContext context = GetOrCreateContext(hud);
        Vector2 iconSize = GetFoodIconSize(hud, context.Root);
        Vector2 slotSize = iconSize;
        LayoutRoot(context, hud, slotSize);
        EnsureSlots(context.RecentSlots, context.RecentRow, DietConfig.GetRecentHistorySize(), hud, slotSize, iconSize);
        EnsureSlots(context.ChefSlots, context.ChefRow, DietConfig.GetChefCollectionSize(), hud, slotSize, iconSize);
        UpdateFullStraightIndicator(context, hud, player);

        bool refreshChefCollection = ShouldRefreshChefCollection(context, player);
        PlayerFoodStateData state = FoodStateStore.GetState(player);

        if (refreshChefCollection)
        {
            if (ChefCollectionService.EnsureChefCollection(player, state))
            {
                FoodStateStore.SaveState(player, state);
            }

            RememberChefCollectionInputs(context, player);
        }

        UpdateRecentSlots(context.RecentSlots, state);
        UpdateChefSlots(context.ChefSlots, state);
    }

    private static void UpdateFullStraightIndicator(PanelContext context, Hud hud, Player player)
    {
        if (!FoodRules.IsFullStraightActive(player))
        {
            HideFullStraightIndicator(context.FullStraight);
            return;
        }

        if (!TryGetTopFoodBounds(context.Root, hud, out Rect topFoodBounds))
        {
            HideFullStraightIndicator(context.FullStraight);
            return;
        }

        if (context.FullStraight?.Root == null)
        {
            context.FullStraight = null;
            context.FullStraight = CreateFullStraightIndicator(context.Root, hud);
            if (context.FullStraight == null)
            {
                return;
            }
        }

        FullStraightContext indicator = context.FullStraight;
        Vector2 indicatorSize = topFoodBounds.size;
        float centerX = topFoodBounds.center.x;
        float desiredBottom = topFoodBounds.yMax + SlotSpacing;
        float centerY = desiredBottom + indicatorSize.y * 0.5f;
        Canvas canvas = context.Root.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.rootCanvas.transform is RectTransform canvasRoot)
        {
            Rect canvasBounds = GetRectInParent(canvasRoot, context.Root);
            float availableHeight = canvasBounds.yMax - desiredBottom;
            float minimumCompactHeight = indicatorSize.y * 0.65f;
            if (availableHeight >= minimumCompactHeight)
            {
                indicatorSize.y = Mathf.Min(indicatorSize.y, availableHeight);
                centerY = desiredBottom + indicatorSize.y * 0.5f;
            }
            else
            {
                float rightCenterX = topFoodBounds.xMax + SlotSpacing + indicatorSize.x * 0.5f;
                float leftCenterX = topFoodBounds.xMin - SlotSpacing - indicatorSize.x * 0.5f;
                centerX = rightCenterX + indicatorSize.x * 0.5f <= canvasBounds.xMax
                    ? rightCenterX
                    : leftCenterX;
                centerY = topFoodBounds.center.y;
            }
        }

        indicator.Root.sizeDelta = indicatorSize;
        indicator.Star.fontSize = Mathf.Clamp(Mathf.Min(indicatorSize.x, indicatorSize.y) * 0.7f, 16f, 36f);
        indicator.Root.anchoredPosition = new Vector2(centerX, centerY);
        EnsureFullStraightTooltip(indicator, hud);

        indicator.Root.gameObject.SetActive(true);
        UpdateTooltipHover(
            indicator.Background,
            indicator.Tooltip,
            indicator.Tooltip != null
            && indicator.Background.isActiveAndEnabled
            && indicator.Root.gameObject.activeInHierarchy);
    }

    private static bool TryGetTopFoodBounds(RectTransform targetRoot, Hud hud, out Rect topFoodBounds)
    {
        topFoodBounds = default;
        int visibleSlots = Mathf.Min(DietConfig.GetMaxFoodSlots(), hud.m_foodIcons.Length);
        bool found = false;
        for (int index = 0; index < visibleSlots; index++)
        {
            Image icon = hud.m_foodIcons[index];
            if (icon == null || !icon.isActiveAndEnabled || !icon.gameObject.activeInHierarchy)
            {
                continue;
            }

            Rect bounds = GetRectInParent(icon.rectTransform, targetRoot);
            if (!found || bounds.yMax > topFoodBounds.yMax)
            {
                topFoodBounds = bounds;
                found = true;
            }
        }

        return found && topFoodBounds.width > 0f && topFoodBounds.height > 0f;
    }

    private static FullStraightContext? CreateFullStraightIndicator(RectTransform parent, Hud hud)
    {
        if (hud.m_foodTime.Length == 0 || hud.m_foodTime[0] == null || hud.m_foodTime[0].font == null)
        {
            return null;
        }

        GameObject rootObject = new("FullStraight", typeof(RectTransform), typeof(Image));
        rootObject.SetActive(false);
        RectTransform root = rootObject.GetComponent<RectTransform>();
        root.SetParent(parent, false);
        root.anchorMin = new Vector2(parent.pivot.x, parent.pivot.y);
        root.anchorMax = root.anchorMin;
        root.pivot = new Vector2(0.5f, 0.5f);

        Image background = rootObject.GetComponent<Image>();
        background.color = DefaultBackground;
        background.raycastTarget = false;

        TMP_Text template = hud.m_foodTime[0];
        TextMeshProUGUI star = CreateText(
            root,
            template,
            "Star",
            new Vector2(2f, 2f),
            new Vector2(-2f, -2f),
            TextAlignmentOptions.Center,
            30f);
        star.color = FullStraightStarColor;
        star.text = template.font.HasCharacter('★', searchFallbacks: true, tryAddCharacter: false) ? "★" : "*";
        star.gameObject.SetActive(true);

        TextMeshProUGUI multiplier = CreateText(
            root,
            template,
            "Multiplier",
            new Vector2(2f, 1f),
            new Vector2(-2f, 3f),
            TextAlignmentOptions.BottomRight,
            11f);
        multiplier.text = FullStraightMultiplierText;
        multiplier.gameObject.SetActive(true);

        FullStraightContext indicator = new()
        {
            Root = root,
            Background = background,
            Star = star
        };
        return indicator;
    }

    private static void EnsureFullStraightTooltip(FullStraightContext indicator, Hud hud)
    {
        if (indicator.Tooltip == null)
        {
            indicator.Tooltip = GetOrCreateTooltip(indicator.Background.gameObject, hud);
        }

        if (indicator.Tooltip == null)
        {
            return;
        }

        Localization localization = Localization.instance;
        string language = localization.GetSelectedLanguage();
        if (string.Equals(
                indicator.TooltipLanguage,
                language,
                System.StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (GetCurrentTooltip() == indicator.Tooltip)
        {
            UITooltip.HideTooltip();
        }

        indicator.Tooltip.Set(
            localization.Localize("$finedining_diet_full_straight_title"),
            localization.Localize(
                "$finedining_diet_full_straight_description",
                FullStraightMultiplierValueText));
        indicator.TooltipLanguage = language;
    }

    private static void HideFullStraightIndicator(FullStraightContext? indicator)
    {
        if (indicator?.Root == null)
        {
            return;
        }

        UpdateTooltipHover(indicator.Background, indicator.Tooltip, canHover: false);
        indicator.Root.gameObject.SetActive(false);
    }

    private static bool ShouldRefreshChefCollection(PanelContext context, Player player)
    {
        ObjectDB objectDb = ObjectDB.instance;
        if (objectDb == null)
        {
            return false;
        }

        return context.ChefPlayer != player
               || context.ChefObjectDb != objectDb
               || context.KnownRecipeCount != PlayerPrivateAccess.KnownRecipes(player).Count
               || context.KnownMaterialCount != PlayerPrivateAccess.KnownMaterials(player).Count
               || context.ObjectDbItemCount != objectDb.m_items.Count;
    }

    private static void RememberChefCollectionInputs(PanelContext context, Player player)
    {
        ObjectDB objectDb = ObjectDB.instance;
        if (objectDb == null)
        {
            return;
        }

        context.ChefPlayer = player;
        context.ChefObjectDb = objectDb;
        context.KnownRecipeCount = PlayerPrivateAccess.KnownRecipes(player).Count;
        context.KnownMaterialCount = PlayerPrivateAccess.KnownMaterials(player).Count;
        context.ObjectDbItemCount = objectDb.m_items.Count;
    }

    internal static void ResetAll()
    {
        if (_context != null)
        {
            HideFullStraightIndicator(_context.FullStraight);
            foreach (SlotContext slot in _context.RecentSlots)
            {
                HideSlot(slot);
            }

            foreach (SlotContext slot in _context.ChefSlots)
            {
                HideSlot(slot);
            }
        }

        if (_context?.Root != null)
        {
            Object.Destroy(_context.Root.gameObject);
        }

        _contextOwner = null;
        _context = null;
    }

    private static Vector2 GetFoodIconSize(Hud hud, RectTransform targetRoot)
    {
        if (hud.m_foodIcons.Length == 0 || hud.m_foodIcons[0] == null)
        {
            return Vector2.one * FallbackFoodIconSize;
        }

        RectTransform foodIcon = hud.m_foodIcons[0].rectTransform;
        Rect foodBounds = GetRectInParent(foodIcon, targetRoot);

        return foodBounds.width > 0f && foodBounds.height > 0f
            ? foodBounds.size
            : Vector2.one * FallbackFoodIconSize;
    }

    private static Rect GetRectInParent(RectTransform source, RectTransform parent)
    {
        source.GetWorldCorners(RectCorners);
        Vector3 firstCorner = parent.InverseTransformPoint(RectCorners[0]);
        float minX = firstCorner.x;
        float maxX = firstCorner.x;
        float minY = firstCorner.y;
        float maxY = firstCorner.y;
        for (int index = 1; index < RectCorners.Length; index++)
        {
            Vector3 corner = parent.InverseTransformPoint(RectCorners[index]);
            minX = Mathf.Min(minX, corner.x);
            maxX = Mathf.Max(maxX, corner.x);
            minY = Mathf.Min(minY, corner.y);
            maxY = Mathf.Max(maxY, corner.y);
        }

        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    private static PanelContext GetOrCreateContext(Hud hud)
    {
        if (_contextOwner == hud && _context?.Root != null)
        {
            return _context;
        }

        ResetAll();

        RectTransform parent = (RectTransform)hud.m_gpRoot.parent;
        GameObject rootObject = new(RootName, typeof(RectTransform));
        RectTransform root = rootObject.GetComponent<RectTransform>();
        root.SetParent(parent, false);
        root.pivot = new Vector2(0f, 1f);

        RectTransform recentRow = CreateRow(root, "RecentRow");
        RectTransform chefRow = CreateRow(root, "ChefRow");

        PanelContext context = new()
        {
            Root = root,
            RecentRow = recentRow,
            ChefRow = chefRow
        };
        _contextOwner = hud;
        _context = context;
        return context;
    }

    private static void LayoutRoot(PanelContext context, Hud hud, Vector2 slotSize)
    {
        RectTransform gpRoot = hud.m_gpRoot;
        RectTransform parent = (RectTransform)context.Root.parent;
        context.Root.anchorMin = Vector2.zero;
        context.Root.anchorMax = Vector2.zero;
        context.Root.pivot = new Vector2(0f, 1f);

        int maxSlots = Mathf.Max(DietConfig.GetRecentHistorySize(), DietConfig.GetChefCollectionSize());
        float panelWidth = maxSlots * slotSize.x + Mathf.Max(0, maxSlots - 1) * SlotSpacing;

        Rect gpBounds = GetRectInParent(gpRoot, parent);
        Rect gpIconBounds = hud.m_gpIcon != null
            ? GetRectInParent(hud.m_gpIcon.rectTransform, parent)
            : gpBounds;

        float panelGap = 0f;
        if (hud.m_healthBarRoot != null)
        {
            Rect healthBarBounds = GetRectInParent(hud.m_healthBarRoot, parent);
            panelGap = Mathf.Max(0f, gpIconBounds.xMin - healthBarBounds.xMax);
        }
        else if (hud.m_healthPanel != null)
        {
            Rect healthPanelBounds = GetRectInParent(hud.m_healthPanel, parent);
            panelGap = Mathf.Max(0f, gpBounds.xMin - healthPanelBounds.xMax);
        }

        float recentTopY = gpBounds.yMax;
        float chefRowOffset = -slotSize.y - SlotSpacing;
        if (hud.m_foodIcons.Length >= 2 && hud.m_foodIcons[0] != null && hud.m_foodIcons[1] != null)
        {
            float foodOneTopY = GetRectInParent(hud.m_foodIcons[1].rectTransform, parent).yMax;
            float foodZeroTopY = GetRectInParent(hud.m_foodIcons[0].rectTransform, parent).yMax;
            recentTopY = Mathf.Max(foodZeroTopY, foodOneTopY);
            chefRowOffset = Mathf.Min(foodZeroTopY, foodOneTopY) - recentTopY;
        }

        context.Root.sizeDelta = new Vector2(panelWidth, Mathf.Abs(chefRowOffset) + slotSize.y);
        context.Root.localPosition = new Vector3(gpIconBounds.xMax + panelGap, recentTopY, gpRoot.localPosition.z);

        context.RecentRow.anchorMin = new Vector2(0f, 1f);
        context.RecentRow.anchorMax = new Vector2(0f, 1f);
        context.RecentRow.pivot = new Vector2(0f, 1f);
        context.RecentRow.anchoredPosition = Vector2.zero;

        context.ChefRow.anchorMin = new Vector2(0f, 1f);
        context.ChefRow.anchorMax = new Vector2(0f, 1f);
        context.ChefRow.pivot = new Vector2(0f, 1f);
        context.ChefRow.anchoredPosition = new Vector2(0f, chefRowOffset);
    }

    private static RectTransform CreateRow(RectTransform parent, string name)
    {
        GameObject rowObject = new(name, typeof(RectTransform));
        RectTransform row = rowObject.GetComponent<RectTransform>();
        row.SetParent(parent, false);
        row.pivot = new Vector2(0f, 1f);
        return row;
    }

    private static void EnsureSlots(List<SlotContext> slots, RectTransform row, int targetCount, Hud hud, Vector2 slotSize, Vector2 iconSize)
    {
        while (slots.Count > targetCount)
        {
            SlotContext slot = slots[slots.Count - 1];
            slots.RemoveAt(slots.Count - 1);
            HideSlot(slot);
            Object.Destroy(slot.Root.gameObject);
        }

        while (slots.Count < targetCount)
        {
            slots.Add(CreateSlot(row, slots.Count, hud, slotSize, iconSize));
        }

        float rowWidth = targetCount * slotSize.x + Mathf.Max(0, targetCount - 1) * SlotSpacing;
        row.sizeDelta = new Vector2(rowWidth, slotSize.y);

        for (int index = 0; index < slots.Count; index++)
        {
            SlotContext slot = slots[index];
            if (slot.Tooltip == null)
            {
                slot.Tooltip = GetOrCreateTooltip(slot.Icon.gameObject, hud);
                if (slot.Tooltip != null)
                {
                    slot.ItemKey = string.Empty;
                    slot.ItemObjectDb = null;
                    slot.ItemObjectDbCount = -1;
                    slot.TooltipDirty = true;
                }
            }

            slot.Root.sizeDelta = slotSize;
            slot.Root.anchoredPosition = new Vector2(index * (slotSize.x + SlotSpacing), 0f);
            slot.Icon.rectTransform.sizeDelta = iconSize;
        }
    }

    private static SlotContext CreateSlot(RectTransform row, int index, Hud hud, Vector2 slotSize, Vector2 iconSize)
    {
        GameObject rootObject = new($"Slot_{index}", typeof(RectTransform));
        RectTransform root = rootObject.GetComponent<RectTransform>();
        root.SetParent(row, false);
        root.anchorMin = new Vector2(0f, 1f);
        root.anchorMax = new Vector2(0f, 1f);
        root.pivot = new Vector2(0f, 1f);
        root.sizeDelta = slotSize;

        GameObject iconObject = new("Icon", typeof(RectTransform), typeof(Image));
        RectTransform iconRect = iconObject.GetComponent<RectTransform>();
        iconRect.SetParent(root, false);
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.sizeDelta = iconSize;

        Image icon = iconObject.GetComponent<Image>();
        UITooltip? tooltip = GetOrCreateTooltip(iconObject, hud);
        icon.raycastTarget = false;

        TextMeshProUGUI cornerText = CreateText(root, hud.m_foodTime[0], "Corner", new Vector2(4f, -2f), new Vector2(-3f, 0f), TextAlignmentOptions.TopRight, 13f);
        TextMeshProUGUI footerText = CreateText(root, hud.m_foodTime[0], "Footer", new Vector2(2f, 1f), new Vector2(-2f, 3f), TextAlignmentOptions.BottomRight, 11f);

        return new SlotContext
        {
            Root = root,
            Icon = icon,
            Tooltip = tooltip,
            CornerText = cornerText,
            FooterText = footerText
        };
    }

    internal static UITooltip? GetOrCreateTooltip(GameObject target, Hud hud)
    {
        UITooltip? tooltip = target.GetComponent<UITooltip>();
        if (tooltip != null && tooltip.m_tooltipPrefab != null)
        {
            return tooltip;
        }

        if (hud.m_pieceIconPrefab == null)
        {
            return null;
        }

        UITooltip template = hud.m_pieceIconPrefab.GetComponent<UITooltip>();
        if (template == null || template.m_tooltipPrefab == null)
        {
            return null;
        }

        if (tooltip == null)
        {
            tooltip = target.AddComponent<UITooltip>();
        }

        tooltip.m_tooltipPrefab = template.m_tooltipPrefab;
        return tooltip;
    }

    private static TextMeshProUGUI CreateText(RectTransform parent, TMP_Text template, string name, Vector2 offsetMin, Vector2 offsetMax, TextAlignmentOptions alignment, float fontSize)
    {
        GameObject textObject = new(name, typeof(RectTransform));
        textObject.SetActive(false);
        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.SetParent(parent, false);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = offsetMin;
        textRect.offsetMax = offsetMax;

        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.font = template.font;
        text.fontSharedMaterial = template.fontSharedMaterial;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        text.color = Color.white;
        return text;
    }

    private static void UpdateRecentSlots(List<SlotContext> slots, PlayerFoodStateData state)
    {
        for (int index = 0; index < slots.Count; index++)
        {
            if (index >= state.Recent.Count)
            {
                HideSlot(slots[index]);
                continue;
            }

            HistoryEntryData entry = state.Recent[index];
            float diminishingScale = FoodRules.CalculateDiminishingScale(entry.Stack);
            bool diminished = diminishingScale < 1f && !Mathf.Approximately(diminishingScale, 1f);
            ShowSlot(
                slots[index],
                entry.Key,
                entry.Stack > 1 ? entry.Stack.ToString(CultureInfo.InvariantCulture) : string.Empty,
                diminished ? $"x{diminishingScale.ToString("0.00", CultureInfo.InvariantCulture)}" : string.Empty,
                isChef: false,
                entry.Stack,
                diminishingScale);
        }
    }

    private static void UpdateChefSlots(List<SlotContext> slots, PlayerFoodStateData state)
    {
        for (int index = 0; index < slots.Count; index++)
        {
            if (index >= state.Chef.Count)
            {
                HideSlot(slots[index]);
                continue;
            }

            ChefEntryData entry = state.Chef[index];
            ShowSlot(
                slots[index],
                entry.Key,
                string.Empty,
                $"x{entry.Multiplier.ToString("0.00", CultureInfo.InvariantCulture)}",
                isChef: true,
                stack: 0,
                entry.Multiplier);
        }
    }

    private static ItemDrop.ItemData? GetFoodItem(string key, ObjectDB objectDb)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        GameObject prefab = objectDb.GetItemPrefab(key);
        ItemDrop? itemDrop = prefab == null ? null : prefab.GetComponent<ItemDrop>();
        if (itemDrop != null && itemDrop.m_itemData != null)
        {
            return itemDrop.m_itemData;
        }

        foreach (GameObject candidate in objectDb.m_items)
        {
            if (candidate == null)
            {
                continue;
            }

            itemDrop = candidate.GetComponent<ItemDrop>();
            if (itemDrop == null || itemDrop.m_itemData == null)
            {
                continue;
            }

            if (candidate.name == key)
            {
                return itemDrop.m_itemData;
            }
        }

        return null;
    }

    private static void ShowSlot(
        SlotContext slot,
        string key,
        string cornerText,
        string footerText,
        bool isChef,
        int stack,
        float multiplier)
    {
        UpdateSlotItem(slot, key);
        UpdateSlotTooltip(slot, isChef, stack, multiplier);
        slot.Root.gameObject.SetActive(true);
        slot.CornerText.text = cornerText;
        slot.CornerText.gameObject.SetActive(!string.IsNullOrWhiteSpace(cornerText));
        slot.FooterText.text = footerText;
        slot.FooterText.gameObject.SetActive(!string.IsNullOrWhiteSpace(footerText));

        UpdateTooltipHover(
            slot.Icon,
            slot.Tooltip,
            slot.Icon.enabled
            && slot.Tooltip != null
            && !string.IsNullOrWhiteSpace(slot.Tooltip.m_text));
    }

    private static void UpdateSlotItem(SlotContext slot, string key)
    {
        ObjectDB objectDb = ObjectDB.instance;
        int itemCount = objectDb == null ? -1 : objectDb.m_items.Count;
        if (slot.ItemKey == key && slot.ItemObjectDb == objectDb && slot.ItemObjectDbCount == itemCount)
        {
            return;
        }

        ItemDrop.ItemData? item = objectDb == null ? null : GetFoodItem(key, objectDb);
        Sprite? icon = item?.GetIcon();
        slot.ItemKey = key;
        slot.ItemObjectDb = objectDb;
        slot.ItemObjectDbCount = itemCount;
        slot.ItemNameToken = item?.m_shared.m_name ?? string.Empty;
        slot.Icon.sprite = icon;
        slot.Icon.enabled = icon != null;
        slot.TooltipDirty = true;
    }

    private static void UpdateSlotTooltip(SlotContext slot, bool isChef, int stack, float multiplier)
    {
        if (slot.Tooltip == null)
        {
            return;
        }

        Localization localization = Localization.instance;
        string language = localization.GetSelectedLanguage();
        if (!slot.TooltipDirty
            && slot.TooltipIsChef == isChef
            && slot.TooltipStack == stack
            && Mathf.Approximately(slot.TooltipMultiplier, multiplier)
            && slot.TooltipLanguage == language)
        {
            return;
        }

        string foodName = localization.Localize(slot.ItemNameToken);
        string multiplierText = multiplier.ToString("0.00", CultureInfo.InvariantCulture);
        string description;
        if (isChef)
        {
            if (multiplier > 1f && !Mathf.Approximately(multiplier, 1f))
            {
                description = localization.Localize(
                    "$finedining_diet_chef_increase",
                    foodName,
                    multiplierText);
            }
            else if (multiplier < 1f && !Mathf.Approximately(multiplier, 1f))
            {
                description = localization.Localize(
                    "$finedining_diet_chef_decrease",
                    foodName,
                    multiplierText);
            }
            else
            {
                description = localization.Localize(
                    "$finedining_diet_chef_unchanged",
                    foodName);
            }
        }
        else if (multiplier < 1f && !Mathf.Approximately(multiplier, 1f))
        {
            description = localization.Localize(
                "$finedining_diet_recent_diminished",
                foodName,
                stack.ToString(CultureInfo.InvariantCulture),
                multiplierText);
        }
        else if (stack == 1)
        {
            description = localization.Localize("$finedining_diet_recent_once", foodName);
        }
        else
        {
            description = localization.Localize(
                "$finedining_diet_recent",
                foodName,
                stack.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.Equals(
                slot.TooltipLanguage,
                language,
                System.StringComparison.OrdinalIgnoreCase)
            && GetCurrentTooltip() == slot.Tooltip)
        {
            UITooltip.HideTooltip();
        }

        slot.Tooltip.Set(string.Empty, description);
        slot.TooltipDirty = false;
        slot.TooltipIsChef = isChef;
        slot.TooltipStack = stack;
        slot.TooltipMultiplier = multiplier;
        slot.TooltipLanguage = language;
    }

    private static void HideSlot(SlotContext slot)
    {
        if (slot.Root == null || slot.Icon == null)
        {
            return;
        }

        UpdateTooltipHover(slot.Icon, slot.Tooltip, canHover: false);
        slot.Root.gameObject.SetActive(false);
    }

    internal static void UpdateTooltipHover(Image icon, UITooltip? tooltip, bool canHover)
    {
        Canvas canvas = icon.canvas;
        Camera? eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
        bool hovered = canHover
                       && Cursor.visible
                       && tooltip != null
                       && RectTransformUtility.RectangleContainsScreenPoint(icon.rectTransform, ZInput.mousePosition, eventCamera);

        if (hovered && tooltip != null && GetCurrentTooltip() != tooltip)
        {
            tooltip.OnHoverStart(icon.gameObject);
        }
        else if (!hovered && GetCurrentTooltip() == tooltip)
        {
            UITooltip.HideTooltip();
        }
    }

    private static UITooltip? GetCurrentTooltip()
    {
        return CurrentTooltipField();
    }

    private sealed class PanelContext
    {
        public RectTransform Root = null!;
        public RectTransform RecentRow = null!;
        public RectTransform ChefRow = null!;
        public FullStraightContext? FullStraight;
        public List<SlotContext> RecentSlots = new();
        public List<SlotContext> ChefSlots = new();
        public Player? ChefPlayer;
        public ObjectDB? ChefObjectDb;
        public int KnownRecipeCount = -1;
        public int KnownMaterialCount = -1;
        public int ObjectDbItemCount = -1;
    }

    private sealed class FullStraightContext
    {
        public RectTransform Root = null!;
        public Image Background = null!;
        public TextMeshProUGUI Star = null!;
        public UITooltip? Tooltip;
        public string TooltipLanguage = string.Empty;
    }

    private sealed class SlotContext
    {
        public RectTransform Root = null!;
        public Image Icon = null!;
        public UITooltip? Tooltip;
        public TextMeshProUGUI CornerText = null!;
        public TextMeshProUGUI FooterText = null!;
        public string ItemKey = string.Empty;
        public string ItemNameToken = string.Empty;
        public ObjectDB? ItemObjectDb;
        public int ItemObjectDbCount = -1;
        public bool TooltipDirty = true;
        public bool TooltipIsChef;
        public int TooltipStack = -1;
        public float TooltipMultiplier = float.NaN;
        public string TooltipLanguage = string.Empty;
    }
}
