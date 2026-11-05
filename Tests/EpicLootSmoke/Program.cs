using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;

internal static class Entry
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            foreach (string root in new[] { args[1], args[2], Path.GetDirectoryName(args[0])!, Path.GetDirectoryName(args[3])! }) {
                string path = Path.Combine(root, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { Checks.Prepare(); Checks.Run(args); return 0; }
        catch (Exception e) { System.Console.WriteLine(e); return 1; }
    }
}

internal static class Checks
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int count, calls;
    private static float effect;
    private static bool throws;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeHash(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeInt();
    private static readonly NativeHash Hash = _ => 0;
    private static readonly NativeInt Offset = () => 0;
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr function);

    private static void Check(bool value, string reason)
    {
        if (!value) throw new Exception(reason);
        System.Console.WriteLine("PASS " + reason); count++;
    }
    private static object? Call(Type type, string method, params object[] args) => type.GetMethod(method, All)!.Invoke(null, args);
    private static bool Effect(Player __0, string __1, float __2, ItemDrop.ItemData __3, ref float __result)
    {
        calls++;
        if (throws) throw new InvalidOperationException("test gear lookup failure");
        if (!ReferenceEquals(__0, Player.m_localPlayer) || __2 != 1f || __3 != null)
            throw new Exception("Incorrect EpicLoot API arguments");
        __result = effect; return false;
    }
    private static void ObserveHelper() { }

    private static Assembly FineFixture(string path)
    {
        using var asm = AssemblyDefinition.ReadAssembly(path);
        var startup = asm.MainModule.GetType("FineDining.FineDiningPlugin").Methods.Single(m => m.Name == ".cctor");
        startup.Body.Instructions.Clear(); startup.Body.Variables.Clear(); startup.Body.ExceptionHandlers.Clear();
        startup.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
        using var bytes = new MemoryStream(); asm.Write(bytes);
        return Assembly.Load(bytes.ToArray());
    }

    internal static void Prepare()
    {
        mono_add_internal_call("UnityEngine.Animator::StringToHash_Injected", Marshal.GetFunctionPointerForDelegate(Hash));
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string[] args)
    {
        Assembly fine = FineFixture(args[0]), epic = Assembly.LoadFrom(args[3]), pov = Assembly.LoadFrom(args[4]);
        fine.GetType("FineDining.FineDiningPlugin")!.GetField("Log", All)!.SetValue(null, new ManualLogSource("health-smoke"));
        pov.GetType("PoVPlus.Compat.Plugin")!.GetField("Log", All)!.SetValue(null, new ManualLogSource("compat-smoke"));
        Type bridge = fine.GetType("FineDining.EpicLootHealthCompatibility", true)!;
        Type regen = epic.GetType("EpicLoot.MagicItemEffects.ModifyPlayerRegen+SEMan_ModifyRegen_Patches", true)!;
        MethodInfo helper = regen.GetMethod("AddHealthTicks", All)!;
        MethodInfo getter = epic.GetType("EpicLoot.PlayerExtensions", true)!.GetMethod("GetTotalActiveMagicEffectValue", All)!;
        MethodInfo transpiler = regen.GetMethod("ModifyHealthRegen_Transpiler", All)!;
        MethodInfo vanilla = AccessTools.DeclaredMethod(typeof(Player), "UpdateFood", new[] { typeof(float), typeof(bool) });
        MethodInfo food = fine.GetType("FineDining.PlayerFoodLogic", true)!.GetMethod("UpdateFood", All)!;
        MethodInfo native = bridge.GetMethod("Add", All)!;
        var add = (Func<Player, float, float>)Delegate.CreateDelegate(typeof(Func<Player, float, float>), native);
        var original = (Func<float, float>)Delegate.CreateDelegate(typeof(Func<float, float>), helper);
        var owner = new Harmony("randyknapp.mods.epicloot");
        var fixture = new Harmony("FineDining.EpicLootSmoke.fixture");
        var compatOwner = new Harmony("povplusplus.healthregencompat");
        Player player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
        Player other = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
        Player.m_localPlayer = player;
        try
        {
            Check(add(other, 6f) == 6f, "non-local player preserves normal regeneration");
            Call(bridge, "Bind", epic);
            Check(add(player, 6f) == 6f && calls == 0, "missing Epic additive patch does not enable its effect");
            fixture.Patch(getter, prefix: new HarmonyMethod(typeof(Checks), nameof(Effect)));
            owner.Patch(vanilla, transpiler: new HarmonyMethod(transpiler));
            effect = 4f;
            Check(add(player, 6f) * 1.5f == 15f && calls == 1, "fixed gear effect is applied once before percentage scaling");
            Check(add(player, 6f) == original(6f), "public API bridge matches the original private helper in original Mono");
            effect = -2f;
            Check(add(player, 6f) == 4f, "changed and negative effect values retain EpicLoot arithmetic");
            int before = calls;
            Player.m_localPlayer = null;
            Check(add(player, 6f) == 6f && calls == before, "headless/no-local-player path does not query gear");
            Player.m_localPlayer = player;
            owner.UnpatchSelf();
            Check(add(player, 6f) == 6f, "removing Epic additive patch disables only the extra effect");
            owner.Patch(vanilla, transpiler: new HarmonyMethod(transpiler));
            Check(add(player, 6f) == 4f, "late registration is recognized at the next regeneration tick");

            Type health = pov.GetType("PoVPlus.Compat.HealthRegen", true)!;
            foreach (bool compatFirst in new[] { false, true })
            {
                Call(bridge, "Shutdown");
                if (compatFirst) Call(health, "Install");
                Call(bridge, "Bind", epic);
                if (!compatFirst) Call(health, "Install");
                var code = PatchProcessor.GetCurrentInstructions(food);
                Check(code.Count(i => i.Calls(native)) == 1 && code.All(i => !i.Calls(helper)),
                    "real PoVPlus does not double additive effect, compat first=" + compatFirst);
                compatOwner.UnpatchSelf();
            }
            fixture.Patch(helper, prefix: new HarmonyMethod(typeof(Checks), nameof(ObserveHelper)));
            Check(add(player, 6f) == 6f, "modified private-helper behavior safely disables this bridge");
            fixture.Unpatch(helper, typeof(Checks).GetMethod(nameof(ObserveHelper), All));
            Call(bridge, "Shutdown"); Call(bridge, "Bind", epic);
            throws = true;
            Check(add(player, 6f) == 6f, "gear API exception preserves base food regeneration");
            before = calls;
            Check(add(player, 6f) == 6f && before == calls, "failed optional bridge stops repeated callbacks");
            Check(!fine.GetReferencedAssemblies().Any(a => a.Name == "EpicLoot"), "EpicLoot remains an optional dependency");
        }
        finally
        {
            Call(bridge, "Shutdown"); owner.UnpatchSelf(); fixture.UnpatchSelf(); compatOwner.UnpatchSelf();
            Player.m_localPlayer = null;
        }
        System.Console.WriteLine("PASS TOTAL " + count + " managed checks; original game Mono, startup-suppressed FineDining, native initialization stubs, no Unity world.");
    }
}
