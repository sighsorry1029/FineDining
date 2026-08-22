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
    private static Sprite? _coldPauseSprite;
    private static int _updateId;
    private static bool _loggedFirstPlayerGridCheck;
    private static bool _loggedFirstContainerGridCheck;
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
        int usedItemCount = 0;
        int timestampCount = 0;
        int visibleCount = 0;
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

            usedItemCount++;
            if (!DecayRuntime.TryGetSpoilageClock(
                    item,
                    nowTicks,
                    out long remainingTicks,
                    out bool paused))
            {
                continue;
            }

            timestampCount++;
            FineDiningTimerOverlayCache cache = EnsureOverlay(element);
            float unscaledTime = Time.unscaledTime;
            bool refreshText = !ReferenceEquals(cache.LastItem, item) ||
                               cache.LastPaused != paused ||
                               unscaledTime >= cache.NextTextRefreshAt ||
                               string.IsNullOrEmpty(cache.LastText);
            cache.LastItem = item;
            cache.LastPaused = paused;
            cache.LastSeenUpdateId = updateId;
            if (refreshText)
            {
                double seconds = remainingTicks / (double)TimeSpan.TicksPerSecond;
                SetOverlayState(cache, FormatRemainingTime(seconds), paused);
                cache.NextTextRefreshAt = unscaledTime + 1f;
            }
            else
            {
                ShowOverlay(cache, paused);
            }

            visibleCount++;
        }

        HideOverlaysNotSeenInUpdate(grid, updateId);
        Player localPlayer = Player.m_localPlayer;
        bool isPlayerGrid = localPlayer != null &&
                            ReferenceEquals(localPlayer.GetInventory(), inventory);
        bool logGridCheck = isPlayerGrid
            ? !_loggedFirstPlayerGridCheck
            : !_loggedFirstContainerGridCheck;
        if (logGridCheck)
        {
            if (isPlayerGrid)
            {
                _loggedFirstPlayerGridCheck = true;
            }
            else
            {
                _loggedFirstContainerGridCheck = true;
            }

            FineDiningPlugin.Log.LogInfo(
                "Spoilage timer UI check (" + (isPlayerGrid ? "player" : "container") + "): " +
                usedItemCount + " occupied stack(s), " +
                timestampCount + " valid timestamp(s), " + visibleCount + " rendered overlay(s).");
        }
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
        bool paused)
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

        ShowOverlay(cache, paused);
    }

    private static void ShowOverlay(FineDiningTimerOverlayCache cache, bool paused)
    {
        TMP_Text? timerText = cache.TimerText;
        if (timerText == null)
        {
            return;
        }

        timerText.color = paused ? PausedTimerColor : RunningTimerColor;
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
    internal string PauseIconLayoutText = "";
}

internal static class SpoilageUiText
{
    internal const string PausedColorHex = "#70C8FF";
    private const string RunningLineKey = "$finedining_tooltip_spoils_in";
    private const string PausedLineKey = "$finedining_tooltip_paused";
    private const string DayUnitKey = "$finedining_duration_day";
    private const string HourUnitKey = "$finedining_duration_hour";
    private const string MinuteUnitKey = "$finedining_duration_minute";
    private const string EnglishRunningLine = "Spoils in {0}";
    private const string EnglishPausedLine = "Cold environment paused spoilage · remaining {0} ❄";
    private const string FreshnessEffectLineKey = "$finedining_tooltip_freshness_effect";
    private const string EnglishFreshnessEffectLine = "Freshness effect: food stats x{0}";

    internal static string BuildStatusLine(long remainingTicks, bool paused)
    {
        string remaining = FormatDetailedRemaining(remainingTicks);
        string line = FormatLocalized(
            paused ? PausedLineKey : RunningLineKey,
            paused ? EnglishPausedLine : EnglishRunningLine,
            remaining);
        return paused ? $"<color={PausedColorHex}>{line}</color>" : line;
    }

    internal static string BuildFreshnessEffectLine(float multiplier)
    {
        multiplier = Mathf.Clamp01(multiplier);
        string factor = multiplier.ToString(
            "0.00",
            System.Globalization.CultureInfo.InvariantCulture);
        return FormatLocalized(
            FreshnessEffectLineKey,
            EnglishFreshnessEffectLine,
            factor);
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
            parts.Add(FormatLocalized(DayUnitKey, "{0}d", days));
        }

        if (hours > 0L)
        {
            parts.Add(FormatLocalized(HourUnitKey, "{0}h", hours));
        }

        if (minutes > 0L || parts.Count == 0)
        {
            parts.Add(FormatLocalized(MinuteUnitKey, "{0}m", minutes > 0L ? minutes : 1L));
        }

        return string.Join(" ", parts);
    }

    private static string FormatLocalized(string key, string englishFallback, params object[] values)
    {
        string? localizedFormat = null;
        Localization? localization = Localization.instance;
        if (localization != null)
        {
            localizedFormat = localization.Localize(key);
        }

        if (!string.IsNullOrWhiteSpace(localizedFormat))
        {
            try
            {
                string localized = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    localizedFormat,
                    values);
                bool containsValues = true;
                foreach (object value in values)
                {
                    string required = Convert.ToString(
                        value,
                        System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    if (required.Length > 0 && localized.IndexOf(required, StringComparison.Ordinal) < 0)
                    {
                        containsValues = false;
                        break;
                    }
                }

                if (containsValues)
                {
                    return localized;
                }
            }
            catch (FormatException)
            {
                // Malformed or unresolved translations fall through to English.
            }
        }

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            englishFallback,
            values);
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
            if (!DecayRuntime.TryGetWorldTicks(out long nowTicks) ||
                !DecayRuntime.TryGetSpoilageClock(
                    __instance,
                    nowTicks,
                    out long remainingTicks,
                    out bool paused))
            {
                return;
            }

            string line = SpoilageUiText.BuildStatusLine(remainingTicks, paused);
            if (!SpoilageUiText.ContainsLine(__result ?? "", line))
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

            if (!DecayRuntime.TryGetWorldTicks(out long nowTicks) ||
                !TryBuildTimerLine(worldDrop.m_itemData, nowTicks, out string timerLine))
            {
                return;
            }

            if (!SpoilageUiText.ContainsLine(hoverText, timerLine))
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

            float multiplier = FreshnessFoodEffects.GetMultiplier(freshnessItem);
            if (multiplier < 0.999999f)
            {
                string effectLine = SpoilageUiText.BuildFreshnessEffectLine(multiplier);
                if (!SpoilageUiText.ContainsLine(hoverText, effectLine))
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
        ItemDrop.ItemData? item,
        long nowTicks,
        out string timerLine)
    {
        timerLine = string.Empty;
        if (!DecayRuntime.TryGetSpoilageClock(
                item,
                nowTicks,
                out long remainingTicks,
                out bool paused))
        {
            return false;
        }

        timerLine = SpoilageUiText.BuildStatusLine(remainingTicks, paused);
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
