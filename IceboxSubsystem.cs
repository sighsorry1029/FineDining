using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace FineDining;

/// <summary>
/// Lifecycle, identity, and configuration for the Icebox runtime.
///
/// The generated-prefab code must keep <see cref="PrefabName"/> stable.  ZDOs in
/// existing worlds refer to its stable hash, so changing this value is a world
/// migration rather than a cosmetic rename.
/// </summary>
internal static class IceboxSubsystem
{
    internal const string PrefabName = "FineDining_Icebox";
    internal const string OwnerAccountIdKey = "sighsorry.FineDining.IceboxOwnerAccountId";
    internal const string LimitRefundProcessedKey = "sighsorry.FineDining.IceboxLimitRefundProcessed";
    internal const string PlacedRecipeKey = "sighsorry.FineDining.IceboxPlacedRecipe";
    internal const int StorageColumns = 8;
    internal const int DefaultStorageRows = 4;
    internal const int MinimumStorageRows = 4;
    internal const int MaximumStorageRows = 20;
    internal const int DefaultPlacementLimit = 2;
    internal const string DefaultRecipe = "TrophySGolem:1,Obsidian:8,Crystal:16,Silver:32";
    internal const bool DefaultShowMapPins = true;

    internal static int PrefabHash => PrefabName.GetStableHashCode();

    private static readonly AccessTools.FieldRef<Inventory, int> InventoryWidth =
        AccessTools.FieldRefAccess<Inventory, int>("m_width");
    private static readonly AccessTools.FieldRef<Inventory, int> InventoryHeight =
        AccessTools.FieldRefAccess<Inventory, int>("m_height");

    private static FineDiningPlugin? _owner;
    private static ConfigEntry<int>? _defaultPlacementLimit;
    private static ConfigEntry<int>? _storageRows;
    private static ConfigEntry<string>? _recipe;
    private static ConfigEntry<bool>? _showMapPins;

    internal static bool IsInitialized => _owner != null;
    internal static int PlacementLimit =>
        _defaultPlacementLimit?.Value ?? DefaultPlacementLimit;
    internal static int StorageRows => Mathf.Clamp(
        _storageRows?.Value ?? DefaultStorageRows,
        MinimumStorageRows,
        MaximumStorageRows);
    internal static string Recipe => _recipe?.Value ?? DefaultRecipe;
    internal static bool ShowMapPins => _showMapPins?.Value == true;

    internal static void Initialize(FineDiningPlugin owner)
    {
        if (owner == null)
        {
            throw new ArgumentNullException(nameof(owner));
        }

        if (ReferenceEquals(_owner, owner))
        {
            return;
        }

        Shutdown();
        _owner = owner;
        _defaultPlacementLimit = BindDefaultPlacementLimit(owner.Config);
        _storageRows = BindStorageRows(owner.Config);
        _recipe = BindRecipe(owner.Config);
        _showMapPins = owner.Config.Bind(
            ConfigPresentation.Spoilage.Name,
            "Icebox Map Pins",
            DefaultShowMapPins,
            ConfigPresentation.Client(
                "Show the positions of Iceboxes owned by this account on the world map and minimap.",
                ConfigPresentation.Spoilage,
                200));

        SyncedConfigEntry<int> synchronizedPlacementLimit =
            FineDiningPlugin.ConfigSync.AddConfigEntry(_defaultPlacementLimit);
        synchronizedPlacementLimit.SynchronizedConfig = true;
        SyncedConfigEntry<int> synchronizedRows =
            FineDiningPlugin.ConfigSync.AddConfigEntry(_storageRows);
        synchronizedRows.SynchronizedConfig = true;
        SyncedConfigEntry<string> synchronizedRecipe =
            FineDiningPlugin.ConfigSync.AddConfigEntry(_recipe);
        synchronizedRecipe.SynchronizedConfig = true;

        _storageRows.SettingChanged += OnStorageRowsChanged;
        _recipe.SettingChanged += OnRecipeChanged;
        _showMapPins.SettingChanged += OnShowMapPinsChanged;

        IceboxLimitPolicy.Initialize();
        IceboxQuotaService.Initialize();
        IceboxMapPins.Initialize();
    }

    internal static void Tick()
    {
        if (_owner == null)
        {
            return;
        }

        IceboxLimitPolicy.Tick();
        IceboxQuotaService.Tick();
        IceboxMapPins.Tick();
    }

    internal static void ResetSession()
    {
        IceboxQuotaService.ResetSession();
        IceboxMapPins.ResetSession();
    }

