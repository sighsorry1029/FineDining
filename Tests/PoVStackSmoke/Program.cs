using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;
using ItemData = ItemDrop.ItemData;

internal static class Entry
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            foreach (string root in new[] { args[2], args[3], Path.GetDirectoryName(args[0])!, args[4] }) {
                string path = Path.Combine(root, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { Program.Run(args); return 0; } catch (Exception e) { System.Console.WriteLine(e); return 1; }
    }

}

internal static class Program
{
    private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Expiry = "sighsorry.FineDining.ExpiryWorldTicks", Lifetime = "sighsorry.FineDining.AssignedLifetimeTicks", Spoiled = "sighsorry.FineDining.Spoiled";
    private const long Now = 1000000000L;
    private static int checks;
    private static bool authority = true, loading, clock = true, paused, throwChanged;
    private static readonly HashSet<Inventory> Owned = new();
    private static Action? insideFind;
    private static ItemData? cancelled;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeInt();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeHash(IntPtr value);
    private static readonly NativeInt Offset = () => 0;
    private static readonly NativeHash Hash = _ => 0;
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void mono_add_internal_call(string name, IntPtr method);
    private static bool Equality(UnityEngine.Object lhs, UnityEngine.Object rhs, ref bool __result) { __result = ReferenceEquals(lhs, rhs); return false; }
    private static bool Authority(Inventory __0, ref bool __result) { __result = authority && Owned.Contains(__0); return false; }
    private static bool Loading(ref bool __result) { __result = loading; return false; }
    private static bool Clock(ref long __0, ref bool __result) { __0 = Now; __result = clock; return false; }
    private static bool Paused(ref bool __result) { __result = paused; return false; }
    private static bool False(ref bool __result) { __result = false; return false; }
    private static bool Skip() => false;
    private static bool Changed(Inventory __instance)
    {
        if (throwChanged) { throwChanged = false; throw new InvalidOperationException("transfer observer failed"); }
        __instance.m_onChanged?.Invoke(); return false;
    }
    private static void InsideFind() { Action? action = insideFind; insideFind = null; action?.Invoke(); }
    private static bool Cancel(ItemData __0, ref bool __result) { if (!ReferenceEquals(__0, cancelled)) return true; __result = false; return false; }
    private static IEnumerable<CodeInstruction> NullBody() { yield return new CodeInstruction(OpCodes.Ldnull); yield return new CodeInstruction(OpCodes.Ret); }
    private static bool Movable(Player __0, ref List<ItemData> __result) { __result = new List<ItemData>(__0.GetInventory().GetAllItems()); return false; }
    private static void Log(object sender, BepInEx.Logging.LogEventArgs args) => System.Console.WriteLine(args.Data);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string[] args)
    {
        Assembly bep = Assembly.LoadFrom(Path.Combine(args[3], "BepInEx.dll"));
        bep.GetType("BepInEx.Paths", true)!.GetMethod("SetExecutablePath", S)!.Invoke(null, new object[] {
            Path.Combine(Path.GetTempPath(), "fd-stack-smoke", "valheim.exe"), null!, args[2], new[] { args[2], args[3] } });
        Type threading = bep.GetType("BepInEx.ThreadingHelper", true)!;
        object queue = FormatterServices.GetUninitializedObject(threading);
        threading.GetField("_invokeLock", I)!.SetValue(queue, new object());
        threading.GetField("<Instance>k__BackingField", S)!.SetValue(null, queue);
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
        mono_add_internal_call("UnityEngine.Animator::StringToHash_Injected", Marshal.GetFunctionPointerForDelegate(Hash));
        var fixture = new Harmony("fd.stack.fixture");
        var povPatches = new Harmony("treextr.pathofvalheiman");
        var fd = new Harmony("sighsorry.FineDining");
        Assembly mod = Assembly.LoadFrom(args[0]), pov = Assembly.LoadFrom(args[1]);
        Type bridge = mod.GetType("FineDining.PathOfValheimanStackCompatibility", true)!;
        void Patch(MethodBase method, string prefix) => fixture.Patch(method, prefix: new HarmonyMethod(typeof(Program), prefix));
        void ModPatch(string type, string method, string prefix) => Patch(AccessTools.Method(mod.GetType("FineDining." + type, true), method), prefix);
        try
        {
            var log = new BepInEx.Logging.ManualLogSource("stack-smoke"); log.LogEvent += Log;
            mod.GetType("FineDining.FineDiningPlugin", true)!.GetField("Log", S)!.SetValue(null, log);
            Check(!mod.GetReferencedAssemblies().Any(a => a.Name == "PathOfValheiman"), "optional PoV dependency");
            Patch(AccessTools.Method(typeof(UnityEngine.Object), "CompareBaseObjects"), nameof(Equality));
            fixture.Patch(AccessTools.PropertyGetter(typeof(Localization), "instance"), transpiler: new HarmonyMethod(typeof(Program), nameof(NullBody)));
            foreach (var name in new[] { "Log", "LogWarning" })
                foreach (var m in typeof(ZLog).GetMethods(S).Where(m => m.Name == name)) Patch(m, nameof(Skip));
            Patch(AccessTools.Method(typeof(Inventory), "Changed"), nameof(Changed));
            Patch(AccessTools.Method(typeof(Inventory), "FindFreeStackItem"), nameof(InsideFind));
            ModPatch("DecayRuntime", "IsAuthoritativeInventory", nameof(Authority));
            ModPatch("DecayRuntime", "IsContainerLoading", nameof(Loading));
            ModPatch("DecayRuntime", "ResolveInventoryPausedState", nameof(Paused));
            ModPatch("SpoilageClock", "TryGetWorldTicks", nameof(Clock));
            Patch(AccessTools.PropertyGetter(mod.GetType("FineDining.SpoilagePolicy", true), "IsEnabled"), nameof(False));
            Patch(AccessTools.Method(pov.GetType("PassiveTree.Gear.EquipRows", true), "Owns"), nameof(False));
            Type rules = pov.GetType("PassiveTree.StackRules", true)!;
            foreach (string name in new[] { "Inventory_FindFreeStackItem", "Inventory_FindFreeStackSpace", "StackContext", "MatchMetadata", "MetadataSpace", "ItemData_IsSameType" })
                povPatches.CreateClassProcessor(rules.GetNestedType(name, BindingFlags.NonPublic)).Patch();
            foreach (string name in new[] { "InventoryAddItemSpoilagePatch", "InventoryAddItemAtPositionSpoilagePatch", "InventoryAddItemAmountAtPositionSpoilagePatch" })
                fd.CreateClassProcessor(mod.GetType("FineDining." + name, true)).Patch();
            var same = (Func<ItemData, ItemData, bool>)Delegate.CreateDelegate(typeof(Func<ItemData, ItemData, bool>), rules.GetMethod("SameData", S)!);
            Inventory inv = Inventory(1,1); ItemData target = Food(45, Now + 900), source = Food(5, Now + 400); AddDirect(inv, target);
            Check(!inv.CanAddItem(source) && !inv.AddItem(source) && target.m_stack == 45, "original PoV reproduces full-inventory rejection");
            bridge.GetMethod("Install", S)!.Invoke(null, new object[] { pov });
            Check((bool)bridge.GetField("_active", S)!.GetValue(null)!, "all original stack and sort patches install in game Mono");
            Check(!same(target,source), "unscoped callers keep original metadata comparison");
            Check(inv.CanAddItem(source) && source.m_customData[Expiry] == (Now+400).ToString(), "capacity preflight accepts without changing timers");
            Check(inv.AddItem(source) && target.m_stack == 50 && target.m_customData[Expiry] == (Now+400).ToString(), "pickup composes earlier expiry");

            inv = Inventory(1,1); target = Food(48, Now+900); source = Food(5,Now+400); AddDirect(inv,target);
            var positional = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemData),typeof(int),typeof(int),typeof(int),typeof(bool) });
            bool result = (bool)positional.Invoke(inv,new object[] {source,5,0,0,false})!;
            Check(!result && target.m_stack == 50 && source.m_stack == 3 && target.m_customData[Expiry] == (Now+400).ToString(), "partial drag keeps total quantity and earliest expiry");
            Check(source.m_customData[Expiry] == (Now+400).ToString(), "partial transfer keeps source remainder metadata");

            inv = Inventory(1,1); target = Food(40,Now+900); source = Food(5,Now+400); target.m_worldLevel=1; source.m_worldLevel=2; AddDirect(inv,target);
            Check(inv.AddItem(source,new Vector2i(0,0)) && target.m_stack==45 && target.m_customData[Expiry]==(Now+400).ToString(), "PoV loose-world-level merge is tracked in positional overload");
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); AddDirect(inv,target);
            var gui=(InventoryGui)FormatterServices.GetUninitializedObject(typeof(InventoryGui));
            var grid=(InventoryGrid)FormatterServices.GetUninitializedObject(typeof(InventoryGrid));
            AccessTools.Field(typeof(InventoryGrid),"m_inventory").SetValue(grid,inv); gui.m_playerGrid=grid;
            AccessTools.Field(typeof(InventoryGui),"m_dragItem").SetValue(gui,source);
            Check((bool)AccessTools.Method(typeof(InventoryGui),"CanDropDragOntoItem").Invoke(gui,new object[]{target})!, "original drag preview accepts timed foods");
            Check(!same(target,source), "preview scope is cleaned up");

            foreach (string mismatch in new[] { "foreign", "variant", "invalid-clock", "invalid-lifetime", "invalid-marker", "placed-anchor" })
            {
                inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); AddDirect(inv,target);
                switch(mismatch) {
                    case "foreign": source.m_customData["pt.food"]="distinct"; break;
                    case "variant": source.m_variant=1; break;
                    case "invalid-clock": source.m_customData[Expiry]="future:400"; break;
                    case "invalid-lifetime": source.m_customData[Lifetime]="bad"; break;
                    case "invalid-marker": source.m_customData[Spoiled]="2"; break;
                    case "placed-anchor": source.m_customData["sighsorry.FineDining.PlacedWorldTicks"]="123"; break;
                }
                Check(!inv.CanAddItem(source) && !inv.AddItem(source) && target.m_stack==45 && source.m_stack==5, mismatch + " stays protected");
            }
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); AddDirect(inv,target);
            source.m_customData.Clear(); source.m_customData[Spoiled]="1";
            Check(inv.AddItem(source) && target.m_customData[Spoiled]=="1" && !target.m_customData.ContainsKey(Expiry) && !target.m_customData.ContainsKey(Lifetime), "kept-spoiled state propagates and clears timer/lifetime");
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,-300); AddDirect(inv,target);
            Check(inv.AddItem(source) && target.m_customData[Expiry]==(Now+300).ToString(), "paused source converts against shared world clock");
            paused=true; inv=Inventory(1,1); target=Food(45,-800); source=Food(5,Now+300); AddDirect(inv,target);
            Check(inv.AddItem(source) && target.m_customData[Expiry]=="-300", "paused destination keeps shorter remaining duration"); paused=false;
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); source.m_customData.Clear(); AddDirect(inv,target);
            Check(inv.AddItem(source) && target.m_customData[Expiry]==(Now+900).ToString(), "timerless source does not reset tracked target while spoilage Off");
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); target.m_customData.Clear(); AddDirect(inv,target);
            Check(inv.AddItem(source) && target.m_customData[Expiry]==(Now+400).ToString(), "timerless destination inherits source clock");
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); source.m_customData[Lifetime]="2000"; AddDirect(inv,target);
            Check(inv.AddItem(source) && target.m_customData[Lifetime]=="2000", "assigned lifetime uses larger basis");
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+900); target.m_customData[Expiry]=source.m_customData[Expiry]="future:equal"; AddDirect(inv,target);
            Check(inv.AddItem(source) && target.m_stack==50 && target.m_customData[Expiry]=="future:equal", "identical unknown payload retains native behavior");
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); target.m_customData["pt.work.test"]="a"; source.m_customData["pt.work.test"]="b"; AddDirect(inv,target);
            Check(inv.AddItem(source) && target.m_stack==50, "PoV bookkeeping exclusions retain native behavior");

            foreach(string denied in new[]{"authority","loading","clock"}) {
                inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); AddDirect(inv,target);
                authority=denied!="authority"; loading=denied=="loading"; clock=denied!="clock";
                Check(!inv.CanAddItem(source) && !inv.AddItem(source) && target.m_stack==45, "no added permission without "+denied);
                authority=true; loading=false; clock=true;
            }
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); AddDirect(inv,target); throwChanged=true;
            try { inv.AddItem(source); throw new Exception("expected observer exception"); }
            catch(InvalidOperationException e) { Check(e.Message=="transfer observer failed", "original exception propagates"); }
            Check(target.m_stack==50 && target.m_customData[Expiry]==(Now+400).ToString() && !same(target,Food(1,Now+500)), "failed transfer completes metadata and releases scope");

            fixture.Patch(AccessTools.Method(typeof(Inventory),"AddItem",new[]{typeof(ItemData)}),prefix:new HarmonyMethod(typeof(Program),nameof(Cancel)){priority=900});
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); AddDirect(inv,target); cancelled=Food(1,Now+200);
            insideFind=()=> { Check(!inv.AddItem(cancelled),"nested transfer cancelled"); Check(same(target,source),"outer permission restored after nested cancellation"); };
            Check(inv.AddItem(source),"outer transfer completes after nested cancellation"); cancelled=null;

            Type quick=pov.GetType("PassiveTree.QuickStack",true)!;
            Patch(AccessTools.Method(quick,"Movable"),nameof(Movable));
            Patch(AccessTools.Method(quick,"ForeignCell"),nameof(False)); Patch(AccessTools.Method(quick,"Locked"),nameof(False));
            MethodInfo sort=AccessTools.Method(quick,"Sort");
            inv=Inventory(4,4); target=Food(5,Now+900); source=Food(7,Now+400); AddDirect(inv,target,0,1); AddDirect(inv,source,1,1);
            Player player=(Player)FormatterServices.GetUninitializedObject(typeof(Player)); AccessTools.Field(typeof(Humanoid),"m_inventory").SetValue(player,inv);
            sort.Invoke(null,new object[]{player});
            Check(inv.GetAllItems().Count==1 && target.m_stack==12 && source.m_stack==0 && target.m_customData[Expiry]==(Now+400).ToString(), "original PoV Sort merges quantity and earliest expiry atomically");
            inv.GetAllItems().Clear(); target=Food(5,Now+900); source=Food(7,Now+400); source.m_customData["pt.food"]="distinct"; AddDirect(inv,target,0,1); AddDirect(inv,source,1,1);
            sort.Invoke(null,new object[]{player}); Check(inv.GetAllItems().Count==2 && target.m_stack==5 && source.m_stack==7,"PoV Sort preserves foreign metadata identity");
            inv.GetAllItems().Clear(); target=Food(48,Now+900); source=Food(7,Now+400); AddDirect(inv,target,0,1); AddDirect(inv,source,1,1);
            sort.Invoke(null,new object[]{player});
            Check(target.m_stack==50 && source.m_stack==5 && target.m_customData[Expiry]==(Now+400).ToString() && source.m_customData[Expiry]==(Now+400).ToString(), "partial PoV sort preserves remainder and both clocks");
            inv.GetAllItems().Clear(); target=Food(5,Now+900); source=Food(7,Now+400); source.m_customData.Clear(); source.m_customData[Spoiled]="1"; AddDirect(inv,target,0,1); AddDirect(inv,source,1,1);
            sort.Invoke(null,new object[]{player});
            Check(target.m_stack==12 && target.m_customData[Spoiled]=="1" && !target.m_customData.ContainsKey(Expiry), "PoV sort preserves kept-spoiled state");
            inv.GetAllItems().Clear(); target=Food(5,Now+900); source=Food(7,Now+400); AddDirect(inv,target,0,1); AddDirect(inv,source,1,1); authority=false;
            sort.Invoke(null,new object[]{player}); authority=true;
            Check(inv.GetAllItems().Count==2 && target.m_stack==5 && target.m_customData[Expiry]==(Now+900).ToString(), "unauthorized sort gets no metadata permission");

            var instructions=PatchProcessor.GetOriginalInstructions(sort).ToList();
            int store=instructions.FindIndex(i=>i.opcode==OpCodes.Stfld && i.operand is FieldInfo f && f.Name=="m_stack");
            instructions.RemoveAt(store);
            try { bridge.GetMethod("RewriteSort",S)!.Invoke(null,new object[]{instructions}); throw new Exception("expected drift rejection"); }
            catch(TargetInvocationException e) { Check(e.InnerException is NotSupportedException,"incomplete sort contract rejected before partial rewrite"); }
            Check(!same(target,source),"sort scope does not leak");
            foreach(string owner in new[]{"sighsorry.InventorySlots.pov.stack-metadata","sighsorry.InventoryActions.pov.stack-metadata"}) {
                var other=new Harmony(owner);
                try {
                    other.Patch(rules.GetMethod("SameData",S),prefix:new HarmonyMethod(typeof(Program),nameof(False)));
                    inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); AddDirect(inv,target);
                    Check(!inv.AddItem(source) && target.m_stack==45,owner+" rejection retains priority");
                } finally { other.UnpatchSelf(); }
            }
            bridge.GetMethod("Shutdown",S)!.Invoke(null,null);
            Check(!(bool)bridge.GetField("_active",S)!.GetValue(null)!,"shutdown clears active state");
            inv=Inventory(1,1); target=Food(45,Now+900); source=Food(5,Now+400); AddDirect(inv,target);
            Check(!inv.AddItem(source),"shutdown restores original PoV restrictions");
            povPatches.UnpatchSelf();
            bridge.GetMethod("Install",S)!.Invoke(null,new object[]{pov});
            Check(!(bool)bridge.GetField("_active",S)!.GetValue(null)!,"incomplete PoV patch installation fails closed");
            Check(inv.AddItem(source) && target.m_stack==50,"failed optional adapter leaves standalone FineDining gameplay usable");
            System.Console.WriteLine("PASS "+checks+" original Mono stack checks. Authority/time/UI/notification fixtures; no live game/server run.");
        }
        finally { bridge.GetMethod("Shutdown",S)!.Invoke(null,null); fd.UnpatchSelf(); povPatches.UnpatchSelf(); fixture.UnpatchSelf(); }
    }
    private static Inventory Inventory(int width,int height) { var inv=new Inventory("test",null!,width,height); Owned.Add(inv); return inv; }
    private static ItemData Food(int amount,long expiry) => new() { m_shared=new ItemData.SharedData { m_name="$item_raspberry",m_maxStackSize=50,m_maxQuality=1 },m_stack=amount,m_customData=new Dictionary<string,string>{{Expiry,expiry.ToString()},{Lifetime,"1000"}} };
    private static void AddDirect(Inventory inv,ItemData item,int x=0,int y=0) { item.m_gridPos=new Vector2i(x,y); inv.GetAllItems().Add(item); }
    private static void Check(bool pass,string text) { if(!pass) throw new Exception("FAIL: "+text); checks++; System.Console.WriteLine("PASS: "+text); }
}
