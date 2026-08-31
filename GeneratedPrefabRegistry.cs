using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Rendering;

namespace FineDining;

/// <summary>
/// Creates FineDining's fixed custom content and hands its registration
/// lifecycle to Jotunn. FineDining remains responsible only for gameplay
/// configuration of those prefabs.
/// </summary>
internal static class GeneratedPrefabRegistry
{
    internal const string RottenProducePukeStatusEffectName = "FineDining_PukeRottenProduce";
    internal const string RottenFoodPukeStatusEffectName = "FineDining_PukeRottenFood";

    internal const string IceboxNameToken = "$finedining_icebox";
    internal const string IceboxDescriptionToken = "$finedining_icebox_description";
    internal const string RottenProduceNameToken = "$finedining_rotten_produce";
    internal const string RottenProduceDescriptionToken = "$finedining_rotten_produce_description";
    internal const string RottenFoodNameToken = "$finedining_rotten_food";
    internal const string RottenFoodDescriptionToken = "$finedining_rotten_food_description";

    private const string IceboxSourcePrefabName = "piece_chest";
    private const string RottenProduceSourcePrefabName = "Resin";
    private const string RottenFoodSourcePrefabName = "BreadDough";
    private const string PukeSourceItemPrefabName = "RottenMeat";
    private const string PukeSourceStatusEffectName = "Puke";
    private const string GeneratedPukeStatusEffectCategory = "FineDining_Puke";
    private const float RottenProducePukeDurationSeconds = 5f;
    private const float RottenFoodPukeDurationSeconds = 10f;
    private const string IceboxMaterialName = "antifreezegland";
    private const string RottenMaterialName = "LoxMeatRotten";
    private const float IceboxHealth = 1000f;
    private const int GeneratedIconSize = 128;

    private static readonly MethodInfo? MemberwiseCloneMethod = typeof(object).GetMethod(
        "MemberwiseClone",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly HashSet<string> ReportedProblems = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RenderedIcons = new(StringComparer.Ordinal);

    private static GameObject? _iceboxPrefab;
    private static GameObject? _rottenProducePrefab;
    private static GameObject? _rottenFoodPrefab;
    private static SE_Puke? _rottenProducePukeStatusEffect;
    private static SE_Puke? _rottenFoodPukeStatusEffect;
    private static Sprite? _iceboxIcon;
    private static Sprite? _rottenProduceIcon;
    private static Sprite? _rottenFoodIcon;
    private static Material? _iceboxMaterial;
    private static Material? _rottenMaterial;
    private static bool _initialized;

    internal static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        PrefabManager.OnVanillaPrefabsAvailable += CreateContent;
        PrefabManager.OnPrefabsRegistered += OnJotunnRegistriesReady;
        ItemManager.OnItemsRegistered += OnJotunnRegistriesReady;
        PieceManager.OnPiecesRegistered += OnJotunnRegistriesReady;
    }

    internal static void Shutdown()
    {
        if (!_initialized)
        {
            return;
        }

        PrefabManager.OnVanillaPrefabsAvailable -= CreateContent;
        PrefabManager.OnPrefabsRegistered -= OnJotunnRegistriesReady;
        ItemManager.OnItemsRegistered -= OnJotunnRegistriesReady;
        PieceManager.OnPiecesRegistered -= OnJotunnRegistriesReady;
        _initialized = false;
    }

