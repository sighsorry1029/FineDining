using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace FineDining;

// Owns the fixed native content and its registration in each live game database.
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
    private static bool _registering;
    private static bool _materialsSearched;
    private static GameObject? _prefabRoot;
    private static readonly HashSet<ObjectDB> Databases = new();
    private static readonly HashSet<ZNetScene> Scenes = new();
    private static readonly HashSet<Sprite> OwnedIcons = new();
    private static readonly AccessTools.FieldRef<StatusEffect, int> EffectNameHash =
        AccessTools.FieldRefAccess<StatusEffect, int>("m_nameHash");

    internal static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
    }

    internal static void Shutdown()
    {
        _initialized = false;
        foreach (GameObject? prefab in new[] { _rottenProducePrefab, _rottenFoodPrefab, _iceboxPrefab })
        {
            if (prefab == null) continue;
            int hash = prefab.name.GetStableHashCode();
            foreach (ObjectDB db in Databases)
            {
                if (db == null) continue;
                db.m_items.RemoveAll(item => ReferenceEquals(item, prefab));
                if (ReferenceEquals(db.GetItemPrefab(hash), prefab)) db.ItemHashes().Remove(hash);
                ItemDrop? drop = prefab.GetComponent<ItemDrop>();
                if (drop != null && ReferenceEquals(db.GetItemPrefab(drop.m_itemData.m_shared), prefab))
                    db.ItemDataPrefabs().Remove(drop.m_itemData.m_shared);
                GetHammerTable(db)?.m_pieces.RemoveAll(item => ReferenceEquals(item, prefab));
            }
            foreach (ZNetScene scene in Scenes)
            {
                if (scene == null) continue;
                scene.m_prefabs.RemoveAll(item => ReferenceEquals(item, prefab));
                scene.m_nonNetViewPrefabs.RemoveAll(item => ReferenceEquals(item, prefab));
                if (ReferenceEquals(scene.GetPrefab(hash), prefab)) scene.NamedPrefabs().Remove(hash);
            }
        }
        foreach (SE_Puke? effect in new[] { _rottenProducePukeStatusEffect, _rottenFoodPukeStatusEffect })
        {
            if (effect == null) continue;
            foreach (ObjectDB db in Databases)
                if (db != null) db.m_StatusEffects.RemoveAll(item => ReferenceEquals(item, effect));
            UnityEngine.Object.Destroy(effect);
        }
        foreach (Sprite icon in OwnedIcons)
        {
            if (icon == null) continue;
            UnityEngine.Object.Destroy(icon.texture);
            UnityEngine.Object.Destroy(icon);
        }
        if (_prefabRoot != null) UnityEngine.Object.Destroy(_prefabRoot);
        _prefabRoot = _iceboxPrefab = _rottenProducePrefab = _rottenFoodPrefab = null;
        _rottenProducePukeStatusEffect = _rottenFoodPukeStatusEffect = null;
        _iceboxIcon = _rottenProduceIcon = _rottenFoodIcon = null;
        _iceboxMaterial = _rottenMaterial = null;
        _materialsSearched = false;
        Databases.Clear();
        Scenes.Clear();
        OwnedIcons.Clear();
        RenderedIcons.Clear();
        ReportedProblems.Clear();
    }

    internal static void RegisterContent(ObjectDB? objectDb = null)
    {
        objectDb ??= ObjectDB.instance;
        if (!_initialized || _registering || objectDb == null) return;
        _registering = true;
        _materialsSearched = false;
        Databases.RemoveWhere(db => db == null);
        Scenes.RemoveWhere(scene => scene == null);
        Databases.Add(objectDb);
        if (ZNetScene.instance != null) Scenes.Add(ZNetScene.instance);
        try
        {

            _rottenProducePukeStatusEffect = AliveOrNull(_rottenProducePukeStatusEffect) ?? CreatePukeStatusEffect(
                objectDb, RottenProducePukeStatusEffectName,
                RottenProducePukeDurationSeconds);
            _rottenFoodPukeStatusEffect = AliveOrNull(_rottenFoodPukeStatusEffect) ?? CreatePukeStatusEffect(
                objectDb, RottenFoodPukeStatusEffectName,
                RottenFoodPukeDurationSeconds);

            if ((object?)_rottenProducePukeStatusEffect != null)
            {
                _rottenProducePrefab = AliveOrNull(_rottenProducePrefab) ?? CreateGeneratedItem(
                    objectDb, SpoilageDefaults.RottenProducePrefabName,
                    RottenProduceSourcePrefabName,
                    RottenProduceNameToken,
                    RottenProduceDescriptionToken,
                    _rottenProducePukeStatusEffect!,
                    ref _rottenProduceIcon);
            }

            if ((object?)_rottenFoodPukeStatusEffect != null)
            {
                _rottenFoodPrefab = AliveOrNull(_rottenFoodPrefab) ?? CreateGeneratedItem(
                    objectDb, SpoilageDefaults.RottenFoodPrefabName,
                    RottenFoodSourcePrefabName,
                    RottenFoodNameToken,
                    RottenFoodDescriptionToken,
                    _rottenFoodPukeStatusEffect!,
                    ref _rottenFoodIcon);
            }

            _iceboxPrefab = AliveOrNull(_iceboxPrefab) ?? CreateIcebox();
            RefreshConfiguredContent();
            RegisterItem(objectDb, _rottenProducePrefab, _rottenProducePukeStatusEffect);
            RegisterItem(objectDb, _rottenFoodPrefab, _rottenFoodPukeStatusEffect);
            if (_iceboxPrefab != null && ZNetScene.instance != null)
                RegisterScenePrefab(ZNetScene.instance, _iceboxPrefab);

            RefreshIceboxBuildContent();
        }
        catch (Exception ex)
        {
            LogProblemOnce(
                "native-content",
                $"Could not register FineDining native content: {ex}");
        }
        finally { _registering = false; }
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
                "native-refresh",
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
                "native-icebox-refresh",
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
    /// Verifies that FineDining has installed the generated replacement into the
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
        bool sceneReady = ReferenceEquals(scene!.GetPrefab(hash), expected);
        ItemDrop? itemDrop = expected!.GetComponent<ItemDrop>();
        StatusEffect? consumeEffect = itemDrop?.m_itemData?.m_shared?.m_consumeStatusEffect;
        bool statusEffectReady = (object?)consumeEffect != null &&
                                 ReferenceEquals(objectDb!.GetStatusEffect(consumeEffect!.NameHash()), consumeEffect);
        bool ready = ReferenceEquals(objectDbPrefab, expected) && sceneReady && statusEffectReady;
        if (!ready)
        {
            LogProblemOnce(
                "replacement-not-ready:" + replacementName,
                $"FineDining has not installed generated replacement '{replacementName}' into every live registry; expired source items will be retained.");
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

    private static SE_Puke? CreatePukeStatusEffect(ObjectDB db, string statusEffectName, float durationSeconds)
    {
        SE_Puke? source = db.GetItemPrefab(PukeSourceItemPrefabName)?.GetComponent<ItemDrop>()
            ?.m_itemData?.m_shared?.m_consumeStatusEffect as SE_Puke;
        source = AliveOrNull(source) ?? db.GetStatusEffect(PukeSourceStatusEffectName.GetStableHashCode()) as SE_Puke;
        if (source == null) return null; // ObjectDB may still be empty during menu Awake.
        if (db.GetStatusEffect(statusEffectName.GetStableHashCode()) != null)
        {
            LogProblemOnce("effect-collision:" + statusEffectName, "Status effect name/hash occupied: " + statusEffectName);
            return null;
        }
        SE_Puke clone = UnityEngine.Object.Instantiate(source);
        clone.name = statusEffectName;
        EffectNameHash(clone) = statusEffectName.GetStableHashCode();
        clone.m_ttl = durationSeconds;
        clone.m_category = GeneratedPukeStatusEffectCategory;
        UnityEngine.Object.DontDestroyOnLoad(clone);
        return clone;
    }

    private static GameObject? CreateGeneratedItem(
        ObjectDB db,
        string prefabName,
        string sourcePrefabName,
        string nameToken,
        string descriptionToken,
        SE_Puke consumeStatusEffect,
        ref Sprite? storedIcon)
    {
        GameObject? source = FindItemPrefab(db, sourcePrefabName);
        if (source == null) return null;
        if (db.GetItemPrefab(prefabName) != null || ZNetScene.instance?.GetPrefab(prefabName) != null)
        {
            LogProblemOnce("item-collision:" + prefabName, "Prefab name/hash occupied: " + prefabName);
            return null;
        }
        GameObject prefab = UnityEngine.Object.Instantiate(source, EnsurePrefabRoot().transform);
        prefab.name = prefabName;
        if (prefab.GetComponent<ItemDrop>() == null || prefab.GetComponent<ZNetView>() == null)
        {
            UnityEngine.Object.Destroy(prefab);
            LogProblemOnce("item-shape:" + prefabName, "Item source is not a complete network prefab: " + sourcePrefabName);
            return null;
        }
        try
        {
            ConfigureGeneratedItem(
                prefab,
                prefabName,
                nameToken,
                descriptionToken,
                consumeStatusEffect,
                ref storedIcon);
            return prefab;
        }
        catch
        {
            UnityEngine.Object.Destroy(prefab);
            throw;
        }
    }

    private static GameObject? CreateIcebox()
    {
        ZNetScene? scene = ZNetScene.instance;
        GameObject? source = scene?.GetPrefab(IceboxSourcePrefabName);
        if (source == null) return null;
        if (scene!.GetPrefab(IceboxSubsystem.PrefabName) != null)
        {
            LogProblemOnce("piece-collision", "Icebox prefab name/hash is occupied.");
            return null;
        }
        GameObject prefab = UnityEngine.Object.Instantiate(source, EnsurePrefabRoot().transform);
        prefab.name = IceboxSubsystem.PrefabName;
        if (prefab.GetComponent<Piece>() == null || prefab.GetComponent<Container>() == null ||
            prefab.GetComponent<WearNTear>() == null || prefab.GetComponent<ZNetView>() == null)
        {
            UnityEngine.Object.Destroy(prefab);
            LogProblemOnce("icebox-shape", "Icebox source is missing required components.");
            return null;
        }
        try
        {
            ConfigureIceboxPrefab(prefab);
            return prefab;
        }
        catch
        {
            UnityEngine.Object.Destroy(prefab);
            throw;
        }
    }

    private static GameObject EnsurePrefabRoot()
    {
        if (_prefabRoot != null) return _prefabRoot;
        _prefabRoot = new GameObject("FineDining Native Prefabs");
        _prefabRoot.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_prefabRoot);
        return _prefabRoot;
    }

    private static bool CanRegisterScenePrefab(ZNetScene scene, GameObject prefab)
    {
        int hash = prefab.name.GetStableHashCode();
        GameObject? existing = scene.GetPrefab(hash);
        return (existing == null || ReferenceEquals(existing, prefab)) &&
            !scene.m_prefabs.Concat(scene.m_nonNetViewPrefabs).Any(p =>
                p != null && !ReferenceEquals(p, prefab) && p.name.GetStableHashCode() == hash);
    }

    private static bool RegisterScenePrefab(ZNetScene scene, GameObject prefab)
    {
        if (!CanRegisterScenePrefab(scene, prefab))
        {
            LogProblemOnce("scene-collision:" + prefab.name, "Network prefab name/hash occupied: " + prefab.name);
            return false;
        }
        var list = prefab.GetComponent<ZNetView>() != null ? scene.m_prefabs : scene.m_nonNetViewPrefabs;
        if (!list.Contains(prefab)) list.Add(prefab);
        scene.NamedPrefabs()[prefab.name.GetStableHashCode()] = prefab;
        return true;
    }

    private static void RegisterItem(ObjectDB db, GameObject? prefab, SE_Puke? effect)
    {
        if (prefab == null || effect == null) return;
        int hash = prefab.name.GetStableHashCode();
        GameObject? existing = db.GetItemPrefab(hash);
        StatusEffect? existingEffect = db.GetStatusEffect(effect.NameHash());
        if ((existing != null && !ReferenceEquals(existing, prefab)) ||
            (existingEffect != null && !ReferenceEquals(existingEffect, effect)) ||
            db.m_items.Any(p => p != null && !ReferenceEquals(p, prefab) && p.name.GetStableHashCode() == hash) ||
            (ZNetScene.instance != null && !CanRegisterScenePrefab(ZNetScene.instance, prefab)))
        {
            LogProblemOnce("registration-collision:" + prefab.name, "Native registration refused a conflicting name/hash: " + prefab.name);
            return;
        }
        if (!db.m_StatusEffects.Contains(effect)) db.m_StatusEffects.Add(effect);
        if (!db.m_items.Contains(prefab)) db.m_items.Add(prefab);
        db.ItemHashes()[hash] = prefab;
        db.ItemDataPrefabs()[prefab.GetComponent<ItemDrop>().m_itemData.m_shared] = prefab;
        if (ZNetScene.instance != null) RegisterScenePrefab(ZNetScene.instance, prefab);
    }

    private static PieceTable? GetHammerTable(ObjectDB? db) =>
        db?.GetItemPrefab("Hammer")?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_buildPieces;

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
            PieceTable? table = GetHammerTable(objectDb);
            // Keep the build menu unavailable until the world can actually instantiate the piece.
            if (table == null || ZNetScene.instance == null ||
                !ReferenceEquals(ZNetScene.instance.GetPrefab(prefab!.name), prefab)) return;
            if (!table.m_pieces.Contains(prefab)) table.m_pieces.Add(prefab);
            RefreshLocalPieceTable(table);
        }
        catch (Exception ex)
        {
            LogDebugOnce("icebox-piece-table", $"Icebox build-table refresh is waiting for Hammer: {ex.Message}");
        }
    }

    private static void RemoveIceboxFromCurrentPieceTable(GameObject prefab)
    {
        PieceTable? pieceTable = GetHammerTable(ObjectDB.instance);
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
            (object?)Player.m_localPlayer!.BuildPieces() != null &&
            ReferenceEquals(Player.m_localPlayer.BuildPieces(), pieceTable))
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

        GameObject? managed = AliveOrNull(ZNetScene.instance?.GetPrefab(prefabName));
        return managed != null && string.Equals(managed.name, prefabName, StringComparison.Ordinal) &&
               managed.GetComponent<ItemDrop>() != null ? managed : null;
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
        Sprite? icon = GeneratedIconRenderer.Render(prefab, iconName);
        if (icon != null) OwnedIcons.Add(icon);
        return icon;
    }

    private static Material? ResolveMaterial(string materialName, ref Material? cached)
    {
        cached = AliveOrNull(cached);
        if ((object?)cached != null)
        {
            return cached;
        }

        // Search once per content notification, including misses. Retain only the two
        // borrowed materials, not an array holding every loaded game material alive.
        if (!_materialsSearched)
        {
            _materialsSearched = true;
            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (material == null) continue;
                string name = NormalizeMaterialName(material.name);
                if (_iceboxMaterial == null && name.Equals(IceboxMaterialName, StringComparison.OrdinalIgnoreCase))
                    _iceboxMaterial = material;
                if (_rottenMaterial == null && name.Equals(RottenMaterialName, StringComparison.OrdinalIgnoreCase))
                    _rottenMaterial = material;
            }
        }
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

    internal static void LogDebugOnce(string key, string message)
    {
        if (ReportedProblems.Add("debug:" + key))
        {
            FineDiningPlugin.Log.LogDebug(message);
        }
    }
}

[HarmonyPatch(typeof(ObjectDB), "Awake")]
internal static class NativeContentObjectDbAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ObjectDB __instance) => GeneratedPrefabRegistry.RegisterContent(__instance);
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class NativeContentObjectDbCopyPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ObjectDB __instance) => GeneratedPrefabRegistry.RegisterContent(__instance);
}

[HarmonyPatch(typeof(ZNetScene), "Awake")]
internal static class NativeContentSceneAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        SpoilageContentLifecycle.Refresh();
    }
}
