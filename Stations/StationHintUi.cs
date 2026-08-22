using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FineDining;

internal sealed class StationHintUi : MonoBehaviour
{
    private const int Columns = 5;
    private const float CellHeight = 96f;
    private const float RowSpacing = 5f;

    private static StationHintUi? _instance;
    private static bool _loggedMissingDependencies;
    private static bool _loggedCreationFailure;
    private static float _nextCreationAttemptTime;

    private readonly List<Element> _inputElements = new(StationModule.MaxHints);
    private readonly List<Element> _progressElements = new(StationModule.MaxHints);
    private GameObject? _root;
    private GameObject? _inputRoot;
    private GameObject? _progressRoot;
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
            if (StationModule.Diagnostics.Value && !_loggedMissingDependencies)
            {
                _loggedMissingDependencies = true;
                FineDiningPlugin.Log.LogInfo(
                    $"[Station Diagnostics] UI not ready. hud={hud != null}, inventoryGui={inventoryGui != null}, "
                    + $"playerGrid={playerGrid != null}, elementPrefab={elementPrefab != null}.");
            }

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
            _loggedMissingDependencies = false;
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
        _loggedMissingDependencies = false;
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
        Transform parent = hud.m_crosshair != null ? hud.m_crosshair.transform.parent : hud.transform;
        _root = new GameObject("FineDining_StationHints");
        _root.SetActive(false);
        _root.transform.SetParent(parent, false);
        _inputRoot = CreateGrid("FineDining_AvailableStationInputs", _root.transform);
        _progressRoot = CreateGrid("FineDining_CookingProgress", _root.transform);

        for (int index = 0; index < StationModule.MaxHints; index++)
        {
            _inputElements.Add(new Element(
                "FineDining_StationInputHint_" + index,
                _inputRoot.transform,
                elementPrefab));
            _progressElements.Add(new Element(
                "FineDining_CookingProgress_" + index,
                _progressRoot.transform,
                elementPrefab));
        }

        ApplyConfiguration();
    }

    private static GameObject CreateGrid(string name, Transform parent)
    {
        GameObject root = new(name);
        root.SetActive(false);
        root.transform.SetParent(parent, false);

        GridLayoutGroup layout = root.AddComponent<GridLayoutGroup>();
        layout.startCorner = GridLayoutGroup.Corner.UpperLeft;
        layout.startAxis = GridLayoutGroup.Axis.Horizontal;
        layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        layout.constraintCount = Columns;
        layout.cellSize = new Vector2(82f, CellHeight);
        layout.spacing = new Vector2(5f, RowSpacing);
        layout.padding = new RectOffset(76, 0, 0, 46);
        layout.childAlignment = TextAnchor.UpperLeft;
        return root;
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

        _root.transform.localPosition = new Vector3(
            StationModule.IconGroupOffsetX.Value,
            StationModule.IconGroupOffsetY.Value,
            0f);
        _root.transform.localScale = Vector3.one * StationModule.IconGroupScale.Value;
        _inputRoot.transform.localPosition = Vector3.zero;

        float progressOffset = 0f;
        if (_visibleProgressCount > 0 && _visibleInputCount > 0)
        {
            int progressRows = (_visibleProgressCount + Columns - 1) / Columns;
            progressOffset = (progressRows - 1) * (CellHeight + RowSpacing)
                             + CellHeight
                             + StationModule.CookingGroupGap.Value;
        }

        _progressRoot.transform.localPosition = Vector3.up * progressOffset;
    }

    private void LateUpdate()
    {
        if (_lastShowFrame != Time.frameCount)
        {
            _root?.SetActive(false);
        }
    }

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

        internal Element(string name, Transform root, GameObject elementPrefab)
        {
            _go = Instantiate(elementPrefab, root);
            _go.name = name;

            foreach (MonoBehaviour behaviour in _go.GetComponents<MonoBehaviour>())
            {
                if (behaviour is Graphic graphic)
                {
                    graphic.enabled = false;
                }

                Object.Destroy(behaviour);
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

            foreach (Transform child in _go.transform)
            {
                if (child.name != "icon"
                    && child.name != "amount"
                    && child.name != "FineDining_StationStatus")
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
            statusRect.anchoredPosition = new Vector2(0f, -1f);
            statusRect.sizeDelta = new Vector2(-4f, 24f);
            _status.textWrappingMode = TextWrappingModes.NoWrap;
            _status.overflowMode = TextOverflowModes.Ellipsis;
            _status.fontSize = 12f;
            _status.enableAutoSizing = true;
            _status.fontSizeMin = 7f;
            _status.fontSizeMax = 12f;
            _status.maxVisibleLines = 1;
            _status.alignment = TextAlignmentOptions.Center;
            _status.color = Color.white;

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
        }

        internal void Hide()
        {
            _go.SetActive(false);
        }
    }
}
