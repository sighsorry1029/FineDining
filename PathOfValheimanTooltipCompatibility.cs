using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using ItemData = ItemDrop.ItemData;

namespace FineDining;

// PoV owns the card, artwork and interaction. Only its food display model is amended.
// These optional private bindings are deliberately independent of the gameplay adapter.
internal static class PathOfValheimanTooltipCompatibility
{
    private static Bindings? _bindings;
    private static bool _warned;
    private static float _nextCheck;
    private static ItemData? _lastItem;
    private static Preview _lastPreview;

    internal static void TryInstall(Harmony harmony, Assembly assembly)
    {
        MethodInfo? target = null;
        MethodInfo postfix = AccessTools.DeclaredMethod(typeof(PathOfValheimanTooltipCompatibility), nameof(AfterBuild));
        try
        {
            Bindings bindings = new(assembly);
            target = bindings.Build;
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            _bindings = bindings;
            FineDiningPlugin.Log.LogInfo("Enabled FineDining food values and freshness in PathOfValheiman item cards.");
        }
        catch (Exception error)
        {
            try { if (target != null) harmony.Unpatch(target, postfix); }
            catch (Exception rollback) { FineDiningPlugin.Log.LogError("Could not remove PoV food card patch: " + rollback); }
            Warn(error);
        }
    }

    private static void AfterBuild(ItemData __0, object __result)
    {
        Bindings? bindings = _bindings;
        if (bindings == null || __result == null || Player.m_localPlayer == null)
            return;
        try { bindings.Apply(__result, BuildPreview(bindings, __0)); }
        catch (Exception error) { Warn(error); }
    }

    private static Preview BuildPreview(Bindings bindings, ItemData item)
    {
        ItemData prepared = item;
        // Replay only the original display prefix: RawView and global food scale.
        // Never call GetTooltip/Describe here (Describe calls BuildCore recursively).
        bindings.Prepare(ref prepared);
        ItemData? display = DietTooltipPatch.CreatePreview(prepared, out FoodEffect? effect);
        ItemDataSpoilageTooltipPatch.TryBuildLine(item, out string freshness);
        return new Preview(display?.m_shared, effect, freshness);
    }

    internal static void Tick()
    {
        Bindings? bindings = _bindings;
        if (bindings == null) return;
        try
        {
            if (Player.m_localPlayer == null || !bindings.Wanted())
            {
                ClearHover();
                return;
            }
            float now = Time.unscaledTime;
            if (now < _nextCheck) return;
            _nextCheck = now + 1f;
            UITooltip owner = bindings.Current();
            GameObject sheet = bindings.Sheet();
            ItemData? item = owner != null && sheet != null && sheet.activeInHierarchy
                ? bindings.OwnerOf(owner) : null;
            if (item?.m_shared == null)
            {
                _lastItem = null;
                _lastPreview = default;
                return;
            }
            Preview preview = BuildPreview(bindings, item);
            // PoV's Alt-held card owns link handlers. Refresh after it is released,
            // not while the user is interacting with those links.
            if (bindings.Pinned())
            {
                if (!ReferenceEquals(item, _lastItem))
                {
                    _lastItem = item;
                    _lastPreview = preview;
                }
                return;
            }
            if (ReferenceEquals(item, _lastItem) && !preview.SameAs(_lastPreview))
                bindings.Refresh();
            _lastItem = item;
            _lastPreview = preview;
        }
        catch (Exception error)
        {
            _lastItem = null;
            _lastPreview = default;
            _nextCheck = Time.unscaledTime + 5f;
            Warn(error);
        }
    }

    private static void ClearHover()
    {
        _lastItem = null;
        _lastPreview = default;
        _nextCheck = 0f;
    }

    internal static void Shutdown()
    {
        _bindings = null;
        _warned = false;
        ClearHover();
    }

    private static void Warn(Exception error)
    {
        if (_warned) return;
        _warned = true;
        FineDiningPlugin.Log.LogWarning("PoV food card compatibility unavailable; existing food gameplay is unchanged: " + error.GetBaseException().Message);
    }

