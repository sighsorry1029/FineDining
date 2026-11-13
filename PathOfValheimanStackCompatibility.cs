using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
using ItemData = ItemDrop.ItemData;

namespace FineDining;

// Separate from food values/cards: failure here leaves PoV's conservative stack
// identity intact. Permission is scoped to paths that also preserve spoilage.
internal static class PathOfValheimanStackCompatibility
{
    private static Harmony? _patches;
    private static Func<ItemData?>? _incoming;
    private static Func<ItemData, ItemData, bool>? _sortStackable;
    private static MethodInfo? _stackableMethod;
    private static MethodInfo? _bookkeepingMethod;
    private static Func<string, bool>? _bookkeeping;
    [ThreadStatic] private static Context _context;
    private static bool _active;

    private readonly struct Context
    {
        internal readonly Inventory? Inventory;
        internal readonly ItemData? Source, Target;
        internal readonly bool CheckIncoming;
        internal Context(Inventory inventory, ItemData source, ItemData? target, bool checkIncoming)
        { Inventory = inventory; Source = source; Target = target; CheckIncoming = checkIncoming; }
    }

    private readonly struct Scope
    {
        internal readonly Context Previous;
        internal readonly bool Entered;
        internal Scope(Context previous) { Previous = previous; Entered = true; }
    }

    internal static void TryInstall()
    {
        if (Chainloader.PluginInfos.TryGetValue(PathOfValheimanCompatibility.PluginGuid, out var plugin) &&
            plugin.Instance != null)
            Install(plugin.Instance.GetType().Assembly);
    }

    private static void Install(Assembly assembly)
    {
        Shutdown();
        Type? rules = assembly.GetType("PassiveTree.StackRules");
        if (rules == null) return; // Earlier PoV has no metadata gate.
        var patches = new Harmony(FineDiningPlugin.ModGUID + ".pov.stacking");
        try
        {
            MethodInfo same = RequireMethod(rules, "SameData", typeof(bool), typeof(ItemData), typeof(ItemData));
            _bookkeepingMethod = RequireMethod(rules, "Bookkeeping", typeof(bool), typeof(string));
            _bookkeeping = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), _bookkeepingMethod);
            RewriteComparison(PatchProcessor.GetOriginalInstructions(same));
            FieldInfo incoming = AccessTools.DeclaredField(rules, "incoming");
            if (incoming?.IsStatic != true || incoming.FieldType != typeof(ItemData))
                throw new MissingFieldException(rules.FullName, "incoming");
            var read = AccessTools.StaticFieldRefAccess<ItemData>(incoming);
            _incoming = () => read();

