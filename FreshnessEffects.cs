using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using ItemData = ItemDrop.ItemData;

namespace FineDining;

/// <summary>
/// Applies per-item freshness without modifying prefab SharedData. Scaled food
/// snapshots are kept by Player.Food so vanilla updates and regeneration use the
/// same health, stamina, eitr, and regeneration values.
/// </summary>
internal static class FreshnessFoodEffects
{
    internal const string GourmetsDietPluginGuid = "blizz.GourmetsDiet";
    private const string GourmetsDietApiTypeName = "GourmetsDiet.GourmetsDietApi";
    private const string GourmetsDietCapabilityMethodName = "HandlesFineDiningFreshness";
    private const string ActiveFoodStateKey = "sighsorry.FineDining.ActiveFoodFreshness";
    private const string ConsumedMultiplierKey = "sighsorry.FineDining.ConsumedFoodMultiplier";
    private const string StateVersion = "v1";
    private const int FactorScale = 1_000_000;

    private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod(
        "MemberwiseClone",
        BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new MissingMethodException(typeof(object).FullName, "MemberwiseClone");

    private static bool _loggedFailure;
    private static Assembly? _gourmetsDietCapabilityAssembly;
    private static Func<bool>? _gourmetsDietCapability;

    static FreshnessFoodEffects()
    {
        RunSmokeAssertions();
    }

    internal static bool GourmetsDietHandlesFreshness
    {
        get
        {
            if (!Chainloader.PluginInfos.TryGetValue(
                    GourmetsDietPluginGuid,
                    out BepInEx.PluginInfo pluginInfo) ||
                pluginInfo.Instance == null)
            {
                return false;
            }

            Assembly assembly = pluginInfo.Instance.GetType().Assembly;
            if (!ReferenceEquals(_gourmetsDietCapabilityAssembly, assembly))
            {
                _gourmetsDietCapabilityAssembly = assembly;
                _gourmetsDietCapability = ResolveGourmetsDietCapability(assembly);
            }

            try
            {
                return _gourmetsDietCapability?.Invoke() == true;
            }
            catch (Exception exception)
            {
                _gourmetsDietCapability = null;
                LogFailureOnce("Could not query GourmetsDiet freshness capability", exception);
                return false;
            }
        }
    }

    internal static bool HasFoodEffect(ItemData? item) =>
        FoodClassifier.IsEdible(item);

    internal static float GetMultiplier(ItemData? item)
    {
        if (!HasFoodEffect(item))
        {
            return 1f;
        }

        if (TryGetConsumedMultiplier(item!, out float consumedMultiplier))
        {
            return consumedMultiplier;
        }

        try
        {
            return NormalizeMultiplier(FineDiningApi.GetFoodStatMultiplier(item!));
        }
        catch (Exception exception)
        {
            LogFailureOnce("Could not calculate a food freshness multiplier", exception);
            return 1f;
        }
    }

    internal static ItemData CreateFoodSnapshot(ItemData item, float multiplier)
    {
        multiplier = NormalizeMultiplier(multiplier);
        ItemData snapshot = item.Clone();
        snapshot.m_shared = CloneSharedData(item.m_shared);
        snapshot.m_shared.m_food *= multiplier;
        snapshot.m_shared.m_foodStamina *= multiplier;
        snapshot.m_shared.m_foodEitr *= multiplier;
        snapshot.m_shared.m_foodRegen *= multiplier;
        snapshot.m_customData[ConsumedMultiplierKey] = EncodeMultiplier(multiplier);
        return snapshot;
    }

    internal static ItemData CreateTooltipSnapshot(ItemData item, float multiplier)
    {
        // Active-food snapshots may appear in HUD tooltips and are already scaled.
        // Clone their SharedData before rounding so display precision never leaks
        // back into the consumed food state used by vanilla's food calculations.
        ItemData snapshot;
        if (TryGetConsumedMultiplier(item, out _))
        {
            snapshot = item.Clone();
            snapshot.m_shared = CloneSharedData(item.m_shared);
        }
        else
        {
            snapshot = CreateFoodSnapshot(item, multiplier);
        }

        snapshot.m_shared.m_food = RoundTooltipStat(snapshot.m_shared.m_food);
        snapshot.m_shared.m_foodStamina = RoundTooltipStat(snapshot.m_shared.m_foodStamina);
        snapshot.m_shared.m_foodEitr = RoundTooltipStat(snapshot.m_shared.m_foodEitr);
        snapshot.m_shared.m_foodRegen = RoundTooltipStat(snapshot.m_shared.m_foodRegen);
        return snapshot;
    }

    private static float RoundTooltipStat(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return value;
        }

        float rounded = (float)Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return value > 0f && rounded == 0f ? 0.1f : rounded;
    }