    private static void CreateContent()
    {
        try
        {
            ReconcileManagedContent();
            _rottenProducePukeStatusEffect ??= CreatePukeStatusEffect(
                RottenProducePukeStatusEffectName,
                RottenProducePukeDurationSeconds);
            _rottenFoodPukeStatusEffect ??= CreatePukeStatusEffect(
                RottenFoodPukeStatusEffectName,
                RottenFoodPukeDurationSeconds);

            if ((object?)_rottenProducePukeStatusEffect != null)
            {
                _rottenProducePrefab ??= CreateGeneratedItem(
                    SpoilageDefaults.RottenProducePrefabName,
                    RottenProduceSourcePrefabName,
                    RottenProduceNameToken,
                    RottenProduceDescriptionToken,
                    _rottenProducePukeStatusEffect!,
                    ref _rottenProduceIcon);
            }

            if ((object?)_rottenFoodPukeStatusEffect != null)
            {
                _rottenFoodPrefab ??= CreateGeneratedItem(
                    SpoilageDefaults.RottenFoodPrefabName,
                    RottenFoodSourcePrefabName,
                    RottenFoodNameToken,
                    RottenFoodDescriptionToken,
                    _rottenFoodPukeStatusEffect!,
                    ref _rottenFoodIcon);
            }

            _iceboxPrefab ??= CreateIcebox();

            if ((object?)_rottenProducePrefab != null &&
                (object?)_rottenFoodPrefab != null &&
                (object?)_iceboxPrefab != null)
            {
                LogInfoOnce("jotunn-content", "FineDining custom content was created and handed to Jotunn.");
            }

            RefreshConfiguredContent();
        }
        catch (Exception ex)
        {
            LogProblemOnce(
                "jotunn-content",
                $"Could not create FineDining custom content through Jotunn: {ex}");
        }
    }

    private static void ReconcileManagedContent()
    {
        CustomItem? managedProduce = ItemManager.Instance.GetItem(SpoilageDefaults.RottenProducePrefabName);
        CustomItem? managedFood = ItemManager.Instance.GetItem(SpoilageDefaults.RottenFoodPrefabName);
        CustomPiece? managedIcebox = PieceManager.Instance.GetPiece(IceboxSubsystem.PrefabName);

        _rottenProducePrefab = AliveOrNull(managedProduce?.ItemPrefab) ?? AliveOrNull(_rottenProducePrefab);
        _rottenFoodPrefab = AliveOrNull(managedFood?.ItemPrefab) ?? AliveOrNull(_rottenFoodPrefab);
        _iceboxPrefab = AliveOrNull(managedIcebox?.PiecePrefab) ?? AliveOrNull(_iceboxPrefab);

        _rottenProducePukeStatusEffect =
            AliveOrNull(managedProduce?.ItemDrop?.m_itemData?.m_shared?.m_consumeStatusEffect as SE_Puke) ??
            AliveOrNull(_rottenProducePukeStatusEffect);
        _rottenFoodPukeStatusEffect =
            AliveOrNull(managedFood?.ItemDrop?.m_itemData?.m_shared?.m_consumeStatusEffect as SE_Puke) ??
            AliveOrNull(_rottenFoodPukeStatusEffect);
    }

    private static void OnJotunnRegistriesReady()
    {
        ReconcileManagedContent();
        RefreshConfiguredContent();
        FoodClassifier.Invalidate();
        DecayRuntime.InvalidateAll();
    }

    internal static void RefreshConfiguredContent()
    {
        try
        {
            if ((object?)AliveOrNull(_rottenProducePrefab) != null &&
                (object?)AliveOrNull(_rottenProducePukeStatusEffect) != null)
            {
                ConfigureGeneratedItem(
                    _rottenProducePrefab!,
                    SpoilageDefaults.RottenProducePrefabName,
                    RottenProduceNameToken,
                    RottenProduceDescriptionToken,
                    _rottenProducePukeStatusEffect!,
                    ref _rottenProduceIcon);
            }

            if ((object?)AliveOrNull(_rottenFoodPrefab) != null &&
                (object?)AliveOrNull(_rottenFoodPukeStatusEffect) != null)
            {
                ConfigureGeneratedItem(
                    _rottenFoodPrefab!,
                    SpoilageDefaults.RottenFoodPrefabName,
                    RottenFoodNameToken,
                    RottenFoodDescriptionToken,
                    _rottenFoodPukeStatusEffect!,
                    ref _rottenFoodIcon);
            }

            RefreshIceboxConfiguredContentCore();
        }
        catch (Exception ex)
        {
            LogProblemOnce(
                "jotunn-refresh",
                $"Could not refresh FineDining custom content: {ex}");
        }
    }

