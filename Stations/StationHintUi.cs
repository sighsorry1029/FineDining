using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FineDining;

internal sealed class StationHintUi : MonoBehaviour
{
    private const float CellWidth = 82f;
    private const float CellHeight = 96f;
    private const float ColumnSpacing = 5f;
    private const float RowSpacing = 5f;
    private const float HoverGap = 6f;
    private const float ProgressInputGap = 12f;
    private const float CookingProgressTextScale = 1.5f;

    private static StationHintUi? _instance;
    private static bool _loggedCreationFailure;
    private static float _nextCreationAttemptTime;

    private readonly List<Element> _inputElements = new(StationModule.MaxHints);
    private readonly List<Element> _progressElements = new(StationModule.MaxHints);
    private GameObject? _root;
    private GameObject? _inputRoot;
    private GameObject? _progressRoot;
    private TMP_Text? _hoverText;
    private int _visibleInputCount;
    private int _visibleProgressCount;
    private int _lastShowFrame = -100;

    internal static void EnsureCreated()
    {
        if (!StationModule.IsInitialized || _instance != null)
        {
            return;
        }

        Hud? hud = Hud.instance;
        InventoryGui? inventoryGui = InventoryGui.instance;
        InventoryGrid? playerGrid = inventoryGui?.m_playerGrid;
        GameObject? elementPrefab = playerGrid?.m_elementPrefab;
        if (hud == null || inventoryGui == null || playerGrid == null || elementPrefab == null)
        {
            return;
        }

        if (Time.unscaledTime < _nextCreationAttemptTime)
        {
            return;
        }

        StationHintUi? candidate = null;
        try
        {
            candidate = hud.gameObject.AddComponent<StationHintUi>();
            candidate.Create(hud, elementPrefab);
            _instance = candidate;
            _loggedCreationFailure = false;
            _nextCreationAttemptTime = 0f;
        }
        catch (Exception exception)
        {
            _nextCreationAttemptTime = Time.unscaledTime + 1f;
            if (candidate != null)
            {
                candidate.DestroyRoot();
                Object.Destroy(candidate);
            }

            if (!_loggedCreationFailure)
            {
                _loggedCreationFailure = true;
                FineDiningPlugin.Log.LogWarning(
                    $"Could not create station hover UI: {exception.Message}");
            }
        }
    }

    internal static void Shutdown()
    {
        StationHintUi? instance = _instance;
        _instance = null;
        _nextCreationAttemptTime = 0f;
        _loggedCreationFailure = false;
        if (instance != null)
        {
            instance.DestroyRoot();
            Object.Destroy(instance);
        }
    }

    internal static void Show(IReadOnlyList<StationHintCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        EnsureCreated();
        _instance?.ShowGroups(Array.Empty<StationHintCandidate>(), candidates);
    }

    internal static void ShowCookingStation(
        IReadOnlyList<StationHintCandidate> progressCandidates,
        IReadOnlyList<StationHintCandidate> inputCandidates)
    {
        if (progressCandidates.Count == 0 && inputCandidates.Count == 0)
        {
            return;
        }

        EnsureCreated();
        _instance?.ShowGroups(progressCandidates, inputCandidates);
    }

    private void Create(Hud hud, GameObject elementPrefab)
    {
        _hoverText = hud.m_hoverName;
        if (_hoverText == null)
        {
            throw new InvalidOperationException("HUD hover text is unavailable.");
        }

        Transform parent = _hoverText.rectTransform.parent;
        _root = new GameObject("FineDining_StationHints", typeof(RectTransform));
        _root.SetActive(false);
        _root.transform.SetParent(parent, false);
        ConfigureTopLeftRect(
            (RectTransform)_root.transform,
            Vector2.zero);
        _inputRoot = CreateGrid("FineDining_AvailableStationInputs", _root.transform);
        _progressRoot = CreateGrid("FineDining_CookingProgress", _root.transform);

        for (int index = 0; index < StationModule.MaxHints; index++)
        {
            _inputElements.Add(new Element(
                "FineDining_StationInputHint_" + index,
                _inputRoot.transform,
                elementPrefab,
                emphasizeStatusText: false));
            _progressElements.Add(new Element(
                "FineDining_CookingProgress_" + index,
                _progressRoot.transform,
                elementPrefab,
                emphasizeStatusText: true));
        }

        ApplyConfiguration();
    }