    internal static bool TryGetConsumedMultiplier(ItemData item, out float multiplier)
    {
        multiplier = 1f;
        return item?.m_customData != null &&
               item.m_customData.TryGetValue(ConsumedMultiplierKey, out string encoded) &&
               TryDecodeMultiplier(encoded, out multiplier);
    }

    internal static void PrepareSave(Player player)
    {
        if (player == null)
        {
            return;
        }

        if (GourmetsDietHandlesFreshness)
        {
            // GourmetsDiet persists its own combined active-food scale. Remove
            // any standalone state left by an earlier session so uninstalling
            // GourmetsDiet cannot resurrect a stale multiplier later.
            player.m_customData.Remove(ActiveFoodStateKey);
            return;
        }

        try
        {
            Dictionary<string, float> factors = new(StringComparer.Ordinal);
            foreach (Player.Food food in player.GetFoods())
            {
                if (food?.m_item == null ||
                    string.IsNullOrWhiteSpace(food.m_name) ||
                    !TryGetConsumedMultiplier(food.m_item, out float multiplier) ||
                    multiplier >= 0.999999f)
                {
                    continue;
                }

                factors[food.m_name] = multiplier;
            }

            if (factors.Count == 0)
            {
                player.m_customData.Remove(ActiveFoodStateKey);
            }
            else
            {
                player.m_customData[ActiveFoodStateKey] = SerializeFactors(factors);
            }
        }
        catch (Exception exception)
        {
            LogFailureOnce("Could not save active-food freshness", exception);
        }
    }

    internal static void RestoreAfterLoad(Player player)
    {
        if (player == null)
        {
            return;
        }

        if (GourmetsDietHandlesFreshness)
        {
            player.m_customData.Remove(ActiveFoodStateKey);
            return;
        }

        if (!player.m_customData.TryGetValue(ActiveFoodStateKey, out string encoded))
        {
            return;
        }

        try
        {
            Dictionary<string, float> factors = DeserializeFactors(encoded);
            bool restored = false;
            foreach (Player.Food food in player.GetFoods())
            {
                if (food?.m_item == null ||
                    string.IsNullOrWhiteSpace(food.m_name) ||
                    !factors.TryGetValue(food.m_name, out float multiplier))
                {
                    continue;
                }

                food.m_item = CreateFoodSnapshot(food.m_item, multiplier);
                restored = true;
            }

            if (restored)
            {
                // Vanilla saves only prefab name and time; rebuild the active food
                // values from the restored SharedData snapshot immediately. Do
                // not call UpdateFood(forceUpdate), because vanilla subtracts a
                // full second even when dt is zero.
                foreach (Player.Food food in player.GetFoods())
                {
                    float normalizedTime = Mathf.Clamp01(
                        food.m_time / food.m_item.m_shared.m_foodBurnTime);
                    normalizedTime = Mathf.Pow(normalizedTime, 0.3f);
                    food.m_health = food.m_item.m_shared.m_food * normalizedTime;
                    food.m_stamina = food.m_item.m_shared.m_foodStamina * normalizedTime;
                    food.m_eitr = food.m_item.m_shared.m_foodEitr * normalizedTime;
                }

                player.GetTotalFoodValue(out float health, out float stamina, out float eitr);
                player.SetMaxHealth(health, flashBar: true);
                player.SetMaxStamina(stamina, flashBar: true);
                player.SetMaxEitr(eitr, flashBar: true);
            }
        }
        catch (Exception exception)
        {
            LogFailureOnce("Could not restore active-food freshness", exception);
        }
    }

    private static ItemData.SharedData CloneSharedData(ItemData.SharedData shared)
    {
        return (ItemData.SharedData)MemberwiseCloneMethod.Invoke(shared, null);
    }

    private static Func<bool>? ResolveGourmetsDietCapability(Assembly assembly)
    {
        try
        {
            Type? apiType = assembly.GetType(GourmetsDietApiTypeName, throwOnError: false);
            MethodInfo? method = apiType?.GetMethod(
                GourmetsDietCapabilityMethodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (method == null || method.ReturnType != typeof(bool))
            {
                return null;
            }

            return (Func<bool>)Delegate.CreateDelegate(
                typeof(Func<bool>),
                method,
                throwOnBindFailure: true);
        }
        catch (Exception exception)
        {
            LogFailureOnce("Could not bind GourmetsDiet freshness capability", exception);
            return null;
        }
    }

    private static float NormalizeMultiplier(float multiplier)
    {
        if (float.IsNaN(multiplier) || float.IsInfinity(multiplier))
        {
            return 1f;
        }

        return Mathf.Clamp01(multiplier);
    }

    private static string EncodeMultiplier(float multiplier)
    {
        int encoded = Mathf.Clamp(
            Mathf.RoundToInt(NormalizeMultiplier(multiplier) * FactorScale),
            0,
            FactorScale);
        return encoded.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryDecodeMultiplier(string encoded, out float multiplier)
    {
        multiplier = 1f;
        if (!int.TryParse(encoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
            value < 0 ||
            value > FactorScale)
        {
            return false;
        }

        multiplier = value / (float)FactorScale;
        return true;
    }

    private static string SerializeFactors(Dictionary<string, float> factors)
    {
        StringBuilder builder = new(StateVersion);
        foreach (KeyValuePair<string, float> pair in factors)
        {
            builder.Append('|');
            builder.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)));
            builder.Append(',');
            builder.Append(EncodeMultiplier(pair.Value));
        }

        return builder.ToString();
    }

