using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using ItemData = ItemDrop.ItemData;

namespace FineDining;

[HarmonyPatch(
    typeof(InventoryGrid),
    nameof(InventoryGrid.UpdateGui),
    new[] { typeof(Player), typeof(ItemData) })]
internal static class InventoryGridSpoilageTimerPatch
{
    private const string OverlayObjectName = "sighsorry.FineDining.TimerOverlay";
    private const string PauseIconObjectName = "sighsorry.FineDining.TimerPauseIcon";
    private static readonly Color RunningTimerColor = new(1f, 0.82f, 0.22f, 1f);
    private static readonly Color PausedTimerColor = new(0.44f, 0.78f, 1f, 1f);
    private static readonly Color SpoiledTimerColor = new(1f, 0.71f, 0.33f, 1f);
    private static Sprite? _coldPauseSprite;
    private static int _updateId;
    private static bool _loggedUiFailure;

    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(new[] { "sighsorry.InventorySlots" })]
    private static void Postfix(InventoryGrid __instance)
    {
        try
        {
            Render(__instance);
        }
        catch (Exception exception)
        {
            // A broken overlay must never break the inventory GUI. Report only the
            // first failure so an incompatible UI mod cannot flood the game log.
            if (!_loggedUiFailure)
            {
                _loggedUiFailure = true;
                FineDiningPlugin.Log.LogWarning("Could not render spoilage timers: " + exception);
            }
        }
    }

    private static void Render(InventoryGrid grid)
    {
        if (grid?.m_elements == null)
        {
            return;
        }

        Inventory inventory = grid.m_inventory;
        if (inventory == null)
        {
            HideAllExistingOverlays(grid);
            return;
        }

        // The normal one-second scheduler remains the primary path, but a grid can
        // be drawn before the plugin's next Update tick. Reconcile immediately on
        // that first visible frame when this peer is allowed to mutate the inventory.
        // Non-owned containers are rejected inside the runtime and stay read-only.
        if (!DecayRuntime.TryPrepareVisibleInventoryTimers(inventory, out long nowTicks))
        {
            HideAllExistingOverlays(grid);
            return;
        }

        int width = inventory.GetWidth();
        if (width <= 0)
        {
            HideAllExistingOverlays(grid);
            return;
        }

        int updateId = NextUpdateId();
        foreach (ItemData item in inventory.GetAllItems())
        {
            if (item == null)
            {
                continue;
            }

            int index = item.m_gridPos.y * width + item.m_gridPos.x;
            if (index < 0 || index >= grid.m_elements.Count)
            {
                continue;
            }

            InventoryGrid.Element element = grid.m_elements[index];
            if (element?.m_go == null ||
                !element.m_used ||
                element.m_pos.x != item.m_gridPos.x ||
                element.m_pos.y != item.m_gridPos.y)
            {
                continue;
            }

            bool spoiled = SpoilageClock.IsSpoiled(item);
            long remainingTicks = 0L;
            bool paused = false;
            if (!spoiled &&
                !DecayRuntime.TryGetSpoilageClock(
                    item,
                    nowTicks,
                    out remainingTicks,
                    out paused))
            {
                continue;
            }

            FineDiningTimerOverlayCache cache = EnsureOverlay(element);
            float unscaledTime = Time.unscaledTime;
            bool refreshText = !ReferenceEquals(cache.LastItem, item) ||
                               cache.LastPaused != paused ||
                               cache.LastSpoiled != spoiled ||
                               unscaledTime >= cache.NextTextRefreshAt ||
                               string.IsNullOrEmpty(cache.LastText);
            cache.LastItem = item;
            cache.LastPaused = paused;
            cache.LastSpoiled = spoiled;
            cache.LastSeenUpdateId = updateId;
            if (refreshText)
            {
                string text = spoiled
                    ? FoodEffectUiText.GetSpoiledLabel()
                    : FormatRemainingTime(remainingTicks / (double)TimeSpan.TicksPerSecond);
                SetOverlayState(cache, text, paused, spoiled);
                cache.NextTextRefreshAt = unscaledTime + 1f;
            }
            else
            {
                ShowOverlay(cache, paused, spoiled);
            }
        }

        HideOverlaysNotSeenInUpdate(grid, updateId);
    }

    private static int NextUpdateId()
    {
        unchecked
        {
            _updateId++;
            if (_updateId == 0)
            {
                _updateId++;
            }

            return _updateId;
        }
    }

    private static FineDiningTimerOverlayCache EnsureOverlay(InventoryGrid.Element element)
    {
        GameObject root = element.m_go;
        FineDiningTimerOverlayCache cache = root.GetComponent<FineDiningTimerOverlayCache>() ??
                                                 root.AddComponent<FineDiningTimerOverlayCache>();
        if (cache.TimerText != null)
        {
            return cache;
        }

        Transform existing = root.transform.Find(OverlayObjectName);
        TMP_Text? timerText = existing != null ? existing.GetComponent<TMP_Text>() : null;
        if (timerText == null)
        {
            // Clone Valheim's amount label instead of constructing a bare TMP
            // component. The clone inherits the active UI canvas' font material,
            // shader and masking setup, which is more reliable across UI mods.
            if (element.m_amount != null)
            {
                timerText = UnityEngine.Object.Instantiate(element.m_amount, root.transform, false);
                timerText.gameObject.name = OverlayObjectName;
            }
            else
            {
                GameObject overlay = new(OverlayObjectName, typeof(RectTransform), typeof(CanvasRenderer));
                overlay.layer = root.layer;
                overlay.transform.SetParent(root.transform, false);
                timerText = overlay.AddComponent<TextMeshProUGUI>();
                timerText.font = TMP_Settings.defaultFontAsset;
            }

            GameObject overlayObject = timerText.gameObject;
            overlayObject.layer = root.layer;
            overlayObject.SetActive(false);

            RectTransform rect = (RectTransform)overlayObject.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            TMP_Text? bindingText = root.transform.Find("binding")?.GetComponent<TMP_Text>();
            float topInset = bindingText != null && bindingText.enabled ? 17f : 3f;
            rect.anchoredPosition = new Vector2(3f, -topInset);
            rect.sizeDelta = new Vector2(42f, 16f);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.SetAsLastSibling();

            timerText.text = "";
            timerText.enabled = true;
            timerText.color = RunningTimerColor;
            timerText.alignment = TextAlignmentOptions.TopLeft;
            timerText.enableAutoSizing = true;
            timerText.fontSizeMin = 8f;
            timerText.fontSizeMax = 12f;
            timerText.textWrappingMode = TextWrappingModes.NoWrap;
            timerText.overflowMode = TextOverflowModes.Overflow;
            timerText.richText = false;
            timerText.raycastTarget = false;
            timerText.margin = Vector4.zero;
        }

        cache.TimerText = timerText;
        cache.LastText = timerText.text;
        cache.Visible = timerText.gameObject.activeSelf;
        return cache;
    }

    private static void SetOverlayState(
        FineDiningTimerOverlayCache cache,
        string text,
        bool paused,
        bool spoiled)
    {
        TMP_Text? timerText = cache.TimerText;
        if (timerText == null)
        {
            return;
        }

        if (!string.Equals(cache.LastText, text, StringComparison.Ordinal) ||
            !string.Equals(timerText.text, text, StringComparison.Ordinal))
        {
            timerText.text = text;
            cache.LastText = text;
        }

        ShowOverlay(cache, paused, spoiled);
    }

    private static void ShowOverlay(
        FineDiningTimerOverlayCache cache,
        bool paused,
        bool spoiled)
    {
        TMP_Text? timerText = cache.TimerText;
        if (timerText == null)
        {
            return;
        }

        timerText.color = spoiled
            ? SpoiledTimerColor
            : paused
                ? PausedTimerColor
                : RunningTimerColor;
        if (!cache.Visible || !timerText.gameObject.activeSelf || !timerText.enabled)
        {
            timerText.enabled = true;
            timerText.gameObject.SetActive(true);
            cache.Visible = true;
        }

        if (paused)
        {
            ShowPauseIcon(cache);
        }
        else
        {
            HidePauseIcon(cache);
        }
    }

    private static void ShowPauseIcon(FineDiningTimerOverlayCache cache)
    {
        TMP_Text? timerText = cache.TimerText;
        if (timerText == null)
        {
            return;
        }

        Image? icon = EnsurePauseIcon(cache, timerText.transform.parent);
        Sprite? sprite = GetColdPauseSprite();
        if (icon == null || sprite == null)
        {
            HidePauseIcon(cache);
            return;
        }

        if (icon.sprite != sprite)
        {
            icon.sprite = sprite;
        }

        RectTransform textRect = timerText.rectTransform;
        RectTransform iconRect = icon.rectTransform;
        if (!string.Equals(cache.PauseIconLayoutText, timerText.text, StringComparison.Ordinal))
        {
            float textWidth = timerText.GetPreferredValues(timerText.text).x;
            iconRect.anchoredPosition = new Vector2(
                textRect.anchoredPosition.x + Mathf.Clamp(textWidth, 7f, 38f) + 1f,
                textRect.anchoredPosition.y - 1f);
            cache.PauseIconLayoutText = timerText.text;
        }
        icon.enabled = true;
        if (!icon.gameObject.activeSelf)
        {
            icon.gameObject.SetActive(true);
        }
    }

    private static Image? EnsurePauseIcon(FineDiningTimerOverlayCache cache, Transform? parent)
    {
        if (cache.PauseIcon != null)
        {
            return cache.PauseIcon;
        }

        if (parent == null)
        {
            return null;
        }

        Transform existing = parent.Find(PauseIconObjectName);
        Image? icon = existing != null ? existing.GetComponent<Image>() : null;
        if (icon == null)
        {
            GameObject iconObject = new(
                PauseIconObjectName,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            iconObject.layer = parent.gameObject.layer;
            iconObject.transform.SetParent(parent, false);
            icon = iconObject.GetComponent<Image>();

            RectTransform rect = icon.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(11f, 11f);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.SetAsLastSibling();

            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.color = Color.white;
            iconObject.SetActive(false);
        }

        cache.PauseIcon = icon;
        return icon;
    }

    private static Sprite? GetColdPauseSprite()
    {
        if (_coldPauseSprite != null)
        {
            return _coldPauseSprite;
        }

        ObjectDB? objectDb = ObjectDB.instance;
        if (objectDb == null)
        {
            return null;
        }

        StatusEffect? frost = objectDb.GetStatusEffect(SEMan.s_statusEffectFrost);
        _coldPauseSprite = frost?.m_icon ??
                           objectDb.GetStatusEffect(SEMan.s_statusEffectFreezing)?.m_icon;
        return _coldPauseSprite;
    }

    private static void HidePauseIcon(FineDiningTimerOverlayCache cache)
    {
        Image? icon = cache.PauseIcon;
        if (icon != null && icon.gameObject.activeSelf)
        {
            icon.gameObject.SetActive(false);
        }
    }

    private static void HideOverlaysNotSeenInUpdate(InventoryGrid grid, int updateId)
    {
        foreach (InventoryGrid.Element element in grid.m_elements)
        {
            if (element?.m_go == null)
            {
                continue;
            }

            FineDiningTimerOverlayCache? cache = element.m_go.GetComponent<FineDiningTimerOverlayCache>();
            if (cache != null && cache.LastSeenUpdateId != updateId)
            {
                HideOverlay(cache);
            }
        }
    }

    private static void HideAllExistingOverlays(InventoryGrid grid)
    {
        foreach (InventoryGrid.Element element in grid.m_elements)
        {
            if (element?.m_go == null)
            {
                continue;
            }

            FineDiningTimerOverlayCache? cache = element.m_go.GetComponent<FineDiningTimerOverlayCache>();
            if (cache != null)
            {
                HideOverlay(cache);
            }
        }
    }

    private static void HideOverlay(FineDiningTimerOverlayCache cache)
    {
        TMP_Text? timerText = cache.TimerText;
        if (timerText != null && (cache.Visible || timerText.gameObject.activeSelf))
        {
            timerText.gameObject.SetActive(false);
        }

        HidePauseIcon(cache);

        cache.Visible = false;
        cache.LastItem = null;
        cache.LastPaused = false;
        cache.LastSpoiled = false;
        cache.PauseIconLayoutText = "";
        cache.NextTextRefreshAt = 0f;
    }

    internal static string FormatRemainingTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d)
        {
            return "--";
        }

        const double secondsPerHour = 60d * 60d;
        if (seconds < secondsPerHour)
        {
            long minutes = Math.Max(1L, (long)Math.Ceiling(seconds / 60d));
            return minutes + "m";
        }

        double hours = Math.Ceiling(seconds / secondsPerHour);
        return hours >= long.MaxValue ? "--" : ((long)hours) + "h";
    }
}