    private static GameObject CreateGrid(string name, Transform parent)
    {
        GameObject root = new(name, typeof(RectTransform));
        root.SetActive(false);
        root.transform.SetParent(parent, false);

        int maximumRows = StationModule.MaxHintRows;
        ConfigureTopLeftRect(
            (RectTransform)root.transform,
            new Vector2(
                StationModule.HintColumns * CellWidth
                + (StationModule.HintColumns - 1) * ColumnSpacing,
                maximumRows * CellHeight + (maximumRows - 1) * RowSpacing));

        GridLayoutGroup layout = root.AddComponent<GridLayoutGroup>();
        layout.startCorner = GridLayoutGroup.Corner.UpperLeft;
        layout.startAxis = GridLayoutGroup.Axis.Horizontal;
        layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        layout.constraintCount = StationModule.HintColumns;
        layout.cellSize = new Vector2(CellWidth, CellHeight);
        layout.spacing = new Vector2(ColumnSpacing, RowSpacing);
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.childAlignment = TextAnchor.UpperLeft;
        return root;
    }

    private static void ConfigureTopLeftRect(RectTransform rect, Vector2 size)
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = rect.anchorMin;
        rect.pivot = new Vector2(0f, 1f);
        rect.sizeDelta = size;
        rect.localPosition = Vector3.zero;
    }

    private void ShowGroups(
        IReadOnlyList<StationHintCandidate> progressCandidates,
        IReadOnlyList<StationHintCandidate> inputCandidates)
    {
        _visibleProgressCount = SetElements(_progressElements, progressCandidates);
        _visibleInputCount = SetElements(_inputElements, inputCandidates);
        ApplyConfiguration();

        _progressRoot?.SetActive(_visibleProgressCount > 0);
        _inputRoot?.SetActive(_visibleInputCount > 0);
        _root?.SetActive(_visibleProgressCount > 0 || _visibleInputCount > 0);
        _lastShowFrame = Time.frameCount;
    }

    private static int SetElements(
        List<Element> elements,
        IReadOnlyList<StationHintCandidate> candidates)
    {
        int visibleCount = Math.Min(elements.Count, candidates.Count);
        for (int index = 0; index < elements.Count; index++)
        {
            if (index < visibleCount)
            {
                elements[index].Set(candidates[index]);
            }
            else
            {
                elements[index].Hide();
            }
        }

        return visibleCount;
    }

    private void ApplyConfiguration()
    {
        if (_root == null || _inputRoot == null || _progressRoot == null)
        {
            return;
        }

        _root.transform.localScale = Vector3.one * StationModule.IconGroupScale.Value;
        _progressRoot.transform.localPosition = Vector3.zero;
        _inputRoot.transform.localPosition = Vector3.zero;

        if (_visibleProgressCount > 0 && _visibleInputCount > 0)
        {
            int progressRows = (_visibleProgressCount + StationModule.HintColumns - 1)
                               / StationModule.HintColumns;
            float progressHeight = progressRows * CellHeight
                                   + (progressRows - 1) * RowSpacing;
            _inputRoot.transform.localPosition =
                Vector3.down * (progressHeight + ProgressInputGap);
        }
    }

    private void LateUpdate()
    {
        if (_lastShowFrame == Time.frameCount)
        {
            LayoutBelowHoverText();
            return;
        }

        _root?.SetActive(false);
    }

    private void LayoutBelowHoverText()
    {
        if (_root == null || _hoverText == null)
        {
            return;
        }

        _hoverText.ForceMeshUpdate();
        RectTransform hoverRect = _hoverText.rectTransform;
        Vector3 bottomLeft = _hoverText.textInfo.characterCount > 0
            ? _hoverText.textBounds.min
            : new Vector3(hoverRect.rect.xMin, hoverRect.rect.yMin, 0f);
        if (!IsFinite(bottomLeft.x) || !IsFinite(bottomLeft.y))
        {
            bottomLeft = new Vector3(hoverRect.rect.xMin, hoverRect.rect.yMin, 0f);
        }

        Vector3 worldBottomLeft = hoverRect.TransformPoint(bottomLeft);
        Transform? parent = _root.transform.parent;
        Vector3 localBottomLeft = parent != null
            ? parent.InverseTransformPoint(worldBottomLeft)
            : worldBottomLeft;
        _root.transform.localPosition = new Vector3(
            localBottomLeft.x,
            localBottomLeft.y - HoverGap,
            0f);
    }

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        DestroyRoot();
    }

    private void DestroyRoot()
    {
        GameObject? root = _root;
        _root = null;
        _inputRoot = null;
        _progressRoot = null;
        _hoverText = null;
        _inputElements.Clear();
        _progressElements.Clear();
        _visibleInputCount = 0;
        _visibleProgressCount = 0;

        if (root != null)
        {
            root.SetActive(false);
            Object.Destroy(root);
        }
    }

    private sealed class Element
    {
        private readonly GameObject _go;
        private readonly Image _icon;
        private readonly TMP_Text _label;
        private readonly TMP_Text _status;
        private readonly TMP_Text _secondaryStatus;

        internal Element(
            string name,
            Transform root,
            GameObject elementPrefab,
            bool emphasizeStatusText)
        {
            _go = Instantiate(elementPrefab, root);
            _go.name = name;
            float statusTextScale = emphasizeStatusText ? CookingProgressTextScale : 1f;

            foreach (MonoBehaviour behaviour in _go.GetComponents<MonoBehaviour>())
            {
                // The Valheim 1.0 inventory element includes TouchRaycastPadding,
                // which requires the root Image. Keep the cloned component graph
                // intact and disable its behaviours instead of removing them.
                behaviour.enabled = false;
            }

            Transform iconTransform = _go.transform.Find("icon");
            Transform amountTransform = _go.transform.Find("amount");

            Image? icon = iconTransform != null ? iconTransform.GetComponent<Image>() : null;
            TMP_Text? label = amountTransform != null ? amountTransform.GetComponent<TMP_Text>() : null;
            if (icon == null || label == null)
            {
                throw new InvalidOperationException(
                    "Inventory element prefab is missing its icon or amount UI.");
            }

            _icon = icon;
            _label = label;

            GameObject statusObject = Instantiate(amountTransform!.gameObject, _go.transform);
            statusObject.name = "FineDining_StationStatus";
            TMP_Text? status = statusObject.GetComponent<TMP_Text>();
            if (status == null)
            {
                throw new InvalidOperationException(
                    "Inventory element prefab is missing a usable amount UI.");
            }

            _status = status;

            GameObject secondaryStatusObject = Instantiate(amountTransform.gameObject, _go.transform);
            secondaryStatusObject.name = "FineDining_StationSecondaryStatus";
            TMP_Text? secondaryStatus = secondaryStatusObject.GetComponent<TMP_Text>();
            if (secondaryStatus == null)
            {
                throw new InvalidOperationException(
                    "Inventory element prefab is missing a usable secondary status UI.");
            }

            _secondaryStatus = secondaryStatus;

            foreach (Transform child in _go.transform)
            {
                if (child.name != "icon"
                    && child.name != "amount"
                    && child.name != "FineDining_StationStatus"
                    && child.name != "FineDining_StationSecondaryStatus")
                {
                    child.gameObject.SetActive(false);
                    Object.Destroy(child.gameObject);
                }
            }

            RectTransform labelRect = _label.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 0f);
            labelRect.pivot = new Vector2(0.5f, 0f);
            labelRect.anchoredPosition = new Vector2(0f, 1f);
            labelRect.sizeDelta = new Vector2(-4f, 30f);
            _label.textWrappingMode = TextWrappingModes.Normal;
            _label.overflowMode = TextOverflowModes.Ellipsis;
            _label.fontSize = 11f;
            _label.enableAutoSizing = true;
            _label.fontSizeMin = 8f;
            _label.fontSizeMax = 11f;
            _label.maxVisibleLines = 2;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color = Color.white;

            RectTransform statusRect = _status.rectTransform;
            statusRect.anchorMin = new Vector2(0f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(0.5f, 1f);
            statusRect.anchoredPosition = new Vector2(0f, -1f * statusTextScale);
            statusRect.sizeDelta = new Vector2(-4f, 24f * statusTextScale);
            _status.textWrappingMode = TextWrappingModes.NoWrap;
            _status.overflowMode = TextOverflowModes.Ellipsis;
            _status.fontSize = 12f * statusTextScale;
            _status.enableAutoSizing = true;
            _status.fontSizeMin = 7f * statusTextScale;
            _status.fontSizeMax = 12f * statusTextScale;
            _status.maxVisibleLines = 1;
            _status.alignment = TextAlignmentOptions.Center;
            _status.color = new Color32(255, 165, 0, 255);

            RectTransform secondaryStatusRect = _secondaryStatus.rectTransform;
            secondaryStatusRect.anchorMin = new Vector2(0f, 1f);
            secondaryStatusRect.anchorMax = new Vector2(1f, 1f);
            secondaryStatusRect.pivot = new Vector2(0.5f, 1f);
            secondaryStatusRect.anchoredPosition = new Vector2(0f, -19f * statusTextScale);
            secondaryStatusRect.sizeDelta = new Vector2(-4f, 20f * statusTextScale);
            _secondaryStatus.textWrappingMode = TextWrappingModes.NoWrap;
            _secondaryStatus.overflowMode = TextOverflowModes.Ellipsis;
            _secondaryStatus.fontSize = 10f * statusTextScale;
            _secondaryStatus.enableAutoSizing = true;
            _secondaryStatus.fontSizeMin = 7f * statusTextScale;
            _secondaryStatus.fontSizeMax = 10f * statusTextScale;
            _secondaryStatus.maxVisibleLines = 1;
            _secondaryStatus.alignment = TextAlignmentOptions.Center;
            _secondaryStatus.color = new Color32(159, 232, 112, 255);

            foreach (Graphic graphic in _go.GetComponentsInChildren<Graphic>(true))
            {
                graphic.raycastTarget = false;
            }

            Hide();
        }

        internal void Set(StationHintCandidate candidate)
        {
            _go.SetActive(true);
            _icon.enabled = candidate.Icon != null;
            _icon.sprite = candidate.Icon;
            _label.enabled = true;
            _label.text = candidate.DisplayName;
            _status.enabled = !string.IsNullOrEmpty(candidate.StatusText);
            _status.text = candidate.StatusText;
            _secondaryStatus.enabled = !string.IsNullOrEmpty(candidate.SecondaryStatusText);
            _secondaryStatus.text = candidate.SecondaryStatusText;
        }

        internal void Hide()
        {
            _go.SetActive(false);
        }
    }
}

internal sealed class StationHintCandidate
{
    internal StationHintCandidate(
        string displayName,
        Sprite? icon,
        string statusText = "",
        string secondaryStatusText = "")
    {
        DisplayName = displayName;
        Icon = icon;
        StatusText = statusText;
        SecondaryStatusText = secondaryStatusText;
    }

    internal string DisplayName { get; }

    internal Sprite? Icon { get; }

    internal string StatusText { get; }

    internal string SecondaryStatusText { get; }
}
