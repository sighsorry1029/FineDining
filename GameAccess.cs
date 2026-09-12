using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

// Cached bindings to original Valheim 1.0.7 members with no equivalent public API.
internal static class GameAccess
{
    private static readonly AccessTools.FieldRef<Container, ZNetView> ContainerNetworkView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this Container instance) => ref ContainerNetworkView(instance);

    private static readonly AccessTools.FieldRef<ItemDrop, ZNetView> ItemDropNetworkView = AccessTools.FieldRefAccess<ItemDrop, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this ItemDrop instance) => ref ItemDropNetworkView(instance);

    private static readonly AccessTools.FieldRef<Piece, ZNetView> PieceNetworkView = AccessTools.FieldRefAccess<Piece, ZNetView>("m_nview");
    internal static ref ZNetView NetworkView(this Piece instance) => ref PieceNetworkView(instance);

    private static readonly AccessTools.FieldRef<Container, bool> ContainerLoading = AccessTools.FieldRefAccess<Container, bool>("m_loading");
    internal static ref bool Loading(this Container instance) => ref ContainerLoading(instance);

    private static readonly AccessTools.FieldRef<Floating, Rigidbody> FloatingBody = AccessTools.FieldRefAccess<Floating, Rigidbody>("m_body");
    internal static ref Rigidbody Body(this Floating instance) => ref FloatingBody(instance);

    private static readonly AccessTools.FieldRef<Floating, float> FloatingWaterLevel = AccessTools.FieldRefAccess<Floating, float>("m_waterLevel");
    internal static ref float WaterLevel(this Floating instance) => ref FloatingWaterLevel(instance);

    private static readonly AccessTools.FieldRef<ItemDrop, Floating> ItemDropFloatingComponent = AccessTools.FieldRefAccess<ItemDrop, Floating>("m_floating");
    internal static ref Floating FloatingComponent(this ItemDrop instance) => ref ItemDropFloatingComponent(instance);

    private static readonly AccessTools.FieldRef<Minimap, List<Minimap.PinData>> MinimapPins = AccessTools.FieldRefAccess<Minimap, List<Minimap.PinData>>("m_pins");
    internal static ref List<Minimap.PinData> Pins(this Minimap instance) => ref MinimapPins(instance);

    private static readonly AccessTools.FieldRef<Minimap, bool[]> MinimapVisibleIconTypes = AccessTools.FieldRefAccess<Minimap, bool[]>("m_visibleIconTypes");
    internal static ref bool[] VisibleIconTypes(this Minimap instance) => ref MinimapVisibleIconTypes(instance);

    private static readonly AccessTools.FieldRef<Minimap, bool> MinimapPinUpdateRequired = AccessTools.FieldRefAccess<Minimap, bool>("m_pinUpdateRequired");
    internal static ref bool PinUpdateRequired(this Minimap instance) => ref MinimapPinUpdateRequired(instance);

    private static readonly AccessTools.FieldRef<SkillsDialog, List<GameObject>> SkillsDialogElements = AccessTools.FieldRefAccess<SkillsDialog, List<GameObject>>("m_elements");
    internal static ref List<GameObject> Elements(this SkillsDialog instance) => ref SkillsDialogElements(instance);

    private static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> InventoryGridElements = AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
    internal static ref List<InventoryElement> Elements(this InventoryGrid instance) => ref InventoryGridElements(instance);

    private static readonly AccessTools.FieldRef<UITooltip, RectTransform?> UITooltipAnchor = AccessTools.FieldRefAccess<UITooltip, RectTransform?>("m_anchor");
    internal static ref RectTransform? Anchor(this UITooltip instance) => ref UITooltipAnchor(instance);

    private static readonly AccessTools.FieldRef<UITooltip, Vector2> UITooltipFixedPosition = AccessTools.FieldRefAccess<UITooltip, Vector2>("m_fixedPosition");
    internal static ref Vector2 FixedPosition(this UITooltip instance) => ref UITooltipFixedPosition(instance);

    private static readonly AccessTools.FieldRef<ObjectDB, Dictionary<int,GameObject>> ObjectDBItemHashes = AccessTools.FieldRefAccess<ObjectDB, Dictionary<int,GameObject>>("m_itemByHash");
    internal static ref Dictionary<int,GameObject> ItemHashes(this ObjectDB instance) => ref ObjectDBItemHashes(instance);

    private static readonly AccessTools.FieldRef<ObjectDB, Dictionary<ItemDrop.ItemData.SharedData,GameObject>> ObjectDBItemDataPrefabs = AccessTools.FieldRefAccess<ObjectDB, Dictionary<ItemDrop.ItemData.SharedData,GameObject>>("m_itemByData");
    internal static ref Dictionary<ItemDrop.ItemData.SharedData,GameObject> ItemDataPrefabs(this ObjectDB instance) => ref ObjectDBItemDataPrefabs(instance);

    private static readonly AccessTools.FieldRef<ZNetScene, Dictionary<int,GameObject>> ZNetSceneNamedPrefabs = AccessTools.FieldRefAccess<ZNetScene, Dictionary<int,GameObject>>("m_namedPrefabs");
    internal static ref Dictionary<int,GameObject> NamedPrefabs(this ZNetScene instance) => ref ZNetSceneNamedPrefabs(instance);

    private static readonly AccessTools.FieldRef<ZNet, ZDOID> ZNetCharacterId = AccessTools.FieldRefAccess<ZNet, ZDOID>("m_characterID");
    internal static ref ZDOID CharacterId(this ZNet instance) => ref ZNetCharacterId(instance);

    private static readonly AccessTools.FieldRef<Player, PieceTable> PlayerBuildPieces = AccessTools.FieldRefAccess<Player, PieceTable>("m_buildPieces");
    internal static ref PieceTable BuildPieces(this Player instance) => ref PlayerBuildPieces(instance);

    private static readonly AccessTools.FieldRef<List<ItemDrop>> ItemInstancesField = AccessTools.StaticFieldRefAccess<List<ItemDrop>>(AccessTools.Field(typeof(ItemDrop), "s_instances"));
    internal static List<ItemDrop> ItemInstances => ItemInstancesField();

    private static readonly AccessTools.FieldRef<UITooltip> CurrentTooltipField = AccessTools.StaticFieldRefAccess<UITooltip>(AccessTools.Field(typeof(UITooltip), "m_current"));
    internal static UITooltip CurrentTooltip => CurrentTooltipField();

    private static readonly AccessTools.FieldRef<GameObject> TooltipObjectField = AccessTools.StaticFieldRefAccess<GameObject>(AccessTools.Field(typeof(UITooltip), "m_tooltip"));
    internal static GameObject TooltipObject => TooltipObjectField();

    private static readonly Action<Humanoid, PieceTable> HumanoidSetPlaceMode = AccessTools.MethodDelegate<Action<Humanoid, PieceTable>>(AccessTools.DeclaredMethod(typeof(Humanoid), "SetPlaceMode", new Type[] { typeof(PieceTable) }));
    internal static void SetPlaceMode(this Humanoid instance, PieceTable arg0) => HumanoidSetPlaceMode(instance, arg0);

    private static readonly Action<Inventory, bool, bool> InventoryChanged = AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.DeclaredMethod(typeof(Inventory), "Changed", new Type[] { typeof(bool), typeof(bool) }));
    internal static void Changed(this Inventory instance, bool arg0 = false, bool arg1 = false) => InventoryChanged(instance, arg0, arg1);

    private static readonly Func<Inventory, bool, Vector2i> InventoryFindEmptySlot = AccessTools.MethodDelegate<Func<Inventory, bool, Vector2i>>(AccessTools.DeclaredMethod(typeof(Inventory), "FindEmptySlot", new Type[] { typeof(bool) }));
    internal static Vector2i FindEmptySlot(this Inventory instance, bool arg0) => InventoryFindEmptySlot(instance, arg0);

    private static readonly Func<Inventory, ItemDrop.ItemData, bool> InventoryTopFirst = AccessTools.MethodDelegate<Func<Inventory, ItemDrop.ItemData, bool>>(AccessTools.DeclaredMethod(typeof(Inventory), "TopFirst", new Type[] { typeof(ItemDrop.ItemData) }));
    internal static bool TopFirst(this Inventory instance, ItemDrop.ItemData arg0) => InventoryTopFirst(instance, arg0);

    private static readonly Func<Inventory, ItemDrop.ItemData, int, int, int, bool, bool> InventoryAddItem = AccessTools.MethodDelegate<Func<Inventory, ItemDrop.ItemData, int, int, int, bool, bool>>(AccessTools.DeclaredMethod(typeof(Inventory), "AddItem", new Type[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) }));
    internal static bool AddItem(this Inventory instance, ItemDrop.ItemData arg0, int arg1, int arg2, int arg3, bool arg4 = false) => InventoryAddItem(instance, arg0, arg1, arg2, arg3, arg4);

    private static readonly Action<ItemDrop> ItemDropSave = AccessTools.MethodDelegate<Action<ItemDrop>>(AccessTools.DeclaredMethod(typeof(ItemDrop), "Save", new Type[] {  }));
    internal static void Save(this ItemDrop instance) => ItemDropSave(instance);

    private static readonly Func<ItemDrop, double> ItemDropGetTimeSinceSpawned = AccessTools.MethodDelegate<Func<ItemDrop, double>>(AccessTools.DeclaredMethod(typeof(ItemDrop), "GetTimeSinceSpawned", new Type[] {  }));
    internal static double GetTimeSinceSpawned(this ItemDrop instance) => ItemDropGetTimeSinceSpawned(instance);

    private static readonly Action<ItemDrop> ItemDropTimedDestruction = AccessTools.MethodDelegate<Action<ItemDrop>>(AccessTools.DeclaredMethod(typeof(ItemDrop), "TimedDestruction", new Type[] {  }));
    internal static void TimedDestruction(this ItemDrop instance) => ItemDropTimedDestruction(instance);

    private static readonly Action<Localization, string, string> LocalizationAddWord = AccessTools.MethodDelegate<Action<Localization, string, string>>(AccessTools.DeclaredMethod(typeof(Localization), "AddWord", new Type[] { typeof(string), typeof(string) }));
    internal static void AddWord(this Localization instance, string arg0, string arg1) => LocalizationAddWord(instance, arg0, arg1);

    private static readonly Func<ZRoutedRpc, long> ZRoutedRpcGetServerPeerID = AccessTools.MethodDelegate<Func<ZRoutedRpc, long>>(AccessTools.DeclaredMethod(typeof(ZRoutedRpc), "GetServerPeerID", new Type[] {  }));
    internal static long GetServerPeerID(this ZRoutedRpc instance) => ZRoutedRpcGetServerPeerID(instance);

    internal static readonly Func<Texture2D, byte[], bool> LoadImage =
        AccessTools.MethodDelegate<Func<Texture2D, byte[], bool>>(AccessTools.DeclaredMethod(typeof(ImageConversion), "LoadImage", new[] { typeof(Texture2D), typeof(byte[]) }));
}