internal sealed class FineDiningTimerOverlayCache : MonoBehaviour
{
    internal TMP_Text? TimerText;
    internal Image? PauseIcon;
    internal ItemData? LastItem;
    internal string LastText = "";
    internal float NextTextRefreshAt;
    internal int LastSeenUpdateId;
    internal bool Visible;
    internal bool LastPaused;
    internal bool LastSpoiled;
    internal string PauseIconLayoutText = "";
}

internal static class FoodEffectUiText
{
    internal const string RunningColorHex = "#FFD138";
    internal const string PausedColorHex = "#70C8FF";
    internal const string PositiveModifierColorHex = "#9FE870";
    internal const string PenaltyModifierColorHex = "#FFB454";
    internal const string NeutralModifierColorHex = "#B8B8B8";
    private const string RunningLineKey = "$finedining_tooltip_spoils_in";
    private const string PausedLineKey = "$finedining_tooltip_paused";
    private const string SpoiledLineKey = "$finedining_tooltip_spoiled";
    private const string DayUnitKey = "$finedining_duration_day";
    private const string HourUnitKey = "$finedining_duration_hour";
    private const string MinuteUnitKey = "$finedining_duration_minute";
    private const string EnglishRunningLine = "Spoils in {0}";
    private const string EnglishPausedLine = "Cold environment paused spoilage · remaining {0} ❄";
    private const string EnglishSpoiledLine = "Spoiled";
    private const string ChefChoiceLineKey = "$finedining_diet_tooltip_chef_choice";
    private const string DiminishingReturnsLineKey =
        "$finedining_diet_tooltip_diminishing_returns";
    private const string StalenessLineKey = "$finedining_tooltip_staleness";
    private const string EnglishChefChoiceLine =
        "<color={0}>Chef's Choice</color>: food stats <color={0}>x{1}</color>";
    private const string EnglishDiminishingReturnsLine =
        "<color={0}>Diminishing returns</color>: food stats <color={0}>x{1}</color>";
    private const string EnglishStalenessLine =
        "<color={0}>Staleness</color>: food stats <color={0}>x{1}</color>";

