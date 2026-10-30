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
using ItemData = ItemDrop.ItemData;

internal static class Entry
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            foreach (string root in new[] { args[1], args[2], Path.GetDirectoryName(args[0])!, Path.GetDirectoryName(args[3])!, Path.GetDirectoryName(args[4])! }) {
                string path = Path.Combine(root, new AssemblyName(e.Name).Name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { Checks.Run(args); return 0; } catch (Exception e) { System.Console.WriteLine(e); return 1; }
    }
}

internal static class Checks
{
    private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Key = "sighsorry.FineDining.ExpiryWorldTicks";
    private static Assembly mod = null!;
    private static Type timeline = null!;
    private static object context = null!;
    private static bool ready = true, paused;
    private static int count;
    private static readonly HashSet<Inventory> owned = new();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeInt();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeHash(IntPtr value);
    private static readonly NativeInt Offset = () => 0;
    private static readonly NativeHash Hash = _ => 0;
    [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mono_add_internal_call(string name, IntPtr method);
    private static bool Equality(UnityEngine.Object lhs, UnityEngine.Object rhs, ref bool __result)
    { __result = ReferenceEquals(lhs, rhs); return false; }
    private static bool Context(object[] __args, ref bool __result)
    { __args[0] = context; __result = ready; return false; }
    private static bool Time(ref long __0, ref bool __result)
    { __0 = C; __result = ready; return false; }
    private static bool Authority(Inventory __0, ref bool __result)
    { __result = owned.Contains(__0); return false; }
    private static bool False(ref bool __result) { __result = false; return false; }
    private static bool Pause(ref bool __result) { __result = paused; return false; }
    private static bool Skip() => false;
    private static IEnumerable<CodeInstruction> NullBody() { yield return new(OpCodes.Ldnull); yield return new(OpCodes.Ret); }
    private static long C => (long)timeline.GetField("ClockTicks", I)!.GetValue(context)!;
    private static Type T(string name) => mod.GetType("FineDining." + name, true)!;
    private static object? Call(string type, string method, params object?[] args) => T(type).GetMethod(method, S)!.Invoke(null, args);
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); count++; }
    private static string Format(long value) => (string)Call("SpoilageClock", "FormatClockValue", value)!;
    private static long Remaining(ItemData item, out bool isPaused)
    {
        object[] args = { item, C, 0L, false };
        Check((bool)Call("SpoilageClock", "TryGetSpoilageClock", args)!, "clock readable");
        isPaused = (bool)args[3]; return (long)args[2];
    }
    private static ItemData Food(int amount, string? value) => new() {
        m_shared = new ItemData.SharedData { m_name = "$item_raspberry", m_maxStackSize = 50, m_maxQuality = 1 },
        m_stack = amount, m_customData = value == null ? new() : new() { [Key] = value }
    };
    private static Inventory Inventory() { var inv = new Inventory("winter-test", null!, 2, 2); owned.Add(inv); return inv; }
    private static void Advance(long world, bool winter)
    {
        Check((bool)timeline.GetMethod("Advance", I)!.Invoke(context, new object[] { world })!, "advance");
        timeline.GetField("Frozen", I)!.SetValue(context, winter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string[] args)
    {
        Assembly bep = Assembly.LoadFrom(Path.Combine(args[2], "BepInEx.dll"));
        bep.GetType("BepInEx.Paths", true)!.GetMethod("SetExecutablePath", S)!.Invoke(null, new object[] {
            Path.Combine(Path.GetTempPath(), "fd-winter-smoke", "valheim.exe"), null!, args[1], new[] { args[1], args[2] } });
        Type threading = bep.GetType("BepInEx.ThreadingHelper", true)!;
        object queue = FormatterServices.GetUninitializedObject(threading);
        threading.GetField("_invokeLock", I)!.SetValue(queue, new object());
        threading.GetField("<Instance>k__BackingField", S)!.SetValue(null, queue);
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject", Marshal.GetFunctionPointerForDelegate(Offset));
        mono_add_internal_call("UnityEngine.Animator::StringToHash_Injected", Marshal.GetFunctionPointerForDelegate(Hash));
        var fixture = new Harmony("fd.winter.fixture");
        var patches = new Harmony("sighsorry.FineDining");
        mod = Assembly.LoadFrom(args[0]);
        timeline = T("SpoilageTimeline");
        context = timeline.GetConstructor(I, null, new[] { typeof(long), typeof(string), typeof(long) }, null)!
            .Invoke(new object[] { 7L, "11111111111111111111111111111111", 1000L });
        void Patch(MethodBase method, string prefix) => fixture.Patch(method, prefix: new HarmonyMethod(typeof(Checks), prefix));
        void ModPatch(string type, string method, string prefix) => Patch(T(type).GetMethod(method, S)!, prefix);
        try
        {
            T("FineDiningPlugin").GetField("Log", S)!.SetValue(null, new BepInEx.Logging.ManualLogSource("winter-test"));
            Patch(AccessTools.Method(typeof(UnityEngine.Object), "CompareBaseObjects"), nameof(Equality));
            fixture.Patch(AccessTools.PropertyGetter(typeof(Localization), "instance"), transpiler: new HarmonyMethod(typeof(Checks), nameof(NullBody)));
            Patch(AccessTools.Method(typeof(Inventory), "Changed"), nameof(Skip));
            foreach (var name in new[] { "Log", "LogWarning" })
                foreach (var m in typeof(ZLog).GetMethods(S).Where(m => m.Name == name)) Patch(m, nameof(Skip));
            ModPatch("WorldSpoilageClock", "TryGetContext", nameof(Context));
            ModPatch("SpoilageClock", "TryGetWorldTicks", nameof(Time));
            ModPatch("DecayRuntime", "IsAuthoritativeInventory", nameof(Authority));
            ModPatch("DecayRuntime", "IsContainerLoading", nameof(False));
            ModPatch("DecayRuntime", "ResolveInventoryPausedState", nameof(Pause));
            Patch(AccessTools.PropertyGetter(T("SpoilagePolicy"), "IsEnabled"), nameof(False));
            foreach (string name in new[] { "InventoryAddItemSpoilagePatch", "InventoryAddItemAtPositionSpoilagePatch", "InventoryAddItemAmountAtPositionSpoilagePatch" })
                patches.CreateClassProcessor(T(name)).Patch();

            ItemData food = Food(10, Format(C + 4800));
            string before = food.m_customData[Key];
            Advance(1600, true); Advance(8800, false); Advance(9600, false);
            Check(Remaining(food, out bool p) == 3400 && !p, "offline winter spent food life or set item paused");
            Check(food.m_customData[Key] == before, "unloaded item needed rewriting");
            Check(Remaining(food.Clone(), out _) == 3400, "split clone lost its atomic clock");
            ItemData frozen = Food(1, Format(-1200));
            Check(Remaining(frozen, out p) == 1200 && p, "environment preservation changed");

            Inventory inv = Inventory();
            ItemData target = Food(45, Format(C+900)), source = Food(5, Format(C+400));
            inv.GetAllItems().Add(target);
            Check(inv.AddItem(source) && target.m_stack == 50 && Remaining(target, out _) == 400, "v2 vanilla merge failed");
            inv = Inventory(); target = Food(48, Format(C+900)); source = Food(5, Format(-300)); inv.GetAllItems().Add(target);
            MethodInfo positional = AccessTools.Method(typeof(Inventory), "AddItem", new[] { typeof(ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });
            Check(!(bool)positional.Invoke(inv, new object[] { source, 5, 0, 0, false })!, "partial move should report a remainder");
            Check(target.m_stack == 50 && source.m_stack == 3 && Remaining(target, out _) == 300, "partial move lost quantity/shorter life");
            Check(Remaining(source, out p) == 300 && p, "partial move altered its paused source remainder");
            paused = true;
            Check((bool)Call("DecayRuntime", "ApplyEarlierInventoryClock", inv, target, C+100)!, "paused destination merge");
            Check(Remaining(target, out p) == 100 && p, "environment pause was not retained"); paused = false;

            inv = Inventory(); target = Food(45, Format(C+900)); source = Food(5, Format(C+400)); inv.GetAllItems().Add(target);
            ready = false;
            T("WorldSpoilageClock").GetField("<Initialized>k__BackingField", S)!.SetValue(null, true);
            Check(!inv.AddItem(source) && target.m_stack == 45 && source.m_stack == 5, "pending clock merged food destructively");
            Inventory empty = Inventory();
            Check(empty.AddItem(source) && empty.GetAllItems().Single().m_customData[Key] == source.m_customData[Key], "pending clock blocked/lost a non-merging move");
            ItemData loadOne = Food(7, before), loadTwo = Food(9, before);
            Inventory loading = Inventory();
            Check((bool)positional.Invoke(loading, new object[] { loadOne, 7, 0, 0, false })! &&
                  (bool)positional.Invoke(loading, new object[] { loadTwo, 9, 1, 0, false })! &&
                  loading.GetAllItems().Sum(i => i.m_stack) == 16 && loading.GetAllItems().All(i => i.m_customData[Key] == before),
                "multiple unresolved same-food stacks were lost on empty-cell load/clone");
            ready = true;
            source = Food(5, "future:broken");
            Check(!inv.AddItem(source) && target.m_stack == 45, "malformed record was erased in a vanilla merge");
            source = Food(5, before.Replace("11111111111111111111111111111111", "22222222222222222222222222222222"));
            Check(!inv.AddItem(source), "lost history granted a fresh merged timer");

            inv = Inventory();
            ItemData running = Food(1, Format(C+800)), cold = Food(1, Format(-500));
            inv.GetAllItems().Add(running); inv.GetAllItems().Add(cold);
            Player player = (Player)FormatterServices.GetUninitializedObject(typeof(Player));
            AccessTools.Field(typeof(Humanoid), "m_inventory").SetValue(player, inv);
            string originalRunning = running.m_customData[Key], originalCold = cold.m_customData[Key];
            Advance(9800, true);
            object state = Call("DecayRuntime", "BeginPlayerSaveClockSnapshot", player)!;
            Check(Remaining(running, out p) == 600 && !p && Remaining(cold, out p) == 500 && !p, "player snapshot did not resume only environmental preservation");
            string serializedRunning = running.m_customData[Key], serializedCold = cold.m_customData[Key];
            var error = new InvalidOperationException("save failed");
            object? returned = Call("PlayerSaveSpoilageClockPatch", "Finalizer", state, error);
            Check(ReferenceEquals(returned,error) && running.m_customData[Key] == originalRunning && cold.m_customData[Key] == originalCold,
                "save finalizer changed the exception or live metadata");
            Advance(20000, false);
            Check(Remaining(Food(1, serializedRunning), out _) == 600 && Remaining(Food(1, serializedCold), out _) == 500,
                "saved offline inventory aged during winter");
            Advance(20100, false);
            Check(Remaining(Food(1, serializedRunning), out _) == 500, "offline food did not resume in spring");
            timeline.GetField("WorldUid",I)!.SetValue(context,8L);
            Check(Remaining(Food(1, serializedRunning),out _) == 600, "foreign world did not use last saved remaining checkpoint");
            timeline.GetField("WorldUid",I)!.SetValue(context,7L);

            var required = T("SeasonalPreservation").GetMethod("RequiredMethod", S)!;
            foreach (var pair in new[] { (args[3], "Seasonality.SeasonTime", "Seasonality.SeasonalityPlugin", true, 2),
                                        (args[4], "Seasons.SeasonState", "Seasons.SeasonState", false, 3) })
            {
                Assembly provider = Assembly.LoadFrom(pair.Item1);
                MethodInfo getter = (MethodInfo)required.Invoke(null,new object?[]{provider.GetType(pair.Item2,true),"GetCurrentSeason",pair.Item4,null})!;
                Check(Convert.ToInt32(Enum.Parse(getter.ReturnType,"Winter")) == pair.Item5,"actual provider Winter contract");
                Check(required.Invoke(null,new object[]{provider.GetType(pair.Item3,true)!, pair.Item4?"OnSeasonChange":"OnSeasonDayChange",true,typeof(void)}) != null,
                    "actual provider transition contract");
                try { required.Invoke(null,new object[]{provider.GetType(pair.Item2,true)!,"GetCurrentSeason",!pair.Item4,typeof(void)}); throw new Exception("bad contract accepted"); }
                catch(TargetInvocationException e) { Check(e.InnerException is MissingMethodException,"changed provider signature rejected"); }
            }
            Check(!mod.GetReferencedAssemblies().Any(a=>a.Name is "Seasons" or "Seasonality"),"optional providers became hard dependencies");
            if (args.Length > 5) EpicGuardChecks(args[5], patches);
            SnapshotChecks();
            System.Console.WriteLine("PASS " + count + " original Mono winter integration checks. Time/authority/UI fixtures; no live world, native save or network transport.");
        }
        catch (Exception error) { System.Console.WriteLine(error); throw; }
        finally { patches.UnpatchSelf(); fixture.UnpatchSelf(); }
    }

    private static void SnapshotChecks()
    {
        // Real decoder and server-transport checks, with synthetic peer objects.
        // This does not simulate sockets, packet delivery or the Unity lifecycle.
        var net = (ZNet)FormatterServices.GetUninitializedObject(typeof(ZNet));
        var world = (World)FormatterServices.GetUninitializedObject(typeof(World));
        world.m_uid = 7;
        AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, net);
        AccessTools.Field(typeof(ZNet), "m_world").SetValue(null, world);
        AccessTools.Field(typeof(ZNet), "m_isServer").SetValue(null, false);
        FieldInfo status = AccessTools.Field(typeof(ZNet), "m_connectionStatus");
        status.SetValue(status.IsStatic ? null : net, Enum.Parse(status.FieldType, "Connected"));
        var server = (ZRpc)FormatterServices.GetUninitializedObject(typeof(ZRpc));
        var foreign = (ZRpc)FormatterServices.GetUninitializedObject(typeof(ZRpc));
        var peer = (ZNetPeer)FormatterServices.GetUninitializedObject(typeof(ZNetPeer));
        peer.m_rpc = server; peer.m_uid = 99; peer.m_server = true;
        AccessTools.Field(typeof(ZNet), "m_peers").SetValue(net, new List<ZNetPeer> { peer });
        Call("WorldSpoilageClock", "ResetSession");
        string payload = (string)timeline.GetMethod("Serialize", I)!.Invoke(context, null)!;
        const string session = "33333333333333333333333333333333";
        ZPackage Packet(long revision, string data, string id = session) {
            var package = new ZPackage(); package.Write(1); package.Write(id); package.Write(revision); package.Write(data); package.SetPos(0); return package;
        }
        bool Ready() => (bool)T("WorldSpoilageClock").GetField("_ready", S)!.GetValue(null)!;
        long Revision() => (long)T("WorldSpoilageClock").GetField("_revision", S)!.GetValue(null)!;
        Call("WorldSpoilageClock", "ReceiveSnapshot", foreign, Packet(10,payload));
        Check(!Ready(), "non-server transport supplied the authoritative clock");
        Call("WorldSpoilageClock", "ReceiveSnapshot", server, Packet(10,payload));
        Check(Ready() && Revision()==10, "valid server snapshot rejected");
        foreach (long stale in new[] { 10L, 9L, 0L, -1L }) {
            Call("WorldSpoilageClock", "ReceiveSnapshot", server, Packet(stale,payload));
            Check(Revision()==10,"duplicate/old snapshot changed state");
        }
        Call("WorldSpoilageClock", "ReceiveSnapshot", server, Packet(11,payload,"44444444444444444444444444444444"));
        Check(Revision()==10,"previous/different session accepted");
        Call("WorldSpoilageClock", "ReceiveSnapshot", server, Packet(11,payload.Replace("1|7|", "1|8|")));
        Check(Revision()==10,"another world's snapshot accepted");
        Call("WorldSpoilageClock", "ReceiveSnapshot", server, Packet(11,payload.Replace("11111111111111111111111111111111", "22222222222222222222222222222222")));
        Check(Revision()==10,"replacement history accepted mid-session");
        Call("WorldSpoilageClock", "ReceiveSnapshot", server, new ZPackage(new byte[]{1,0}));
        Check(Revision()==10,"truncated snapshot changed state");
        Call("WorldSpoilageClock", "ReceiveSnapshot", server, Packet(11,new string('x',600)));
        Check(Revision()==10,"oversized snapshot changed state");
        Call("WorldSpoilageClock", "ReceiveSnapshot", server, Packet(11,payload));
        Check(Revision()==11,"next confirmed snapshot rejected");
        Call("WorldSpoilageClock", "ResetSession");
        Check(!Ready(),"world exit retained a usable old clock");
        AccessTools.Field(typeof(ZNet), "m_instance").SetValue(null, null);
    }

    private static void EpicGuardChecks(string path, Harmony patches)
    {
        var epicOwner = new Harmony("randyknapp.mods.epicloot");
        var foreignOwner = new Harmony("fd.winter.unexpected-prefix");
        try
        {
            Assembly epic = Assembly.LoadFrom(path);
            MethodInfo target = AccessTools.Method(typeof(ItemDrop), "AutoStackItems");
            Type replacement = epic.GetType("EpicLoot.MagicItemData+ItemDrop_AutoStackItems_Patch", true)!;
            patches.CreateClassProcessor(T("ItemDropAutoStackSpoilagePatch")).Patch();
            patches.CreateClassProcessor(T("ItemDropClockReadinessPatch")).Patch();
            epicOwner.Patch(target, prefix: new HarmonyMethod(replacement.GetMethod("Prefix", S)!));
            Call("EpicLootStackCompatibility", "Initialize");
            Check(!(bool)T("EpicLootStackCompatibility").GetField("_blocked", S)!.GetValue(null)! &&
                T("EpicLootStackCompatibility").GetField("_prefix", S)!.GetValue(null) != null,
                "EpicLoot rejected the known clock-readiness guard");
            Check((bool)Call("EpicLootStackCompatibility", "Ensure", Harmony.GetPatchInfo(target))!,
                "actual three-prefix EpicLoot composition failed");
            foreignOwner.Patch(target,prefix:new HarmonyMethod(typeof(Checks),nameof(Skip)));
            try { Call("EpicLootStackCompatibility", "Ensure", Harmony.GetPatchInfo(target)); throw new Exception("unknown prefix accepted"); }
            catch(TargetInvocationException e) { Check(e.InnerException is InvalidOperationException &&
                e.InnerException.Message.Contains("additional original-method prefix"),"unknown wrapper still refused"); }
        }
        finally { foreignOwner.UnpatchSelf(); Call("EpicLootStackCompatibility", "Shutdown"); epicOwner.UnpatchSelf(); }
    }
}