    private static Dictionary<string, float> DeserializeFactors(string encoded)
    {
        Dictionary<string, float> factors = new(StringComparer.Ordinal);
        string[] entries = (encoded ?? string.Empty).Split('|');
        if (entries.Length == 0 || !string.Equals(entries[0], StateVersion, StringComparison.Ordinal))
        {
            return factors;
        }

        for (int index = 1; index < entries.Length; index++)
        {
            string[] parts = entries[index].Split(',');
            if (parts.Length != 2 || !TryDecodeMultiplier(parts[1], out float multiplier))
            {
                continue;
            }

            try
            {
                string name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    factors[name] = multiplier;
                }
            }
            catch (FormatException)
            {
                // Ignore one malformed record and restore the remaining foods.
            }
        }

        return factors;
    }

    private static void LogFailureOnce(string context, Exception exception)
    {
        if (_loggedFailure)
        {
            return;
        }

        _loggedFailure = true;
        FineDiningPlugin.Log.LogWarning(context + ": " + exception);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void RunSmokeAssertions()
    {
        const float expected = 0.875f;
        System.Diagnostics.Debug.Assert(TryDecodeMultiplier(EncodeMultiplier(expected), out float decoded));
        System.Diagnostics.Debug.Assert(Math.Abs(decoded - expected) < 0.000001f);
        System.Diagnostics.Debug.Assert(TryDecodeMultiplier(EncodeMultiplier(0f), out float zero));
        System.Diagnostics.Debug.Assert(Math.Abs(zero) < 0.000001f);
        System.Diagnostics.Debug.Assert(Math.Abs(NormalizeMultiplier(float.NaN) - 1f) < 0.000001f);

        Dictionary<string, float> source = new(StringComparer.Ordinal)
        {
            ["Food,With|Separators"] = expected
        };
        Dictionary<string, float> restored = DeserializeFactors(SerializeFactors(source));
        System.Diagnostics.Debug.Assert(restored.TryGetValue("Food,With|Separators", out float roundTrip));
        System.Diagnostics.Debug.Assert(Math.Abs(roundTrip - expected) < 0.000001f);
    }
}

