using System;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FineDining;

internal static class CookingSkillTooltipPanel
{
    // InventoryGui owns one SkillsDialog and one Cooking row. Keep their lifetime together.
    private static Binding? _binding;
    private static readonly Vector3[] Corners = new Vector3[4];
    private static bool _layoutFailureLogged;

    internal static void Clear()
    {
        Binding? binding = _binding;
        _binding = null;
        if (binding == null)
        {
            return;
        }

        if (binding.Tooltip != null)
        {
            binding.Tooltip.Anchor() = binding.OriginalAnchor;
            binding.Tooltip.FixedPosition() = binding.OriginalFixedPosition;
        }

        if (ReferenceEquals(GameAccess.CurrentTooltip, binding.Tooltip))
        {
            UITooltip.HideTooltip();
        }
    }

    internal static void ClearForDialog(SkillsDialog dialog)
    {
        // An old inventory may be destroyed after its replacement has already bound a new row.
        if (_binding != null && ReferenceEquals(_binding.Dialog, dialog))
        {
            Clear();
        }
    }

    internal static void Bind(SkillsDialog dialog, UITooltip tooltip, string originalText)
    {
        Canvas? canvas = tooltip.GetComponentInParent<Canvas>();
        RectTransform? panel = FindSkillPanel(dialog);
        if (canvas == null || canvas.transform is not RectTransform canvasRect || panel == null)
        {
            return;
        }

        RectTransform? row = null;
        for (Transform current = tooltip.transform; current != null && current != dialog.m_listRoot; current = current.parent)
        {
            if (current.parent == dialog.m_listRoot)
            {
                row = current as RectTransform;
                break;
            }
        }

        if (row == null)
        {
            return; // A replacement UI with an unknown hierarchy keeps its existing placement.
        }

        _binding = new Binding(dialog, panel, row, canvas, canvasRect, tooltip, originalText);
        // Gamepad tooltips must also stay outside the scroll viewport's mask.
        tooltip.Anchor() = canvasRect;
        tooltip.FixedPosition() = Vector2.zero;
    }

    private static RectTransform? FindSkillPanel(SkillsDialog dialog)
    {
        if (dialog.m_listRoot == null)
        {
            return null;
        }

        // SkillsDialog itself fills the screen; the list's branch below it is the visible frame.
        for (Transform current = dialog.m_listRoot.parent; current != null && current != dialog.transform; current = current.parent)
        {
            if (current.parent == dialog.transform && current is RectTransform frame)
            {
                return frame.Find("bkg") as RectTransform ?? frame;
            }
        }

        return null;
    }

