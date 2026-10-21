using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using HarmonyLib;

internal static class Program
{
    private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Key = "sighsorry.FineDining.DietState";
    private static Assembly mod = null!;
    private static Type store = null!;
    private static int checks;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeInt();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeHash(IntPtr value);
    private static readonly NativeInt Offset = () => 0;
    private static readonly NativeHash Hash = _ => 0;
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr method);
    private static bool Equality(UnityEngine.Object lhs, UnityEngine.Object rhs, ref bool __result)
    { __result = ReferenceEquals(lhs, rhs); return false; }

    private static int Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("FineDining.dll Managed BepInEx/core");
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            foreach (string root in new[] { args[1], args[2], Path.GetDirectoryName(args[0])! })
            {
                string path = Path.Combine(root, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { return Run(args); }
        catch (Exception e) { System.Console.Error.WriteLine(e); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args)
    {
        Assembly bep = Assembly.LoadFrom(Path.Combine(args[2], "BepInEx.dll"));
        bep.GetType("BepInEx.Paths", true)!.GetMethod("SetExecutablePath", S)!.Invoke(null, new object[] {
            Path.Combine(Path.GetTempPath(), "fd-diet-smoke", "valheim.exe"), null!, args[1], new[] { args[1], args[2] } });
        Type threading = bep.GetType("BepInEx.ThreadingHelper", true)!;
        object queue = FormatterServices.GetUninitializedObject(threading);
        threading.GetField("_invokeLock", I)!.SetValue(queue, new object());
        threading.GetField("<Instance>k__BackingField", S)!.SetValue(null, queue);
        mod = Assembly.LoadFrom(args[0]);
        mod.GetType("FineDining.FineDiningPlugin", true)!.GetField("Log", S)!
            .SetValue(null, new BepInEx.Logging.ManualLogSource("diet-smoke"));
        store = mod.GetType("FineDining.FoodStateStore", true)!;
        Check((string)store.GetField("CustomDataKey", S)!.GetRawConstantValue() == Key, "stable customData key");
        object state = PopulatedState();
        foreach (string culture in new[] { "en-US", "fr-FR", "ko-KR" })
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string text = Serialize(state);
            Check(text.StartsWith("v3:{", StringComparison.Ordinal), "v3 JSON object: " + culture);
            Check(text.Contains("\"Recent\": [") && text.Contains("\"Chef\": [") && text.Contains("\"Active\": ["), "all three lists written");
            object restored = Deserialize(text);
            Equal(state, restored, "full codec roundtrip " + culture);
            // Discard every object/cache and repeat; serialization must not drift floats.
            for (int i = 0; i < 10; i++) restored = Deserialize(Serialize(restored));
            Equal(state, restored, "repeated cold roundtrip " + culture);
        }

        object partial = Deserialize("v3:{\"UnlockedFoodSlots\":5,\"AppliedBaseSlotScale\":0.5399999618530273}");
        Check(List(partial, "Recent").Count == 0 && List(partial, "Chef").Count == 0 && List(partial, "Active").Count == 0,
            "observed scalar-only saves remain readable; missing history is not fabricated");
        Equal(0.5399999618530273f, Get(partial, "AppliedBaseSlotScale"), "existing scalar precision");
        object oldActive = Deserialize("v3:{\"Active\":[{\"Key\":\"Raspberry\",\"AppliedScale\":0.375}],\"Unknown\":{\"Nested\":[1,2]}}");
        Check(!(bool)Get(List(oldActive, "Active")[0]!, "HasEffectBreakdown"), "optional breakdown remains absent");
        Equal(0.375f, Get(List(oldActive, "Active")[0]!, "AppliedScale"), "old active scale retained");
        object empty = New("PlayerFoodStateData");
        Equal(empty, Deserialize(Serialize(empty)), "explicit empty arrays and default fields");
        foreach (string invalid in new[] { "v3:", "v3:  ", "v3:null", "v3:[]", "v3:{\"Recent\":[", "v3:{\"Recent\":42}", "v3:{\"Recent\":[{\"Stack\":\"invalid\"}]}" })
        {
            bool rejected = false;
            try { Deserialize(invalid); }
            catch (TargetInvocationException) { rejected = true; }
            Check(rejected, "reject malformed/wrong-shaped payload " + invalid);
        }
        // This artifact is synthetic, never a user's character file.
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diet-state.json"), Serialize(state).Substring(3));
        Lifecycle();
        System.Console.WriteLine($"PASS {checks} diet persistence checks on original game Mono.");
        System.Console.WriteLine("Isolated serializer and save/cache/death-hook fixtures; no live Unity world, Player.Save/Load, transport or server session.");
        return 0;
    }

    private static void Lifecycle()
    {
        // Engine identity only is substituted. Original game DLLs and all FD
        // serialization, normalization, cache and death-hook bodies stay intact.
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
        mono_add_internal_call("UnityEngine.Animator::StringToHash_Injected", Marshal.GetFunctionPointerForDelegate(Hash));
        var fixture = new Harmony("fd.diet.persistence.fixture");
        fixture.Patch(AccessTools.Method(typeof(UnityEngine.Object), "CompareBaseObjects"), prefix: new HarmonyMethod(typeof(Program), nameof(Equality)));
        try
        {
            var config = new BepInEx.Configuration.ConfigFile(Path.Combine(Path.GetTempPath(), "fd-diet-smoke-unused.cfg"), false);
            config.SaveOnConfigSet = false;
            Call(mod.GetType("FineDining.DietConfig", true)!, "Initialize", config,
                mod.GetType("FineDining.FineDiningPlugin", true)!.GetField("ConfigSync", S)!.GetValue(null)!);
            object state = PopulatedState();
            var player = NewPlayer(new Dictionary<string, string> { ["other.mod"] = "unchanged" }, true);
            Call(store, "SaveState", player, state);
            object expected = Deserialize(player.m_customData[Key]);
            Check(List(expected, "Recent").Count == 2 && List(expected, "Chef").Count == 2 && List(expected, "Active").Count == 2, "SaveState writes populated lists");
            Call(store, "Invalidate", player);
            Call(store, "Reset");
            var reconnect = NewPlayer(Transfer(player.m_customData), true);
            object loaded = Call(store, "GetState", reconnect);
            Equal(expected, loaded, "new Player from saved customData");
            Check(reconnect.m_customData["other.mod"] == "unchanged", "unrelated customData preserved");
            Type recent = mod.GetType("FineDining.RecentHistoryService", true)!;
            Check((int)Call(recent, "GetNextStack", loaded, "Raspberry", false) == 4, "next consumption continues saved stack");
            Call(recent, "RegisterConsumption", loaded, "Raspberry", false);
            Call(store, "SaveState", reconnect, loaded);
            Call(store, "Invalidate", reconnect);
            var next = NewPlayer(Transfer(reconnect.m_customData), true);
            loaded = Call(store, "GetState", next);
            Check((int)Call(recent, "GetNextStack", loaded, "Raspberry", false) == 5, "subsequent save continues incremented stack");
            object beforeDeath = Deserialize(Serialize(loaded));
            next.GetFoods().Clear(); // Vanilla clears active foods before FD's death postfix.
            Call(mod.GetType("FineDining.DietPlayerFoodPatches", true)!, "OnDeathPostfix", next);
            Call(store, "Invalidate", next);
            Call(store, "Reset");
            var respawn = NewPlayer(Transfer(next.m_customData), false);
            object afterDeath = Call(store, "GetState", respawn);
            Equal(Get(beforeDeath, "Recent"), Get(afterDeath, "Recent"), "death preserves recent order/counts");
            Equal(Get(beforeDeath, "Chef"), Get(afterDeath, "Chef"), "death preserves Chef choices/multipliers");
            Check(List(afterDeath, "Active").Count == 0, "death still removes only inactive food effects");
            var missingLists = NewPlayer(new Dictionary<string, string> { [Key] = "v3:{\"Recent\":null,\"Chef\":null,\"Active\":null}" }, false);
            object normalized = Call(store, "GetState", missingLists);
            Check(List(normalized, "Recent").Count == 0 && List(normalized, "Chef").Count == 0 && List(normalized, "Active").Count == 0, "null lists retain existing normalization");
        }
        finally { Call(store, "Reset"); fixture.UnpatchSelf(); }
    }

    private static Player NewPlayer(Dictionary<string, string> data, bool active)
    {
        var player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
        AccessTools.Field(typeof(Player), "m_customData").SetValue(player, data);
        AccessTools.Field(typeof(Player), "m_foods").SetValue(player, active
            ? new List<Player.Food> { new Player.Food { m_name = "Raspberry" }, new Player.Food { m_name = "CookedMeat" } }
            : new List<Player.Food>());
        return player;
    }

    private static Dictionary<string, string> Transfer(Dictionary<string, string> data)
    {
        // Native ZPackage string/dictionary layout, not a simulated full .fch.
        var package = new ZPackage();
        package.Write(data.Count);
        foreach (var pair in data) { package.Write(pair.Key); package.Write(pair.Value); }
        var reader = new ZPackage(package.GetArray());
        var restored = new Dictionary<string, string>();
        int count = reader.ReadInt();
        for (int i = 0; i < count; i++) restored.Add(reader.ReadString(), reader.ReadString());
        return restored;
    }

    private static object PopulatedState()
    {
        object state = New("PlayerFoodStateData");
        Set(state, "UnlockedFoodSlots", 5); Set(state, "AppliedBaseSlotScale", 0.5399999618530273f);
        Add(state, "Recent", "HistoryEntryData", "Raspberry", "Stack", 3);
        Add(state, "Recent", "HistoryEntryData", "식재료_日本\"\\\n", "Stack", 17);
        Add(state, "Chef", "ChefEntryData", "CookedMeat", "Multiplier", 1.23456705f);
        Add(state, "Chef", "ChefEntryData", "FishCooked", "Multiplier", 1.375f);
        object active = Add(state, "Active", "ActiveFoodData", "Raspberry", "AppliedScale", 0.5399999618530273f);
        Set(active, "HasEffectBreakdown", true); Set(active, "ChefMultiplier", 1.23456705f);
        Set(active, "FreshnessScale", 0.765432119f); Set(active, "DiminishingScale", 0.456789017f);
        Add(state, "Active", "ActiveFoodData", "CookedMeat", "AppliedScale", 0f);
        return state;
    }

    private static object New(string name) => Activator.CreateInstance(mod.GetType("FineDining." + name, true)!, true)!;
    private static object Call(Type type, string method, params object[] args) => type.GetMethod(method, S)!.Invoke(null, args)!;
    private static string Serialize(object state) => (string)Call(store, "SerializeState", state);
    private static object Deserialize(string text) => Call(store, "DeserializeState", text);
    private static object Get(object value, string field) => value.GetType().GetField(field, I)!.GetValue(value)!;
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, I)!.SetValue(value, data);
    private static IList List(object value, string field) => (IList)Get(value, field);
    private static object Add(object state, string list, string type, string key, string field, object value)
    { object entry = New(type); Set(entry, "Key", key); Set(entry, field, value); List(state, list).Add(entry); return entry; }
    private static void Equal(object expected, object actual, string path)
    {
        if (expected is float f)
        { Check(actual is float a && BitConverter.ToInt32(BitConverter.GetBytes(f), 0) == BitConverter.ToInt32(BitConverter.GetBytes(a), 0), path + " exact Single bits"); }
        else if (expected is IList list)
        {
            Check(actual is IList && list.Count == ((IList)actual).Count, path + " count");
            for (int i = 0; i < list.Count; i++) Equal(list[i]!, ((IList)actual)[i]!, path + "[" + i + "]");
        }
        else if (expected.GetType().Assembly == mod)
        { foreach (FieldInfo field in expected.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) Equal(field.GetValue(expected)!, field.GetValue(actual)!, path + "." + field.Name); }
        else Check(expected.Equals(actual), path);
    }
    private static void Check(bool passed, string message)
    { if (!passed) throw new Exception("FAIL " + message); checks++; }
}
