using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace FineDining;

internal enum FineDiningGeneratedPrefabKind
{
    Icebox,
    RottenProduce,
    RottenFood
}

/// <summary>
/// A small, serialized identity marker that is copied to spawned instances with
/// the generated prefab.  Runtime systems should still prefer the registry's
/// IsIcebox helpers so name/hash checks also work while a ZDO is unloaded.
/// </summary>
[DisallowMultipleComponent]
internal sealed class FineDiningGeneratedPrefabMarker : MonoBehaviour
{
    [SerializeField]
    private FineDiningGeneratedPrefabKind _kind;

    internal FineDiningGeneratedPrefabKind Kind => _kind;

    internal void Initialize(FineDiningGeneratedPrefabKind kind)
    {
        _kind = kind;
    }
}

/// <summary>
/// Owns the stable generated prefabs and Puke variants used by FineDining.
/// Item templates are children of an inactive DontDestroyOnLoad root: cloning
/// therefore does not run their world/network Awake path.  The Puke
/// ScriptableObjects are also persistent, and all generated content is
/// re-registered into each ObjectDB/ZNetScene created during an in-process
/// world restart.
/// </summary>
internal static class GeneratedPrefabRegistry
{
    internal const string IceboxPrefabName = "FineDining_Icebox";
    internal const string RottenProducePrefabName = "FineDining_RottenProduce";
    internal const string RottenFoodPrefabName = "FineDining_RottenFood";
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
    private const string GeneratedRootName = "FineDining Generated Prefabs";
    private const float IceboxHealth = 1000f;
    private const int GeneratedIconSize = 128;
    private const int GeneratedIconLayer = 30;