    internal static void UpdateVisibleTooltip(UITooltip tooltip)
    {
        Binding? binding = _binding;
        if (binding == null || !ReferenceEquals(binding.Tooltip, tooltip) ||
            GameAccess.CurrentTooltip != tooltip || GameAccess.TooltipObject == null)
        {
            return;
        }

        try
        {
            if (binding.Dialog == null || !binding.Dialog.isActiveAndEnabled ||
                binding.Row == null || !binding.Row.gameObject.activeInHierarchy ||
                binding.Panel == null || binding.Canvas == null || binding.CanvasRect == null ||
                !CookingSkillTooltipText.HasFineDiningHeading(tooltip.m_text))
            {
                Clear();
                return;
            }

            bool chefEnabled = DietConfig.IsChefChoiceEnabled();
            if (binding.ChefChoiceEnabled != chefEnabled)
            {
                tooltip.Set(
                    tooltip.m_topic,
                    CookingSkillTooltipText.AppendConfigured(binding.OriginalText),
                    tooltip.Anchor(),
                    tooltip.FixedPosition());
                binding.ChefChoiceEnabled = chefEnabled;
            }

            GameObject root = GameAccess.TooltipObject;
            if (!root.activeSelf)
            {
                return; // Preserve vanilla's mouse hover delay and gamepad timing.
            }

            if (binding.View == null || binding.View.Root != root)
            {
                binding.View = View.Create(root, binding.CanvasRect);
                if (binding.View == null)
                {
                    // No compatible Text child: leave the unmodified clone visible in its original layout.
                    tooltip.Anchor() = binding.OriginalAnchor;
                    tooltip.FixedPosition() = binding.OriginalFixedPosition;
                    _binding = null;
                    return;
                }
            }

            Camera? camera = binding.Canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : binding.Canvas.worldCamera;
            Rect safeArea = Screen.safeArea;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(binding.CanvasRect, safeArea.min, camera, out Vector2 screenMin) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(binding.CanvasRect, safeArea.max, camera, out Vector2 screenMax))
            {
                return;
            }

            Rect viewport = Rect.MinMaxRect(screenMin.x, screenMin.y, screenMax.x, screenMax.y);
            Rect panelBounds = GetCanvasBounds(binding.Panel, binding.CanvasRect);
            Rect rowBounds = GetCanvasBounds(binding.Row, binding.CanvasRect);
            binding.View.UpdateLayout();
            float scale = CookingSkillTooltipLayout.GetScale(viewport, binding.View.Size);
            Vector2 topLeft = CookingSkillTooltipLayout.GetTopLeft(viewport, panelBounds, rowBounds.yMax, binding.View.Size * scale);
            binding.View.Panel.localScale = new Vector3(scale, scale, 1f);
            // Vanilla clamps the first child as well as moving the root. Set an absolute position to avoid drift.
            binding.View.Panel.position = binding.CanvasRect.TransformPoint(new Vector3(topLeft.x, topLeft.y, 0f));
        }
        catch (Exception exception)
        {
            Clear();
            if (!_layoutFailureLogged)
            {
                _layoutFailureLogged = true;
                FineDiningPlugin.Log.LogWarning("Could not position the Cooking skill tooltip: " + exception.GetBaseException().Message);
            }
        }
    }

    private static Rect GetCanvasBounds(RectTransform rect, RectTransform canvas)
    {
        rect.GetWorldCorners(Corners);
        Vector2 min = canvas.InverseTransformPoint(Corners[0]);
        Vector2 max = min;
        for (int index = 1; index < Corners.Length; index++)
        {
            Vector2 point = canvas.InverseTransformPoint(Corners[index]);
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }

        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private sealed class Binding
    {
        internal readonly SkillsDialog Dialog;
        internal readonly RectTransform Panel;
        internal readonly RectTransform Row;
        internal readonly Canvas Canvas;
        internal readonly RectTransform CanvasRect;
        internal readonly UITooltip Tooltip;
        internal readonly RectTransform? OriginalAnchor;
        internal readonly Vector2 OriginalFixedPosition;
        internal readonly string OriginalText;
        internal bool ChefChoiceEnabled;
        internal View? View;

        internal Binding(SkillsDialog dialog, RectTransform panel, RectTransform row, Canvas canvas, RectTransform canvasRect, UITooltip tooltip, string originalText)
        {
            Dialog = dialog;
            Panel = panel;
            Row = row;
            Canvas = canvas;
            CanvasRect = canvasRect;
            Tooltip = tooltip;
            OriginalAnchor = tooltip.Anchor();
            OriginalFixedPosition = tooltip.FixedPosition();
            OriginalText = originalText;
            ChefChoiceEnabled = DietConfig.IsChefChoiceEnabled();
        }
    }

    private sealed class View
    {
        internal readonly GameObject Root;
        internal readonly RectTransform Panel;
        private readonly TMP_Text _body;
        private readonly TMP_Text? _topic;
        private string? _bodyText;
        private string? _topicText;
        private TMP_FontAsset? _bodyFont;
        private TMP_FontAsset? _topicFont;
        private float _bodyFontSize;
        private float _topicFontSize;
        internal Vector2 Size => Panel.sizeDelta;

        private View(GameObject root, RectTransform panel, TMP_Text body, TMP_Text? topic)
        {
            Root = root;
            Panel = panel;
            _body = body;
            _topic = topic;
        }

        internal static View? Create(GameObject root, RectTransform canvas)
        {
            TMP_Text? body = Utils.FindChild(root.transform, "Text")?.GetComponent<TMP_Text>();
            TMP_Text? topic = Utils.FindChild(root.transform, "Topic")?.GetComponent<TMP_Text>();
            if (body == null || body.font == null || body.transform == root.transform)
            {
                return null;
            }

            root.transform.SetParent(canvas, false);
            root.transform.localScale = Vector3.one;
            root.transform.localRotation = Quaternion.identity;
            DisableAutomaticLayout(root);
            CanvasGroup group = root.GetComponent<CanvasGroup>() ?? root.AddComponent<CanvasGroup>();
            group.blocksRaycasts = false;
            group.interactable = false;

            GameObject panelObject = new("FineDining_CookingSkillTooltip", typeof(RectTransform), typeof(Image));
            RectTransform panel = (RectTransform)panelObject.transform;
            panel.SetParent(root.transform, false);
            panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
            Image background = panelObject.GetComponent<Image>();
            background.color = new Color(0.055f, 0.045f, 0.04f, 0.94f);
            background.raycastTarget = false;

            PrepareText(body, panel, TextAlignmentOptions.TopLeft);
            if (topic != null && topic != body)
            {
                PrepareText(topic, panel, TextAlignmentOptions.Top);
            }

            // Only reshape the active clone. Reuse its localized text, fonts and materials, not a shared prefab.
            for (int index = 0; index < root.transform.childCount; index++)
            {
                Transform child = root.transform.GetChild(index);
                if (child != panel)
                {
                    child.gameObject.SetActive(false);
                }
            }

            panel.SetAsFirstSibling();
            return new View(root, panel, body, topic == body ? null : topic);
        }

        private static void PrepareText(TMP_Text text, RectTransform panel, TextAlignmentOptions alignment)
        {
            DisableAutomaticLayout(text.gameObject);
            RectTransform rect = text.rectTransform;
            rect.SetParent(panel, false);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            text.alignment = alignment;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = false;
            text.margin = Vector4.zero;
            text.raycastTarget = false;
            text.gameObject.SetActive(true);
        }

        private static void DisableAutomaticLayout(GameObject target)
        {
            LayoutGroup? layout = target.GetComponent<LayoutGroup>();
            ContentSizeFitter? fitter = target.GetComponent<ContentSizeFitter>();
            AspectRatioFitter? aspect = target.GetComponent<AspectRatioFitter>();
            if (layout != null) layout.enabled = false;
            if (fitter != null) fitter.enabled = false;
            if (aspect != null) aspect.enabled = false;
        }

        internal void UpdateLayout()
        {
            if (_bodyText == _body.text && _topicText == _topic?.text &&
                ReferenceEquals(_bodyFont, _body.font) && ReferenceEquals(_topicFont, _topic?.font) &&
                _bodyFontSize == _body.fontSize && _topicFontSize == (_topic?.fontSize ?? 0f))
            {
                return;
            }

            _bodyText = _body.text;
            _topicText = _topic?.text;
            _bodyFont = _body.font;
            _topicFont = _topic?.font;
            _bodyFontSize = _body.fontSize;
            _topicFontSize = _topic?.fontSize ?? 0f;
            const float innerWidth = CookingSkillTooltipLayout.BodyWidth;
            float y = CookingSkillTooltipLayout.Padding;
            if (_topic != null)
            {
                bool hasTopic = !string.IsNullOrWhiteSpace(_topicText);
                _topic.gameObject.SetActive(hasTopic);
                if (hasTopic)
                {
                    y += SetTextRect(_topic, innerWidth, y) + 8f;
                }
            }

            y += SetTextRect(_body, innerWidth, y);
            Panel.sizeDelta = new Vector2(CookingSkillTooltipLayout.Width, y + CookingSkillTooltipLayout.Padding);
        }

        private static float SetTextRect(TMP_Text text, float width, float y)
        {
            float height = Mathf.Max(1f, Mathf.Ceil(text.GetPreferredValues(text.text, width, float.PositiveInfinity).y));
            text.rectTransform.anchoredPosition = new Vector2(CookingSkillTooltipLayout.Padding, -y);
            text.rectTransform.sizeDelta = new Vector2(width, height);
            return height;
        }
    }
}