    internal static void Shutdown()
    {
        _defaultPlacementLimit = null;

        if (_storageRows != null)
        {
            _storageRows.SettingChanged -= OnStorageRowsChanged;
            _storageRows = null;
        }

        if (_recipe != null)
        {
            _recipe.SettingChanged -= OnRecipeChanged;
            _recipe = null;
        }

        if (_showMapPins != null)
        {
            _showMapPins.SettingChanged -= OnShowMapPinsChanged;
            _showMapPins = null;
        }

        IceboxMapPins.Shutdown();
        IceboxQuotaService.Shutdown();
        IceboxLimitPolicy.Shutdown();
        _owner = null;
    }

    internal static ConfigEntry<int> BindDefaultPlacementLimit(ConfigFile config) =>
        config.Bind(
            ConfigPresentation.Spoilage.Name,
            "Icebox Default Placement Limit",
            DefaultPlacementLimit,
            ConfigPresentation.Synced(
                "Default number of Iceboxes each Steam account may place. -1 allows unlimited " +
                "placement, 0 denies placement, and positive values set the maximum. Exact " +
                "Steam64 overrides in Icebox.yml take priority.",
                ConfigPresentation.Spoilage,
                450,
                new PlacementLimitAcceptableValue()));

    internal static ConfigEntry<int> BindStorageRows(ConfigFile config) =>
        config.Bind(
            ConfigPresentation.Spoilage.Name,
            "Icebox Storage Rows",
            DefaultStorageRows,
            ConfigPresentation.Synced(
                "Number of Icebox inventory rows. Iceboxes always have eight columns.",
                ConfigPresentation.Spoilage,
                400,
                new AcceptableValueRange<int>(MinimumStorageRows, MaximumStorageRows)));

    internal static ConfigEntry<string> BindRecipe(ConfigFile config) =>
        config.Bind(
            ConfigPresentation.Spoilage.Name,
            "Icebox Build Recipe",
            DefaultRecipe,
            ConfigPresentation.Synced(
                "Comma-separated ItemPrefab:Amount entries used to build the Icebox, for example " +
                $"{DefaultRecipe}. Every amount must be a positive integer and every prefab must " +
                "be a registered item.",
                ConfigPresentation.Spoilage,
                300));

    internal static bool TryCreateRequirements(
        ObjectDB objectDb,
        string? recipe,
        out Piece.Requirement[] requirements)
    {
        requirements = Array.Empty<Piece.Requirement>();
        if (!TryParseRecipe(
                recipe,
                out List<KeyValuePair<string, int>> ingredients,
                out string error))
        {
            GeneratedPrefabRegistry.LogProblemOnce(
                "icebox-recipe-format:" + (recipe ?? string.Empty),
                $"Could not apply the Icebox recipe '{recipe ?? string.Empty}': {error} " +
                $"Expected comma-separated ItemPrefab:Amount entries such as '{DefaultRecipe}'.");
            return false;
        }

        Piece.Requirement[] resolved = new Piece.Requirement[ingredients.Count];
        for (int index = 0; index < ingredients.Count; index++)
        {
            KeyValuePair<string, int> ingredient = ingredients[index];
            ItemDrop? itemDrop = GeneratedPrefabRegistry.FindItemPrefab(objectDb, ingredient.Key)
                ?.GetComponent<ItemDrop>();
            if ((object?)itemDrop == null)
            {
                GeneratedPrefabRegistry.LogDebugOnce(
                    "icebox-recipe-item:" + ingredient.Key,
                    $"Could not apply the Icebox recipe yet: item prefab '{ingredient.Key}' is not ready.");
                return false;
            }

            resolved[index] = new Piece.Requirement
            {
                m_resItem = itemDrop!,
                m_amount = ingredient.Value,
                m_amountPerLevel = 0,
                m_recover = true
            };
        }

        requirements = resolved;
        return true;
    }