    private readonly struct Preview
    {
        internal readonly bool HasFood;
        internal readonly float Health, Stamina, Eitr, Regen, Duration;
        internal readonly string Modifiers;
        internal readonly string Freshness;

        internal Preview(ItemData.SharedData? display, FoodEffect? effect, string freshness)
        {
            HasFood = display != null;
            Health = DietTooltipPatch.RoundTooltipStat(display?.m_food ?? 0f);
            Stamina = DietTooltipPatch.RoundTooltipStat(display?.m_foodStamina ?? 0f);
            Eitr = DietTooltipPatch.RoundTooltipStat(display?.m_foodEitr ?? 0f);
            Regen = DietTooltipPatch.RoundTooltipStat(display?.m_foodRegen ?? 0f);
            Duration = display?.m_foodBurnTime ?? 0f;
            string modifiers = "";
            DietTooltipPatch.AppendModifiers(effect, ref modifiers);
            Modifiers = modifiers;
            Freshness = freshness;
        }

        internal bool SameAs(Preview other) => HasFood == other.HasFood && Health.Equals(other.Health) &&
            Stamina.Equals(other.Stamina) && Eitr.Equals(other.Eitr) && Regen.Equals(other.Regen) &&
            Duration.Equals(other.Duration) && Modifiers == other.Modifiers && Freshness == other.Freshness;
    }

    private delegate void PrepareItem(ref ItemData item);

    private sealed class Bindings
    {
        internal readonly MethodInfo Build;
        internal readonly PrepareItem Prepare;
        internal readonly Func<bool> Wanted, Pinned;
        internal readonly Func<UITooltip> Current;
        internal readonly Func<GameObject> Sheet;
        internal readonly Func<UITooltip, ItemData?> OwnerOf;
        internal readonly Action Refresh;
        private readonly Func<object, IList> _main, _facts;
        private readonly Func<object, List<string>> _unknown;
        private readonly Func<string, string> _label;
        private readonly Type _core;
        private readonly FieldInfo _token, _rowLabel, _value, _second, _sort;
        private readonly object _foodSort;