[HarmonyPatch(typeof(ItemData), nameof(ItemData.GetTooltip), new[]
{
    typeof(ItemData), typeof(int), typeof(bool), typeof(float), typeof(int)
})]
internal static class FreshnessTooltipPatch
{
    private readonly struct State
    {
        internal State(float multiplier)
        {
            Multiplier = multiplier;
        }

        internal float Multiplier { get; }
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore(new[] { FreshnessFoodEffects.GourmetsDietPluginGuid })]
    private static void Prefix(ref ItemData __0, out State __state)
    {
        float multiplier = FreshnessFoodEffects.GetMultiplier(__0);
        __state = new State(multiplier);
        if (FreshnessFoodEffects.GourmetsDietHandlesFreshness ||
            !FreshnessFoodEffects.HasFoodEffect(__0))
        {
            return;
        }

        __0 = FreshnessFoodEffects.CreateTooltipSnapshot(__0, multiplier);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(State __state, ref string __result)
    {
        if (__state.Multiplier >= 0.999999f)
        {
            return;
        }

        string line = SpoilageUiText.BuildFreshnessEffectLine(__state.Multiplier);
        if (!SpoilageUiText.ContainsLine(__result ?? string.Empty, line))
        {
            __result = string.IsNullOrEmpty(__result) ? line : __result + "\n" + line;
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.EatFood))]
internal static class PlayerEatFoodFreshnessPatch
{
    private sealed class State
    {
        internal ItemData? Snapshot;
        internal string SharedName = string.Empty;
        internal Player.Food? ExistingFood;
        internal ItemData? ExistingFoodItem;
        internal bool CompletedSuccessfully;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyBefore(new[] { FreshnessFoodEffects.GourmetsDietPluginGuid })]
    private static void Prefix(Player __instance, ref ItemData item, out State? __state)
    {
        __state = null;
        if (FreshnessFoodEffects.GourmetsDietHandlesFreshness ||
            !FreshnessFoodEffects.HasFoodEffect(item))
        {
            return;
        }

        float multiplier = FreshnessFoodEffects.GetMultiplier(item);
        ItemData snapshot = FreshnessFoodEffects.TryGetConsumedMultiplier(item, out _)
            ? item.Clone()
            : FreshnessFoodEffects.CreateFoodSnapshot(item, multiplier);

        State state = new()
        {
            Snapshot = snapshot,
            SharedName = snapshot.m_shared.m_name ?? string.Empty
        };
        foreach (Player.Food food in __instance.GetFoods())
        {
            if (food?.m_item?.m_shared == null ||
                !string.Equals(food.m_item.m_shared.m_name, state.SharedName, StringComparison.Ordinal))
            {
                continue;
            }

            // EatFood calls UpdateFood before returning. Give an eligible re-eat
            // that snapshot early so the forced recalculation does not use the
            // previous meal's multiplier for one extra tick.
            state.ExistingFood = food;
            state.ExistingFoodItem = food.m_item;
            food.m_item = snapshot;
            break;
        }

        item = snapshot;
        __state = state;
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Player __instance, bool __result, State? __state)
    {
        if (__state?.Snapshot == null)
        {
            return;
        }

        if (!__result)
        {
            if (__state.ExistingFood != null && __state.ExistingFoodItem != null)
            {
                __state.ExistingFood.m_item = __state.ExistingFoodItem;
            }

            return;
        }

        // Vanilla refreshes numbers when the same food is eaten again but leaves
        // Food.m_item pointing to the previous snapshot. Replace it for later
        // UpdateFood and regeneration ticks.
        foreach (Player.Food food in __instance.GetFoods())
        {
            if (food?.m_item?.m_shared == null ||
                !string.Equals(food.m_item.m_shared.m_name, __state.SharedName, StringComparison.Ordinal))
            {
                continue;
            }

            food.m_item = __state.Snapshot;
            break;
        }

        FreshnessFoodEffects.PrepareSave(__instance);
        __state.CompletedSuccessfully = true;
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(State? __state, Exception? __exception)
    {
        if (__exception != null &&
            __state != null &&
            !__state.CompletedSuccessfully &&
            __state.ExistingFood != null &&
            __state.ExistingFoodItem != null)
        {
            __state.ExistingFood.m_item = __state.ExistingFoodItem;
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.RemoveOneFood))]
internal static class PlayerRemoveActiveFoodFreshnessPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance, bool __result)
    {
        if (__result)
        {
            FreshnessFoodEffects.PrepareSave(__instance);
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.ClearFood))]
internal static class PlayerClearActiveFoodFreshnessPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance)
    {
        FreshnessFoodEffects.PrepareSave(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.Save))]
internal static class PlayerSaveActiveFoodFreshnessPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __instance)
    {
        FreshnessFoodEffects.PrepareSave(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.Load))]
internal static class PlayerLoadActiveFoodFreshnessPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Player __instance)
    {
        FreshnessFoodEffects.RestoreAfterLoad(__instance);
    }
}

/// <summary>
/// Feast can keep its edible item separately from the Piece ItemDrop where the
/// timer and assigned-lifetime basis are persisted. Bridge those two values for
/// the synchronous confirmation call, then restore the original ItemData.
/// </summary>
[HarmonyPatch(typeof(Feast), "RPC_EatConfirmation")]
internal static class PlacedFeastFreshnessConsumptionPatch
{
    internal sealed class State
    {
        internal ItemDrop? FoodDrop;
        internal ItemData? OriginalItem;
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Feast __instance, out State? __state)
    {
        __state = null;
        ItemDrop? foodDrop = __instance?.m_foodItem;
        ItemDrop? placedDrop = __instance != null ? __instance.GetComponent<ItemDrop>() : null;
        if (foodDrop?.m_itemData == null || placedDrop?.m_itemData == null)
        {
            return;
        }

        try
        {
            placedDrop.Load();
            ItemData bridged = foodDrop.m_itemData.Clone();
            FreshnessRuntime.CopyFreshnessMetadata(placedDrop.m_itemData, bridged);
            __state = new State
            {
                FoodDrop = foodDrop,
                OriginalItem = foodDrop.m_itemData
            };
            foodDrop.m_itemData = bridged;
        }
        catch (Exception exception)
        {
            FineDiningPlugin.Log.LogWarning(
                "Could not bridge placed-feast freshness into its food item: " + exception.Message);
        }
    }

    [HarmonyFinalizer]
    private static Exception? Finalizer(State? __state, Exception? __exception)
    {
        if (__state?.FoodDrop != null && __state.OriginalItem != null)
        {
            __state.FoodDrop.m_itemData = __state.OriginalItem;
        }

        return __exception;
    }
}