internal static class CookingSkillTooltipLayout
{
    internal const float BodyWidth = 250f;
    internal const float Padding = 16f;
    internal const float Width = BodyWidth + Padding * 2f;
    private const float Gap = 8f;
    private const float Margin = 12f;

    internal static float GetScale(Rect viewport, Vector2 size)
    {
        float widthScale = Mathf.Max(1f, viewport.width - Margin * 2f) / Mathf.Max(1f, size.x);
        float heightScale = Mathf.Max(1f, viewport.height - Margin * 2f) / Mathf.Max(1f, size.y);
        return Mathf.Min(1f, Mathf.Min(widthScale, heightScale));
    }

    internal static Vector2 GetTopLeft(Rect viewport, Rect skillPanel, float rowTop, Vector2 size)
    {
        float minX = viewport.xMin + Margin;
        float maxY = viewport.yMax - Margin;
        float x = Mathf.Clamp(skillPanel.xMin - Gap - size.x, minX, Mathf.Max(minX, viewport.xMax - Margin - size.x));
        float y = Mathf.Clamp(rowTop, Mathf.Min(maxY, viewport.yMin + Margin + size.y), maxY);
        return new Vector2(x, y);
    }
}

internal static class CookingSkillTooltipText
{
    internal const string HeadingToken = "$finedining_skill_cooking_heading";
    internal const string AutoEjectToken = "$finedining_skill_cooking_auto_eject";
    internal const string BonusOutputToken = "$finedining_skill_cooking_bonus_output";
    internal const string ChefTierToken = "$finedining_skill_cooking_chef_tier";
    internal const string ChefMultiplierToken = "$finedining_skill_cooking_chef_multiplier";
    internal const string ChefBothToken = "$finedining_skill_cooking_chef_both";