    internal static string BuildStatusLine(long remainingTicks, bool paused)
    {
        string remaining = FormatDetailedRemaining(remainingTicks);
        string line = FineDiningLocalization.FormatOrFallback(
            paused ? PausedLineKey : RunningLineKey,
            paused ? EnglishPausedLine : EnglishRunningLine,
            remaining);
        string color = paused ? PausedColorHex : RunningColorHex;
        return $"<color={color}>{line}</color>";
    }

    internal static string GetSpoiledLabel() =>
        FineDiningLocalization.LocalizeOrFallback(SpoiledLineKey, EnglishSpoiledLine);

    internal static string BuildSpoiledLine() =>
        $"<color={PenaltyModifierColorHex}>{GetSpoiledLabel()}</color>";

    internal static string BuildChefChoiceModifierLine(float multiplier)
    {
        float displayedMultiplier = Math.Max(1f, RoundMultiplierForDisplay(multiplier));
        string color = GetChefChoiceModifierColor(displayedMultiplier);
        return BuildModifierLine(
            ChefChoiceLineKey,
            EnglishChefChoiceLine,
            color,
            displayedMultiplier);
    }

    internal static bool TryBuildDiminishingReturnsLine(
        float multiplier,
        out string line) =>
        TryBuildPenaltyModifierLine(
            DiminishingReturnsLineKey,
            EnglishDiminishingReturnsLine,
            multiplier,
            out line);