    private static readonly MethodInfo? MemberwiseCloneMethod = typeof(object).GetMethod(
        "MemberwiseClone",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly HashSet<string> ReportedProblems = new(StringComparer.Ordinal);

    private static GameObject? _generatedRoot;
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
    private static readonly HashSet<FineDiningGeneratedPrefabKind> AutoRenderedIcons = new();
    private static bool _registering;
    private static MonoBehaviour? _registrationRetryRunner;

    internal static int IceboxPrefabHash => IceboxPrefabName.GetStableHashCode();

    internal static bool IsGeneratedReplacementPrefabName(string? prefabName)
    {
        return string.Equals(prefabName, RottenProducePrefabName, StringComparison.Ordinal) ||
               string.Equals(prefabName, RottenFoodPrefabName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Synchronously recreates and registers one of FineDining's two fixed
    /// replacement items.  A successful result means both live registries point
    /// at the same owned prefab; callers can therefore avoid deleting an expired
    /// item while a generated replacement is still waiting for game data.
    /// </summary>
    internal static bool EnsureGeneratedReplacementAvailable(string? prefabName)
    {
        if (!TryGetGeneratedReplacementKind(prefabName, out FineDiningGeneratedPrefabKind kind))
        {
            return false;
        }

        ObjectDB? objectDb = AliveOrNull(ObjectDB.instance);
        ZNetScene? zNetScene = AliveOrNull(ZNetScene.instance);
        if (_registering)
        {
            return IsGeneratedItemRegistrationComplete(objectDb, zNetScene, kind);
        }

        _registering = true;
        try
        {
            return TryRegisterGeneratedReplacement(objectDb, zNetScene, kind);
        }
        finally
        {
            _registering = false;
        }
    }

    /// <summary>
    /// Coalesces ObjectDB/ZNetScene lifecycle callbacks into one next-frame
    /// registration pass.  This mirrors the proven FeedLikeGrandma timing: the
    /// synchronous pass keeps saved data safe, while the deferred pass sees
    /// sources and registry dictionaries populated by later postfixes.
    /// </summary>
    internal static void QueueRegistrationRetry(MonoBehaviour? runner)
    {
        MonoBehaviour? liveRunner = AliveOrNull(runner);
        if ((object?)liveRunner == null || (object?)AliveOrNull(_registrationRetryRunner) != null)
        {
            return;
        }

        _registrationRetryRunner = liveRunner;
        liveRunner!.StartCoroutine(RetryRegistrationNextFrame(liveRunner));
    }

    private static IEnumerator RetryRegistrationNextFrame(MonoBehaviour runner)
    {
        yield return null;
        if (ReferenceEquals(_registrationRetryRunner, runner))
        {
            _registrationRetryRunner = null;
        }

        RegisterConfiguredContent(ObjectDB.instance, ZNetScene.instance);
    }

    /// <summary>
    /// Re-establishes configured build data after ObjectDB has copied another
    /// database.  Persistent registration deliberately happens first; saved
    /// items and pieces therefore remain loadable even if build registration
    /// cannot yet resolve an ingredient or Hammer table.
    /// </summary>
    internal static void RegisterConfiguredContent(
        ObjectDB? objectDb = null,
        ZNetScene? zNetScene = null)
    {
        if (_registering)
        {
            return;
        }

        _registering = true;
        try
        {
            ObjectDB? db = AliveOrNull(objectDb) ?? AliveOrNull(ObjectDB.instance);
            ZNetScene? scene = AliveOrNull(zNetScene) ?? AliveOrNull(ZNetScene.instance);
            RegisterPersistentPrefabsCore(db, scene);
            RegisterIceboxBuildContent(db);
            IceboxSubsystem.ApplyStoredRecipesToLoadedIceboxes();
        }
        catch (Exception ex)
        {
            FineDiningPlugin.Log.LogError($"Generated build-content registration failed: {ex}");
        }
        finally
        {
            _registering = false;
        }
    }

    internal static bool IsIcebox(Container? container)
    {
        return (object?)container != null && IsIcebox(((Component)container!).gameObject);
    }

    internal static bool IsIcebox(GameObject? gameObject)
    {
        if ((object?)gameObject == null)
        {
            return false;
        }

        FineDiningGeneratedPrefabMarker? marker =
            gameObject!.GetComponent<FineDiningGeneratedPrefabMarker>();
        if ((object?)marker != null && marker!.Kind == FineDiningGeneratedPrefabKind.Icebox)
        {
            return true;
        }

        return IsIceboxPrefabName(Utils.GetPrefabName(gameObject));
    }

    internal static bool IsIceboxPrefabName(string? prefabName)
    {
        string normalized = NormalizePrefabName(prefabName);
        return string.Equals(normalized, IceboxPrefabName, StringComparison.Ordinal);
    }

    internal static Sprite? GetIceboxIcon() => AliveOrNull(_iceboxIcon);

    private static void RegisterPersistentPrefabsCore(ObjectDB? objectDb, ZNetScene? zNetScene)
    {
        // Keep each generated replacement independent.  A malformed source or
        // temporarily incomplete registry for one item must not prevent the
        // other item (or the Icebox) from becoming loadable.
        TryRegisterGeneratedReplacement(
            objectDb,
            zNetScene,
            FineDiningGeneratedPrefabKind.RottenProduce);
        TryRegisterGeneratedReplacement(
            objectDb,
            zNetScene,
            FineDiningGeneratedPrefabKind.RottenFood);

        try
        {
            GameObject? icebox = EnsureIcebox(objectDb, zNetScene);
            if ((object?)icebox != null)
            {
                RegisterGeneratedPiece(zNetScene, icebox!);
            }
        }
        catch (Exception ex)
        {
            LogProblemOnce(
                "registration-exception:" + IceboxPrefabName,
                $"Could not register generated prefab '{IceboxPrefabName}' yet; registration will be retried: {ex}");
        }
    }

    private static bool TryRegisterGeneratedReplacement(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        FineDiningGeneratedPrefabKind kind)
    {
        try
        {
            SE_Puke? consumeStatusEffect = EnsureGeneratedPukeStatusEffect(objectDb, kind);
            if ((object?)consumeStatusEffect == null)
            {
                return false;
            }

            return kind switch
            {
                FineDiningGeneratedPrefabKind.RottenProduce => TryRegisterGeneratedItem(
                    objectDb,
                    zNetScene,
                    RottenProducePrefabName,
                    RottenProduceSourcePrefabName,
                    RottenProduceNameToken,
                    RottenProduceDescriptionToken,
                    kind,
                    consumeStatusEffect!,
                    ref _rottenProducePrefab,
                    ref _rottenProduceIcon),
                FineDiningGeneratedPrefabKind.RottenFood => TryRegisterGeneratedItem(
                    objectDb,
                    zNetScene,
                    RottenFoodPrefabName,
                    RottenFoodSourcePrefabName,
                    RottenFoodNameToken,
                    RottenFoodDescriptionToken,
                    kind,
                    consumeStatusEffect!,
                    ref _rottenFoodPrefab,
                    ref _rottenFoodIcon),
                _ => false
            };
        }
        catch (Exception ex)
        {
            string prefabName = GetExpectedPrefabName(kind);
            LogProblemOnce(
                "registration-exception:" + prefabName,
                $"Could not register generated item '{prefabName}' yet; registration will be retried: {ex}");
            return false;
        }
    }

    private static SE_Puke? EnsureGeneratedPukeStatusEffect(
        ObjectDB? objectDb,
        FineDiningGeneratedPrefabKind kind)
    {
        return kind switch
        {
            FineDiningGeneratedPrefabKind.RottenProduce => EnsureGeneratedPukeStatusEffect(
                objectDb,
                RottenProducePukeStatusEffectName,
                RottenProducePukeDurationSeconds,
                ref _rottenProducePukeStatusEffect),
            FineDiningGeneratedPrefabKind.RottenFood => EnsureGeneratedPukeStatusEffect(
                objectDb,
                RottenFoodPukeStatusEffectName,
                RottenFoodPukeDurationSeconds,
                ref _rottenFoodPukeStatusEffect),
            _ => null
        };
    }

    private static SE_Puke? EnsureGeneratedPukeStatusEffect(
        ObjectDB? objectDb,
        string statusEffectName,
        float durationSeconds,
        ref SE_Puke? storedStatusEffect)
    {
        if ((object?)objectDb == null)
        {
            LogDebugOnce(
                "status-objectdb:" + statusEffectName,
                $"Could not create '{statusEffectName}' yet: ObjectDB is not ready.");
            return null;
        }

        storedStatusEffect = AliveOrNull(storedStatusEffect);
        if ((object?)storedStatusEffect == null)
        {
            StatusEffect? existing = FindStatusEffectByIdentity(objectDb!, statusEffectName);
            if ((object?)existing != null)
            {
                if (existing is not SE_Puke existingPuke ||
                    !string.Equals(existing.name, statusEffectName, StringComparison.Ordinal) ||
                    existingPuke.m_nameHash != statusEffectName.GetStableHashCode() ||
                    existingPuke.m_ttl != durationSeconds ||
                    !string.Equals(
                        existingPuke.m_category,
                        GeneratedPukeStatusEffectCategory,
                        StringComparison.Ordinal))
                {
                    string existingName = existing!.name ?? "<unnamed>";
                    LogProblemOnce(
                        "status-collision:" + statusEffectName,
                        $"Refusing to register status effect '{statusEffectName}': ObjectDB is already occupied by '{existingName}'.");
                    return null;
                }

                storedStatusEffect = existingPuke;
            }
            else
            {
                SE_Puke? source = FindPukeSourceStatusEffect(objectDb);
                if ((object?)source == null)
                {
                    LogDebugOnce(
                        "status-source:" + statusEffectName,
                        $"Could not create '{statusEffectName}' yet: vanilla status effect '{PukeSourceStatusEffectName}' is not ready.");
                    return null;
                }

                SE_Puke? clone = null;
                try
                {
                    clone = UnityEngine.Object.Instantiate(source!);
                    clone.name = statusEffectName;
                    clone.m_nameHash = statusEffectName.GetStableHashCode();
                    UnityEngine.Object.DontDestroyOnLoad(clone);
                    storedStatusEffect = clone;
                }
                catch (Exception ex)
                {
                    if ((object?)clone != null)
                    {
                        UnityEngine.Object.Destroy(clone);
                    }

                    LogProblemOnce(
                        "status-clone:" + statusEffectName,
                        $"Could not clone status effect '{PukeSourceStatusEffectName}' as '{statusEffectName}': {ex.GetBaseException().Message}");
                    return null;
                }
            }
        }

        ConfigureGeneratedPukeStatusEffect(
            storedStatusEffect!,
            statusEffectName,
            durationSeconds);
        UnityEngine.Object.DontDestroyOnLoad(storedStatusEffect!);
        return RegisterGeneratedPukeStatusEffect(
                objectDb!,
                storedStatusEffect!,
                statusEffectName)
            ? storedStatusEffect
            : null;
    }

    private static void ConfigureGeneratedPukeStatusEffect(
        SE_Puke statusEffect,
        string statusEffectName,
        float durationSeconds)
    {
        statusEffect.name = statusEffectName;
        // Instantiate copies Puke's cached hash. Rebind it to the generated
        // identity so SEMan RPC lookup cannot resolve the vanilla effect.
        statusEffect.m_nameHash = statusEffectName.GetStableHashCode();
        statusEffect.m_ttl = durationSeconds;
        // Vanilla Puke has no category because it has only one identity. The
        // generated variants need a shared category to prevent double ticking.
        statusEffect.m_category = GeneratedPukeStatusEffectCategory;
    }

    private static SE_Puke? FindPukeSourceStatusEffect(ObjectDB objectDb)
    {
        ItemDrop? rottenMeat = FindItemPrefab(objectDb, PukeSourceItemPrefabName)?.GetComponent<ItemDrop>();
        SE_Puke? linked = AliveOrNull(rottenMeat?.m_itemData?.m_shared?.m_consumeStatusEffect as SE_Puke);
        if ((object?)linked != null &&
            string.Equals(linked!.name, PukeSourceStatusEffectName, StringComparison.Ordinal))
        {
            return linked;
        }

        return objectDb.m_StatusEffects
            .Select(AliveOrNull)
            .OfType<SE_Puke>()
            .FirstOrDefault(statusEffect =>
                string.Equals(statusEffect.name, PukeSourceStatusEffectName, StringComparison.Ordinal));
    }

    private static StatusEffect? FindStatusEffectByIdentity(
        ObjectDB objectDb,
        string statusEffectName)
    {
        int expectedHash = statusEffectName.GetStableHashCode();
        foreach (StatusEffect? candidate in objectDb.m_StatusEffects)
        {
            StatusEffect? live = AliveOrNull(candidate);
            if ((object?)live == null)
            {
                continue;
            }

            if (string.Equals(live!.name, statusEffectName, StringComparison.Ordinal) ||
                live.NameHash() == expectedHash)
            {
                return live;
            }
        }

        return null;
    }

    private static bool RegisterGeneratedPukeStatusEffect(
        ObjectDB objectDb,
        SE_Puke statusEffect,
        string statusEffectName)
    {
        int expectedHash = statusEffectName.GetStableHashCode();
        foreach (StatusEffect? candidate in objectDb.m_StatusEffects)
        {
            StatusEffect? live = AliveOrNull(candidate);
            if ((object?)live == null || ReferenceEquals(live, statusEffect))
            {
                continue;
            }

            if (string.Equals(live!.name, statusEffectName, StringComparison.Ordinal) ||
                live.NameHash() == expectedHash)
            {
                LogProblemOnce(
                    "status-collision:" + statusEffectName,
                    $"Refusing to register status effect '{statusEffectName}': ObjectDB is already occupied by '{live.name}'.");
                return false;
            }
        }

        if (!objectDb.m_StatusEffects.Any(existing => ReferenceEquals(existing, statusEffect)))
        {
            objectDb.m_StatusEffects.Add(statusEffect);
        }

        if (!ReferenceEquals(objectDb.GetStatusEffect(expectedHash), statusEffect))
        {
            LogProblemOnce(
                "status-registration-incomplete:" + statusEffectName,
                $"Generated status effect '{statusEffectName}' registration is incomplete; registration will be retried.");
            return false;
        }

        LogInfoOnce(
            "status-registration-complete:" + statusEffectName,
            $"Generated status effect '{statusEffectName}' is registered with ObjectDB.");
        return true;
    }

    private static bool TryRegisterGeneratedItem(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        string prefabName,
        string sourcePrefabName,
        string nameToken,
        string descriptionToken,
        FineDiningGeneratedPrefabKind kind,
        SE_Puke consumeStatusEffect,
        ref GameObject? storedPrefab,
        ref Sprite? storedIcon)
    {
        try
        {
            GameObject? prefab = EnsureGeneratedItem(
                objectDb,
                zNetScene,
                prefabName,
                sourcePrefabName,
                nameToken,
                descriptionToken,
                kind,
                consumeStatusEffect,
                ref storedPrefab,
                ref storedIcon);
            if ((object?)prefab == null)
            {
                return false;
            }

            RegisterGeneratedItem(objectDb, zNetScene, prefab!, kind);
            return VerifyGeneratedItemRegistration(objectDb, zNetScene, prefab!, kind);
        }
        catch (Exception ex)
        {
            LogProblemOnce(
                "registration-exception:" + prefabName,
                $"Could not register generated item '{prefabName}' yet; registration will be retried: {ex}");
            return false;
        }
    }

    private static GameObject? EnsureGeneratedItem(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        string prefabName,
        string sourcePrefabName,
        string nameToken,
        string descriptionToken,
        FineDiningGeneratedPrefabKind kind,
        SE_Puke consumeStatusEffect,
        ref GameObject? storedPrefab,
        ref Sprite? storedIcon)
    {
        storedPrefab = AliveOrNull(storedPrefab);
        if ((object?)storedPrefab != null)
        {
            ConfigureGeneratedItem(
                storedPrefab!,
                prefabName,
                nameToken,
                descriptionToken,
                kind,
                consumeStatusEffect,
                ref storedIcon);
            return storedPrefab;
        }

        if (HasRegistryCollision(objectDb, zNetScene, prefabName, kind, includeObjectDb: true))
        {
            return null;
        }

        GameObject? ownedExisting = FindOwnedRegisteredPrefab(objectDb, zNetScene, prefabName, kind, includeObjectDb: true);
        if ((object?)ownedExisting != null)
        {
            storedPrefab = ownedExisting;
            ConfigureGeneratedItem(
                storedPrefab!,
                prefabName,
                nameToken,
                descriptionToken,
                kind,
                consumeStatusEffect,
                ref storedIcon);
            return storedPrefab;
        }

        GameObject? source = FindItemCloneSource(objectDb, zNetScene, sourcePrefabName);
        if ((object?)source == null)
        {
            LogDebugOnce(
                "source:" + prefabName,
                $"Could not create '{prefabName}' yet: source item '{sourcePrefabName}' is not ready.");
            return null;
        }

        if (!HasSpawnableItemShape(source!))
        {
            LogProblemOnce(
                "shape:" + prefabName,
                $"Could not create '{prefabName}': source item '{sourcePrefabName}' must have " +
                "ItemDrop, ZNetView, ZSyncTransform, Rigidbody, and Collider components.");
            return null;
        }

        GameObject clone = UnityEngine.Object.Instantiate(
            source!,
            EnsureGeneratedRoot().transform,
            worldPositionStays: false);
        clone.name = prefabName;
        AddMarker(clone, kind);
        storedPrefab = clone;
        ConfigureGeneratedItem(
            clone,
            prefabName,
            nameToken,
            descriptionToken,
            kind,
            consumeStatusEffect,
            ref storedIcon);
        return storedPrefab;
    }

    private static GameObject? EnsureIcebox(ObjectDB? objectDb, ZNetScene? zNetScene)
    {
        _iceboxPrefab = AliveOrNull(_iceboxPrefab);
        if ((object?)_iceboxPrefab != null)
        {
            ConfigureIceboxPrefab(_iceboxPrefab!);
            return _iceboxPrefab;
        }

        if (HasRegistryCollision(
                objectDb,
                zNetScene,
                IceboxPrefabName,
                FineDiningGeneratedPrefabKind.Icebox,
                includeObjectDb: false))
        {
            return null;
        }

        GameObject? ownedExisting = FindOwnedRegisteredPrefab(
            objectDb,
            zNetScene,
            IceboxPrefabName,
            FineDiningGeneratedPrefabKind.Icebox,
            includeObjectDb: false);
        if ((object?)ownedExisting != null)
        {
            _iceboxPrefab = ownedExisting;
            ConfigureIceboxPrefab(_iceboxPrefab!);
            return _iceboxPrefab;
        }

        GameObject? source = FindScenePrefab(zNetScene, IceboxSourcePrefabName);
        if ((object?)source == null)
        {
            LogDebugOnce(
                "source:" + IceboxPrefabName,
                $"Could not create '{IceboxPrefabName}' yet: source piece '{IceboxSourcePrefabName}' is not ready.");
            return null;
        }

        if ((object?)source!.GetComponent<Piece>() == null ||
            (object?)source.GetComponent<Container>() == null ||
            (object?)source.GetComponent<WearNTear>() == null ||
            (object?)source.GetComponent<ZNetView>() == null)
        {
            LogProblemOnce(
                "shape:" + IceboxPrefabName,
                $"Could not create '{IceboxPrefabName}': '{IceboxSourcePrefabName}' lacks Piece, Container, WearNTear, or ZNetView.");
            return null;
        }

        GameObject clone = UnityEngine.Object.Instantiate(
            source,
            EnsureGeneratedRoot().transform,
            worldPositionStays: false);
        clone.name = IceboxPrefabName;
        AddMarker(clone, FineDiningGeneratedPrefabKind.Icebox);
        _iceboxPrefab = clone;
        ConfigureIceboxPrefab(clone);
        return _iceboxPrefab;
    }

    private static void ConfigureGeneratedItem(
        GameObject prefab,
        string prefabName,
        string nameToken,
        string descriptionToken,
        FineDiningGeneratedPrefabKind kind,
        SE_Puke consumeStatusEffect,
        ref Sprite? storedIcon)
    {
        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        if ((object?)itemDrop == null)
        {
            LogProblemOnce("itemdrop:" + prefabName, $"Generated prefab '{prefabName}' lost its ItemDrop component.");
            return;
        }

        FineDiningGeneratedPrefabMarker? marker = prefab.GetComponent<FineDiningGeneratedPrefabMarker>();
        if ((object?)marker == null || marker!.Kind != kind)
        {
            LogProblemOnce("marker:" + prefabName, $"Generated prefab '{prefabName}' has an invalid identity marker.");
            return;
        }

        ItemDrop.ItemData.SharedData currentShared = GetConfiguredSharedData(itemDrop!, prefabName);
        if (!IsConfiguredGeneratedSharedData(currentShared, nameToken, consumeStatusEffect))
        {
            ItemDrop.ItemData independentItemData = itemDrop.m_itemData.Clone();
            ItemDrop.ItemData.SharedData shared = CloneSharedData(currentShared);
            SanitizeAsRottenConsumable(
                shared,
                nameToken,
                descriptionToken,
                consumeStatusEffect);
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

        if ((object?)storedIcon == null && itemDrop.m_itemData.m_shared.m_icons is { Length: > 0 })
        {
            storedIcon = itemDrop.m_itemData.m_shared.m_icons[0];
        }

        Material? rottenMaterial = ResolveMaterial(RottenMaterialName, ref _rottenMaterial);
        if ((object?)rottenMaterial != null)
        {
            ApplyMaterialOverride(prefab, rottenMaterial!);
            TryApplyAutoRenderedItemIcon(prefab, itemDrop, kind, ref storedIcon);
        }
        else if (AliveOrNull(ZNetScene.instance) != null)
        {
            LogProblemOnce(
                "material:" + RottenMaterialName,
                $"Material '{RottenMaterialName}' was not found; generated rotten items will use their source visuals.");
        }
    }

    // Keeps the configuration check explicit without relying on a source prefab's
    // original name/description, both of which other data mods may have changed.
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

    private static ItemDrop.ItemData.SharedData GetConfiguredSharedData(
        ItemDrop itemDrop,
        string prefabName)
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

        if (shared.m_icons == null)
        {
            shared.m_icons = Array.Empty<Sprite>();
        }

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
            LogProblemOnce("icebox-components", $"Generated prefab '{IceboxPrefabName}' is missing a required component.");
            return;
        }

        piece!.m_name = IceboxNameToken;
        piece.m_description = IceboxDescriptionToken;
        piece.m_enabled = true;
        container!.m_name = IceboxNameToken;
        container.m_width = IceboxSubsystem.StorageColumns;
        container.m_height = IceboxSubsystem.StorageRows;
        wearNTear!.m_health = IceboxHealth;

        _iceboxIcon ??= piece.m_icon;
        if ((object?)_iceboxIcon != null)
        {
            piece.m_icon = _iceboxIcon;
        }

        Material? material = ResolveMaterial(IceboxMaterialName, ref _iceboxMaterial);
        if ((object?)material != null)
        {
            ApplyMaterialOverride(prefab, material!);
            TryApplyAutoRenderedPieceIcon(prefab, piece, ref _iceboxIcon);
        }
        else if (AliveOrNull(ZNetScene.instance) != null)
        {
            LogProblemOnce(
                "material:" + IceboxMaterialName,
                $"Material '{IceboxMaterialName}' was not found; '{IceboxPrefabName}' will use the source chest visuals.");
        }
    }

    private static void RegisterGeneratedItem(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        GameObject prefab,
        FineDiningGeneratedPrefabKind kind)
    {
        string prefabName = GetExpectedPrefabName(kind);
        if (HasRegistryCollision(objectDb, zNetScene, prefabName, kind, includeObjectDb: true))
        {
            return;
        }

        if ((object?)objectDb != null)
        {
            AddItemToObjectDb(objectDb!, prefab, prefabName, kind);
        }

        if ((object?)zNetScene != null)
        {
            AddPrefabToZNetScene(zNetScene!, prefab, prefabName, kind);
        }
    }

    private static void RegisterGeneratedPiece(ZNetScene? zNetScene, GameObject prefab)
    {
        if ((object?)zNetScene == null ||
            HasRegistryCollision(
                null,
                zNetScene,
                IceboxPrefabName,
                FineDiningGeneratedPrefabKind.Icebox,
                includeObjectDb: false))
        {
            return;
        }

        AddPrefabToZNetScene(
            zNetScene!,
            prefab,
            IceboxPrefabName,
            FineDiningGeneratedPrefabKind.Icebox);
    }

    private static void AddItemToObjectDb(
        ObjectDB objectDb,
        GameObject prefab,
        string prefabName,
        FineDiningGeneratedPrefabKind kind)
    {
        int hash = prefabName.GetStableHashCode();
        objectDb.m_items.RemoveAll(existing =>
            !ReferenceEquals(existing, prefab) &&
            (object?)existing != null &&
            string.Equals(existing!.name, prefabName, StringComparison.Ordinal) &&
            IsOwnedPrefab(existing, kind));
        if (!objectDb.m_items.Any(existing => ReferenceEquals(existing, prefab)))
        {
            objectDb.m_items.Add(prefab);
        }

        objectDb.m_itemByHash[hash] = prefab;
        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        if ((object?)itemDrop != null && itemDrop!.m_itemData?.m_shared != null)
        {
            objectDb.m_itemByData[itemDrop.m_itemData.m_shared] = prefab;
        }
    }

    private static void AddPrefabToZNetScene(
        ZNetScene scene,
        GameObject prefab,
        string prefabName,
        FineDiningGeneratedPrefabKind kind)
    {
        List<GameObject> target = (object?)prefab.GetComponent<ZNetView>() != null
            ? scene.m_prefabs
            : scene.m_nonNetViewPrefabs;
        scene.m_prefabs.RemoveAll(existing =>
            !ReferenceEquals(existing, prefab) &&
            (object?)existing != null &&
            string.Equals(existing!.name, prefabName, StringComparison.Ordinal) &&
            IsOwnedPrefab(existing, kind));
        scene.m_nonNetViewPrefabs.RemoveAll(existing =>
            !ReferenceEquals(existing, prefab) &&
            (object?)existing != null &&
            string.Equals(existing!.name, prefabName, StringComparison.Ordinal) &&
            IsOwnedPrefab(existing, kind));
        if (!target.Any(existing => ReferenceEquals(existing, prefab)))
        {
            target.Add(prefab);
        }

        scene.m_namedPrefabs[prefabName.GetStableHashCode()] = prefab;
    }

    private static bool VerifyGeneratedItemRegistration(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        GameObject prefab,
        FineDiningGeneratedPrefabKind kind)
    {
        string prefabName = GetExpectedPrefabName(kind);
        bool statusEffectReady = IsGeneratedPukeStatusEffectRegistered(objectDb, kind);
        bool objectDbReady = IsGeneratedItemRegisteredWithObjectDb(objectDb, prefab, kind);
        bool zNetSceneReady = IsGeneratedItemRegisteredWithZNetScene(zNetScene, prefab, kind);
        if (statusEffectReady && objectDbReady && zNetSceneReady)
        {
            LogInfoOnce(
                "registration-complete:" + prefabName,
                $"Generated item '{prefabName}' is registered with ObjectDB and ZNetScene.");
            return true;
        }

        if ((object?)objectDb == null || (object?)zNetScene == null)
        {
            LogDebugOnce(
                "registration-waiting:" + prefabName,
                $"Generated item '{prefabName}' is ready and waiting for ObjectDB/ZNetScene registration to complete.");
        }
        else
        {
            LogProblemOnce(
                "registration-incomplete:" + prefabName,
                $"Generated item '{prefabName}' registration is incomplete " +
                $"(StatusEffect={statusEffectReady}, ObjectDB={objectDbReady}, ZNetScene={zNetSceneReady}); registration will be retried.");
        }

        return false;
    }

    private static bool IsGeneratedItemRegistrationComplete(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        FineDiningGeneratedPrefabKind kind)
    {
        GameObject? prefab = kind switch
        {
            FineDiningGeneratedPrefabKind.RottenProduce => AliveOrNull(_rottenProducePrefab),
            FineDiningGeneratedPrefabKind.RottenFood => AliveOrNull(_rottenFoodPrefab),
            _ => null
        };
        return (object?)prefab != null &&
               IsGeneratedPukeStatusEffectRegistered(objectDb, kind) &&
               IsGeneratedItemRegisteredWithObjectDb(objectDb, prefab!, kind) &&
               IsGeneratedItemRegisteredWithZNetScene(zNetScene, prefab!, kind);
    }

    private static bool IsGeneratedItemRegisteredWithObjectDb(
        ObjectDB? objectDb,
        GameObject prefab,
        FineDiningGeneratedPrefabKind kind)
    {
        if ((object?)objectDb == null || !IsOwnedPrefab(prefab, kind))
        {
            return false;
        }

        string prefabName = GetExpectedPrefabName(kind);
        int hash = prefabName.GetStableHashCode();
        ItemDrop? itemDrop = prefab.GetComponent<ItemDrop>();
        SE_Puke? consumeStatusEffect = GetGeneratedPukeStatusEffect(kind);
        return (object?)itemDrop != null &&
               (object?)consumeStatusEffect != null &&
               ReferenceEquals(itemDrop!.m_itemData.m_shared.m_consumeStatusEffect, consumeStatusEffect) &&
               objectDb!.m_items.Any(existing => ReferenceEquals(existing, prefab)) &&
               objectDb.m_itemByHash.TryGetValue(hash, out GameObject byHash) &&
               ReferenceEquals(byHash, prefab) &&
               objectDb.m_itemByData.TryGetValue(itemDrop!.m_itemData.m_shared, out GameObject byData) &&
               ReferenceEquals(byData, prefab);
    }

    private static bool IsGeneratedPukeStatusEffectRegistered(
        ObjectDB? objectDb,
        FineDiningGeneratedPrefabKind kind)
    {
        SE_Puke? statusEffect = GetGeneratedPukeStatusEffect(kind);
        if ((object?)objectDb == null || (object?)statusEffect == null)
        {
            return false;
        }

        string expectedName = GetExpectedPukeStatusEffectName(kind);
        float expectedDuration = GetExpectedPukeDurationSeconds(kind);
        int expectedHash = expectedName.GetStableHashCode();
        return expectedName.Length > 0 &&
               string.Equals(statusEffect!.name, expectedName, StringComparison.Ordinal) &&
               statusEffect.m_nameHash == expectedHash &&
               statusEffect.m_ttl == expectedDuration &&
               string.Equals(
                   statusEffect.m_category,
                   GeneratedPukeStatusEffectCategory,
                   StringComparison.Ordinal) &&
               objectDb!.m_StatusEffects.Any(existing => ReferenceEquals(existing, statusEffect)) &&
               ReferenceEquals(objectDb.GetStatusEffect(expectedHash), statusEffect);
    }

    private static SE_Puke? GetGeneratedPukeStatusEffect(FineDiningGeneratedPrefabKind kind)
    {
        return kind switch
        {
            FineDiningGeneratedPrefabKind.RottenProduce =>
                AliveOrNull(_rottenProducePukeStatusEffect),
            FineDiningGeneratedPrefabKind.RottenFood =>
                AliveOrNull(_rottenFoodPukeStatusEffect),
            _ => null
        };
    }

    private static string GetExpectedPukeStatusEffectName(FineDiningGeneratedPrefabKind kind)
    {
        return kind switch
        {
            FineDiningGeneratedPrefabKind.RottenProduce => RottenProducePukeStatusEffectName,
            FineDiningGeneratedPrefabKind.RottenFood => RottenFoodPukeStatusEffectName,
            _ => string.Empty
        };
    }

    private static float GetExpectedPukeDurationSeconds(FineDiningGeneratedPrefabKind kind)
    {
        return kind switch
        {
            FineDiningGeneratedPrefabKind.RottenProduce => RottenProducePukeDurationSeconds,
            FineDiningGeneratedPrefabKind.RottenFood => RottenFoodPukeDurationSeconds,
            _ => 0f
        };
    }

    private static bool IsGeneratedItemRegisteredWithZNetScene(
        ZNetScene? zNetScene,
        GameObject prefab,
        FineDiningGeneratedPrefabKind kind)
    {
        if ((object?)zNetScene == null || !IsOwnedPrefab(prefab, kind))
        {
            return false;
        }

        string prefabName = GetExpectedPrefabName(kind);
        int hash = prefabName.GetStableHashCode();
        List<GameObject> target = (object?)prefab.GetComponent<ZNetView>() != null
            ? zNetScene!.m_prefabs
            : zNetScene!.m_nonNetViewPrefabs;
        return target.Any(existing => ReferenceEquals(existing, prefab)) &&
               zNetScene.m_namedPrefabs.TryGetValue(hash, out GameObject byHash) &&
               ReferenceEquals(byHash, prefab);
    }

    private static void RegisterIceboxBuildContent(ObjectDB? objectDb)
    {
        GameObject? prefab = AliveOrNull(_iceboxPrefab);
        if ((object?)objectDb == null || (object?)prefab == null)
        {
            return;
        }

        Piece? piece = prefab!.GetComponent<Piece>();
        if ((object?)piece == null)
        {
            return;
        }

        GameObject? hammerPrefab = FindItemPrefab(objectDb, "Hammer");
        ItemDrop? hammer = hammerPrefab != null ? hammerPrefab.GetComponent<ItemDrop>() : null;
        PieceTable? pieceTable = hammer?.m_itemData?.m_shared?.m_buildPieces;
        if ((object?)pieceTable == null)
        {
            LogDebugOnce("icebox-hammer", $"Could not register '{IceboxPrefabName}' with Hammer yet.");
            return;
        }

        if (!TryCreateIceboxRequirements(
                objectDb!,
                IceboxSubsystem.Recipe,
                out Piece.Requirement[] requirements))
        {
            pieceTable.m_pieces.RemoveAll(existing =>
                (object?)existing != null &&
                string.Equals(existing!.name, IceboxPrefabName, StringComparison.Ordinal) &&
                IsOwnedPrefab(existing, FineDiningGeneratedPrefabKind.Icebox));
            RefreshLocalPieceTable(pieceTable);
            return;
        }

        GameObject? conflictingEntry = pieceTable!.m_pieces.FirstOrDefault(existing =>
            (object?)existing != null &&
            string.Equals(existing!.name, IceboxPrefabName, StringComparison.Ordinal) &&
            !IsOwnedPrefab(existing, FineDiningGeneratedPrefabKind.Icebox));
        if ((object?)conflictingEntry != null)
        {
            pieceTable.m_pieces.RemoveAll(existing =>
                (object?)existing != null &&
                string.Equals(existing!.name, IceboxPrefabName, StringComparison.Ordinal) &&
                IsOwnedPrefab(existing, FineDiningGeneratedPrefabKind.Icebox));
            RefreshLocalPieceTable(pieceTable);
            LogProblemOnce(
                "hammer-name-collision",
                $"Did not register '{IceboxPrefabName}' with Hammer because another piece uses that name.");
            return;
        }

        piece!.m_resources = requirements;

        pieceTable.m_pieces.RemoveAll(existing =>
            (object?)existing != null &&
            string.Equals(existing!.name, IceboxPrefabName, StringComparison.Ordinal));
        pieceTable.m_pieces.Add(prefab);

        RefreshLocalPieceTable(pieceTable);
    }

    private static void RefreshLocalPieceTable(PieceTable pieceTable)
    {
        if ((object?)Player.m_localPlayer != null &&
            (object?)Player.m_localPlayer!.m_buildPieces != null &&
            ReferenceEquals(Player.m_localPlayer.m_buildPieces, pieceTable))
        {
            ((Humanoid)Player.m_localPlayer).SetPlaceMode(pieceTable);
        }
    }

    internal static bool TryCreateIceboxRequirements(
        ObjectDB objectDb,
        string? recipe,
        out Piece.Requirement[] requirements)
    {
        requirements = Array.Empty<Piece.Requirement>();
        if (!TryParseIceboxRecipe(
                recipe,
                out List<KeyValuePair<string, int>> ingredients,
                out string error))
        {
            LogProblemOnce(
                "icebox-recipe-format:" + (recipe ?? string.Empty),
                $"Could not apply the Icebox recipe '{recipe ?? string.Empty}': {error} " +
                $"Expected comma-separated ItemPrefab:Amount entries such as '{IceboxSubsystem.DefaultRecipe}'.");
            return false;
        }

        Piece.Requirement[] resolved = new Piece.Requirement[ingredients.Count];
        for (int index = 0; index < ingredients.Count; index++)
        {
            KeyValuePair<string, int> ingredient = ingredients[index];
            ItemDrop? itemDrop = FindItemPrefab(objectDb, ingredient.Key)?.GetComponent<ItemDrop>();
            if ((object?)itemDrop == null)
            {
                LogDebugOnce(
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

    internal static string SerializeIceboxRequirements(Piece.Requirement[]? requirements)
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
                ? NormalizePrefabName(Utils.GetPrefabName(itemDrop!.gameObject))
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

    private static bool TryParseIceboxRecipe(
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
        string[] entries = text.Split(',');
        foreach (string rawEntry in entries)
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

    private static bool HasRegistryCollision(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        string prefabName,
        FineDiningGeneratedPrefabKind kind,
        bool includeObjectDb)
    {
        int hash = prefabName.GetStableHashCode();

        if (includeObjectDb && (object?)objectDb != null)
        {
            if (objectDb!.m_itemByHash.TryGetValue(hash, out GameObject byHash) &&
                (object?)byHash != null &&
                !IsOwnedPrefab(byHash, kind))
            {
                return ReportCollision(prefabName, byHash!, "ObjectDB hash");
            }

            GameObject? byName = objectDb.m_items.FirstOrDefault(existing =>
                (object?)existing != null &&
                string.Equals(existing!.name, prefabName, StringComparison.Ordinal) &&
                !IsOwnedPrefab(existing, kind));
            if ((object?)byName != null)
            {
                return ReportCollision(prefabName, byName!, "ObjectDB name");
            }
        }

        if ((object?)zNetScene != null)
        {
            if (zNetScene!.m_namedPrefabs.TryGetValue(hash, out GameObject byHash) &&
                (object?)byHash != null &&
                !IsOwnedPrefab(byHash, kind))
            {
                return ReportCollision(prefabName, byHash!, "ZNetScene hash");
            }

            foreach (GameObject? byName in zNetScene.m_prefabs.Concat(zNetScene.m_nonNetViewPrefabs))
            {
                if ((object?)byName != null &&
                    string.Equals(byName!.name, prefabName, StringComparison.Ordinal) &&
                    !IsOwnedPrefab(byName, kind))
                {
                    return ReportCollision(prefabName, byName, "ZNetScene name");
                }
            }
        }

        return false;
    }

    private static bool ReportCollision(string requestedName, GameObject existing, string registry)
    {
        string existingName = (object?)existing != null ? existing.name : "<null>";
        LogProblemOnce(
            "collision:" + requestedName + ":" + registry,
            $"Refusing to register '{requestedName}': {registry} is already occupied by unowned prefab '{existingName}'.");
        return true;
    }

    private static GameObject? FindOwnedRegisteredPrefab(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        string prefabName,
        FineDiningGeneratedPrefabKind kind,
        bool includeObjectDb)
    {
        int hash = prefabName.GetStableHashCode();
        if (includeObjectDb && (object?)objectDb != null)
        {
            if (objectDb!.m_itemByHash.TryGetValue(hash, out GameObject byHash) && IsOwnedPrefab(byHash, kind))
            {
                return byHash;
            }

            GameObject? byName = objectDb.m_items.FirstOrDefault(existing => IsOwnedPrefab(existing, kind));
            if ((object?)byName != null && string.Equals(byName!.name, prefabName, StringComparison.Ordinal))
            {
                return byName;
            }
        }

        if ((object?)zNetScene != null)
        {
            if (zNetScene!.m_namedPrefabs.TryGetValue(hash, out GameObject byHash) && IsOwnedPrefab(byHash, kind))
            {
                return byHash;
            }

            return zNetScene.m_prefabs
                .Concat(zNetScene.m_nonNetViewPrefabs)
                .FirstOrDefault(existing =>
                    IsOwnedPrefab(existing, kind) &&
                    string.Equals(existing!.name, prefabName, StringComparison.Ordinal));
        }

        return null;
    }

    private static bool IsOwnedPrefab(GameObject? prefab, FineDiningGeneratedPrefabKind kind)
    {
        if ((object?)prefab == null ||
            !string.Equals(prefab!.name, GetExpectedPrefabName(kind), StringComparison.Ordinal))
        {
            return false;
        }

        FineDiningGeneratedPrefabMarker? marker =
            prefab.GetComponent<FineDiningGeneratedPrefabMarker>();
        return (object?)marker != null && marker!.Kind == kind;
    }

    private static string GetExpectedPrefabName(FineDiningGeneratedPrefabKind kind)
    {
        return kind switch
        {
            FineDiningGeneratedPrefabKind.Icebox => IceboxPrefabName,
            FineDiningGeneratedPrefabKind.RottenProduce => RottenProducePrefabName,
            FineDiningGeneratedPrefabKind.RottenFood => RottenFoodPrefabName,
            _ => string.Empty
        };
    }

    private static bool TryGetGeneratedReplacementKind(
        string? prefabName,
        out FineDiningGeneratedPrefabKind kind)
    {
        if (string.Equals(prefabName, RottenProducePrefabName, StringComparison.Ordinal))
        {
            kind = FineDiningGeneratedPrefabKind.RottenProduce;
            return true;
        }

        if (string.Equals(prefabName, RottenFoodPrefabName, StringComparison.Ordinal))
        {
            kind = FineDiningGeneratedPrefabKind.RottenFood;
            return true;
        }

        kind = default;
        return false;
    }

    private static void AddMarker(GameObject prefab, FineDiningGeneratedPrefabKind kind)
    {
        FineDiningGeneratedPrefabMarker? marker =
            prefab.GetComponent<FineDiningGeneratedPrefabMarker>();
        marker ??= prefab.AddComponent<FineDiningGeneratedPrefabMarker>();
        marker.Initialize(kind);
    }

    private static GameObject EnsureGeneratedRoot()
    {
        _generatedRoot = AliveOrNull(_generatedRoot);
        if ((object?)_generatedRoot != null)
        {
            return _generatedRoot!;
        }

        _generatedRoot = new GameObject(GeneratedRootName);
        _generatedRoot.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_generatedRoot);
        return _generatedRoot;
    }

    private static GameObject? FindItemCloneSource(
        ObjectDB? objectDb,
        ZNetScene? zNetScene,
        string prefabName)
    {
        return FindItemPrefab(objectDb, prefabName) ?? FindScenePrefab(zNetScene, prefabName);
    }

    private static GameObject? FindItemPrefab(ObjectDB? objectDb, string prefabName)
    {
        if ((object?)objectDb == null)
        {
            return null;
        }

        int hash = prefabName.GetStableHashCode();
        if (objectDb!.m_itemByHash.TryGetValue(hash, out GameObject byHash) &&
            (object?)byHash != null &&
            string.Equals(byHash!.name, prefabName, StringComparison.Ordinal))
        {
            return byHash;
        }

        GameObject? byName = objectDb.m_items.FirstOrDefault(existing =>
            (object?)existing != null && string.Equals(existing!.name, prefabName, StringComparison.Ordinal));
        if ((object?)byName != null)
        {
            return byName;
        }

        GameObject? byApi = objectDb.GetItemPrefab(prefabName);
        return (object?)byApi != null && string.Equals(byApi!.name, prefabName, StringComparison.Ordinal)
            ? byApi
            : null;
    }

    private static GameObject? FindScenePrefab(ZNetScene? scene, string prefabName)
    {
        if ((object?)scene == null)
        {
            return null;
        }

        int hash = prefabName.GetStableHashCode();
        if (scene!.m_namedPrefabs.TryGetValue(hash, out GameObject byHash) &&
            (object?)byHash != null &&
            string.Equals(byHash!.name, prefabName, StringComparison.Ordinal))
        {
            return byHash;
        }

        return scene.m_prefabs
            .Concat(scene.m_nonNetViewPrefabs)
            .FirstOrDefault(existing =>
                (object?)existing != null && string.Equals(existing!.name, prefabName, StringComparison.Ordinal));
    }

    private static bool HasSpawnableItemShape(GameObject prefab)
    {
        return (object?)prefab.GetComponent<ItemDrop>() != null &&
               (object?)prefab.GetComponent<ZNetView>() != null &&
               (object?)prefab.GetComponent<ZSyncTransform>() != null &&
               (object?)prefab.GetComponent<Rigidbody>() != null &&
               (object?)prefab.GetComponentInChildren<Collider>(true) != null;
    }

    private static void TryApplyAutoRenderedItemIcon(
        GameObject prefab,
        ItemDrop itemDrop,
        FineDiningGeneratedPrefabKind kind,
        ref Sprite? storedIcon)
    {
        if (AutoRenderedIcons.Contains(kind))
        {
            Sprite? current = AliveOrNull(storedIcon);
            if ((object?)current != null)
            {
                itemDrop.m_itemData.m_shared.m_icons = new[] { current! };
                itemDrop.m_itemData.m_variant = 0;
                return;
            }

            AutoRenderedIcons.Remove(kind);
        }

        Sprite? rendered = RenderGeneratedIcon(
            prefab,
            GetExpectedPrefabName(kind) + "_Icon",
            Quaternion.Euler(23f, 51f, 25.8f));
        if ((object?)rendered == null)
        {
            return;
        }

        storedIcon = rendered;
        itemDrop.m_itemData.m_shared.m_icons = new[] { rendered! };
        itemDrop.m_itemData.m_variant = 0;
        itemDrop.m_itemData.m_shared.m_variants = 1;
        AutoRenderedIcons.Add(kind);
    }

    private static void TryApplyAutoRenderedPieceIcon(
        GameObject prefab,
        Piece piece,
        ref Sprite? storedIcon)
    {
        const FineDiningGeneratedPrefabKind kind = FineDiningGeneratedPrefabKind.Icebox;
        if (AutoRenderedIcons.Contains(kind))
        {
            Sprite? current = AliveOrNull(storedIcon);
            if ((object?)current != null)
            {
                piece.m_icon = current;
                return;
            }

            AutoRenderedIcons.Remove(kind);
        }

        Sprite? rendered = RenderGeneratedIcon(
            prefab,
            IceboxPrefabName + "_Icon",
            Quaternion.Euler(23f, 51f, 25.8f));
        if ((object?)rendered == null)
        {
            return;
        }

        storedIcon = rendered;
        piece.m_icon = rendered;
        AutoRenderedIcons.Add(kind);
    }

    private static Sprite? RenderGeneratedIcon(
        GameObject sourcePrefab,
        string iconName,
        Quaternion rotation)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            return null;
        }

        GameObject? renderObject = null;
        Camera? camera = null;
        Light? light = null;
        RenderTexture? renderTexture = null;
        RenderTexture? previousActive = RenderTexture.active;

        try
        {
            renderObject = SpawnIconRenderClone(sourcePrefab, iconName, rotation, out List<Renderer> renderers);
            if ((object?)renderObject == null || renderers.Count == 0)
            {
                return null;
            }

            Bounds bounds = renderers[0].bounds;
            for (int index = 1; index < renderers.Count; index++)
            {
                bounds.Encapsulate(renderers[index].bounds);
            }

            Vector3 renderSize = bounds.size;
            renderObject!.transform.position -= bounds.center;

            camera = new GameObject(iconName + " Camera", typeof(Camera)).GetComponent<Camera>();
            camera.backgroundColor = Color.clear;
            camera.clearFlags = CameraClearFlags.Color;
            camera.fieldOfView = 0.5f;
            camera.farClipPlane = 10000000f;
            camera.cullingMask = 1 << GeneratedIconLayer;
            camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            light = new GameObject(iconName + " Light", typeof(Light)).GetComponent<Light>();
            light.transform.position = Vector3.zero;
            light.transform.rotation = Quaternion.Euler(5f, 180f, 5f);
            light.type = LightType.Directional;
            light.cullingMask = 1 << GeneratedIconLayer;
            light.intensity = 1.3f;

            float framedSize = Mathf.Max(renderSize.x, renderSize.y) + 0.1f;
            float distance = framedSize / Mathf.Tan(camera.fieldOfView * ((float)Math.PI / 180f));
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0f)
            {
                distance = Mathf.Max(renderSize.x, Mathf.Max(renderSize.y, renderSize.z)) + 1f;
            }

            camera.transform.position = new Vector3(0f, 0f, distance);
            renderTexture = RenderTexture.GetTemporary(
                GeneratedIconSize,
                GeneratedIconSize,
                24,
                RenderTextureFormat.ARGB32);
            camera.targetTexture = renderTexture;
            RenderTexture.active = renderTexture;
            GL.Clear(clearDepth: true, clearColor: true, Color.clear);
            camera.Render();

            Texture2D texture = new(
                GeneratedIconSize,
                GeneratedIconSize,
                TextureFormat.RGBA32,
                mipChain: false)
            {
                name = iconName,
                wrapMode = TextureWrapMode.Clamp
            };
            Rect rect = new(0f, 0f, GeneratedIconSize, GeneratedIconSize);
            texture.ReadPixels(rect, 0, 0);
            texture.Apply();

            Sprite sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), 100f);
            sprite.name = iconName;
            return sprite;
        }
        catch (Exception ex)
        {
            LogProblemOnce(
                "icon-render:" + iconName,
                $"Could not auto-render icon '{iconName}'; using the source icon instead: {ex.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = previousActive;
            if ((object?)camera != null)
            {
                camera!.targetTexture = null;
                DestroyTemporaryObject(camera.gameObject);
            }

            if ((object?)light != null)
            {
                DestroyTemporaryObject(light!.gameObject);
            }

            if (renderTexture != null)
            {
                RenderTexture.ReleaseTemporary(renderTexture);
            }

            if ((object?)renderObject != null)
            {
                DestroyTemporaryObject(renderObject);
            }
        }
    }

    private static GameObject? SpawnIconRenderClone(
        GameObject sourcePrefab,
        string iconName,
        Quaternion rotation,
        out List<Renderer> renderers)
    {
        renderers = new List<Renderer>();
        GameObject? inactiveRoot = null;
        try
        {
            inactiveRoot = new GameObject(iconName + " Render Root");
            inactiveRoot.SetActive(false);

            GameObject renderObject = UnityEngine.Object.Instantiate(
                sourcePrefab,
                inactiveRoot.transform,
                worldPositionStays: false);
            renderObject.name = iconName + " Render";
            StripIconRenderClone(renderObject);
            renderObject.transform.SetParent(null, worldPositionStays: false);
            UnityEngine.Object.DestroyImmediate(inactiveRoot);
            inactiveRoot = null;

            renderObject.transform.position = Vector3.zero;
            renderObject.transform.rotation = rotation;
            SetLayerRecursive(renderObject, GeneratedIconLayer);
            renderObject.SetActive(true);

            renderers = renderObject
                .GetComponentsInChildren<Renderer>(includeInactive: true)
                .Where(renderer =>
                    (object?)renderer != null &&
                    renderer!.enabled &&
                    renderer.gameObject.activeInHierarchy &&
                    !renderer.GetType().Name.Equals("ParticleSystemRenderer", StringComparison.Ordinal))
                .ToList();
            if (renderers.Count == 0)
            {
                DestroyTemporaryObject(renderObject);
                return null;
            }

            HashSet<Renderer> selected = new(renderers);
            foreach (Renderer renderer in renderObject.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if ((object?)renderer != null && !selected.Contains(renderer))
                {
                    renderer!.enabled = false;
                }
            }

            return renderObject;
        }
        catch
        {
            if ((object?)inactiveRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(inactiveRoot);
            }

            throw;
        }
    }

    private static void StripIconRenderClone(GameObject renderObject)
    {
        foreach (Transform transform in renderObject.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            Component[] components = transform.GetComponents<Component>();
            for (int index = components.Length - 1; index >= 0; index--)
            {
                Component component = components[index];
                if (component == null || component is Transform || component is Renderer || component is MeshFilter)
                {
                    continue;
                }

                try
                {
                    UnityEngine.Object.DestroyImmediate(component);
                }
                catch (Exception ex)
                {
                    FineDiningPlugin.Log.LogDebug(
                        $"Could not strip icon render component '{component.GetType().Name}' from '{renderObject.name}': {ex.Message}");
                }
            }
        }
    }

    private static void SetLayerRecursive(GameObject gameObject, int layer)
    {
        gameObject.layer = layer;
        foreach (Transform child in gameObject.transform)
        {
            SetLayerRecursive(child.gameObject, layer);
        }
    }

    private static void DestroyTemporaryObject(GameObject? gameObject)
    {
        if ((object?)gameObject == null)
        {
            return;
        }

        try
        {
            gameObject!.SetActive(false);
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
        catch (Exception ex)
        {
            FineDiningPlugin.Log.LogDebug(
                $"Could not immediately destroy icon render object '{gameObject!.name}': {ex.Message}");
            UnityEngine.Object.Destroy(gameObject);
        }
    }

    private static Material? ResolveMaterial(string materialName, ref Material? cached)
    {
        if ((object?)cached != null && cached != null)
        {
            return cached;
        }

        cached = null;

        string normalizedTarget = NormalizeMaterialName(materialName);
        foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
        {
            if ((object?)material == null)
            {
                continue;
            }

            if (string.Equals(
                    NormalizeMaterialName(material.name),
                    normalizedTarget,
                    StringComparison.OrdinalIgnoreCase))
            {
                cached = material;
                return cached;
            }
        }

        return null;
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

    private static string NormalizePrefabName(string? prefabName)
    {
        string normalized = (prefabName ?? string.Empty).Trim();
        const string cloneSuffix = "(Clone)";
        return normalized.EndsWith(cloneSuffix, StringComparison.Ordinal)
            ? normalized.Substring(0, normalized.Length - cloneSuffix.Length).Trim()
            : normalized;
    }

    private static T? AliveOrNull<T>(T? value) where T : UnityEngine.Object
    {
        return (object?)value != null && value != null ? value : null;
    }

    private static void LogProblemOnce(string key, string message)
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

    private static void LogDebugOnce(string key, string message)
    {
        if (ReportedProblems.Add("debug:" + key))
        {
            FineDiningPlugin.Log.LogDebug(message);
        }
    }
}