    internal static string AppendConfigured(string? original) =>
        Append(
            original,
            DietConfig.GetCookingBonusChanceAtMaxCookingPercent() > 0f
            || DietConfig.GetFermenterOutputBonusChanceAtMaxCookingPercent() > 0f,
            DietConfig.IsChefChoiceEnabled() && DietConfig.GetChefHighTierSelectionStrength() > 0f,
            DietConfig.IsChefChoiceEnabled() &&
            DietConfig.GetChefMultiplierModeAtMaxCooking() > DietConfig.GetChefMultiplierMin());

    internal static string Append(
        string? original,
        bool bonusOutputEnabled,
        bool chefTierEnabled,
        bool chefMultiplierEnabled)
    {
        original ??= string.Empty;
        if (original.IndexOf(HeadingToken, StringComparison.Ordinal) >= 0)
        {
            return original;
        }

        StringBuilder extra = new(HeadingToken);
        extra.Append('\n').Append(AutoEjectToken);
        if (bonusOutputEnabled)
        {
            extra.Append('\n').Append(BonusOutputToken);
        }

        string chefToken = chefTierEnabled && chefMultiplierEnabled
            ? ChefBothToken
            : chefTierEnabled
                ? ChefTierToken
                : chefMultiplierEnabled
                    ? ChefMultiplierToken
                    : string.Empty;
        if (chefToken.Length > 0)
        {
            extra.Append('\n').Append(chefToken);
        }

        return original.Length > 0
            ? original + "\n\n" + extra
            : extra.ToString();
    }

    internal static bool MatchesSkillDescription(
        string? tooltipText,
        string? skillDescription) =>
        !string.IsNullOrWhiteSpace(tooltipText) &&
        !string.IsNullOrWhiteSpace(skillDescription) &&
        tooltipText!.IndexOf(skillDescription!, StringComparison.Ordinal) >= 0;

    internal static bool HasFineDiningHeading(string? tooltipText) =>
        !string.IsNullOrEmpty(tooltipText)
        && tooltipText!.IndexOf(HeadingToken, StringComparison.Ordinal) >= 0;
}