    internal static string SerializeRequirements(Piece.Requirement[]? requirements)
    {
        if (requirements == null || requirements.Length == 0)
        {
            return string.Empty;
        }

        List<string> entries = new(requirements.Length);
        HashSet<string> prefabNames = new(StringComparer.Ordinal);
        foreach (Piece.Requirement? requirement in requirements)
        {
            ItemDrop? itemDrop = requirement?.m_resItem;
            string prefabName = (object?)itemDrop != null
                ? FoodIdentity.NormalizePrefabName(Utils.GetPrefabName(itemDrop!.gameObject))
                : string.Empty;
            int amount = requirement?.m_amount ?? 0;
            if (prefabName.Length == 0 || amount <= 0 || !prefabNames.Add(prefabName))
            {
                return string.Empty;
            }

            entries.Add(prefabName + ":" + amount.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(",", entries);
    }

    private static bool TryParseRecipe(
        string? recipe,
        out List<KeyValuePair<string, int>> ingredients,
        out string error)
    {
        ingredients = new List<KeyValuePair<string, int>>();
        error = string.Empty;
        string text = recipe?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "the recipe is empty.";
            return false;
        }

        HashSet<string> prefabNames = new(StringComparer.Ordinal);
        foreach (string rawEntry in text.Split(','))
        {
            string entry = rawEntry.Trim();
            string[] fields = entry.Split(':');
            if (fields.Length != 2)
            {
                error = $"entry '{entry}' does not contain exactly one ':' separator.";
                return false;
            }

            string prefabName = fields[0].Trim();
            string rawAmount = fields[1].Trim();
            if (prefabName.Length == 0)
            {
                error = $"entry '{entry}' has an empty prefab name.";
                return false;
            }

            if (!int.TryParse(
                    rawAmount,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int amount) ||
                amount <= 0)
            {
                error = $"entry '{entry}' must use a positive integer amount.";
                return false;
            }

            if (!prefabNames.Add(prefabName))
            {
                error = $"item prefab '{prefabName}' is listed more than once.";
                return false;
            }

            ingredients.Add(new KeyValuePair<string, int>(prefabName, amount));
        }

        return true;
    }

    internal static void ApplyConfiguredStorageSize(Container? container)
    {
        if (container == null || !IsIcebox(container))
        {
            return;
        }

        Inventory? inventory = container.GetInventory();
        int rows = ResolveSafeStorageRows(inventory);
        container.m_width = StorageColumns;
        container.m_height = rows;

        if (inventory == null)
        {
            return;
        }

        InventoryWidth(inventory) = StorageColumns;
        InventoryHeight(inventory) = rows;
    }

    internal static bool PrepareStorageLoad(Container? container)
    {
        if (container == null || !IsIcebox(container))
        {
            return false;
        }

        // Inventory.Load silently drops saved entries whose coordinates are
        // outside the current dimensions. Load every supported row first, then
        // reduce only to a size that still contains all loaded slots.
        container.m_width = StorageColumns;
        container.m_height = MaximumStorageRows;
        Inventory? inventory = container.GetInventory();
        if (inventory != null)
        {
            InventoryWidth(inventory) = StorageColumns;
            InventoryHeight(inventory) = MaximumStorageRows;
        }

        return true;
    }

    internal static void CapturePlacedRecipe(Piece? piece)
    {
        if (piece == null || !IsIcebox(piece))
        {
            return;
        }

        ZNetView? view = piece.NetworkView() ?? piece.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner())
        {
            return;
        }

        ZDO zdo = view.GetZDO();
        if (!string.IsNullOrEmpty(zdo.GetString(PlacedRecipeKey, string.Empty)))
        {
            return;
        }

        string snapshot = SerializeRequirements(piece.m_resources);
        if (snapshot.Length == 0)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not persist the construction recipe for a newly placed Icebox; " +
                "its recovery requirements cannot be protected across a recipe change.");
            return;
        }

        zdo.Set(PlacedRecipeKey, snapshot);
    }

    internal static void ApplyStoredRecipe(Container? container)
    {
        if (container == null || !IsIcebox(container))
        {
            return;
        }

        Piece? piece = container.GetComponent<Piece>();
        ZNetView? view = piece?.NetworkView() ?? container.GetComponent<ZNetView>();
        if (piece == null || view == null || !view.IsValid())
        {
            return;
        }

        string snapshot = view.GetZDO().GetString(PlacedRecipeKey, string.Empty);
        if (snapshot.Length == 0)
        {
            // There is intentionally no migration path for Iceboxes placed
            // before recipe snapshots existed.
            return;
        }

        ObjectDB? objectDb = ObjectDB.instance;
        if (objectDb != null &&
            TryCreateRequirements(
                objectDb,
                snapshot,
                out Piece.Requirement[] requirements))
        {
            piece.m_resources = requirements;
            return;
        }

        // Never substitute the current recipe for a stored one: doing so would
        // let a later config change transform or duplicate refunded resources.
        piece.m_resources = Array.Empty<Piece.Requirement>();
    }

    internal static void ApplyStoredRecipesToLoadedIceboxes()
    {
        foreach (Container container in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
        {
            ApplyStoredRecipe(container);
        }
    }

    internal static void HandleInventoryChanged(Inventory? inventory)
    {
        if (inventory == null ||
            DecayRuntime.IsContainerLoading(inventory) ||
            !DecayRuntime.TryGetContainer(inventory, out Container? container) ||
            container == null ||
            !IsIcebox(container))
        {
            return;
        }

        ApplyConfiguredStorageSize(container);
    }

    private static int ResolveSafeStorageRows(Inventory? inventory)
    {
        int rows = StorageRows;
        if (inventory == null)
        {
            return rows;
        }

        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            rows = Math.Max(rows, item.m_gridPos.y + 1);
        }

        return Mathf.Clamp(rows, MinimumStorageRows, MaximumStorageRows);
    }

    private static void ApplyConfiguredStorageSizeToLoadedIceboxes()
    {
        foreach (Container container in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
        {
            ApplyConfiguredStorageSize(container);
        }
    }

    internal static bool IsIcebox(Piece? piece)
    {
        if (piece == null)
        {
            return false;
        }

        ZNetView? view = piece.NetworkView();
        if (view != null && view.IsValid())
        {
            return IsIcebox(view.GetZDO());
        }

        return IsIcebox(piece.gameObject);
    }

    internal static bool IsIcebox(Container? container)
    {
        return container != null && IsIcebox(((Component)container).gameObject);
    }

    internal static bool IsIcebox(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return false;
        }

        string normalized = FoodIdentity.NormalizePrefabName(Utils.GetPrefabName(gameObject));
        return string.Equals(normalized, PrefabName, StringComparison.Ordinal);
    }

    internal static bool IsIcebox(ZDO? zdo)
    {
        return zdo != null && zdo.IsValid() && zdo.GetPrefab() == PrefabHash;
    }

    internal static string NormalizeAccountId(string? rawAccountId)
    {
        string value = rawAccountId?.Trim() ?? string.Empty;
        const string steamPrefix = "Steam_";
        if (value.StartsWith(steamPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value.Substring(steamPrefix.Length);
        }

        return value;
    }

    private static void OnShowMapPinsChanged(object sender, EventArgs args)
    {
        IceboxMapPins.HandleConfigChanged();
    }

    private static void OnStorageRowsChanged(object sender, EventArgs args)
    {
        ApplyConfiguredStorageSizeToLoadedIceboxes();
        GeneratedPrefabRegistry.RefreshIceboxConfiguredContent();
    }

    private static void OnRecipeChanged(object sender, EventArgs args)
    {
        GeneratedPrefabRegistry.RefreshIceboxConfiguredContent();
    }

    private sealed class PlacementLimitAcceptableValue : AcceptableValueBase
    {
        internal PlacementLimitAcceptableValue()
            : base(typeof(int))
        {
        }

        public override object Clamp(object value) =>
            value is int limit && limit >= -1 ? limit : DefaultPlacementLimit;

        public override bool IsValid(object value) =>
            value is int limit && limit >= -1;

        public override string ToDescriptionString() =>
            "# Acceptable values: -1 or greater";
    }
}

