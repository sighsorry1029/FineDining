using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
    private const float HoverPanelGap = 6f;
    private const float HoverPanelHeight = 48f;
    private const float HoverPanelMinimumWidth = 460f;
    private const float HoverPanelFontSize = 15f;
    private const float HoverPanelMinimumFontSize = 10f;
    private const float HoverPanelShowDelay = 0.5f;
    private const float EatenHoverRefreshInterval = 0.2f;
    private const float EatenArrowThickness = 2f;
    private const float EatenArrowHeadLength = 9f;
    private const string FullCourseIconResourceName = "FineDining.Resources.UI.FullCourseIcon.png";
    private const float FullCourseIconSize = 52f;
    private const float FullCourseTooltipGap = 6f;
    private const float FullCourseTooltipCanvasMargin = 6f;
    private const float FullCourseTooltipWidth = 340f;
    private const float FullCourseTooltipHeight = 96f;
    private const float FullCourseTooltipFontSize = 14f;
    private const float FullCourseTooltipMinimumFontSize = 10f;

    private static readonly Vector3[] RectCorners = new Vector3[4];
    private static readonly Color DefaultBackground = new(0f, 0f, 0f, 0.45f);
    private static readonly AccessTools.FieldRef<UITooltip> CurrentTooltipField =
        AccessTools.StaticFieldRefAccess<UITooltip>(
            AccessTools.DeclaredField(typeof(UITooltip), "m_current")
            ?? throw new System.MissingFieldException(typeof(UITooltip).FullName, "m_current"));
    private static Sprite? _fullCourseIconSprite;
    private static bool _fullCourseIconLoadAttempted;
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
        UpdateFullCourseIndicator(context, hud, player);

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

        UpdateHoverGuidance(context);
        UpdateRecentSlots(context.RecentSlots, state, context.RecentGuidance);
        UpdateChefSlots(context.ChefSlots, state, context.ChefGuidance);
        UpdateHoverPanel(context.RecentHover, context.RecentSlots);
        UpdateHoverPanel(context.ChefHover, context.ChefSlots);
        UpdateEatenHover(context, hud, player, state);
    }

    private static void UpdateFullCourseIndicator(PanelContext context, Hud hud, Player player)
    {
        if (!FoodRules.IsFullCourseActive(player))
        {
            HideFullCourseIndicator(context.FullCourse);
            return;
        }

        if (!TryGetTopFoodBounds(context.Root, hud, out Rect topFoodBounds))
        {
            HideFullCourseIndicator(context.FullCourse);
            return;
        }

        if (context.FullCourse?.Root == null)
        {
            context.FullCourse = null;
            context.FullCourse = CreateFullCourseIndicator(context.Root, hud);
            if (context.FullCourse == null)
            {
                return;
            }
        }

        FullCourseContext indicator = context.FullCourse;
        Vector2 indicatorSize = Vector2.one * FullCourseIconSize;
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
                float compactSize = Mathf.Min(indicatorSize.y, availableHeight);
                indicatorSize = Vector2.one * compactSize;
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
        indicator.Root.anchoredPosition = new Vector2(centerX, centerY);
        EnsureFullCourseTooltip(indicator);

        indicator.Root.gameObject.SetActive(true);
        UpdateFullCourseTooltipPanel(indicator);
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

    private static FullCourseContext? CreateFullCourseIndicator(RectTransform parent, Hud hud)
    {
        if (hud.m_foodTime.Length == 0 || hud.m_foodTime[0] == null || hud.m_foodTime[0].font == null)
        {
            return null;
        }

        GameObject rootObject = new("FullCourse", typeof(RectTransform));
        rootObject.SetActive(false);
        RectTransform root = rootObject.GetComponent<RectTransform>();
        root.SetParent(parent, false);
        root.anchorMin = new Vector2(parent.pivot.x, parent.pivot.y);
        root.anchorMax = root.anchorMin;
        root.pivot = new Vector2(0.5f, 0.5f);

        GameObject backgroundObject = new("Background", typeof(RectTransform), typeof(Image));
        RectTransform backgroundRect = backgroundObject.GetComponent<RectTransform>();
        backgroundRect.SetParent(root, false);
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;

        Image background = backgroundObject.GetComponent<Image>();
        background.color = Color.clear;
        background.raycastTarget = false;

        GameObject iconObject = new("Icon", typeof(RectTransform), typeof(Image));
        RectTransform iconRect = iconObject.GetComponent<RectTransform>();
        iconRect.SetParent(root, false);
        iconRect.anchorMin = Vector2.zero;
        iconRect.anchorMax = Vector2.one;
        iconRect.offsetMin = new Vector2(2f, 2f);
        iconRect.offsetMax = new Vector2(-2f, -2f);

        Image icon = iconObject.GetComponent<Image>();
        icon.sprite = LoadFullCourseIconSprite();
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        icon.enabled = icon.sprite != null;

        TMP_Text template = hud.m_foodTime[0];

        TextMeshProUGUI multiplier = CreateText(
            root,
            template,
            "Multiplier",
            new Vector2(3f, 3f),
            new Vector2(-3f, -3f),
            TextAlignmentOptions.Center,
            12f);
        multiplier.fontStyle = FontStyles.Bold;
        multiplier.text = string.Empty;
        multiplier.gameObject.SetActive(true);

        Outline multiplierOutline = multiplier.gameObject.AddComponent<Outline>();
        multiplierOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);
        multiplierOutline.effectDistance = new Vector2(1f, -1f);
        multiplierOutline.useGraphicAlpha = true;

        Shadow multiplierShadow = multiplier.gameObject.AddComponent<Shadow>();
        multiplierShadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
        multiplierShadow.effectDistance = new Vector2(1f, -1f);
        multiplierShadow.useGraphicAlpha = true;

        HoverPanelContext tooltipPanel = CreateFullCourseTooltipPanel(parent, template);

        FullCourseContext indicator = new()
        {
            Root = root,
            Background = background,
            Icon = icon,
            Multiplier = multiplier,
            TooltipPanel = tooltipPanel
        };
        return indicator;
    }

    private static void EnsureFullCourseTooltip(FullCourseContext indicator)
    {
        float multiplier = DietConfig.GetFullCourseMultiplier();
        string multiplierValueText = multiplier.ToString(
            "0.00",
            CultureInfo.InvariantCulture);
        string multiplierText = $"x{multiplierValueText}";
        if (!string.Equals(
                indicator.Multiplier.text,
                multiplierText,
                System.StringComparison.Ordinal))
        {
            indicator.Multiplier.text = multiplierText;
        }

        Localization localization = Localization.instance;
        string language = localization.GetSelectedLanguage();
        if (string.Equals(
                indicator.TooltipLanguage,
                language,
                System.StringComparison.OrdinalIgnoreCase) &&
            Mathf.Approximately(indicator.TooltipMultiplier, multiplier))
        {
            return;
        }

        string title = localization.Localize("$finedining_diet_full_course_title");
        string description = localization.Localize(
            "$finedining_diet_full_course_description",
            multiplierValueText);
        indicator.TooltipPanel.Text.text =
            $"<color=orange><b>{title}</b></color>\n{description}";
        indicator.TooltipLanguage = language;
        indicator.TooltipMultiplier = multiplier;
    }

    private static HoverPanelContext CreateFullCourseTooltipPanel(
        RectTransform parent,
        TMP_Text template)
    {
        GameObject rootObject = new(
            "FullCourseTooltip",
            typeof(RectTransform),
            typeof(Image));
        rootObject.SetActive(false);
        RectTransform root = rootObject.GetComponent<RectTransform>();
        root.SetParent(parent, false);
        root.anchorMin = new Vector2(parent.pivot.x, parent.pivot.y);
        root.anchorMax = root.anchorMin;
        root.pivot = new Vector2(0f, 0.5f);
        root.sizeDelta = new Vector2(FullCourseTooltipWidth, FullCourseTooltipHeight);

        Image background = rootObject.GetComponent<Image>();
        background.color = DefaultBackground;
        background.raycastTarget = false;

        TextMeshProUGUI text = CreateText(
            root,
            template,
            "Text",
            new Vector2(8f, 5f),
            new Vector2(-8f, -5f),
            TextAlignmentOptions.TopLeft,
            FullCourseTooltipFontSize);
        text.enableAutoSizing = true;
        text.fontSizeMin = FullCourseTooltipMinimumFontSize;
        text.fontSizeMax = FullCourseTooltipFontSize;
        text.maxVisibleLines = 5;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.richText = true;
        text.gameObject.SetActive(true);

        return new HoverPanelContext
        {
            Root = root,
            Text = text
        };
    }

    private static void UpdateFullCourseTooltipPanel(FullCourseContext indicator)
    {
        bool hovered = indicator.Background.isActiveAndEnabled
                       && indicator.Root.gameObject.activeInHierarchy
                       && IsHovered(indicator.Background, canHover: true);
        if (indicator.Hovered != hovered)
        {
            indicator.Hovered = hovered;
            indicator.HoverStartedAt = Time.unscaledTime;
            indicator.TooltipPanel.Root.gameObject.SetActive(false);
            return;
        }

        bool visible = hovered
                       && Time.unscaledTime - indicator.HoverStartedAt >= HoverPanelShowDelay;
        if (visible)
        {
            LayoutFullCourseTooltipPanel(indicator);
        }

        if (indicator.TooltipPanel.Root.gameObject.activeSelf != visible)
        {
            indicator.TooltipPanel.Root.gameObject.SetActive(visible);
        }
    }

    private static void LayoutFullCourseTooltipPanel(FullCourseContext indicator)
    {
        RectTransform panel = indicator.TooltipPanel.Root;
        if (panel.parent is not RectTransform parent)
        {
            return;
        }

        Rect iconBounds = GetRectInParent(indicator.Root, parent);
        float width = FullCourseTooltipWidth;
        float height = FullCourseTooltipHeight;
        float x = iconBounds.xMax + FullCourseTooltipGap;
        float y = iconBounds.center.y;

        Canvas canvas = indicator.Root.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.rootCanvas.transform is RectTransform canvasRoot)
        {
            Rect canvasBounds = GetRectInParent(canvasRoot, parent);
            width = Mathf.Min(
                width,
                Mathf.Max(1f, canvasBounds.width - FullCourseTooltipCanvasMargin * 2f));
            height = Mathf.Min(
                height,
                Mathf.Max(1f, canvasBounds.height - FullCourseTooltipCanvasMargin * 2f));

            float rightX = iconBounds.xMax + FullCourseTooltipGap;
            float leftX = iconBounds.xMin - FullCourseTooltipGap - width;
            x = rightX + width <= canvasBounds.xMax - FullCourseTooltipCanvasMargin
                ? rightX
                : leftX;

            float minimumX = canvasBounds.xMin + FullCourseTooltipCanvasMargin;
            float maximumX = canvasBounds.xMax - FullCourseTooltipCanvasMargin - width;
            x = Mathf.Clamp(x, minimumX, Mathf.Max(minimumX, maximumX));

            float minimumY = canvasBounds.yMin + FullCourseTooltipCanvasMargin + height * 0.5f;
            float maximumY = canvasBounds.yMax - FullCourseTooltipCanvasMargin - height * 0.5f;
            y = Mathf.Clamp(y, minimumY, Mathf.Max(minimumY, maximumY));
        }

        panel.sizeDelta = new Vector2(width, height);
        panel.anchoredPosition = new Vector2(x, y);
    }

    private static Sprite? LoadFullCourseIconSprite()
    {
        if (_fullCourseIconLoadAttempted)
        {
            return _fullCourseIconSprite;
        }

        _fullCourseIconLoadAttempted = true;
        try
        {
            using Stream? stream = typeof(HudFoodPanels).Assembly.GetManifestResourceStream(
                FullCourseIconResourceName);
            if (stream == null)
            {
                FineDiningPlugin.Log.LogWarning(
                    $"Embedded Full Course icon was not found: {FullCourseIconResourceName}");
                return null;
            }

            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            Texture2D texture = Jotunn.Utils.AssetUtils.LoadImage(buffer.ToArray());
            if (texture == null)
            {
                FineDiningPlugin.Log.LogWarning("Embedded Full Course icon could not be decoded.");
                return null;
            }

            texture.name = "FineDining_FullCourseIcon_Texture";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            _fullCourseIconSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            _fullCourseIconSprite.name = "FineDining_FullCourseIcon";
            return _fullCourseIconSprite;
        }
        catch (System.Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                $"Embedded Full Course icon load failed: {exception.Message}");
            return null;
        }
    }

    private static void HideFullCourseIndicator(FullCourseContext? indicator)
    {
        if (indicator?.Root == null)
        {
            return;
        }

        indicator.Hovered = false;
        indicator.HoverStartedAt = 0f;
        indicator.TooltipPanel.Root.gameObject.SetActive(false);
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
            HideFullCourseIndicator(_context.FullCourse);
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
        HoverPanelContext recentHover = CreateHoverPanel(root, hud.m_foodTime[0], "RecentHover");
        HoverPanelContext chefHover = CreateHoverPanel(root, hud.m_foodTime[0], "ChefHover");
        RectTransform arrowRoot = CreateRow(root, "EatenFoodArrow");
        arrowRoot.sizeDelta = Vector2.zero;
        arrowRoot.gameObject.SetActive(false);
        RectTransform[] arrowSegments = new RectTransform[3];
        for (int index = 0; index < arrowSegments.Length; index++)
        {
            GameObject segment = new("Segment" + index, typeof(RectTransform), typeof(Image));
            RectTransform segmentRect = (RectTransform)segment.transform;
            segmentRect.SetParent(arrowRoot, false);
            segmentRect.anchorMin = Vector2.zero;
            segmentRect.anchorMax = Vector2.zero;
            segmentRect.pivot = new Vector2(0f, 0.5f);
            Image line = segment.GetComponent<Image>();
            line.color = Color.white;
            line.raycastTarget = false;
            arrowSegments[index] = segmentRect;
        }

        HoverPanelContext eatenHover = CreateHoverPanel(root, hud.m_foodTime[0], "EatenHover");
        eatenHover.Text.alignment = TextAlignmentOptions.TopLeft;

        PanelContext context = new()
        {
            Root = root,
            RecentRow = recentRow,
            ChefRow = chefRow,
            RecentHover = recentHover,
            ChefHover = chefHover,
            EatenHover = eatenHover,
            EatenArrowRoot = arrowRoot,
            EatenArrowSegments = arrowSegments
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

        float availableWidth = GetAvailableHoverWidth(context.Root, parent, panelWidth);
        float hoverWidth = Mathf.Min(Mathf.Max(panelWidth, HoverPanelMinimumWidth), availableWidth);
        LayoutHoverPanel(
            context.RecentHover,
            hoverWidth,
            new Vector2(0f, HoverPanelGap),
            new Vector2(0f, 0f));
        LayoutHoverPanel(
            context.ChefHover,
            hoverWidth,
            new Vector2(0f, chefRowOffset - slotSize.y - HoverPanelGap),
            new Vector2(0f, 1f));
        LayoutHoverPanel(
            context.EatenHover,
            hoverWidth,
            new Vector2(0f, HoverPanelGap),
            new Vector2(0f, 0f));
    }

    private static float GetAvailableHoverWidth(
        RectTransform root,
        RectTransform parent,
        float panelWidth)
    {
        Canvas canvas = root.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.rootCanvas.transform is not RectTransform canvasRoot)
        {
            return Mathf.Max(panelWidth, HoverPanelMinimumWidth);
        }

        Rect canvasBounds = GetRectInParent(canvasRoot, parent);
        float availableWidth = canvasBounds.xMax - root.localPosition.x - HoverPanelGap;
        return Mathf.Max(panelWidth, availableWidth);
    }

    private static void LayoutHoverPanel(
        HoverPanelContext panel,
        float width,
        Vector2 anchoredPosition,
        Vector2 pivot)
    {
        panel.Root.anchorMin = new Vector2(0f, 1f);
        panel.Root.anchorMax = panel.Root.anchorMin;
        panel.Root.pivot = pivot;
        panel.Root.sizeDelta = new Vector2(width, HoverPanelHeight);
        panel.Root.anchoredPosition = anchoredPosition;
    }

    private static RectTransform CreateRow(RectTransform parent, string name)
    {
        GameObject rowObject = new(name, typeof(RectTransform));
        RectTransform row = rowObject.GetComponent<RectTransform>();
        row.SetParent(parent, false);
        row.pivot = new Vector2(0f, 1f);
        return row;
    }

    private static HoverPanelContext CreateHoverPanel(
        RectTransform parent,
        TMP_Text template,
        string name)
    {
        GameObject rootObject = new(name, typeof(RectTransform), typeof(Image));
        rootObject.SetActive(false);
        RectTransform root = rootObject.GetComponent<RectTransform>();
        root.SetParent(parent, false);

        Image background = rootObject.GetComponent<Image>();
        background.color = DefaultBackground;
        background.raycastTarget = false;

        TextMeshProUGUI text = CreateText(
            root,
            template,
            "Text",
            new Vector2(8f, 4f),
            new Vector2(-8f, -4f),
            TextAlignmentOptions.Center,
            HoverPanelFontSize);
        text.enableAutoSizing = true;
        text.fontSizeMin = HoverPanelMinimumFontSize;
        text.fontSizeMax = HoverPanelFontSize;
        text.maxVisibleLines = 2;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.richText = true;
        text.gameObject.SetActive(true);

        return new HoverPanelContext
        {
            Root = root,
            Text = text
        };
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
        icon.raycastTarget = false;

        TextMeshProUGUI cornerText = CreateText(root, hud.m_foodTime[0], "Corner", new Vector2(4f, -2f), new Vector2(-3f, 0f), TextAlignmentOptions.TopRight, 13f);
        TextMeshProUGUI footerText = CreateText(root, hud.m_foodTime[0], "Footer", new Vector2(2f, 1f), new Vector2(-2f, 3f), TextAlignmentOptions.BottomRight, 11f);

        return new SlotContext
        {
            Root = root,
            Icon = icon,
            CornerText = cornerText,
            FooterText = footerText
        };
    }

    internal static string FormatFoodNameForTooltip(string? foodName) =>
        string.IsNullOrWhiteSpace(foodName)
            ? string.Empty
            : $"<color=orange>{foodName}</color>";

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

    private static void UpdateHoverGuidance(PanelContext context)
    {
        Localization localization = Localization.instance;
        string language = localization.GetSelectedLanguage();
        bool recentPreferenceEnabled =
            DietConfig.GetChefRecentFoodPreferencePercent() > 0f;
        bool chefTierEnabled =
            DietConfig.GetChefHighTierSelectionStrength() > 0f;
        float chefMultiplierMinimum = DietConfig.GetChefMultiplierMin();
        float chefMultiplierMode = DietConfig.GetChefMultiplierModeAtMaxCooking();
        bool chefMultiplierEnabled = chefMultiplierMode > chefMultiplierMinimum
                                     && !Mathf.Approximately(
                                         chefMultiplierMode,
                                         chefMultiplierMinimum);

        if (context.GuidanceInitialized
            && string.Equals(
                context.GuidanceLanguage,
                language,
                System.StringComparison.OrdinalIgnoreCase)
            && context.RecentPreferenceEnabled == recentPreferenceEnabled
            && context.ChefTierEnabled == chefTierEnabled
            && context.ChefMultiplierEnabled == chefMultiplierEnabled)
        {
            return;
        }

        context.GuidanceLanguage = language;
        context.GuidanceInitialized = true;
        context.RecentPreferenceEnabled = recentPreferenceEnabled;
        context.ChefTierEnabled = chefTierEnabled;
        context.ChefMultiplierEnabled = chefMultiplierEnabled;
        context.RecentGuidance = localization.Localize(
            recentPreferenceEnabled
                ? "$finedining_diet_recent_chef_guidance"
                : "$finedining_diet_recent_chef_guidance_disabled");
        context.ChefGuidance = localization.Localize(
            chefTierEnabled && chefMultiplierEnabled
                ? "$finedining_diet_chef_cooking_guidance_both"
                : chefTierEnabled
                    ? "$finedining_diet_chef_cooking_guidance_tier"
                    : chefMultiplierEnabled
                        ? "$finedining_diet_chef_cooking_guidance_multiplier"
                        : "$finedining_diet_chef_cooking_guidance_disabled");

        MarkHoverTextDirty(context.RecentSlots);
        MarkHoverTextDirty(context.ChefSlots);
    }

    private static void MarkHoverTextDirty(List<SlotContext> slots)
    {
        foreach (SlotContext slot in slots)
        {
            slot.HoverTextDirty = true;
        }
    }

    private static void UpdateRecentSlots(
        List<SlotContext> slots,
        PlayerFoodStateData state,
        string guidance)
    {
        for (int index = 0; index < slots.Count; index++)
        {
            if (index >= state.Recent.Count)
            {
                HideSlot(slots[index]);
                continue;
            }

            HistoryEntryData entry = state.Recent[index];
            int regularNextStack = RecentHistoryService.GetNextStack(
                state,
                entry.Key,
                isChef: false);
            float nextDiminishingScale = FoodRules.CalculateDiminishingScale(regularNextStack);
            bool wouldDiminish = nextDiminishingScale < 1f &&
                                  !Mathf.Approximately(nextDiminishingScale, 1f);
            bool isChef = ChefCollectionService.GetEntry(state, entry.Key) != null;
            bool chefExemptsDiminishing = isChef && wouldDiminish;
            float previewScale = chefExemptsDiminishing ? 1f : nextDiminishingScale;
            ShowSlot(
                slots[index],
                entry.Key,
                entry.Stack > 1 ? entry.Stack.ToString(CultureInfo.InvariantCulture) : string.Empty,
                wouldDiminish && !chefExemptsDiminishing
                    ? $"x{nextDiminishingScale.ToString("0.00", CultureInfo.InvariantCulture)}"
                    : string.Empty,
                isChef: false,
                chefExemptsDiminishing,
                stack: entry.Stack,
                multiplier: previewScale,
                guidance);
        }
    }

    private static void UpdateChefSlots(
        List<SlotContext> slots,
        PlayerFoodStateData state,
        string guidance)
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
                chefExemptsDiminishing: false,
                stack: 0,
                entry.Multiplier,
                guidance);
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
        bool chefExemptsDiminishing,
        int stack,
        float multiplier,
        string guidance)
    {
        UpdateSlotItem(slot, key);
        UpdateSlotHoverText(
            slot,
            isChef,
            chefExemptsDiminishing,
            stack,
            multiplier,
            guidance);
        slot.Root.gameObject.SetActive(true);
        slot.CornerText.text = cornerText;
        slot.CornerText.gameObject.SetActive(!string.IsNullOrWhiteSpace(cornerText));
        slot.FooterText.text = footerText;
        slot.FooterText.gameObject.SetActive(!string.IsNullOrWhiteSpace(footerText));
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
        slot.HoverTextDirty = true;
    }

    private static void UpdateSlotHoverText(
        SlotContext slot,
        bool isChef,
        bool chefExemptsDiminishing,
        int stack,
        float multiplier,
        string guidance)
    {
        Localization localization = Localization.instance;
        string language = localization.GetSelectedLanguage();
        if (!slot.HoverTextDirty
            && slot.HoverIsChef == isChef
            && slot.HoverChefExemptsDiminishing == chefExemptsDiminishing
            && slot.HoverStack == stack
            && Mathf.Approximately(slot.HoverMultiplier, multiplier)
            && slot.HoverLanguage == language
            && slot.HoverGuidance == guidance)
        {
            return;
        }

        string foodName = FormatFoodNameForTooltip(
            localization.Localize(slot.ItemNameToken));
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
            else
            {
                description = localization.Localize(
                    "$finedining_diet_chef_unchanged",
                    foodName);
            }
        }
        else if (chefExemptsDiminishing)
        {
            description = localization.Localize(
                "$finedining_diet_recent_chef_exempt",
                foodName,
                stack.ToString(CultureInfo.InvariantCulture));
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

        slot.HoverText = isChef
            ? description + "\n" + guidance
            : guidance + "\n" + description;
        slot.HoverTextDirty = false;
        slot.HoverIsChef = isChef;
        slot.HoverChefExemptsDiminishing = chefExemptsDiminishing;
        slot.HoverStack = stack;
        slot.HoverMultiplier = multiplier;
        slot.HoverLanguage = language;
        slot.HoverGuidance = guidance;
    }

    private static void HideSlot(SlotContext slot)
    {
        if (slot.Root == null || slot.Icon == null)
        {
            return;
        }

        slot.Root.gameObject.SetActive(false);
    }

    private static void UpdateHoverPanel(
        HoverPanelContext panel,
        List<SlotContext> slots)
    {
        SlotContext? hoveredSlot = null;
        foreach (SlotContext slot in slots)
        {
            if (!string.IsNullOrWhiteSpace(slot.HoverText)
                && slot.Root.gameObject.activeInHierarchy
                && slot.Icon.enabled
                && IsHovered(slot.Icon, canHover: true))
            {
                hoveredSlot = slot;
                break;
            }
        }

        if (!ReferenceEquals(panel.HoveredSlot, hoveredSlot))
        {
            panel.HoveredSlot = hoveredSlot;
            panel.HoverStartedAt = Time.unscaledTime;
            panel.Root.gameObject.SetActive(false);
            return;
        }

        string hoverText = hoveredSlot?.HoverText ?? string.Empty;
        bool visible = hoveredSlot != null
                       && Time.unscaledTime - panel.HoverStartedAt >= HoverPanelShowDelay;
        if (visible && !string.Equals(
                panel.Text.text,
                hoverText,
                System.StringComparison.Ordinal))
        {
            panel.Text.text = hoverText;
        }

        if (panel.Root.gameObject.activeSelf != visible)
        {
            panel.Root.gameObject.SetActive(visible);
        }
    }

    private static void UpdateEatenHover(
        PanelContext context,
        Hud hud,
        Player player,
        PlayerFoodStateData state)
    {
        Player.Food? hoveredFood = null;
        Image? hoveredIcon = null;
        List<Player.Food> foods = player.GetFoods();
        for (int index = 0; index < hud.m_foodIcons.Length; index++)
        {
            Image icon = hud.m_foodIcons[index];
            if (icon == null)
            {
                continue;
            }

            DisableEatenCursorTooltip(icon);
            if (hoveredFood == null && index < foods.Count &&
                foods[index]?.m_item?.m_shared != null &&
                icon.isActiveAndEnabled && icon.gameObject.activeInHierarchy &&
                IsHovered(icon, canHover: true))
            {
                hoveredFood = foods[index];
                hoveredIcon = icon;
            }
        }

        HoverPanelContext panel = context.EatenHover;
        if (!ReferenceEquals(context.EatenHoveredFood, hoveredFood) ||
            context.EatenHoveredIcon != hoveredIcon)
        {
            context.EatenHoveredFood = hoveredFood;
            context.EatenHoveredIcon = hoveredIcon;
            panel.HoverStartedAt = Time.unscaledTime;
            context.EatenTextRefreshAt = 0f;
        }

        bool visible = hoveredFood != null && hoveredIcon != null &&
                       Time.unscaledTime - panel.HoverStartedAt >= HoverPanelShowDelay;
        panel.Root.gameObject.SetActive(visible);
        context.EatenArrowRoot.gameObject.SetActive(visible);
        if (!visible)
        {
            return;
        }

        // The fixed area is shared visually with recent-history guidance, never
        // with a second cursor-following tooltip.
        context.RecentHover.Root.gameObject.SetActive(false);
        context.ChefHover.Root.gameObject.SetActive(false);
        LayoutEatenArrow(context, hoveredIcon!);

        if (Time.unscaledTime < context.EatenTextRefreshAt)
        {
            return;
        }

        context.EatenTextRefreshAt = Time.unscaledTime + EatenHoverRefreshInterval;
        bool isDietFood = FoodIdentity.IsDirectlyEdible(hoveredFood!.m_item);
        float fullCourseScale = isDietFood
            ? FoodRules.GetFullCourseScale(player, state, FoodRules.CountActiveDietFoods(foods))
            : 1f;
        float extraScale = isDietFood
            ? CalculateExtraEffectScale(
                FoodRules.GetAppliedScale(player, state, hoveredFood),
                DietConfig.GetBaseSlotScale(FoodSlotProgression.GetCurrentSlots(player, state)),
                fullCourseScale)
            : 1f;
        ActiveFoodData? active = isDietFood
            ? FoodRules.GetActiveFood(state, FoodIdentity.GetCanonicalPrefabName(hoveredFood))
            : null;
        string foodName = Localization.instance.Localize(hoveredFood.m_item.m_shared.m_name);
        string text = FormatEatenFoodHover(
            foodName,
            FineDiningLocalization.LocalizeOrFallback("$finedining_diet_extra_effect", "Net effect"),
            new[]
            {
                FineDiningLocalization.LocalizeOrFallback("$finedining_diet_full_course_title", "Full Course"),
                FineDiningLocalization.LocalizeOrFallback("$finedining_diet_effect_chef", "Chef"),
                FineDiningLocalization.LocalizeOrFallback("$finedining_diet_effect_freshness", "Freshness"),
                FineDiningLocalization.LocalizeOrFallback("$finedining_diet_effect_diminishing", "Diminish")
            },
            extraScale,
            fullCourseScale,
            active);
        if (!string.Equals(panel.Text.text, text, System.StringComparison.Ordinal))
        {
            panel.Text.text = text;
        }
    }

    private static void DisableEatenCursorTooltip(Image icon)
    {
        UITooltip? tooltip = icon.GetComponent<UITooltip>();
        if (tooltip == null)
        {
            return;
        }

        if (CurrentTooltipField() == tooltip)
        {
            UITooltip.HideTooltip();
        }

        tooltip.Set(string.Empty, string.Empty);
        tooltip.enabled = false;
    }

    internal static float CalculateExtraEffectScale(
        float appliedScale,
        float baseSlotScale,
        float fullCourseScale)
    {
        if (float.IsNaN(appliedScale) || float.IsInfinity(appliedScale) || appliedScale < 0f ||
            float.IsNaN(baseSlotScale) || float.IsInfinity(baseSlotScale) || baseSlotScale <= 0f ||
            float.IsNaN(fullCourseScale) || float.IsInfinity(fullCourseScale) || fullCourseScale <= 0f)
        {
            return 1f;
        }

        // AppliedScale already contains the base slot scale. Exclude that
        // baseline, but keep the live Full Course bonus. Never read food time.
        float extraScale = appliedScale / baseSlotScale * fullCourseScale;
        return float.IsNaN(extraScale) || float.IsInfinity(extraScale) ? 1f : extraScale;
    }

    internal static string FormatEatenFoodHover(
        string foodName,
        string extraEffectLabel,
        string[] factorLabels,
        float extraScale,
        float fullCourseScale,
        ActiveFoodData? active)
    {
        float displayedScale = FoodEffectUiText.RoundMultiplierForDisplay(extraScale);
        string color = displayedScale > 1f
            ? FoodEffectUiText.PositiveModifierColorHex
            : displayedScale < 1f
                ? FoodEffectUiText.PenaltyModifierColorHex
                : FoodEffectUiText.NeutralModifierColorHex;
        string summary = $"{FormatFoodNameForTooltip(foodName)} — {extraEffectLabel} " +
                         $"<color={color}>×{displayedScale.ToString("0.00", CultureInfo.InvariantCulture)}</color>";
        if (active == null || !active.HasEffectBreakdown)
        {
            // A combined saved scale cannot reconstruct historical factors.
            return summary;
        }

        StringBuilder details = new();
        AppendEatenFactor(details, factorLabels[0], fullCourseScale);
        AppendEatenFactor(details, factorLabels[1], active.ChefMultiplier);
        AppendEatenFactor(details, factorLabels[2], active.FreshnessScale);
        AppendEatenFactor(details, factorLabels[3], active.DiminishingScale);
        return details.Length == 0 ? summary : summary + "\n" + details;
    }

    private static void AppendEatenFactor(StringBuilder details, string label, float multiplier)
    {
        float displayed = FoodEffectUiText.RoundMultiplierForDisplay(multiplier);
        if (displayed == 1f)
        {
            return;
        }

        if (details.Length > 0)
        {
            details.Append(" · ");
        }

        string color = displayed > 1f
            ? FoodEffectUiText.PositiveModifierColorHex
            : FoodEffectUiText.PenaltyModifierColorHex;
        details.Append("<color=").Append(color).Append('>')
            .Append(label).Append(" ×")
            .Append(displayed.ToString("0.00", CultureInfo.InvariantCulture))
            .Append("</color>");
    }

    private static void LayoutEatenArrow(PanelContext context, Image icon)
    {
        RectTransform root = context.EatenArrowRoot;
        Rect iconBounds = GetRectInParent(icon.rectTransform, root);
        Rect panelBounds = GetRectInParent(context.EatenHover.Root, root);
        Vector2 start = new(iconBounds.xMax + 3f, iconBounds.center.y);
        Vector2 end = new(panelBounds.xMin - 3f, panelBounds.center.y);
        Vector2 direction = (end - start).normalized;
        Vector2 perpendicular = new(-direction.y, direction.x);
        Vector2 headBase = end - direction * EatenArrowHeadLength;
        LayoutArrowSegment(context.EatenArrowSegments[0], start, end);
        LayoutArrowSegment(context.EatenArrowSegments[1], end, headBase + perpendicular * 4f);
        LayoutArrowSegment(context.EatenArrowSegments[2], end, headBase - perpendicular * 4f);
    }

    private static void LayoutArrowSegment(RectTransform segment, Vector2 start, Vector2 end)
    {
        Vector2 delta = end - start;
        segment.anchoredPosition = start;
        segment.sizeDelta = new Vector2(delta.magnitude, EatenArrowThickness);
        segment.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    private static bool IsHovered(Image icon, bool canHover)
    {
        Canvas canvas = icon.canvas;
        Camera? eventCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
            ? canvas.worldCamera
            : null;
        return canHover
               && Cursor.visible
               && RectTransformUtility.RectangleContainsScreenPoint(
                   icon.rectTransform,
                   ZInput.mousePosition,
                   eventCamera);
    }

    private sealed class PanelContext
    {
        public RectTransform Root = null!;
        public RectTransform RecentRow = null!;
        public RectTransform ChefRow = null!;
        public HoverPanelContext RecentHover = null!;
        public HoverPanelContext ChefHover = null!;
        public HoverPanelContext EatenHover = null!;
        public RectTransform EatenArrowRoot = null!;
        public RectTransform[] EatenArrowSegments = null!;
        public Player.Food? EatenHoveredFood;
        public Image? EatenHoveredIcon;
        public float EatenTextRefreshAt;
        public FullCourseContext? FullCourse;
        public List<SlotContext> RecentSlots = new();
        public List<SlotContext> ChefSlots = new();
        public string RecentGuidance = string.Empty;
        public string ChefGuidance = string.Empty;
        public string GuidanceLanguage = string.Empty;
        public bool GuidanceInitialized;
        public bool RecentPreferenceEnabled;
        public bool ChefTierEnabled;
        public bool ChefMultiplierEnabled;
        public Player? ChefPlayer;
        public ObjectDB? ChefObjectDb;
        public int KnownRecipeCount = -1;
        public int KnownMaterialCount = -1;
        public int ObjectDbItemCount = -1;
    }

    private sealed class HoverPanelContext
    {
        public RectTransform Root = null!;
        public TextMeshProUGUI Text = null!;
        public SlotContext? HoveredSlot;
        public float HoverStartedAt;
    }

    private sealed class FullCourseContext
    {
        public RectTransform Root = null!;
        public Image Background = null!;
        public Image Icon = null!;
        public TextMeshProUGUI Multiplier = null!;
        public HoverPanelContext TooltipPanel = null!;
        public bool Hovered;
        public float HoverStartedAt;
        public string TooltipLanguage = string.Empty;
        public float TooltipMultiplier = float.NaN;
    }

    private sealed class SlotContext
    {
        public RectTransform Root = null!;
        public Image Icon = null!;
        public TextMeshProUGUI CornerText = null!;
        public TextMeshProUGUI FooterText = null!;
        public string ItemKey = string.Empty;
        public string ItemNameToken = string.Empty;
        public ObjectDB? ItemObjectDb;
        public int ItemObjectDbCount = -1;
        public string HoverText = string.Empty;
        public string HoverGuidance = string.Empty;
        public bool HoverTextDirty = true;
        public bool HoverIsChef;
        public bool HoverChefExemptsDiminishing;
        public int HoverStack = -1;
        public float HoverMultiplier = float.NaN;
        public string HoverLanguage = string.Empty;
    }
}