    internal static bool TryBuildStalenessLine(float multiplier, out string line) =>
        TryBuildPenaltyModifierLine(
            StalenessLineKey,
            EnglishStalenessLine,
            multiplier,
            out line);

    internal static float RoundMultiplierForDisplay(float multiplier)
    {
        if (float.IsNaN(multiplier) || float.IsInfinity(multiplier))
        {
            return 1f;
        }

        return (float)Math.Round(
            Math.Max(0f, multiplier),
            2,
            MidpointRounding.AwayFromZero);
    }

    internal static string GetChefChoiceModifierColor(float multiplier) =>
        RoundMultiplierForDisplay(multiplier) > 1f
            ? PositiveModifierColorHex
            : NeutralModifierColorHex;

    internal static bool TryGetPenaltyDisplayMultiplier(
        float multiplier,
        out float displayedMultiplier)
    {
        displayedMultiplier = RoundMultiplierForDisplay(multiplier);
        return displayedMultiplier < 1f;
    }

    private static bool TryBuildPenaltyModifierLine(
        string key,
        string englishFallback,
        float multiplier,
        out string line)
    {
        if (!TryGetPenaltyDisplayMultiplier(multiplier, out float displayedMultiplier))
        {
            line = string.Empty;
            return false;
        }

        line = BuildModifierLine(
            key,
            englishFallback,
            PenaltyModifierColorHex,
            displayedMultiplier);
        return true;
    }

