using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ItemData = ItemDrop.ItemData;

internal static class Program
{
    private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeInt();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeHash(IntPtr value);
    private static readonly NativeInt Offset = () => 0;
    private static readonly NativeHash Hash = _ => 0;
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void mono_add_internal_call(string name, IntPtr method);
    private static bool Equality(UnityEngine.Object lhs, UnityEngine.Object rhs, ref bool __result) { __result = ReferenceEquals(lhs, rhs); return false; }
    private static bool ColorStub(ref Color color, ref bool __result) { color = Color.white; __result = true; return false; }
    private static IEnumerable<CodeInstruction> NullBody() { yield return new CodeInstruction(OpCodes.Ldnull); yield return new CodeInstruction(OpCodes.Ret); }
    private static bool NotRaw(ref bool __result) { __result = false; return false; }
    private static bool isSnack;
    private static bool Snack(ref bool __result) { __result = isSnack; return false; }
    private static bool Nourish(ref float __result) { __result = 1.25f; return false; }
    private static bool Tunable(ref float __result) { __result = 0.2f; return false; }
    private static void Log(object sender, BepInEx.Logging.LogEventArgs args) => System.Console.WriteLine(args.Data);
    private static string Key(ItemData item) => item.m_shared.m_name;
    private static bool Prefab(ItemData item, ref string __result) { __result = Key(item); return false; }
    private static object? renderedModel;
    private static object ModelFixture() => renderedModel!;
    private static IEnumerable<CodeInstruction> ModelBody(MethodBase __originalMethod)
    {
        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Program), nameof(ModelFixture)));
        yield return new CodeInstruction(OpCodes.Castclass, ((MethodInfo)__originalMethod).ReturnType);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static int Main(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("FineDining.dll PoV.dll Managed BepInEx/core JotunnDirectory");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            foreach (string root in new[] { args[2], args[3], Path.GetDirectoryName(args[0])!, args[4] })
            { string path = Path.Combine(root, new AssemblyName(e.Name).Name + ".dll"); if (File.Exists(path)) return Assembly.LoadFrom(path); }
            return null;
        };
        try { return Run(args); }
        catch (Exception e) { System.Console.Error.WriteLine(e); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Assembly bep = Assembly.LoadFrom(Path.Combine(args[3], "BepInEx.dll"));
        bep.GetType("BepInEx.Paths", true)!.GetMethod("SetExecutablePath", S)!.Invoke(null, new object[] {
            Path.Combine(Path.GetTempPath(), "fd-tooltip-smoke", "valheim.exe"), null!, args[2], new[] { args[2], args[3] } });
        Type threading = bep.GetType("BepInEx.ThreadingHelper", true)!;
        object queue = FormatterServices.GetUninitializedObject(threading);
        threading.GetField("_invokeLock", I)!.SetValue(queue, new object());
        threading.GetField("<Instance>k__BackingField", S)!.SetValue(null, queue);
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
        mono_add_internal_call("UnityEngine.Animator::StringToHash_Injected", Marshal.GetFunctionPointerForDelegate(Hash));
        var fixture = new Harmony("fd.tooltip.fixture");
        var patch = new Harmony("fd.tooltip.test");
        try
        {
            fixture.Patch(AccessTools.Method(typeof(UnityEngine.Object), "CompareBaseObjects"), prefix: new HarmonyMethod(typeof(Program), nameof(Equality)));
            fixture.Patch(AccessTools.PropertyGetter(typeof(Localization), "instance"), transpiler: new HarmonyMethod(typeof(Program), nameof(NullBody)));
            fixture.Patch(AccessTools.Method(typeof(ColorUtility), "TryParseHtmlString"), prefix: new HarmonyMethod(typeof(Program), nameof(ColorStub)));
            Assembly mod = Assembly.LoadFrom(args[0]), pov = Assembly.LoadFrom(args[1]);
            var log = new BepInEx.Logging.ManualLogSource("smoke");
            log.LogEvent += Log;
            mod.GetType("FineDining.FineDiningPlugin", true)!.GetField("Log", S)!.SetValue(null, log);
            Check(!mod.GetReferencedAssemblies().Any(a => a.Name == "PathOfValheiman"), "PoV remains optional");
            Type bridge = mod.GetType("FineDining.PathOfValheimanTooltipCompatibility", true)!;
            Type bindingsType = bridge.GetNestedType("Bindings", BindingFlags.NonPublic)!;
            object bindings = Activator.CreateInstance(bindingsType, I, null, new object[] { pov }, null)!;
            MethodInfo build = (MethodInfo)bindingsType.GetField("Build", I)!.GetValue(bindings)!;
            Check(build.IsPrivate, "original private BuildCore boundary");
            patch.Patch(build, postfix: new HarmonyMethod(bridge.GetMethod("AfterBuild", S)!));
            Check(Harmony.GetPatchInfo(build).Postfixes.Any(p => p.owner == patch.Id), "original Mono compiles model postfix");
            Type modelType = build.ReturnType;
            object model = Activator.CreateInstance(modelType, true)!;
            IList main = (IList)modelType.GetField("Main", I)!.GetValue(model)!;
            IList facts = (IList)modelType.GetField("Facts", I)!.GetValue(model)!;
            var unknown = (List<string>)modelType.GetField("Unknown", I)!.GetValue(model)!;
            Type core = pov.GetType("PassiveTree.UI.ItemTooltip+Core", true)!;
            object Row(string token, string text)
            {
                object row = Activator.CreateInstance(core, true)!;
                core.GetField("Token", I)!.SetValue(row, token); core.GetField("Value", I)!.SetValue(row, text);
                return row;
            }
            string? Value(IList rows, string token) => rows.Cast<object>().Where(r => (string)core.GetField("Token", I)!.GetValue(r)! == token)
                .Select(r => (string)core.GetField("Value", I)!.GetValue(r)!).SingleOrDefault();
            object armor = Row("$item_armor", "unchanged"); main.Add(armor);
            main.Add(Row("$item_food_health", "3")); facts.Add(Row("$item_food_regen", "1"));
            facts.Add(Row("$item_food_duration", "5:00")); unknown.Add("another mod's line");
            Type previewType = bridge.GetNestedType("Preview", BindingFlags.NonPublic)!;
            object Preview(ItemData.SharedData? shared, string line) => Activator.CreateInstance(previewType, I, null, new object?[] { shared, null, line }, null)!;
            void Apply(object preview) => bindingsType.GetMethod("Apply", I)!.Invoke(bindings, new[] { model, preview });
            var display = new ItemData.SharedData { m_food = 1.8f, m_foodEitr = 6.3f, m_foodRegen = 0.5f, m_foodBurnTime = 300f };
            object snapshot = Preview(display, "<color=yellow>freshness</color>");
            Apply(snapshot); Apply(snapshot);
            Check(Value(main, "$item_food_health") == "1.8" && Value(main, "$item_food_eitr") == "6.3" && Value(facts, "$item_food_regen") == "0.5", "decimal values and missing food rows");
            Check(ReferenceEquals(main[0], armor) && Value(main, "$item_armor") == "unchanged" && Value(facts, "$item_food_duration") == "5:00", "unrelated rows and PoV duration survive");
            Check(unknown.SequenceEqual(new[] { "another mod's line", "<color=yellow>freshness</color>" }), "freshness is appended once; other mods preserved");
            display.m_foodRegen = 0f; display.m_foodEitr = 0f;
            Apply(Preview(display, ""));
            Check(Value(facts, "$item_food_regen") == null && Value(main, "$item_food_eitr") == null, "zero snack values remove raw rows");
            facts.Clear(); Apply(Preview(display, ""));
            Check(Value(facts, "$item_food_duration") == "5:00", "raw food view gains missing duration");
            Check(modelType.GetField("Sort", I)!.GetValue(model)!.ToString() == "Food", "raw food facts use the renderer's visible consumable section");
            int count = main.Count + facts.Count; Apply(Preview(null, ""));
            Check(count == main.Count + facts.Count, "nonfood preview leaves rows alone");
            Check((bool)previewType.GetMethod("SameAs", I)!.Invoke(snapshot, new[] { Preview(new ItemData.SharedData { m_food = 1.8f, m_foodEitr = 6.3f, m_foodRegen = 0.5f, m_foodBurnTime = 300f }, "<color=yellow>freshness</color>") })!, "stable presentation does not request rebuild");
            Check(!(bool)previewType.GetMethod("SameAs", I)!.Invoke(snapshot, new[] { Preview(display, "changed") })!, "food/freshness change invalidates presentation");
            Check((bool)previewType.GetMethod("SameAs", I)!.Invoke(snapshot, new[] { Preview(new ItemData.SharedData { m_food = 1.80001f, m_foodEitr = 6.30001f, m_foodRegen = 0.50001f, m_foodBurnTime = 300f }, "<color=yellow>freshness</color>") })!, "sub-display precision does not rebuild cards");

            // Original PoV display prefix + built FD preview. Engine identity,
            // progression getters and player state are controlled fixture inputs.
            Type povPlugin = pov.GetType("PassiveTree.Plugin", true)!;
            var cfg = new ConfigFile(Path.Combine(Path.GetTempPath(), "fd-tooltip-smoke-unused.cfg"), false);
            cfg.SaveOnConfigSet = false;
            mod.GetType("FineDining.DietConfig", true)!.GetMethod("Initialize", S)!.Invoke(null, new object[] {
                cfg, mod.GetType("FineDining.FineDiningPlugin", true)!.GetField("ConfigSync", S)!.GetValue(null)! });
            povPlugin.GetField("FoodBaseMultiplier", S)!.SetValue(null, cfg.Bind("test", "base", 2f));
            fixture.Patch(AccessTools.Method(pov.GetType("PassiveTree.FoodMasteries"), "Allowed"), prefix: new HarmonyMethod(typeof(Program), nameof(NotRaw)));
            fixture.Patch(AccessTools.Method(pov.GetType("PassiveTree.CraftSnack"), "Snackable"), prefix: new HarmonyMethod(typeof(Program), nameof(Snack)));
            fixture.Patch(AccessTools.PropertyGetter(pov.GetType("PassiveTree.Effects"), "S"), transpiler: new HarmonyMethod(typeof(Program), nameof(NullBody)));
            fixture.Patch(AccessTools.Method(pov.GetType("PassiveTree.FoodNourishment"), "Multiplier"), prefix: new HarmonyMethod(typeof(Program), nameof(Nourish)));
            fixture.Patch(AccessTools.Method(pov.GetType("PassiveTree.Tunables"), "Of"), prefix: new HarmonyMethod(typeof(Program), nameof(Tunable)));
            fixture.Patch(AccessTools.Method(mod.GetType("FineDining.FoodIdentity"), "GetCanonicalPrefabName", new[] { typeof(ItemData) }), prefix: new HarmonyMethod(typeof(Program), nameof(Prefab)));
            Type compat = mod.GetType("FineDining.PathOfValheimanCompatibility", true)!;
            object gameBindings = Activator.CreateInstance(compat.GetNestedType("Bindings", BindingFlags.NonPublic)!, I, null, new object[] { pov }, null)!;
            compat.GetField("_bindings", S)!.SetValue(null, gameBindings);
            var player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, player);
            AccessTools.Field(typeof(Player), "m_foods").SetValue(player, new List<Player.Food>());
            AccessTools.Field(typeof(Player), "m_customData").SetValue(player, new Dictionary<string, string>());
            Type stateType = mod.GetType("FineDining.PlayerFoodStateData", true)!;
            object state = Activator.CreateInstance(stateType, true)!;
            ((IDictionary)mod.GetType("FineDining.FoodStateStore", true)!.GetField("Cache", S)!.GetValue(null)!).Add(player, state);
            var item = new ItemData { m_shared = new ItemData.SharedData { m_name = "SmokeFood", m_itemType = ItemData.ItemType.Consumable, m_food = 10f, m_foodEitr = 20f, m_foodRegen = 1f, m_foodBurnTime = 300f } };
            item.m_customData["pt.food"] = "0.2";
            mod.GetType("FineDining.FoodRules", true)!.GetMethod("PreviewNextFoodEffect", S)!.Invoke(null, new[] { player, state, (object)item });
            MethodInfo previewBuilder = bridge.GetMethod("BuildPreview", S)!;
            object first = previewBuilder.Invoke(null, new object[] { bindings, item })!;
            object second = previewBuilder.Invoke(null, new object[] { bindings, item })!;
            float health = (float)previewType.GetField("Health", I)!.GetValue(first)!;
            Check(health > 0 && (bool)previewType.GetField("HasFood", I)!.GetValue(first)!, "real PoV and FD preview execute");
            Check((bool)previewType.GetMethod("SameAs", I)!.Invoke(first, new[] { second })!, "repeated preview has no cumulative multiplier");
            Check(item.m_shared.m_food == 10f && item.m_shared.m_foodRegen == 1f && item.m_customData.Count == 1 && player.m_customData.Count == 0 && player.GetFoods().Count == 0, "hover leaves shared data, metadata, saved state and foods unchanged");
            // Compare the bridge to the actual existing text-prefix chain.
            object?[] originalArgs = { item };
            pov.GetType("PassiveTree.FoodMasteries+Tooltip", true)!.GetMethod("Prefix", S)!.Invoke(null, originalArgs);
            object?[] fdArgs = { originalArgs[0], null };
            mod.GetType("FineDining.DietTooltipPatch", true)!.GetMethod("Prefix", S)!.Invoke(null, fdArgs);
            Check(health == ((ItemData)fdArgs[0]!).m_shared.m_food, "card matches existing text pipeline with global, cook and nourishment factors");
            // Substitute only PoV's native-dependent model production; execute
            // our actual installed postfix on that original private method.
            renderedModel = model;
            bridge.GetField("_bindings", S)!.SetValue(null, bindings);
            fixture.Patch(build, transpiler: new HarmonyMethod(typeof(Program), nameof(ModelBody)));
            build.Invoke(null, new object[] { item });
            Check(Value(main, "$item_food_health") == health.ToString("0.#"), "installed original-Mono postfix receives and amends the private result");
            item.m_customData["sighsorry.FineDining.Spoiled"] = "1";
            object stale = previewBuilder.Invoke(null, new object[] { bindings, item })!;
            string staleLine = (string)previewType.GetField("Freshness", I)!.GetValue(stale)!;
            Check(!string.IsNullOrEmpty(staleLine) && (float)previewType.GetField("Health", I)!.GetValue(stale)! < health, "minimum freshness updates food values and status");
            object?[] timerArgs = { item, "base" };
            MethodInfo timer = mod.GetType("FineDining.ItemDataSpoilageTooltipPatch", true)!.GetMethod("Postfix", S)!;
            timer.Invoke(null, timerArgs); timer.Invoke(null, timerArgs);
            Check((string)timerArgs[1]! == "base\n" + staleLine, "card and ordinary tooltip reuse the same nonduplicated freshness wording");
            isSnack = true;
            object snack = previewBuilder.Invoke(null, new object[] { bindings, item })!;
            Check((float)previewType.GetField("Regen", I)!.GetValue(snack)! == 0f, "real snack preview excludes regeneration");
            AccessTools.Field(typeof(Player), "m_localPlayer").SetValue(null, null);
            object noPlayer = previewBuilder.Invoke(null, new object[] { bindings, item })!;
            Check(!(bool)previewType.GetField("HasFood", I)!.GetValue(noPlayer)!, "no local player has no diet preview");
            bridge.GetMethod("Shutdown", S)!.Invoke(null, null);
            Check(bridge.GetField("_bindings", S)!.GetValue(null) == null && bridge.GetField("_lastItem", S)!.GetValue(null) == null, "shutdown clears binding and hover references");
            System.Console.WriteLine("PASS " + checks + " original Mono tooltip checks; no Unity rendering or multiplayer.");
            return 0;
        }
        finally { patch.UnpatchSelf(); fixture.UnpatchSelf(); }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); checks++; System.Console.WriteLine("PASS " + message); }
}