            MethodInfo[] adds =
            {
                AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemData) }),
                AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemData), typeof(Vector2i) }),
                AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.AddItem),
                    new[] { typeof(ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })
            };
            MethodInfo canAdd = AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.CanAddItem),
                new[] { typeof(ItemData), typeof(int) });
            foreach (MethodInfo method in adds.Concat(new[] { canAdd }))
            {
                RequirePatch(method, "StackContext", HarmonyPatchType.Prefix);
                RequirePatch(method, "StackContext", HarmonyPatchType.Finalizer);
            }
            foreach (MethodInfo method in adds)
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (!info.Prefixes.Any(p => p.owner == FineDiningPlugin.ModGUID) ||
                    !info.Postfixes.Any(p => p.owner == FineDiningPlugin.ModGUID) ||
                    !info.Finalizers.Any(p => p.owner == FineDiningPlugin.ModGUID))
                    throw new InvalidOperationException("FineDining's merge tracker is missing on " + method);
            }
            RequirePatch(AccessTools.DeclaredMethod(typeof(Inventory), "FindFreeStackItem"), "MatchMetadata", HarmonyPatchType.Postfix);
            RequirePatch(AccessTools.DeclaredMethod(typeof(Inventory), "FindFreeStackSpace"), "MetadataSpace", HarmonyPatchType.Postfix);
            RequirePatch(AccessTools.DeclaredMethod(typeof(ItemData), nameof(ItemData.IsSameType)), "ItemData_IsSameType", HarmonyPatchType.Postfix);

            Type quick = assembly.GetType("PassiveTree.QuickStack", true)!;
            _stackableMethod = RequireMethod(quick, "Stackable", typeof(bool), typeof(ItemData), typeof(ItemData));
            if (PatchProcessor.GetOriginalInstructions(_stackableMethod).Count(i =>
                    i.operand is MethodInfo m && m.DeclaringType == quick && m.Name == "SameData") != 1)
                throw new NotSupportedException("PoV sort comparison changed");
            MethodInfo sortSame = RequireMethod(quick, "SameData", typeof(bool), typeof(ItemData), typeof(ItemData));
            if (PatchProcessor.GetOriginalInstructions(sortSame).Count(i => i.Calls(same)) != 1)
                throw new NotSupportedException("PoV sort no longer uses StackRules.SameData");
            _sortStackable = (Func<ItemData, ItemData, bool>)Delegate.CreateDelegate(typeof(Func<ItemData, ItemData, bool>), _stackableMethod);
            MethodInfo sort = RequireMethod(quick, "Sort", typeof(void), typeof(Player));
            RewriteSort(PatchProcessor.GetOriginalInstructions(sort)); // Validate both anchors before installing anything.

            var before = new HarmonyMethod(typeof(PathOfValheimanStackCompatibility), nameof(BeforeInventory))
            { priority = Priority.High }; // After PoV's 1000 and FineDining's preparation (800).
            var after = new HarmonyMethod(typeof(PathOfValheimanStackCompatibility), nameof(EndContext));
            foreach (MethodInfo method in adds.Concat(new[] { canAdd }))
                patches.Patch(method, prefix: before, finalizer: after);
            MethodInfo preview = AccessTools.DeclaredMethod(typeof(InventoryGui), "CanDropDragOntoItem", new[] { typeof(ItemData) });
            patches.Patch(preview, prefix: new HarmonyMethod(typeof(PathOfValheimanStackCompatibility), nameof(BeforePreview)), finalizer: after);
            patches.Patch(sort, transpiler: new HarmonyMethod(typeof(PathOfValheimanStackCompatibility), nameof(RewriteSort)));
            patches.Patch(same, transpiler: new HarmonyMethod(typeof(PathOfValheimanStackCompatibility), nameof(RewriteComparison)));
            _patches = patches;
            _active = true;
            FineDiningPlugin.Log.LogInfo("Enabled PoV spoilage-aware inventory transfers, capacity previews and sorting.");
        }
        catch (Exception error)
        {
            patches.UnpatchSelf();
            Shutdown();
            FineDiningPlugin.Log.LogWarning("PoV spoilage stacking compatibility unavailable; its original stack restrictions remain active: " + error.GetBaseException().Message);
        }
    }

    internal static void Shutdown()
    {
        _active = false;
        _patches?.UnpatchSelf();
        _patches = null; _incoming = null; _sortStackable = null; _stackableMethod = null;
        _bookkeepingMethod = null; _bookkeeping = null;
        _context = default;
    }

    private static MethodInfo RequireMethod(Type type, string name, Type result, params Type[] parameters)
    {
        MethodInfo? method = AccessTools.DeclaredMethod(type, name, parameters);
        if (method?.IsStatic != true || method.ReturnType != result) throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static void RequirePatch(MethodInfo target, string nested, HarmonyPatchType kind)
    {
        Patches? info = target == null ? null : Harmony.GetPatchInfo(target);
        var patches = kind == HarmonyPatchType.Prefix ? info?.Prefixes :
            kind == HarmonyPatchType.Finalizer ? info?.Finalizers : info?.Postfixes;
        if (patches == null || !patches.Any(p => p.owner == PathOfValheimanCompatibility.PluginGuid &&
                p.PatchMethod.DeclaringType?.FullName == "PassiveTree.StackRules+" + nested))
            throw new InvalidOperationException("PoV stack patch missing: " + nested + " on " + target);
    }

    private static bool CanOwnMerge(Inventory? inventory) => _active && inventory != null &&
        !DecayRuntime.IsContainerLoading(inventory) && DecayRuntime.IsAuthoritativeInventory(inventory) &&
        SpoilageClock.TryGetWorldTicks(out _);

    private static void BeforeInventory(Inventory __instance, ItemData __0, MethodBase __originalMethod,
        bool __runOriginal, out Scope __state)
    {
        __state = new Scope(_context);
        _context = default; // Even an unsupported nested call must mask its caller's permission.
        ItemData? source = _incoming?.Invoke(); // CanAddItem may use PoV's crafting preview clone.
        if (__runOriginal && source != null &&
            (__originalMethod.Name == nameof(Inventory.CanAddItem) || ReferenceEquals(source, __0)) &&
            CanOwnMerge(__instance))
            _context = new Context(__instance, source, null, true);
    }

    private static void BeforePreview(InventoryGui __instance, ItemData item, ItemData ___m_dragItem, out Scope __state)
    {
        __state = new Scope(_context);
        _context = default;
        if (item == null || ___m_dragItem == null) return;
        Inventory? inventory = __instance.m_playerGrid?.GetInventory();
        if (inventory?.ContainsItem(item) != true) inventory = __instance.ContainerGrid?.GetInventory();
        if (inventory?.ContainsItem(item) == true && CanOwnMerge(inventory))
            _context = new Context(inventory, ___m_dragItem, item, false);
    }

    private static void EndContext(Scope __state)
    {
        if (__state.Entered) _context = __state.Previous;
    }

    // Preserve PoV's actual null/variant checks, implicit normalization and all
    // foreign-data comparisons. Other owners can still reject/skip SameData.
    // Only the two comparison-local Bookkeeping calls get this extra policy.
    private static bool BookkeepingForComparison(string key, ItemData a, ItemData b)
    {
        if (_bookkeeping!(key)) return true;
        if (key != SpoilageClock.ExpiryDataKey && key != FreshnessRuntime.AssignedLifetimeDataKey &&
            key != SpoilageClock.SpoiledDataKey) return false;
        Context context = _context;
        if (!_active || context.Source == null || a == null || b == null) return false;
        ItemData? target = ReferenceEquals(a, context.Source) ? b : ReferenceEquals(b, context.Source) ? a : null;
        if (target == null || (context.Target != null && !ReferenceEquals(target, context.Target)) ||
            context.Inventory?.ContainsItem(target) != true ||
            context.CheckIncoming && !ReferenceEquals(_incoming?.Invoke(), context.Source) ||
            !DifferentMetadata(a, b)) return false;
        return Compatible(a, b) && SourceCanBeComposed(context.Source);
    }

    private static bool SourceCanBeComposed(ItemData source) =>
        !HasMetadata(source) || SpoilageClock.IsSpoiled(source) || SpoilageClock.TryGetExpiryTicks(source, out _);

    private static bool HasMetadata(ItemData item) => item.m_customData != null &&
        (item.m_customData.ContainsKey(SpoilageClock.ExpiryDataKey) ||
         item.m_customData.ContainsKey(FreshnessRuntime.AssignedLifetimeDataKey) ||
         item.m_customData.ContainsKey(SpoilageClock.SpoiledDataKey));

    private static string? Value(ItemData item, string key) =>
        item.m_customData != null && item.m_customData.TryGetValue(key, out string value) ? value : null;

    private static bool DifferentMetadata(ItemData a, ItemData b) =>
        DifferentKey(a, b, SpoilageClock.ExpiryDataKey) || DifferentKey(a, b, FreshnessRuntime.AssignedLifetimeDataKey) ||
        DifferentKey(a, b, SpoilageClock.SpoiledDataKey);

    private static bool DifferentKey(ItemData a, ItemData b, string key) =>
        (a.m_customData?.ContainsKey(key) == true) != (b.m_customData?.ContainsKey(key) == true) || Value(a, key) != Value(b, key);

    private static bool Compatible(ItemData a, ItemData b) =>
        DecayRuntime.CanMergeStackClockValues(Value(a, SpoilageClock.ExpiryDataKey), Value(b, SpoilageClock.ExpiryDataKey)) &&
        FreshnessRuntime.CanMergeAssignedLifetimeValues(Value(a, FreshnessRuntime.AssignedLifetimeDataKey), Value(b, FreshnessRuntime.AssignedLifetimeDataKey)) &&
        SpoilageClock.CanMergeSpoiledValues(Value(a, SpoilageClock.SpoiledDataKey), Value(b, SpoilageClock.SpoiledDataKey));

    private static IEnumerable<CodeInstruction> RewriteComparison(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToList();
        int[] calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(_bookkeepingMethod)).ToArray();
        if (calls.Length != 2 || calls.Any(i => code[i].opcode != OpCodes.Call || code[i].blocks.Count != 0))
            throw new NotSupportedException("PoV metadata bookkeeping comparison changed; no partial rewrite applied");
        // Own only the two key-exclusion predicates. PoV keeps responsibility for
        // normalization, additional item checks and its native bookkeeping policy.
        MethodInfo key = AccessTools.PropertyGetter(typeof(KeyValuePair<string, string>), "Key");
        MethodInfo next = AccessTools.DeclaredMethod(typeof(Dictionary<string, string>.Enumerator), "MoveNext");
        foreach (int index in calls)
        {
            if (index < 1 || index + 1 >= code.Count || !code[index - 1].Calls(key) ||
                (code[index + 1].opcode != OpCodes.Brtrue && code[index + 1].opcode != OpCodes.Brtrue_S) ||
                !(code[index + 1].operand is Label skip))
                throw new NotSupportedException("PoV metadata key predicate changed; no partial rewrite applied");
            int resume = code.FindIndex(i => i.labels.Contains(skip));
            if (resume <= index + 1 || resume + 1 >= code.Count ||
                (code[resume].opcode != OpCodes.Ldloca && code[resume].opcode != OpCodes.Ldloca_S) ||
                !code[resume + 1].Calls(next))
                throw new NotSupportedException("PoV metadata exclusion no longer skips to the next key; no partial rewrite applied");
        }
        MethodInfo helper = AccessTools.DeclaredMethod(typeof(PathOfValheimanStackCompatibility), nameof(BookkeepingForComparison));
        foreach (int index in calls.Reverse())
        {
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.MoveLabelsFrom(code[index]);
            code[index].operand = helper;
            code.InsertRange(index, new[] { first, new CodeInstruction(OpCodes.Ldarg_1) });
        }
        return code;
    }

    private static bool SortStackable(ItemData target, ItemData source, Player player)
    {
        Context previous = _context;
        _context = default;
        try
        {
            Inventory? inventory = player?.GetInventory();
            if (inventory?.ContainsItem(source) == true && CanOwnMerge(inventory))
                _context = new Context(inventory, source, target, false);
            return _sortStackable!(target, source);
        }
        finally { _context = previous; }
    }

    private static void MergeSortedMetadata(ItemData target, ItemData source, int amount, Player player)
    {
        Inventory? inventory = player?.GetInventory();
        if (inventory?.ContainsItem(source) == true && inventory.ContainsItem(target) && CanOwnMerge(inventory))
            DecayRuntime.ComposeDirectRecoveryMergeExpiry(target, source, amount);
    }

    private static IEnumerable<CodeInstruction> RewriteSort(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToList();
        FieldInfo stack = AccessTools.Field(typeof(ItemData), nameof(ItemData.m_stack));
        var comparisons = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(_stackableMethod)).ToArray();
        var merges = new List<int>();
        for (int i = 0; i + 11 < code.Count; i++)
            if (IsLocal(code[i]) && code[i + 1].opcode == OpCodes.Dup && code[i + 2].LoadsField(stack) &&
                IsLocal(code[i + 3]) && code[i + 4].opcode == OpCodes.Add && code[i + 5].StoresField(stack) &&
                IsLocal(code[i + 6]) && code[i + 7].opcode == OpCodes.Dup && code[i + 8].LoadsField(stack) &&
                SameLocal(code[i + 3], code[i + 9]) && code[i + 10].opcode == OpCodes.Sub && code[i + 11].StoresField(stack))
                merges.Add(i);
        if (comparisons.Length != 1 || merges.Count != 1 || comparisons[0] < 2 || comparisons[0] >= merges[0] ||
            !SameLocal(code[comparisons[0] - 2], code[merges[0]]) ||
            !SameLocal(code[comparisons[0] - 1], code[merges[0] + 6]) ||
            code.Count(i => i.StoresField(stack)) != 2)
            throw new NotSupportedException("PoV sort comparison/quantity transfer changed; no partial rewrite applied");
        int merge = merges[0];
        var transfer = new[] { Load(code[merge]), Load(code[merge + 6]), Load(code[merge + 3]), new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PathOfValheimanStackCompatibility), nameof(MergeSortedMetadata))) };
        transfer[0].MoveLabelsFrom(code[merge]); transfer[0].MoveBlocksFrom(code[merge]);
        code.InsertRange(merge, transfer);
        int comparison = comparisons[0];
        var player = new CodeInstruction(OpCodes.Ldarg_0);
        player.MoveLabelsFrom(code[comparison]); player.MoveBlocksFrom(code[comparison]);
        code[comparison].operand = AccessTools.Method(typeof(PathOfValheimanStackCompatibility), nameof(SortStackable));
        code.Insert(comparison, player);
        return code;
    }

    private static bool IsLocal(CodeInstruction i) => i.opcode == OpCodes.Ldloc || i.opcode == OpCodes.Ldloc_S ||
        i.opcode == OpCodes.Ldloc_0 || i.opcode == OpCodes.Ldloc_1 || i.opcode == OpCodes.Ldloc_2 || i.opcode == OpCodes.Ldloc_3;
    private static bool SameLocal(CodeInstruction a, CodeInstruction b) => IsLocal(a) && a.opcode == b.opcode && Equals(a.operand, b.operand);
    private static CodeInstruction Load(CodeInstruction i) => new(i.opcode, i.operand);

}