[HarmonyPatch(typeof(SkillsDialog), nameof(SkillsDialog.Setup))]
internal static class CookingSkillTooltipPatch
{
    private static bool _failureLogged;

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix() => CookingSkillTooltipPanel.Clear();

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter("randyknapp.mods.epicloot")]
    private static void Postfix(SkillsDialog __instance, Player player)
    {
        if (__instance == null || player == null)
        {
            return;
        }

        try
        {
            var skills = player.GetSkills()?.GetSkillList();
            if (skills == null)
            {
                return;
            }

            Skills.Skill? cookingSkill = null;
            int cookingIndex = -1;
            for (int index = 0; index < skills.Count; index++)
            {
                Skills.Skill skill = skills[index];
                if (skill?.m_info?.m_skill == Skills.SkillType.Cooking)
                {
                    cookingSkill = skill;
                    cookingIndex = index;
                    break;
                }
            }

            if (cookingSkill?.m_info == null)
            {
                return;
            }

            UITooltip? tooltip = FindCookingTooltip(
                __instance,
                cookingIndex,
                cookingSkill.m_info.m_description);
            if (tooltip == null)
            {
                return;
            }

            string originalText = tooltip.m_text;
            string text = CookingSkillTooltipText.AppendConfigured(originalText);
            if (!string.Equals(text, tooltip.m_text, StringComparison.Ordinal))
            {
                tooltip.Set(
                    tooltip.m_topic,
                    text,
                    tooltip.Anchor(),
                    tooltip.FixedPosition());
            }

            CookingSkillTooltipPanel.Bind(__instance, tooltip, originalText);
        }
        catch (Exception exception)
        {
            if (_failureLogged)
            {
                return;
            }

            _failureLogged = true;
            FineDiningPlugin.Log.LogWarning(
                "Could not extend the Cooking skill tooltip: " +
                exception.GetBaseException().Message);
        }
    }

    private static UITooltip? FindCookingTooltip(
        SkillsDialog dialog,
        int cookingIndex,
        string cookingDescription)
    {
        if (dialog.Elements() != null &&
            cookingIndex >= 0 &&
            cookingIndex < dialog.Elements().Count)
        {
            UITooltip? indexedTooltip = dialog.Elements()[cookingIndex]?
                .GetComponentInChildren<UITooltip>();
            if (indexedTooltip != null &&
                CookingSkillTooltipText.MatchesSkillDescription(
                    indexedTooltip.m_text,
                    cookingDescription))
            {
                return indexedTooltip;
            }
        }

        InventoryGui? inventory = dialog.GetComponentInParent<InventoryGui>();
        if (inventory == null)
        {
            return null;
        }

        UITooltip[] candidates =
            inventory.GetComponentsInChildren<UITooltip>(true);
        foreach (UITooltip candidate in candidates)
        {
            if (candidate != null &&
                candidate.gameObject.activeInHierarchy &&
                CookingSkillTooltipText.MatchesSkillDescription(
                    candidate.m_text,
                    cookingDescription))
            {
                return candidate;
            }
        }

        foreach (UITooltip candidate in candidates)
        {
            if (candidate != null &&
                CookingSkillTooltipText.MatchesSkillDescription(
                    candidate.m_text,
                    cookingDescription))
            {
                return candidate;
            }
        }

        return null;
    }
}

[HarmonyPatch(typeof(UITooltip), "UpdateTextElements")]
internal static class CookingSkillTooltipAlignmentPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(UITooltip __instance)
    {
        if (__instance == null
            || !CookingSkillTooltipText.HasFineDiningHeading(__instance.m_text)
            || GameAccess.CurrentTooltip != null && GameAccess.CurrentTooltip != __instance
            || GameAccess.TooltipObject == null)
        {
            return;
        }

        TMP_Text[] textElements =
            GameAccess.TooltipObject.GetComponentsInChildren<TMP_Text>(true);
        foreach (TMP_Text textElement in textElements)
        {
            if (textElement != null
                && string.Equals(textElement.name, "Text", StringComparison.Ordinal))
            {
                textElement.horizontalAlignment = HorizontalAlignmentOptions.Left;
                return;
            }
        }
    }
}

[HarmonyPatch(typeof(UITooltip), "LateUpdate")]
internal static class CookingSkillTooltipPositionPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(UITooltip __instance) => CookingSkillTooltipPanel.UpdateVisibleTooltip(__instance);
}

[HarmonyPatch(typeof(InventoryGui), "OnDestroy")]
internal static class CookingSkillTooltipDestroyPatch
{
    [HarmonyPrefix]
    private static void Prefix(InventoryGui __instance) => CookingSkillTooltipPanel.ClearForDialog(__instance.m_skillsDialog);
}