    private static string BuildModifierLine(
        string key,
        string englishFallback,
        string color,
        float displayedMultiplier)
    {
        string factor = displayedMultiplier.ToString(
            "0.00",
            System.Globalization.CultureInfo.InvariantCulture);
        return FineDiningLocalization.FormatOrFallback(key, englishFallback, color, factor);
    }

    internal static string FormatDetailedRemaining(long remainingTicks)
    {
        double minutesValue = Math.Ceiling(
            Math.Max(0L, remainingTicks) / (double)TimeSpan.TicksPerMinute);
        long totalMinutes = minutesValue >= long.MaxValue
            ? long.MaxValue
            : Math.Max(1L, (long)minutesValue);

        long days = totalMinutes / (24L * 60L);
        long remainder = totalMinutes % (24L * 60L);
        long hours = remainder / 60L;
        long minutes = remainder % 60L;
        List<string> parts = new(3);
        if (days > 0L)
        {
            parts.Add(FineDiningLocalization.FormatOrFallback(DayUnitKey, "{0}d", days));
        }

        if (hours > 0L)
        {
            parts.Add(FineDiningLocalization.FormatOrFallback(HourUnitKey, "{0}h", hours));
        }

        if (minutes > 0L || parts.Count == 0)
        {
            parts.Add(FineDiningLocalization.FormatOrFallback(
                MinuteUnitKey,
                "{0}m",
                minutes > 0L ? minutes : 1L));
        }

        return string.Join(" ", parts);
    }

    internal static bool ContainsLine(string text, string line)
    {
        if (string.Equals(text, line, StringComparison.Ordinal))
        {
            return true;
        }

        string prefixedLine = "\n" + line;
        int index = text.IndexOf(prefixedLine, StringComparison.Ordinal);
        return index >= 0 &&
               (index + prefixedLine.Length == text.Length || text[index + prefixedLine.Length] == '\n');
    }
}

[HarmonyPatch(typeof(ItemData), nameof(ItemData.GetTooltip), new[] { typeof(int) })]
internal static class ItemDataSpoilageTooltipPatch
{
    private static bool _loggedFailure;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemData __instance, ref string __result)
    {
        if (__instance == null || __instance.m_stack <= 0)
        {
            return;
        }

        try
        {
            if (SpoilageClock.IsSpoiled(__instance))
            {
                string spoiledLine = FoodEffectUiText.BuildSpoiledLine();
                if (!FoodEffectUiText.ContainsLine(__result ?? "", spoiledLine))
                {
                    __result = string.IsNullOrEmpty(__result)
                        ? spoiledLine
                        : __result + "\n" + spoiledLine;
                }

                return;
            }

            if (!SpoilageClock.TryGetWorldTicks(out long nowTicks) ||
                !DecayRuntime.TryGetSpoilageClock(
                    __instance,
                    nowTicks,
                    out long remainingTicks,
                    out bool paused))
            {
                return;
            }

            string line = FoodEffectUiText.BuildStatusLine(remainingTicks, paused);
            if (!FoodEffectUiText.ContainsLine(__result ?? "", line))
            {
                __result = string.IsNullOrEmpty(__result) ? line : __result + "\n" + line;
            }
        }
        catch (Exception exception)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not append spoilage state to an item tooltip: " + exception);
            }
        }
    }
}