[HarmonyPatch(typeof(ZNet), "Awake")]
internal static class ZNetAwakeIceboxSubsystemPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix()
    {
        IceboxSubsystem.ResetSession();
    }
}

[HarmonyPatch(typeof(ZNetScene), "Awake")]
internal static class ZNetSceneAwakeIceboxSubsystemPatch
{
    private static void Postfix()
    {
        IceboxQuotaService.EnsureRpcBindings();
        IceboxMapPins.EnsureRpcBindings();
    }
}

[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
internal static class ZdoManLoadIceboxSubsystemPatch
{
    private static void Postfix(ZDOMan __instance)
    {
        IceboxQuotaService.OnAuthoritativeWorldLoaded(__instance);
    }
}

[HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
internal static class PieceSetCreatorIceboxSubsystemPatch
{
    private static void Postfix(Piece __instance)
    {
        IceboxSubsystem.CapturePlacedRecipe(__instance);
        IceboxQuotaService.NotifyLocallyPlacedIcebox(__instance);
    }
}

[HarmonyPatch(typeof(Minimap), "OnDestroy")]
internal static class MinimapDestroyIceboxSubsystemPatch
{
    private static void Prefix(Minimap __instance)
    {
        IceboxMapPins.HandleMinimapDestroyed(__instance);
    }
}