    internal static void RefreshIceboxConfiguredContent()
    {
        try
        {
            RefreshIceboxConfiguredContentCore();
        }
        catch (Exception ex)
        {
            LogProblemOnce(
                "jotunn-icebox-refresh",
                $"Could not refresh FineDining Icebox content: {ex}");
        }
    }

    private static void RefreshIceboxConfiguredContentCore()
    {
        if ((object?)AliveOrNull(_iceboxPrefab) != null)
        {
            ConfigureIceboxPrefab(_iceboxPrefab!);
            RefreshIceboxBuildContent();
        }

        IceboxSubsystem.ApplyStoredRecipesToLoadedIceboxes();
    }

    internal static bool IsGeneratedReplacementPrefabName(string? prefabName)
    {
        return string.Equals(
                   prefabName,
                   SpoilageDefaults.RottenProducePrefabName,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   prefabName,
                   SpoilageDefaults.RottenFoodPrefabName,
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that Jotunn has installed the generated replacement into the
    /// live registries. Expired source items are retained when this returns
    /// false, preventing item loss after an incomplete content load.
    /// </summary>
    internal static bool EnsureGeneratedReplacementAvailable(string? prefabName)
    {
        string replacementName;
        GameObject? expected;
        if (string.Equals(
                prefabName,
                SpoilageDefaults.RottenProducePrefabName,
                StringComparison.OrdinalIgnoreCase))
        {
            replacementName = SpoilageDefaults.RottenProducePrefabName;
            expected = AliveOrNull(_rottenProducePrefab);
        }
        else if (string.Equals(
                     prefabName,
                     SpoilageDefaults.RottenFoodPrefabName,
                     StringComparison.OrdinalIgnoreCase))
        {
            replacementName = SpoilageDefaults.RottenFoodPrefabName;
            expected = AliveOrNull(_rottenFoodPrefab);
        }
        else
        {
            return false;
        }

        ObjectDB? objectDb = AliveOrNull(ObjectDB.instance);
        ZNetScene? scene = AliveOrNull(ZNetScene.instance);
        if ((object?)expected == null || (object?)objectDb == null || (object?)scene == null)
        {
            return false;
        }

        int hash = replacementName.GetStableHashCode();
        GameObject? objectDbPrefab = AliveOrNull(objectDb!.GetItemPrefab(replacementName));
        bool sceneReady = scene!.m_namedPrefabs.TryGetValue(hash, out GameObject scenePrefab) &&
                          ReferenceEquals(scenePrefab, expected);
        ItemDrop? itemDrop = expected!.GetComponent<ItemDrop>();
        StatusEffect? consumeEffect = itemDrop?.m_itemData?.m_shared?.m_consumeStatusEffect;
        bool statusEffectReady = (object?)consumeEffect != null &&
                                 ReferenceEquals(objectDb!.GetStatusEffect(consumeEffect!.NameHash()), consumeEffect);
        bool ready = ReferenceEquals(objectDbPrefab, expected) && sceneReady && statusEffectReady;
        if (!ready)
        {
            LogProblemOnce(
                "replacement-not-ready:" + replacementName,
                $"Jotunn has not installed generated replacement '{replacementName}' into every live registry; expired source items will be retained.");
        }

        return ready;
    }

    internal static Sprite? GetIceboxIcon()
    {
        Sprite? icon = AliveOrNull(_iceboxIcon);
        if ((object?)icon != null)
        {
            return icon;
        }

        return AliveOrNull(_iceboxPrefab)?.GetComponent<Piece>()?.m_icon;
    }

    private static SE_Puke? CreatePukeStatusEffect(string statusEffectName, float durationSeconds)
    {
        GameObject? rottenMeat = PrefabManager.Instance.GetPrefab(PukeSourceItemPrefabName);
        SE_Puke? source = rottenMeat?.GetComponent<ItemDrop>()?.m_itemData?.m_shared
            ?.m_consumeStatusEffect as SE_Puke;
        source = AliveOrNull(source) ?? PrefabManager.Cache.GetPrefab<SE_Puke>(PukeSourceStatusEffectName);
        if ((object?)source == null)
        {
            LogProblemOnce(
                "puke-source:" + statusEffectName,
                $"Could not create '{statusEffectName}': vanilla status effect '{PukeSourceStatusEffectName}' was not found.");
            return null;
        }

        SE_Puke clone = UnityEngine.Object.Instantiate(source!);
        clone.name = statusEffectName;
        clone.m_nameHash = statusEffectName.GetStableHashCode();
        clone.m_ttl = durationSeconds;
        clone.m_category = GeneratedPukeStatusEffectCategory;
        UnityEngine.Object.DontDestroyOnLoad(clone);

        if (ItemManager.Instance.AddStatusEffect(new CustomStatusEffect(clone, fixReference: false)))
        {
            return clone;
        }

        UnityEngine.Object.Destroy(clone);
        LogProblemOnce(
            "puke-registration:" + statusEffectName,
            $"Jotunn refused status effect '{statusEffectName}', usually because that name is already registered.");
        return null;
    }

    private static GameObject? CreateGeneratedItem(
        string prefabName,
        string sourcePrefabName,
        string nameToken,
        string descriptionToken,
        SE_Puke consumeStatusEffect,
        ref Sprite? storedIcon)
    {
        if ((object?)AliveOrNull(PrefabManager.Instance.GetPrefab(prefabName)) != null)
        {
            LogProblemOnce(
                "item-collision:" + prefabName,
                $"Jotunn could not create '{prefabName}' because that prefab name is already occupied.");
            return null;
        }

        CustomItem customItem = new(prefabName, sourcePrefabName);
        GameObject? prefab = AliveOrNull(customItem.ItemPrefab);
        ItemDrop? itemDrop = AliveOrNull(customItem.ItemDrop);
        if ((object?)prefab == null || (object?)itemDrop == null)
        {
            LogProblemOnce(
                "item-source:" + prefabName,
                $"Could not clone source item '{sourcePrefabName}' as '{prefabName}'.");
            return null;
        }

        ConfigureGeneratedItem(
            prefab!,
            prefabName,
            nameToken,
            descriptionToken,
            consumeStatusEffect,
            ref storedIcon);
        if (!ItemManager.Instance.AddItem(customItem))
        {
            LogProblemOnce(
                "item-registration:" + prefabName,
                $"Jotunn refused generated item '{prefabName}'.");
            return null;
        }

        return prefab;
    }

    private static GameObject? CreateIcebox()
    {
        if ((object?)AliveOrNull(PrefabManager.Instance.GetPrefab(IceboxSubsystem.PrefabName)) != null)
        {
            LogProblemOnce(
                "piece-collision:" + IceboxSubsystem.PrefabName,
                $"Jotunn could not create '{IceboxSubsystem.PrefabName}' because that prefab name is already occupied.");
            return null;
        }

        CustomPiece customPiece = new(
            IceboxSubsystem.PrefabName,
            IceboxSourcePrefabName,
            PieceTables.Hammer);
        GameObject? prefab = AliveOrNull(customPiece.PiecePrefab);
        if ((object?)prefab == null ||
            (object?)AliveOrNull(customPiece.Piece) == null ||
            (object?)prefab!.GetComponent<Container>() == null ||
            (object?)prefab.GetComponent<WearNTear>() == null)
        {
            LogProblemOnce(
                "piece-source:" + IceboxSubsystem.PrefabName,
                $"Could not clone '{IceboxSourcePrefabName}' as '{IceboxSubsystem.PrefabName}' with its required components.");
            return null;
        }

        ConfigureIceboxPrefab(prefab);
        if (!PieceManager.Instance.AddPiece(customPiece))
        {
            LogProblemOnce(
                "piece-registration:" + IceboxSubsystem.PrefabName,
                $"Jotunn refused generated piece '{IceboxSubsystem.PrefabName}'.");
            return null;
        }

        return prefab;
    }

    private static void ConfigureGeneratedItem(
        GameObject prefab,
        string prefabName,
        string nameToken,
        string descriptionToken,
        SE_Puke consumeStatusEffect,
        ref Sprite? storedIcon)
    {
        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        if ((object?)itemDrop == null)
        {
            LogProblemOnce("itemdrop:" + prefabName, $"Generated prefab '{prefabName}' has no ItemDrop component.");
            return;
        }

        ItemDrop.ItemData.SharedData currentShared = GetConfiguredSharedData(itemDrop!, prefabName);
        if (!IsConfiguredGeneratedSharedData(currentShared, nameToken, consumeStatusEffect))
        {
            ItemDrop.ItemData independentItemData = itemDrop.m_itemData.Clone();
            ItemDrop.ItemData.SharedData shared = CloneSharedData(currentShared);
            SanitizeAsRottenConsumable(shared, nameToken, descriptionToken, consumeStatusEffect);
            independentItemData.m_shared = shared;
            itemDrop.m_itemData = independentItemData;
        }
        else
        {
            SanitizeAsRottenConsumable(
                itemDrop.m_itemData.m_shared,
                nameToken,
                descriptionToken,
                consumeStatusEffect);
        }

        itemDrop.m_itemData.m_stack = 1;
        itemDrop.m_itemData.m_quality = 1;
        itemDrop.m_itemData.m_variant = 0;
        itemDrop.m_itemData.m_crafterID = 0L;
        itemDrop.m_itemData.m_crafterName = string.Empty;
        itemDrop.m_itemData.m_customData.Clear();
        itemDrop.m_itemData.m_equipped = false;
        itemDrop.m_itemData.m_dropPrefab = prefab;

        storedIcon ??= itemDrop.m_itemData.m_shared.m_icons?.FirstOrDefault();
        Material? rottenMaterial = ResolveMaterial(RottenMaterialName, ref _rottenMaterial);
        if ((object?)rottenMaterial == null)
        {
            LogProblemOnce(
                "material:" + RottenMaterialName,
                $"Material '{RottenMaterialName}' was not found; generated rotten items use their source visuals.");
            return;
        }

        ApplyMaterialOverride(prefab, rottenMaterial!);
        TryApplyRenderedItemIcon(prefab, itemDrop, prefabName, ref storedIcon);
    }

    private static bool IsConfiguredGeneratedSharedData(
        ItemDrop.ItemData.SharedData? shared,
        string expectedNameToken,
        SE_Puke consumeStatusEffect)
    {
        return shared != null &&
               shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable &&
               string.Equals(shared.m_name, expectedNameToken, StringComparison.Ordinal) &&
               shared.m_appendToolTip == null &&
               ReferenceEquals(shared.m_consumeStatusEffect, consumeStatusEffect);
    }

    private static ItemDrop.ItemData.SharedData GetConfiguredSharedData(ItemDrop itemDrop, string prefabName)
    {
        if (itemDrop.m_itemData?.m_shared == null)
        {
            LogProblemOnce("shared:" + prefabName, $"Generated prefab '{prefabName}' has no item SharedData.");
            itemDrop.m_itemData ??= new ItemDrop.ItemData();
            itemDrop.m_itemData.m_shared = new ItemDrop.ItemData.SharedData();
        }

        return itemDrop.m_itemData.m_shared;
    }

    private static void SanitizeAsRottenConsumable(
        ItemDrop.ItemData.SharedData shared,
        string nameToken,
        string descriptionToken,
        SE_Puke consumeStatusEffect)
    {
        shared.m_name = nameToken;
        shared.m_description = descriptionToken;
        shared.m_subtitle = string.Empty;
        shared.m_dlc = string.Empty;
        shared.m_itemType = ItemDrop.ItemData.ItemType.Consumable;
        shared.m_attachOverride = ItemDrop.ItemData.ItemType.None;
        shared.m_buildPieces = null;
        shared.m_questItem = false;
        shared.m_autoStack = true;
        shared.m_maxQuality = 1;
        shared.m_scaleByQuality = 0f;
        shared.m_scaleWeightByQuality = 0f;
        shared.m_value = 0;
        shared.m_useDurability = false;
        shared.m_destroyBroken = false;
        shared.m_canBeReparied = false;
        shared.m_maxDurability = 0f;
        shared.m_durabilityPerLevel = 0f;
        shared.m_useDurabilityDrain = 0f;
        shared.m_durabilityDrain = 0f;
        shared.m_setName = string.Empty;
        shared.m_setSize = 0;
        shared.m_setStatusEffect = null;
        shared.m_equipStatusEffect = null;
        shared.m_consumeStatusEffect = consumeStatusEffect;
        shared.m_appendToolTip = null;
        shared.m_food = 0f;
        shared.m_foodStamina = 0f;
        shared.m_foodEitr = 0f;
        shared.m_foodBurnTime = 0f;
        shared.m_foodRegen = 0f;
        shared.m_isDrink = false;
        shared.m_eitrRegenModifier = 0f;
        shared.m_movementModifier = 0f;
        shared.m_homeItemsStaminaModifier = 0f;
        shared.m_heatResistanceModifier = 0f;
        shared.m_jumpStaminaModifier = 0f;
        shared.m_attackStaminaModifier = 0f;
        shared.m_blockStaminaModifier = 0f;
        shared.m_dodgeStaminaModifier = 0f;
        shared.m_swimStaminaModifier = 0f;
        shared.m_sneakStaminaModifier = 0f;
        shared.m_runStaminaModifier = 0f;

        if (shared.m_maxStackSize < 1)
        {
            shared.m_maxStackSize = 1;
        }

        shared.m_icons ??= Array.Empty<Sprite>();
        shared.m_variants = shared.m_icons.Length > 0 ? 1 : 0;
    }

    private static ItemDrop.ItemData.SharedData CloneSharedData(ItemDrop.ItemData.SharedData? source)
    {
        ItemDrop.ItemData.SharedData clone;
        try
        {
            clone = source != null && MemberwiseCloneMethod != null
                ? (ItemDrop.ItemData.SharedData)MemberwiseCloneMethod.Invoke(source, null)
                : new ItemDrop.ItemData.SharedData();
        }
        catch (Exception ex)
        {
            FineDiningPlugin.Log.LogDebug($"Could not copy generated item SharedData; using safe defaults: {ex.Message}");
            clone = new ItemDrop.ItemData.SharedData();
        }

        clone.m_icons = source?.m_icons?.ToArray() ?? Array.Empty<Sprite>();
        clone.m_helmetHairSettings = source?.m_helmetHairSettings != null
            ? new List<ItemDrop.ItemData.HelmetHairSettings>(source.m_helmetHairSettings)
            : new List<ItemDrop.ItemData.HelmetHairSettings>();
        clone.m_helmetBeardSettings = source?.m_helmetBeardSettings != null
            ? new List<ItemDrop.ItemData.HelmetHairSettings>(source.m_helmetBeardSettings)
            : new List<ItemDrop.ItemData.HelmetHairSettings>();
        clone.m_damageModifiers = source?.m_damageModifiers != null
            ? new List<HitData.DamageModPair>(source.m_damageModifiers)
            : new List<HitData.DamageModPair>();
        clone.m_itemStandOffsets = source?.m_itemStandOffsets != null
            ? new List<ItemStand.OrientationSettings>(source.m_itemStandOffsets)
            : new List<ItemStand.OrientationSettings>();
        return clone;
    }

    private static void ConfigureIceboxPrefab(GameObject prefab)
    {
        Piece? piece = prefab.GetComponent<Piece>();
        Container? container = prefab.GetComponent<Container>();
        WearNTear? wearNTear = prefab.GetComponent<WearNTear>();
        if ((object?)piece == null || (object?)container == null || (object?)wearNTear == null)
        {
            LogProblemOnce("icebox-components", $"Generated prefab '{IceboxSubsystem.PrefabName}' is missing a required component.");
            return;
        }

        piece!.m_name = IceboxNameToken;
        piece.m_description = IceboxDescriptionToken;
        piece.m_category = Piece.PieceCategory.Misc;
        container!.m_name = IceboxNameToken;
        container.m_width = IceboxSubsystem.StorageColumns;
        container.m_height = IceboxSubsystem.StorageRows;
        wearNTear!.m_health = IceboxHealth;

        _iceboxIcon ??= piece.m_icon;
        Material? material = ResolveMaterial(IceboxMaterialName, ref _iceboxMaterial);
        if ((object?)material == null)
        {
            LogProblemOnce(
                "material:" + IceboxMaterialName,
                $"Material '{IceboxMaterialName}' was not found; '{IceboxSubsystem.PrefabName}' uses the source chest visuals.");
            return;
        }

        ApplyMaterialOverride(prefab, material!);
        TryApplyRenderedPieceIcon(prefab, piece, ref _iceboxIcon);
    }

    private static void RefreshIceboxBuildContent()
    {
        GameObject? prefab = AliveOrNull(_iceboxPrefab);
        ObjectDB? objectDb = AliveOrNull(ObjectDB.instance);
        Piece? piece = prefab?.GetComponent<Piece>();
        if ((object?)prefab == null || (object?)objectDb == null || (object?)piece == null)
        {
            return;
        }

        if (!IceboxSubsystem.TryCreateRequirements(
                objectDb!,
                IceboxSubsystem.Recipe,
                out Piece.Requirement[] requirements))
        {
            piece!.m_enabled = false;
            RemoveIceboxFromCurrentPieceTable(prefab!);
            return;
        }

        piece!.m_resources = requirements;
        piece.m_enabled = true;
        try
        {
            PieceManager.Instance.RegisterPieceInPieceTable(prefab!, PieceTables.Hammer);
            RefreshLocalPieceTable(PieceManager.Instance.GetPieceTable(PieceTables.Hammer));
        }
        catch (Exception ex)
        {
            LogDebugOnce("icebox-piece-table", $"Icebox build-table refresh is waiting for Hammer: {ex.Message}");
        }
    }

    private static void RemoveIceboxFromCurrentPieceTable(GameObject prefab)
    {
        PieceTable? pieceTable = PieceManager.Instance.GetPieceTable(PieceTables.Hammer);
        if ((object?)pieceTable == null)
        {
            return;
        }

        pieceTable!.m_pieces.RemoveAll(existing => ReferenceEquals(existing, prefab));
        RefreshLocalPieceTable(pieceTable);
    }

    private static void RefreshLocalPieceTable(PieceTable? pieceTable)
    {
        if ((object?)pieceTable != null &&
            (object?)Player.m_localPlayer != null &&
            (object?)Player.m_localPlayer!.m_buildPieces != null &&
            ReferenceEquals(Player.m_localPlayer.m_buildPieces, pieceTable))
        {
            ((Humanoid)Player.m_localPlayer).SetPlaceMode(pieceTable);
        }
    }

    internal static GameObject? FindItemPrefab(ObjectDB? objectDb, string prefabName)
    {
        if ((object?)objectDb != null)
        {
            GameObject? registered = AliveOrNull(objectDb!.GetItemPrefab(prefabName));
            return (object?)registered != null &&
                   string.Equals(registered!.name, prefabName, StringComparison.Ordinal)
                ? registered
                : null;
        }

        GameObject? managed = AliveOrNull(PrefabManager.Instance.GetPrefab(prefabName));
        return (object?)managed != null &&
               string.Equals(managed!.name, prefabName, StringComparison.Ordinal) &&
               (object?)managed.GetComponent<ItemDrop>() != null
            ? managed
            : null;
    }

    private static void TryApplyRenderedItemIcon(
        GameObject prefab,
        ItemDrop itemDrop,
        string prefabName,
        ref Sprite? storedIcon)
    {
        if (RenderedIcons.Contains(prefabName))
        {
            Sprite? current = AliveOrNull(storedIcon);
            if ((object?)current != null)
            {
                itemDrop.m_itemData.m_shared.m_icons = new[] { current! };
                itemDrop.m_itemData.m_variant = 0;
            }

            return;
        }

        Sprite? rendered = RenderGeneratedIcon(prefab, prefabName + "_Icon");
        if ((object?)rendered == null)
        {
            return;
        }

        storedIcon = rendered;
        itemDrop.m_itemData.m_shared.m_icons = new[] { rendered! };
        itemDrop.m_itemData.m_variant = 0;
        itemDrop.m_itemData.m_shared.m_variants = 1;
        RenderedIcons.Add(prefabName);
    }

    private static void TryApplyRenderedPieceIcon(GameObject prefab, Piece piece, ref Sprite? storedIcon)
    {
        if (RenderedIcons.Contains(IceboxSubsystem.PrefabName))
        {
            Sprite? current = AliveOrNull(storedIcon);
            if ((object?)current != null)
            {
                piece.m_icon = current;
            }

            return;
        }

        Sprite? rendered = RenderGeneratedIcon(prefab, IceboxSubsystem.PrefabName + "_Icon");
        if ((object?)rendered == null)
        {
            return;
        }

        storedIcon = rendered;
        piece.m_icon = rendered;
        RenderedIcons.Add(IceboxSubsystem.PrefabName);
    }

    private static Sprite? RenderGeneratedIcon(GameObject prefab, string iconName)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return null;
        }

        try
        {
            Sprite? rendered = RenderManager.Instance.Render(
                new RenderManager.RenderRequest(prefab)
                {
                    Width = GeneratedIconSize,
                    Height = GeneratedIconSize,
                    Rotation = Quaternion.Euler(23f, 51f, 25.8f),
                    ParticleSimulationTime = -1f,
                    UseCache = false
                });
            if ((object?)rendered != null)
            {
                rendered!.name = iconName;
            }

            return rendered;
        }
        catch (Exception ex)
        {
            LogProblemOnce(
                "icon-render:" + iconName,
                $"Could not render icon '{iconName}' through Jotunn; using the source icon instead: {ex.Message}");
            return null;
        }
    }