        internal Bindings(Assembly assembly)
        {
            Type tooltip = assembly.GetType("PassiveTree.UI.ItemTooltip", true)!;
            Type model = tooltip.GetNestedType("Model", BindingFlags.NonPublic) ?? throw new TypeLoadException("PoV tooltip Model");
            _core = tooltip.GetNestedType("Core", BindingFlags.NonPublic) ?? throw new TypeLoadException("PoV tooltip Core");
            Type sort = tooltip.GetNestedType("Sort", BindingFlags.NonPublic) ?? throw new TypeLoadException("PoV tooltip Sort");
            _sort = Field(model, "Sort", sort);
            _foodSort = Enum.Parse(sort, "Food");
            Build = Require(tooltip, "BuildCore", model, typeof(ItemData));
            Type foodTooltip = assembly.GetType("PassiveTree.FoodMasteries+Tooltip", true)!;
            Prepare = (PrepareItem)Delegate.CreateDelegate(typeof(PrepareItem),
                Require(foodTooltip, "Prefix", typeof(void), typeof(ItemData).MakeByRefType()));
            _main = ReadList(model, "Main");
            _facts = ReadList(model, "Facts");
            _unknown = ReadField<object, List<string>>(model, "Unknown");
            _token = Field(_core, "Token", typeof(string));
            _rowLabel = Field(_core, "Label", typeof(string));
            _value = Field(_core, "Value", typeof(string));
            _second = Field(_core, "Second", typeof(string));
            _label = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), Require(tooltip, "LabelOf", typeof(string), typeof(string)));
            Wanted = StaticMethod<Func<bool>>(tooltip, "get_Wanted");
            Pinned = StaticField<bool>(tooltip, "pinned");
            Current = StaticField<UITooltip>(typeof(UITooltip), "m_current");
            Sheet = StaticField<GameObject>(tooltip, "sheetGo");
            if (AccessTools.DeclaredMethod(tooltip, "OwnerOf", new[] { typeof(UITooltip) }) != null)
            {
                // Use PoV's renderer lookup; newer weak-table values wrap ItemData.
                OwnerOf = (Func<UITooltip, ItemData?>)Delegate.CreateDelegate(typeof(Func<UITooltip, ItemData?>),
                    Require(tooltip, "OwnerOf", typeof(ItemData), typeof(UITooltip)));
            }
            else
            {
                // Earlier PoV cards expose only the direct ItemData weak table.
                var owners = StaticField<ConditionalWeakTable<UITooltip, ItemData>>(tooltip, "owners")()
                    ?? throw new InvalidOperationException("PoV tooltip owners missing");
                OwnerOf = owner => owner != null && owners.TryGetValue(owner, out ItemData item) ? item : null;
            }
            Refresh = StaticMethod<Action>(tooltip, "RefreshCurrent");
        }

        internal void Apply(object model, Preview preview)
        {
            if (preview.HasFood)
            {
                // The renderer only shows Facts for Food, including a raw item
                // made edible by PoV whose original classification was Resource.
                _sort.SetValue(model, _foodSort);
                IList main = _main(model), facts = _facts(model);
                SetStat(main, "$item_food_health", preview.Health);
                SetStat(main, "$item_food_stamina", preview.Stamina);
                SetStat(main, "$item_food_eitr", preview.Eitr);
                SetStat(facts, "$item_food_regen", preview.Regen);
                // Leave PoV's existing duration formatting intact. Raw food views
                // may not have a row yet because PoV classified the original item.
                if (preview.Duration > 0f && Find(facts, "$item_food_duration") == null)
                    SetValue(facts, "$item_food_duration", Mathf.RoundToInt(preview.Duration / 60f) + ":00");
            }
            if (!string.IsNullOrEmpty(preview.Freshness))
            {
                List<string> lines = _unknown(model);
                if (!lines.Exists(line => FoodEffectUiText.ContainsLine(line, preview.Freshness)))
                    lines.Add(preview.Freshness);
            }
        }

        private void SetStat(IList rows, string token, float number)
        {
            if (float.IsNaN(number) || float.IsInfinity(number)) return;
            if (number <= 0f)
            {
                object? row = Find(rows, token);
                if (row != null) rows.Remove(row);
                return;
            }
            SetValue(rows, token, number.ToString("0.#"));
        }

        private object? Find(IList rows, string token)
        {
            foreach (object row in rows)
                if ((string)_token.GetValue(row) == token) return row;
            return null;
        }

        private void SetValue(IList rows, string token, string value)
        {
            object? row = Find(rows, token);
            if (row == null)
            {
                row = Activator.CreateInstance(_core, nonPublic: true)!;
                _token.SetValue(row, token);
                _rowLabel.SetValue(row, _label(token));
                rows.Add(row);
            }
            _value.SetValue(row, value);
            _second.SetValue(row, null);
        }

        private Func<object, IList> ReadList(Type model, string name)
        {
            FieldInfo field = Field(model, name, typeof(List<>).MakeGenericType(_core));
            var target = Expression.Parameter(typeof(object));
            return Expression.Lambda<Func<object, IList>>(Expression.Convert(
                Expression.Field(Expression.Convert(target, model), field), typeof(IList)), target).Compile();
        }
    }

    private static FieldInfo Field(Type type, string name, Type expected, bool isStatic = false)
    {
        FieldInfo? field = AccessTools.DeclaredField(type, name);
        if (field == null || field.FieldType != expected || field.IsStatic != isStatic)
            throw new MissingFieldException(type.FullName, name);
        return field;
    }

    private static Func<TTarget, TValue> ReadField<TTarget, TValue>(Type type, string name)
    {
        var target = Expression.Parameter(typeof(TTarget));
        return Expression.Lambda<Func<TTarget, TValue>>(Expression.Field(Expression.Convert(target, type),
            Field(type, name, typeof(TValue))), target).Compile();
    }

    private static Func<T> StaticField<T>(Type type, string name) =>
        Expression.Lambda<Func<T>>(Expression.Field(null, Field(type, name, typeof(T), true))).Compile();

    private static MethodInfo Require(Type type, string name, Type returns, params Type[] args)
    {
        MethodInfo? method = AccessTools.DeclaredMethod(type, name, args);
        if (method == null || !method.IsStatic || method.ReturnType != returns || method.ContainsGenericParameters)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static T StaticMethod<T>(Type type, string name) where T : Delegate =>
        (T)Delegate.CreateDelegate(typeof(T), Require(type, name, typeof(T).GetMethod("Invoke")!.ReturnType));
}
