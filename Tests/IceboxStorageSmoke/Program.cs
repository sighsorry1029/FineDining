using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ItemData = ItemDrop.ItemData;

internal static class Entry
{
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => {
            foreach (string root in new[] { args[1], args[2], Path.GetDirectoryName(args[0])! }) {
                string path=Path.Combine(root,new AssemblyName(e.Name).Name+".dll");
                if(File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { Program.Run(args); return 0; } catch(Exception e) { System.Console.WriteLine(e); return 1; }
    }
}

internal static class Program
{
    private const BindingFlags S=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private const BindingFlags I=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static readonly Dictionary<Inventory,Container> Containers=new();
    private static readonly MethodInfo Positional=AccessTools.Method(typeof(Inventory),"AddItem",new[]{typeof(ItemData),typeof(int),typeof(int),typeof(int),typeof(bool)});
    private static Type icebox=null!;
    private static int checks;
    private static bool loading;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeInt();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeHash(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeDestroy(IntPtr value,float delay);
    private static readonly NativeInt Offset=()=>0;
    private static readonly NativeHash Hash=_=>0;
    private static readonly NativeDestroy Destroy=(_,__)=>{};
    [DllImport("mono-2.0-bdwgc.dll",CallingConvention=CallingConvention.Cdecl)] private static extern void mono_add_internal_call(string name,IntPtr method);
    private static bool Equality(UnityEngine.Object lhs,UnityEngine.Object rhs,ref bool __result) { __result=ReferenceEquals(lhs,rhs); return false; }
    private static bool PrefabName(ref string __result) { __result="fd-storage-food"; return false; }
    private static bool Icebox(Container __0,ref bool __result) { __result=Containers.Values.Contains(__0); return false; }
    private static bool Loading(ref bool __result) { __result=loading; return false; }
    private static bool Container(Inventory __0,ref Container __1,ref bool __result) { __result=Containers.TryGetValue(__0,out __1!); return false; }
    private static bool Skip() => false;
    private static bool Changed(Inventory __instance) { Call("HandleInventoryChanged",__instance); return false; }
    // Only the native prefab construction boundary is replaced. Original Save,
    // Load, ItemData.Load and positional bounds checking still execute.
    private static bool Construct(Inventory __instance,int stack,float durability,Vector2i pos,bool equipped,int quality,int variant,long crafterID,string crafterName,Dictionary<string,string> customData,int worldLevel,bool pickedUp,bool cheated,bool skipValidPositionCheck,ref bool __result)
    {
        ItemData item=Food("loaded",pos.x,pos.y);
        item.m_stack=stack; item.m_durability=durability; item.m_equipped=equipped; item.m_quality=quality; item.m_variant=variant;
        item.m_crafterID=crafterID; item.m_crafterName=crafterName; item.m_customData=customData; item.m_worldLevel=(byte)worldLevel; item.m_pickedUp=pickedUp; item.m_cheated=cheated;
        Positional.Invoke(__instance,new object[]{item,stack,pos.x,pos.y,skipValidPositionCheck});
        __result=true; return false;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string[] args)
    {
        Assembly bep=Assembly.LoadFrom(Path.Combine(args[2],"BepInEx.dll"));
        bep.GetType("BepInEx.Paths",true)!.GetMethod("SetExecutablePath",S)!.Invoke(null,new object[]{Path.Combine(Path.GetTempPath(),"fd-storage-smoke","valheim.exe"),null!,args[1],new[]{args[1],args[2]}});
        mono_add_internal_call("UnityEngine.Object::GetOffsetOfInstanceIDInCPlusPlusObject",Marshal.GetFunctionPointerForDelegate(Offset));
        mono_add_internal_call("UnityEngine.Animator::StringToHash_Injected",Marshal.GetFunctionPointerForDelegate(Hash));
        mono_add_internal_call("UnityEngine.Object::Destroy_Injected",Marshal.GetFunctionPointerForDelegate(Destroy));
        var fixture=new Harmony("fd.icebox.storage.fixture");
        Assembly mod=Assembly.LoadFrom(args[0]); icebox=mod.GetType("FineDining.IceboxSubsystem",true)!;
        void Patch(MethodBase method,string prefix)=>fixture.Patch(method,prefix:new HarmonyMethod(typeof(Program),prefix));
        try {
            Patch(AccessTools.Method(typeof(UnityEngine.Object),"CompareBaseObjects"),nameof(Equality));
            Patch(AccessTools.PropertyGetter(typeof(UnityEngine.Object),"name"),nameof(PrefabName));
            Patch(AccessTools.Method(icebox,"IsIcebox",new[]{typeof(Container)}),nameof(Icebox));
            Patch(AccessTools.Method(mod.GetType("FineDining.DecayRuntime",true),"IsContainerLoading"),nameof(Loading));
            Patch(AccessTools.Method(mod.GetType("FineDining.DecayRuntime",true),"TryGetContainer"),nameof(Container));
            Patch(AccessTools.Method(typeof(Inventory),"Changed"),nameof(Changed));
            foreach(var method in typeof(ZLog).GetMethods(S).Where(m=>m.Name=="Log"||m.Name=="LogWarning")) Patch(method,nameof(Skip));
            Patch(typeof(Inventory).GetMethods(I).Single(m=>m.Name=="AddItem"&&m.GetParameters().Length==14&&m.GetParameters()[0].ParameterType==typeof(int)),nameof(Construct));
            var config=new ConfigFile(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".cfg"),false);
            var rows=(ConfigEntry<int>)Call("BindStorageRows",config)!;
            var columns=(ConfigEntry<int>)Call("BindStorageColumns",config)!;
            icebox.GetField("_storageRows",S)!.SetValue(null,rows); icebox.GetField("_storageColumns",S)!.SetValue(null,columns);
            Check(rows.Value==4 && columns.Value==8,"defaults remain eight columns by four rows");
            rows.Value=-1; columns.Value=0; Check(rows.Value==1 && columns.Value==1,"minimum config clamps to 1x1");
            rows.Value=21; columns.Value=9; Check(rows.Value==20 && columns.Value==8,"maximum config clamps to 8x20");
            var inv=new Inventory("test",null!,8,20); var box=(Container)FormatterServices.GetUninitializedObject(typeof(Container));
            AccessTools.Field(typeof(Container),"m_inventory").SetValue(box,inv); Containers.Add(inv,box);
            Call("ApplyConfiguredStorageSize",box); Size(inv,8,20,"empty maximum size");
            rows.Value=columns.Value=1; Call("ApplyConfiguredStorageSize",box); Size(inv,1,1,"empty minimum size");
            ItemData outer=Food("outer",7,19); inv.GetAllItems().Add(outer); Call("ApplyConfiguredStorageSize",box);
            Size(inv,8,20,"occupied bounds retained on both axes");
            var packet=new ZPackage(); inv.Save(packet); byte[] saved=packet.GetArray();
            // Demonstrate why max width must be restored BEFORE vanilla Load.
            var narrow=new Inventory("control",null!,1,1); narrow.Load(new ZPackage(saved));
            Check(narrow.GetAllItems().Count==0,"vanilla narrow load rejects saved outer column");
            inv.GetAllItems().Clear(); Call("ApplyConfiguredStorageSize",box);
            Check((bool)Call("PrepareStorageLoad",box)!,"Icebox load preparation recognized"); Size(inv,8,20,"load uses supported maximum capacity");
            loading=true; inv.Load(new ZPackage(saved)); loading=false; Call("ApplyConfiguredStorageSize",box);
            ItemData restored=inv.GetAllItems().Single();
            Check(restored.m_gridPos==new Vector2i(7,19) && restored.m_stack==3 && restored.m_customData["identity"]=="outer" && restored.m_customData["sighsorry.FineDining.ExpiryWorldTicks"]=="-12345","original save/load preserves coordinates, count and custom data");
            Size(inv,8,20,"reload retains occupied outer bounds");
            inv.RemoveAll(); Size(inv,8,20,"inventory change queues shrinking"); Call("RefreshPendingStorageSizes"); Size(inv,1,1,"queued refresh shrinks emptied inventory");
            foreach(var pos in new[]{new Vector2i(7,0),new Vector2i(0,19)}) {
                inv.GetAllItems().Clear(); var a=Food("A",pos.x,pos.y); var b=Food("B",0,0); a.m_stack=b.m_stack=1;
                inv.GetAllItems().Add(a); inv.GetAllItems().Add(b); Call("ApplyConfiguredStorageSize",box);
                var grid=(InventoryGrid)FormatterServices.GetUninitializedObject(typeof(InventoryGrid)); AccessTools.Field(typeof(InventoryGrid),"m_inventory").SetValue(grid,inv);
                Check(grid.DropItem(inv,a,1,new Vector2i(0,0)),"original slot swap completes at "+pos);
                Call("RefreshPendingStorageSizes");
                Check(inv.GetAllItems().Count==2 && inv.GetItemAt(0,0).m_customData["identity"]=="A" && inv.GetItemAt(pos.x,pos.y).m_customData["identity"]=="B" && inv.GetAllItems().All(i=>i.m_stack==1 && i.m_customData["sighsorry.FineDining.ExpiryWorldTicks"]=="-12345"),"slot swap preserves both items, positions and freshness at "+pos);
                inv.RemoveItem(inv.GetItemAt(pos.x,pos.y)); Call("RefreshPendingStorageSizes"); Size(inv,1,1,"removing outer item permits later shrink");
            }
            Check(box.m_width==inv.GetWidth() && box.m_height==inv.GetHeight(),"container and inventory dimensions agree");
            System.Console.WriteLine("PASS "+checks+" original Mono Icebox checks; native prefab/identity/registration fixtures, no live world/server.");
        } finally { fixture.UnpatchSelf(); }
    }
    private static object? Call(string name,params object[] args)=>icebox.GetMethod(name,S)!.Invoke(null,args);
    private static ItemData Food(string name,int x,int y)=>new() { m_shared=new ItemData.SharedData{m_name=name,m_maxStackSize=50,m_maxQuality=1},m_stack=3,m_gridPos=new Vector2i(x,y),m_dropPrefab=(GameObject)FormatterServices.GetUninitializedObject(typeof(GameObject)),m_customData=new Dictionary<string,string>{{"identity",name},{"sighsorry.FineDining.ExpiryWorldTicks","-12345"}} };
    private static void Size(Inventory inv,int x,int y,string text)=>Check(inv.GetWidth()==x && inv.GetHeight()==y,text);
    private static void Check(bool pass,string text) { if(!pass) throw new Exception("FAIL: "+text); checks++; System.Console.WriteLine("PASS: "+text); }
}