    private static Material? ResolveMaterial(string materialName, ref Material? cached)
    {
        cached = AliveOrNull(cached);
        if ((object?)cached != null)
        {
            return cached;
        }

        cached = AliveOrNull(PrefabManager.Cache.GetPrefab<Material>(materialName));
        if ((object?)cached != null)
        {
            return cached;
        }

        string normalizedTarget = NormalizeMaterialName(materialName);
        cached = PrefabManager.Cache.GetPrefabs(typeof(Material))
            .Values
            .OfType<Material>()
            .Select(AliveOrNull)
            .FirstOrDefault(material =>
                (object?)material != null &&
                string.Equals(
                    NormalizeMaterialName(material!.name),
                    normalizedTarget,
                    StringComparison.OrdinalIgnoreCase));
        return cached;
    }

    private static void ApplyMaterialOverride(GameObject prefab, Material material)
    {
        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            if ((object?)renderer == null ||
                renderer.GetType().Name.Equals("ParticleSystemRenderer", StringComparison.Ordinal))
            {
                continue;
            }

            Material[] current = renderer!.sharedMaterials;
            if (current.Length == 0 || current.All(existing => ReferenceEquals(existing, material)))
            {
                continue;
            }

            renderer.sharedMaterials = Enumerable.Repeat(material, current.Length).ToArray();
        }
    }

    private static string NormalizeMaterialName(string? materialName)
    {
        return (materialName ?? string.Empty)
            .Replace(" (Instance)", string.Empty)
            .Trim();
    }

    private static T? AliveOrNull<T>(T? value) where T : UnityEngine.Object
    {
        return (object?)value != null && value != null ? value : null;
    }

    internal static void LogProblemOnce(string key, string message)
    {
        if (ReportedProblems.Add("warning:" + key))
        {
            FineDiningPlugin.Log.LogWarning(message);
        }
    }

    private static void LogInfoOnce(string key, string message)
    {
        if (ReportedProblems.Add("info:" + key))
        {
            FineDiningPlugin.Log.LogInfo(message);
        }
    }

    internal static void LogDebugOnce(string key, string message)
    {
        if (ReportedProblems.Add("debug:" + key))
        {
            FineDiningPlugin.Log.LogDebug(message);
        }
    }
}