internal static class WorldItemSpoilageHover
{
    private static bool _loggedFailure;

    internal static void Append(GameObject? host, bool refreshItemFromZdo, ref string hoverText)
    {
        // Preserve vanilla's empty hover result for depleted or otherwise
        // non-interactable world items.
        if (host == null || string.IsNullOrEmpty(hoverText))
        {
            return;
        }

        try
        {
            // Feast.m_foodItem can refer to another prefab. The persisted clock
            // belongs to the ItemDrop on this world GameObject. Requiring that
            // clock below keeps timerless natural drops out of the hover UI.
            ItemDrop? worldDrop = host.GetComponent<ItemDrop>();
            if (worldDrop == null)
            {
                return;
            }

            if (refreshItemFromZdo)
            {
                // ItemDrop.GetHoverText already calls Load itself, while Feast's
                // implementation does not. Load only refreshes local ItemData from
                // the current ZDO revision and performs no network or ZDO write.
                worldDrop.Load();
            }

            if (!SpoilageClock.TryGetWorldTicks(out long nowTicks) ||
                !TryBuildTimerLine(worldDrop, nowTicks, out string timerLine))
            {
                return;
            }

            if (!FoodEffectUiText.ContainsLine(hoverText, timerLine))
            {
                hoverText += "\n" + timerLine;
            }

            // Feaster-style Pieces often keep the edible data on Feast.m_foodItem
            // while the placed host ItemDrop is only a routing/material item.
            // Evaluate the edible clone with the host's persisted freshness so
            // hover text agrees with the value used by RPC_EatConfirmation.
            ItemDrop.ItemData freshnessItem = worldDrop.m_itemData;
            Feast? feast = host.GetComponent<Feast>();
            if (feast?.m_foodItem?.m_itemData != null)
            {
                freshnessItem = feast.m_foodItem.m_itemData.Clone();
                FreshnessRuntime.CopyFreshnessMetadata(worldDrop.m_itemData, freshnessItem);
            }

            float multiplier = FreshnessRuntime.GetFoodStatMultiplier(freshnessItem);
            if (FoodEffectUiText.TryBuildStalenessLine(multiplier, out string effectLine))
            {
                if (!FoodEffectUiText.ContainsLine(hoverText, effectLine))
                {
                    hoverText += "\n" + effectLine;
                }
            }
        }
        catch (Exception exception)
        {
            // Hover rendering must remain harmless even when a third-party world
            // prefab has incomplete components or item data.
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    "Could not render a world-item spoilage timer: " + exception);
            }
        }
    }

    internal static bool TryBuildTimerLine(
        ItemDrop? worldDrop,
        long nowTicks,
        out string timerLine)
    {
        timerLine = string.Empty;
        if (DecayRuntime.IsCreatorlessPlacedDrop(worldDrop))
        {
            return false;
        }

        if (SpoilageClock.IsSpoiled(worldDrop?.m_itemData))
        {
            timerLine = FoodEffectUiText.BuildSpoiledLine();
            return true;
        }

        if (
            !DecayRuntime.TryGetSpoilageClock(
                worldDrop?.m_itemData,
                nowTicks,
                out long remainingTicks,
                out bool paused))
        {
            return false;
        }

        timerLine = FoodEffectUiText.BuildStatusLine(remainingTicks, paused);
        return true;
    }
}

[HarmonyPatch(typeof(Feast), nameof(Feast.GetHoverText))]
internal static class FeastSpoilageHoverPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Feast __instance, ref string __result)
    {
        WorldItemSpoilageHover.Append(__instance?.gameObject, refreshItemFromZdo: true, ref __result);
    }
}

[HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.GetHoverText))]
internal static class ItemDropWorldItemSpoilageHoverPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemDrop __instance, ref string __result)
    {
        // ItemDrop.GetHoverText already loaded its ItemData. The same helper also
        // serves Feast hosts, keeping both component-order paths idempotent.
        WorldItemSpoilageHover.Append(__instance?.gameObject, refreshItemFromZdo: false, ref __result);
    }
}
