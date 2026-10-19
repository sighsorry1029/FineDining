using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace FineDining;

// Optional runtime bindings: PoV is neither a compile reference nor a bundled dependency.
// Keep its progression, per-portion rolls, food decay policy and saved keys in PoV.
internal static class PathOfValheimanCompatibility
{
    internal const string PluginGuid = "treextr.pathofvalheiman";
    private static Bindings? _bindings;
    private static readonly List<MethodBase> PatchedMethods = new();
    private static ConditionalWeakTable<Fermenter, BrewOutput> _brewOutputs = new();

    internal static void TryInstall(Harmony harmony)
    {
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin) || plugin.Instance == null)
            return;

        try
        {
            Bindings bindings = new(plugin.Instance.GetType().Assembly);
            RequireInstalledPatch(AccessTools.DeclaredMethod(typeof(Player), "GetTotalFoodValue"),
                "FoodMasteries+FoodValues", HarmonyPatchType.Prefix);
            RequireInstalledPatch(AccessTools.DeclaredMethod(typeof(Player), "UpdateFood"),
                "HealthRegenTicks+FoodClock", HarmonyPatchType.Transpiler);
            RequireInstalledPatch(AccessTools.DeclaredMethod(typeof(Player), nameof(Player.EatFood)),
                "CraftSnack+Player_Eat", HarmonyPatchType.Prefix);
            RequireInstalledPatch(AccessTools.DeclaredMethod(typeof(CookingStation), "CookItem"),
                "CraftCook+Station_CookItem", HarmonyPatchType.Postfix);
            RequireInstalledPatch(AccessTools.DeclaredMethod(typeof(Fermenter), "DelayedTap"),
                "Brewing+Fermenter_Tap", HarmonyPatchType.Prefix);
            RequireInstalledPatch(AccessTools.DeclaredMethod(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
                    new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) }),
                "FoodMasteries+Tooltip", HarmonyPatchType.Prefix);
            _bindings = bindings;
            Patch(harmony, bindings.FoodValue, nameof(ScaleFoodValue));
            Patch(harmony, bindings.SnackEat, nameof(ScaleSnack));
            Patch(harmony, bindings.BatchCook, nameof(PlanBatchCooking));
            PatchedMethods.Add(bindings.BrewPrefix);
            harmony.Patch(bindings.BrewPrefix,
                prefix: new HarmonyMethod(typeof(PathOfValheimanCompatibility), nameof(BeforeBrewOutput)),
                postfix: new HarmonyMethod(typeof(PathOfValheimanCompatibility), nameof(AfterBrewOutput)));
            FineDiningPlugin.Log.LogInfo(
                "Enabled PathOfValheiman " + plugin.Metadata.Version +
                " food, regeneration, snack and cooking compatibility. PoV skill progression and bonus rolls are unchanged.");
            PathOfValheimanTooltipCompatibility.TryInstall(harmony, plugin.Instance.GetType().Assembly);
        }
        catch (Exception exception)
        {
            for (int i = PatchedMethods.Count - 1; i >= 0; i--)
            {
                try { harmony.Unpatch(PatchedMethods[i], HarmonyPatchType.All, harmony.Id); }
                catch (Exception rollbackException)
                {
                    FineDiningPlugin.Log.LogError("Could not remove PoV compatibility patch from " +
                                                PatchedMethods[i] + ": " + rollbackException);
                }
            }
            Shutdown();
            FineDiningPlugin.Log.LogWarning(
                "PathOfValheiman compatibility could not be installed; the expected runtime contract was not available. " + exception);
        }
    }

    private static void Patch(Harmony harmony, MethodInfo target, string transpiler)
    {
        PatchedMethods.Add(target);
        harmony.Patch(target, transpiler: new HarmonyMethod(typeof(PathOfValheimanCompatibility), transpiler));
    }

    private static void RequireInstalledPatch(MethodBase target, string patchType, HarmonyPatchType kind)
    {
        Patches? patches = Harmony.GetPatchInfo(target);
        var candidates = kind == HarmonyPatchType.Transpiler ? patches?.Transpilers :
            kind == HarmonyPatchType.Postfix ? patches?.Postfixes : patches?.Prefixes;
        if (candidates == null || !candidates.Any(p => p.owner == PluginGuid &&
                p.PatchMethod.DeclaringType?.FullName == "PassiveTree." + patchType))
            throw new InvalidOperationException("PoV did not install " + patchType + " on " + target);
    }

    internal static void Shutdown()
    {
        PathOfValheimanTooltipCompatibility.Shutdown();
        _bindings = null;
        PatchedMethods.Clear();
        _brewOutputs = new ConditionalWeakTable<Fermenter, BrewOutput>();
    }

    internal static bool IsSnack(Player player, ItemDrop.ItemData item) =>
        _bindings?.Snackable(player, item) == true;

    internal static void PrepareActiveFoods(Player player)
    {
        if (_bindings == null || player != Player.m_localPlayer)
            return;
        // Vanilla restores raw foods from their non-edible prefab on character load.
        // Restore PoV's view before counting Full Course slots or recalculating regen.
        foreach (Player.Food food in player.GetFoods())
            if (food.m_item != null && !FoodIdentity.IsDirectlyEdible(food.m_item))
                food.m_item = _bindings.RawView(food.m_item, food.m_name);
    }

    internal static float RegenInterval(Player player) => _bindings?.Interval(10f, player) ?? 10f;

    internal static float AdvanceRegen(Player player) => _bindings?.Advance(player) ?? 0f;

    internal static void HealFood(Player player, float amount)
    {
        if (_bindings != null)
            _bindings.HealFood(player, amount, true);
        else
            player.Heal(amount);
    }

    private static float DietScale(Player.Food food)
    {
        Player player = Player.m_localPlayer;
        if (_bindings == null || player == null || !FoodIdentity.IsDirectlyEdible(food) ||
            !player.GetFoods().Contains(food))
            return 1f;
        PlayerFoodStateData state = FoodStateStore.GetState(player);
        return FoodRules.GetAppliedScale(player, state, food) *
               FoodRules.GetFullCourseScale(player, state, FoodRules.CountActiveDietFoods(player.GetFoods()));
    }

    // The input already passed PoV's tooltip prefix (RawView + global food scale).
    // Per-resource nourishment and the cook's stamp do not include that global scale.
    // Hearty Meal is deliberately not rolled or predicted by a hover operation.
    internal static void AdjustTooltip(Player player, ItemDrop.ItemData item,
        ItemDrop.ItemData.SharedData display, bool snack)
    {
        Bindings? bindings = _bindings;
        if (bindings == null || player != Player.m_localPlayer)
            return;
        object? stats = bindings.Stats();
        float scale = snack
            ? FreshnessRuntime.GetFoodStatMultiplier(item) * bindings.Tunable("snack.share") / bindings.BaseMultiplier()
            : 1f + Math.Max(0f, bindings.Stamped(item, "pt.food"));
        display.m_food *= scale * bindings.Nourishment[0](stats);
        display.m_foodStamina *= scale * bindings.Nourishment[1](stats);
        display.m_foodEitr *= scale * bindings.Nourishment[2](stats);
        if (snack)
            display.m_foodRegen = 0f;
    }

    // Apply FD once at the shared per-food evaluator, so GetTotalFoodValue and
    // PoV's food-derived damage/attack-speed masteries see the same portions.
    private static IEnumerable<CodeInstruction> ScaleFoodValue(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod)
    {
        var method = (MethodInfo)__originalMethod;
        FieldInfo[] fields = ValueFields(method.ReturnType);
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        if (!codes.Any(c => c.opcode == OpCodes.Ret))
            throw new InvalidOperationException("PoV food evaluator has no return.");
        LocalBuilder value = generator.DeclareLocal(method.ReturnType);
        LocalBuilder scale = generator.DeclareLocal(typeof(float));
        MethodInfo helper = AccessTools.DeclaredMethod(typeof(PathOfValheimanCompatibility), nameof(DietScale));
        var result = new List<CodeInstruction>();
        foreach (CodeInstruction code in codes)
        {
            if (code.opcode != OpCodes.Ret)
            {
                result.Add(code);
                continue;
            }
            var save = new CodeInstruction(OpCodes.Stloc, value);
            MoveBoundary(code, save);
            result.Add(save);
            result.Add(new CodeInstruction(OpCodes.Ldarg_0));
            result.Add(new CodeInstruction(OpCodes.Call, helper));
            result.Add(new CodeInstruction(OpCodes.Stloc, scale));
            foreach (FieldInfo field in fields)
            {
                result.Add(new CodeInstruction(OpCodes.Ldloca, value));
                result.Add(new CodeInstruction(OpCodes.Dup));
                result.Add(new CodeInstruction(OpCodes.Ldfld, field));
                result.Add(new CodeInstruction(OpCodes.Ldloc, scale));
                result.Add(new CodeInstruction(OpCodes.Mul));
                result.Add(new CodeInstruction(OpCodes.Stfld, field));
            }
            result.Add(new CodeInstruction(OpCodes.Ldloc, value));
            result.Add(code);
        }
        return result;
    }

    // Only the three values stored in a new snack change. Item identity, duration,
    // no-slot handling, inventory removal and PoV's original clone are untouched.
    private static IEnumerable<CodeInstruction> ScaleSnack(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        string[] names = { "m_food", "m_foodStamina", "m_foodEitr" };
        var fields = names.Select(n => AccessTools.Field(typeof(ItemDrop.ItemData.SharedData), n)).ToArray();
        foreach (FieldInfo field in fields)
        {
            int index = codes.FindIndex(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, field));
            if (codes.Count(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, field)) != 1 ||
                index + 3 >= codes.Count || codes[index + 2].opcode != OpCodes.Mul ||
                codes[index + 3].opcode != OpCodes.Stfld ||
                codes[index + 3].operand is not FieldInfo destination ||
                destination.DeclaringType?.FullName != "PassiveTree.CraftSnack+Snack" ||
                destination.Name != new[] { "Health", "Stamina", "Eitr" }[Array.IndexOf(fields, field)])
                throw new InvalidOperationException("PoV snack value pattern changed: " + field.Name);
        }
        MethodInfo scale = AccessTools.DeclaredMethod(typeof(FreshnessRuntime), nameof(FreshnessRuntime.GetFoodStatMultiplier));
        var result = new List<CodeInstruction>();
        foreach (CodeInstruction code in codes)
        {
            result.Add(code);
            if (code.opcode == OpCodes.Ldfld && fields.Contains(code.operand as FieldInfo))
            {
                result.Add(new CodeInstruction(OpCodes.Ldarg_1));
                result.Add(new CodeInstruction(OpCodes.Call, scale));
                result.Add(new CodeInstruction(OpCodes.Mul));
            }
        }
        return result;
    }

    private static IEnumerable<CodeInstruction> PlanBatchCooking(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(c => new CodeInstruction(c)).ToList();
        MethodInfo invoke = AccessTools.DeclaredMethod(typeof(ZNetView), nameof(ZNetView.InvokeRPC),
            new[] { typeof(string), typeof(object[]) });
        var sites = codes.Select((code, index) => (code, index))
            .Where(p => p.code.Calls(invoke)).Select(p => p.index).ToArray();
        if (sites.Length != 1 || codes.Count(c => c.opcode == OpCodes.Ldstr && Equals(c.operand, "RPC_AddItem")) != 1)
            throw new InvalidOperationException("PoV batch cooking RPC pattern changed.");
        int site = sites[0];
        // Preserve the original name + cheated payload; reject a new overload or
        // payload shape instead of sending a plan for an unrelated RPC.
        if (site < 15 || codes[site - 15].opcode != OpCodes.Ldstr ||
            !Equals(codes[site - 15].operand, "RPC_AddItem") || !codes[site - 14].LoadsConstant(2) ||
            codes[site - 13].opcode != OpCodes.Newarr || !Equals(codes[site - 13].operand, typeof(object)) ||
            codes[site - 3].opcode != OpCodes.Ldfld || !Equals(codes[site - 3].operand,
                AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_cheated))) ||
            codes[site - 2].opcode != OpCodes.Box || !Equals(codes[site - 2].operand, typeof(bool)) ||
            codes[site - 1].opcode != OpCodes.Stelem_Ref)
            throw new InvalidOperationException("PoV batch cooking payload changed.");
        var loadUser = new CodeInstruction(OpCodes.Ldarg_1);
        MoveBoundary(codes[site], loadUser);
        codes.InsertRange(site, new[] { loadUser, new CodeInstruction(OpCodes.Ldarg_0) });
        codes[site + 2].opcode = OpCodes.Call;
        codes[site + 2].operand = AccessTools.DeclaredMethod(typeof(CookingStationAutoPopSystem), nameof(CookingStationAutoPopSystem.RequestAdd));
        return codes;
    }

    private static void MoveBoundary(CodeInstruction from, CodeInstruction to)
    {
        to.labels.AddRange(from.labels);
        to.blocks.AddRange(from.blocks);
        from.labels.Clear();
        from.blocks.Clear();
    }

    // PoV temporarily adds one bottle to the conversion during DelayedTap and
    // restores it in its finalizer. Record that exact mutation, not any arbitrary
    // changed recipe, so FD's existing output validation can remain strict.
    private sealed class BrewOutput
    {
        internal Fermenter.ItemConversion? Conversion;
        internal int Before;
        internal int After;
        internal BrewOutput? Previous;
    }

    private static void BeforeBrewOutput(Fermenter __0, out BrewOutput? __state)
    {
        _brewOutputs.TryGetValue(__0, out BrewOutput previous);
        __state = new BrewOutput { Previous = previous };
        _brewOutputs.Remove(__0);
        _brewOutputs.Add(__0, __state);
        // Push before looking up the conversion, so DelayedTap's finalizer can
        // also unwind a failed prefix without removing an outer tap's state.
        __state.Conversion = FermenterCookingBonusSystem.GetDelayedTapConversion(__0);
        __state.Before = __state.Conversion?.m_producedItems ?? 0;
    }

    private static void AfterBrewOutput(Fermenter __0, BrewOutput? __state)
    {
        if (__state?.Conversion == null)
            return;
        __state.After = __state.Conversion.m_producedItems;
    }

    internal static bool IsBrewOutputCount(Fermenter fermenter, Fermenter.ItemConversion conversion, int expected)
    {
        return _bindings != null && _brewOutputs.TryGetValue(fermenter, out BrewOutput output) &&
               ReferenceEquals(output.Conversion, conversion) && output.Before == expected &&
               output.After == conversion.m_producedItems && (long)output.After == (long)expected + 1;
    }

    internal static void EndBrewOutput(Fermenter fermenter)
    {
        if (!_brewOutputs.TryGetValue(fermenter, out BrewOutput output))
            return;
        _brewOutputs.Remove(fermenter);
        if (output.Previous != null)
            _brewOutputs.Add(fermenter, output.Previous);
    }

    private static FieldInfo[] ValueFields(Type type)
    {
        if (!type.IsValueType || type.FullName != "PassiveTree.FoodMasteries+Values")
            throw new InvalidOperationException("Unexpected PoV food result type.");
        return new[] { "Health", "Stamina", "Eitr" }.Select(name =>
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field?.FieldType != typeof(float))
                throw new MissingFieldException(type.FullName, name);
            return field;
        }).ToArray();
    }

    private sealed class Bindings
    {
        internal readonly MethodInfo FoodValue, SnackEat, BatchCook, BrewPrefix;
        internal readonly Func<Player, ItemDrop.ItemData, bool> Snackable;
        internal readonly Func<ItemDrop.ItemData, string, ItemDrop.ItemData> RawView;
        internal readonly Func<float, Player, float> Interval;
        internal readonly Func<Player, float> Advance;
        internal readonly Action<Character, float, bool> HealFood;
        internal readonly Func<ItemDrop.ItemData, string, float> Stamped;
        internal readonly Func<string, float> Tunable;
        internal readonly Func<float> BaseMultiplier;
        internal readonly Func<object?> Stats;
        internal readonly Func<object?, float>[] Nourishment;

        internal Bindings(Assembly assembly)
        {
            Type Type(string name) => assembly.GetType("PassiveTree." + name, throwOnError: true)!;
            Type stats = Type("StatSet");
            Type values = Type("FoodMasteries+Values");
            ValueFields(values);
            FoodValue = Require(Type("FoodMasteries"), "Value", values, typeof(Player.Food), stats);
            SnackEat = Require(Type("CraftSnack+Player_Eat"), "Prefix", typeof(bool),
                typeof(Player), typeof(ItemDrop.ItemData), typeof(bool).MakeByRefType());
            BatchCook = Require(Type("CraftCook+Station_CookItem"), "Postfix", typeof(void),
                typeof(CookingStation), typeof(Humanoid), typeof(ItemDrop.ItemData), typeof(bool), typeof(int));
            BrewPrefix = Require(Type("Brewing+Fermenter_Tap"), "Prefix", typeof(void),
                typeof(Fermenter), Type("Brewing+TapState").MakeByRefType());
            Snackable = Bind<Func<Player, ItemDrop.ItemData, bool>>(Type("CraftSnack"), "Snackable");
            RawView = Bind<Func<ItemDrop.ItemData, string, ItemDrop.ItemData>>(Type("FoodMasteries"), "RawView");
            Interval = Bind<Func<float, Player, float>>(Type("HealthRegenTicks"), "Interval");
            Advance = Bind<Func<Player, float>>(Type("HealthRegenTicks"), "Advance");
            HealFood = Bind<Action<Character, float, bool>>(Type("HealthRegenTicks"), "HealFood");
            Stamped = Bind<Func<ItemDrop.ItemData, string, float>>(Type("Crafting"), "Stamped");
            Tunable = Bind<Func<string, float>>(Type("Tunables"), "Of");
            BaseMultiplier = Bind<Func<float>>(Type("FoodMasteries"), "get_BaseMultiplier");
            MethodInfo getStats = Require(Type("Effects"), "get_S", stats);
            Stats = Expression.Lambda<Func<object?>>(Expression.Convert(Expression.Call(getStats), typeof(object))).Compile();
            Type subject = Type("Subject");
            MethodInfo multiplier = Require(Type("FoodNourishment"), "Multiplier", typeof(float), stats, subject);
            Nourishment = new[] { "OfHealth", "OfStamina", "OfEitr" }.Select(name =>
            {
                ParameterExpression input = Expression.Parameter(typeof(object));
                return Expression.Lambda<Func<object?, float>>(
                    Expression.Call(multiplier, Expression.Convert(input, stats),
                        Expression.Constant(Enum.Parse(subject, name), subject)), input).Compile();
            }).ToArray();
        }
    }

    private static T Bind<T>(Type type, string name) where T : Delegate
    {
        MethodInfo signature = typeof(T).GetMethod("Invoke")!;
        MethodInfo method = Require(type, name, signature.ReturnType,
            signature.GetParameters().Select(p => p.ParameterType).ToArray());
        return AccessTools.MethodDelegate<T>(method);
    }

    private static MethodInfo Require(Type type, string name, Type returns, params Type[] args)
    {
        MethodInfo? method = AccessTools.DeclaredMethod(type, name, args);
        if (method == null || !method.IsStatic || method.ReturnType != returns || method.ContainsGenericParameters)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }
}
