using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace FineDining;

// ItemDataManager copies own independent GUID-prefixed data. Their vanilla
// apply helper replaces the entire custom-data dictionary, so it must not be
// replayed in a composed EpicLoot merge. Evaluate each policy once and combine
// only its owned keys after every provider has accepted the same item pair.
internal static class ItemDataStackCompatibility
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const string Standard = "ItemDataManager.ItemInfo";
    private const string Vapok = "Vapok.Common.Managers.ItemInfo";
    private const string EpicMagicKey = "randyknapp.mods.epicloot#EpicLoot.MagicItemComponent";
    private static readonly MethodInfo AddItem = AccessTools.DeclaredMethod(typeof(Inventory), "AddItem",
        new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) });

    internal static bool IsManagerPatch(Patch patch) =>
        patch.PatchMethod.Name == "HandleAutostackableItems" && IsManager(patch.PatchMethod.DeclaringType);

    private static bool IsManager(Type? type) => type?.FullName == Standard || type?.FullName == Vapok;

    internal static MethodInfo[] Discover(Patches original)
    {
        var methods = new HashSet<MethodInfo>();
        foreach (Patch patch in original.Transpilers)
            if (IsManagerPatch(patch))
            {
                Require(!patch.wrapTryCatch, "ItemDataManager transpiler exception policy");
                methods.Add(patch.PatchMethod);
            }
        // PoVPlus removes only the auto-stack transpiler. The active inventory
        // pair still identifies the manager without initializing dormant copies.
        Patches? inventory = Harmony.GetPatchInfo(AddItem);
        if (inventory != null)
        {
            var prefixes = inventory.Prefixes.Where(p => IsManager(p.PatchMethod.DeclaringType) &&
                p.PatchMethod.Name == "CheckItemDataStackableAddItem").ToArray();
            var postfixes = inventory.Postfixes.Where(p => IsManager(p.PatchMethod.DeclaringType) &&
                p.PatchMethod.Name == "ApplyCustomItemDataStackableAddItem").ToArray();
            foreach (Type type in prefixes.Concat(postfixes).Select(p => p.PatchMethod.DeclaringType!).Distinct())
            {
                Patch[] before = prefixes.Where(p => p.PatchMethod.DeclaringType == type).ToArray();
                Patch[] after = postfixes.Where(p => p.PatchMethod.DeclaringType == type).ToArray();
                Require(before.Length == 1 && after.Length == 1 && before[0].owner == after[0].owner &&
                    !before[0].wrapTryCatch && !after[0].wrapTryCatch, "incomplete ItemDataManager inventory policy");
                methods.Add(Method(type, "HandleAutostackableItems", typeof(IEnumerable<CodeInstruction>),
                    typeof(IEnumerable<CodeInstruction>), typeof(ILGenerator)));
            }
        }
        return methods.OrderBy(m => m.DeclaringType!.Assembly.FullName, StringComparer.Ordinal)
            .ThenBy(m => m.DeclaringType!.FullName, StringComparer.Ordinal).ToArray();
    }

    internal sealed class Provider
    {
        internal MethodInfo Transpiler = null!;
        internal string Prefix = "";
        internal MethodInfo[] Methods = Array.Empty<MethodInfo>();
        internal Func<ItemDrop, ItemDrop.ItemData, Dictionary<string, string>?> Evaluate = null!;
    }

    // Relevant normalized method bodies, not assembly version/MVID/hash. The
    // standard bodies match the installed BowsBeforeHoes, Cuisine and Crafty
    // copies; Vapok has a separate inspected implementation. ItemData.TryStack
    // remains the owning mod's policy, including its virtual overrides.
    internal static Provider[] Bind(IReadOnlyList<MethodInfo> methods)
    {
        var providers = new List<Provider>(methods.Count);
        foreach (MethodInfo transpiler in methods)
        {
            Type type = transpiler.DeclaringType ?? throw new InvalidOperationException("ItemDataManager transpiler has no declaring type.");
            Require((type.FullName == Standard || type.FullName == Vapok) &&
                transpiler.Name == "HandleAutostackableItems",
                "unrecognized ItemDataManager transpiler");
            Require(HasSignature(transpiler, typeof(IEnumerable<CodeInstruction>),
                typeof(IEnumerable<CodeInstruction>), typeof(ILGenerator)), "ItemDataManager transpiler signature");
            bool vapok = type.FullName == Vapok;
            MethodInfo evaluate = Method(type, "IsStackableItemDrop", typeof(Dictionary<string, string>),
                typeof(ItemDrop), typeof(ItemDrop.ItemData));
            MethodInfo apply = Method(type, "ApplyCustomItemDataStackableAutoStack", typeof(void),
                typeof(ItemDrop), typeof(Dictionary<string, string>));
            MethodInfo key = Method(type, "dataKey", typeof(string), typeof(string));
            MethodInfo stack = Method(type, "IsStackable", typeof(Dictionary<string, string>),
                typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData));
            Require(transpiler.IsPrivate && evaluate.IsPrivate && apply.IsPrivate && stack.IsPrivate && key.IsAssembly,
                "ItemDataManager callback access changed");
            Match(transpiler, vapok
                ? "9612B5BD1B548596F1993EAA17C29DAF7D37CE69F0E0A9B74B5E739AC3ADAFC1"
                : "1DB0442BBAFAC8A546A571C90B7FCAE855A290B3EA52578F51001265F2C08589");
            Match(evaluate, vapok
                ? "C8497F4FCABBBB695CD6B118A45C836CFF16B6E116D50234685E27F61E8187FC"
                : "CC18EDC4B77D30C55736935044B47F2488C12AFAD4A61E48BF52BE1E2F952430");
            Match(apply, "343DEC869C181482B3CF5347841BCB586CBC5F3E70FDBCDB34B7977B8B7D94F5");
            Match(key, vapok
                ? "F8A8D98DAD54904871AA348069FABD4E5A8F74008E1AEFBB00F6E80D846FCCEE"
                : "644C393BD793235C84E30AC50086BAF56E2715BD42A0C468B4AFCAA5E12937B0");
            Match(stack, vapok
                ? "BEDE7B296E3AF89CF5B7FFB2DC969EA2E3F9D2866D6980B19B2FEE13C10AF197"
                : "00D81EAE7BEEB16908E6275EA98FCEA6066E8C73B7DC76A3D8125A87810DA5F5");
            string extensionsName = vapok ? "Vapok.Common.Managers.ItemExtensions" : "ItemDataManager.ItemExtensions";
            Type extensions = type.Assembly.GetType(extensionsName, true)!;
            MethodInfo data = Method(extensions, "Data", type, typeof(ItemDrop.ItemData));
            MethodInfo policy = type.GetMethod("IsStackableWithOtherInfo", Declared, null, new[] { type }, null)!;
            MethodInfo load = type.GetMethod("LoadAll", Declared, null, Type.EmptyTypes, null)!;
            Require(policy != null && !policy.IsStatic && policy.ReturnType == typeof(Dictionary<string, string>) &&
                load != null && !load.IsStatic && load.ReturnType == typeof(void), "ItemDataManager policy binding");
            Match(data, vapok
                ? "715A84894D86EC37BF657A1E3C38B5ED2198471DA96D14A40985C0EB9AB1D6C8"
                : "DF6D0C7BF7FCA8CD05A61AB1AD715E5FAD17056FF4F857AAC4E11CA9819D9B24");
            Require(ExceptionFingerprint(policy!) == (vapok
                ? "24473C6C4FCB3C2D3B8985986D7AFF7FDDE9A1F9C447F423157D9CD8C7BDF5F7"
                : "7BDC1639E580C7E261BC3501A48979362FDF86561412D781BA8C87659A0CCBDC"), "ItemDataManager merge policy changed");
            Require(ExceptionFingerprint(load!) == (vapok
                ? "14B13CCFF4FFE612519F44B7DBAD4D54184C5127EC7D4C64550CE634B0402928"
                : "6A5193C79F2A0A35402EB95D7E6673FCA449587AC11B0593F30A6A3F150A312C"), "ItemDataManager loading policy changed");
            MethodInfo[] selectors = PatchProcessor.GetOriginalInstructions(policy!).Where(i => i.opcode == OpCodes.Ldftn)
                .Select(i => (MethodInfo)i.operand).ToArray();
            Require(selectors.Length == 2, "ItemDataManager result selectors changed");
            Match(selectors[0], vapok
                ? "ABDEF6C737A5A8CC29AD6B91197E86C6ADA498598D34A7F470487522CD89D83B"
                : "894A013CDA770F76C10ED83C26F05ED960186463163A28D568F408674290AF50");
            Match(selectors[1], "BC12D2BC28D89E90A12F848AEDD7E546E3754BDAD2E0DE071F842F6F15C739B0");
            MethodInfo inventoryCheck = Method(type, "CheckItemDataStackableAddItem", typeof(bool),
                typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int), typeof(int),
                typeof(Dictionary<string, string>).MakeByRefType(), typeof(bool).MakeByRefType());
            MethodInfo inventoryApply = Method(type, "ApplyCustomItemDataStackableAddItem", typeof(void),
                typeof(Inventory), typeof(int), typeof(int), typeof(Dictionary<string, string>), typeof(bool));
            Require(inventoryCheck.IsPrivate && inventoryApply.IsPrivate &&
                inventoryCheck.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "__instance", "item", "x", "y", "__state", "__result" }) &&
                inventoryApply.GetParameters().Select(p => p.Name).SequenceEqual(new[] { "__instance", "x", "y", "__state", "__result" }),
                "ItemDataManager inventory binding changed");
            Match(inventoryCheck, vapok
                ? "1B0FC5EFA8BDD7CD39168162C5F4EF4863AB0C2B3A62A724D8A67E34E69B2B52"
                : "47B9E5715BF038C7E6E6257DD514A57ACDA00EEBC1EB48E94B025975FA120AB5");
            Require(ExceptionFingerprint(inventoryApply) == (vapok
                ? "399C771775935D7B0662E72120303674A1B1352974E7573B8922EA6500DF67B6"
                : "2E401C647DC04F081F0E36B1A789D3B4D7B4BFA2487F66F8636C751F291177A2"), "ItemDataManager inventory result policy changed");
            var provider = new Provider
            {
                Transpiler = transpiler,
                Methods = new[] { transpiler, evaluate, apply, key, stack, data, policy!, load!,
                    Method(type, "get_modGuid", typeof(string)), selectors[0], selectors[1], inventoryCheck, inventoryApply },
                Evaluate = (Func<ItemDrop, ItemDrop.ItemData, Dictionary<string, string>?>)
                    Delegate.CreateDelegate(typeof(Func<ItemDrop, ItemDrop.ItemData, Dictionary<string, string>?>), evaluate)
            };
            Validate(new[] { provider });
            string? prefix = key.Invoke(null, new object[] { "" }) as string;
            Require(!string.IsNullOrEmpty(prefix) && prefix!.Length > 1 && prefix.EndsWith("#", StringComparison.Ordinal) &&
                prefix.IndexOf('#') == prefix.Length - 1, "ItemDataManager data namespace");
            Require(providers.All(existing => !prefix!.StartsWith(existing.Prefix, StringComparison.Ordinal) &&
                !existing.Prefix.StartsWith(prefix, StringComparison.Ordinal)), "overlapping ItemDataManager data namespaces");
            provider.Prefix = prefix!;
            providers.Add(provider);
        }
        return providers.ToArray();
    }

    internal static void Validate(Provider[] providers)
    {
        foreach (Provider provider in providers)
            foreach (MethodInfo method in provider.Methods)
            {
                Patches? info = Harmony.GetPatchInfo(method);
                Require(info == null || !info.Owners.Any(), "uninspected patch on " + method.DeclaringType?.FullName + "." + method.Name);
            }
    }

    // Called only after EpicLoot's ordinary eligibility/ownership/space checks,
    // immediately before quantity changes. No reflection or global scans here.
    internal static bool TryCompose(ItemDrop receiver, ItemDrop candidate, Provider[] providers)
    {
        if (ReferenceEquals(receiver, null) || ReferenceEquals(candidate, null) || ReferenceEquals(receiver, candidate)) return false;
        ItemDrop.ItemData? target = receiver.m_itemData;
        ItemDrop.ItemData? source = candidate.m_itemData;
        if (target?.m_customData == null || source?.m_customData == null || ReferenceEquals(target, source)) return false;
        try
        {
            // Preserve foreign metadata only when both stacks already agree.
            // An absent/unhandled provider must never silently discard its data.
            Dictionary<string, string> beforeTarget = new(target.m_customData, StringComparer.Ordinal);
            Dictionary<string, string> beforeSource = new(source.m_customData, StringComparer.Ordinal);
            int targetCount = target.m_stack, sourceCount = source.m_stack;
            foreach (KeyValuePair<string, string> pair in beforeTarget)
                if (!Owned(pair.Key, providers) && !FineDiningKey(pair.Key) &&
                    (UnknownManagerKey(pair.Key) || !beforeSource.TryGetValue(pair.Key, out string value) || value != pair.Value)) return false;
            foreach (KeyValuePair<string, string> pair in beforeSource)
                if (!Owned(pair.Key, providers) && !FineDiningKey(pair.Key) &&
                    (UnknownManagerKey(pair.Key) || !beforeTarget.ContainsKey(pair.Key))) return false;

            var results = new Dictionary<string, string>[providers.Length];
            for (int i = 0; i < providers.Length; i++)
            {
                Provider provider = providers[i];
                Dictionary<string, string>? result = provider.Evaluate(receiver, source);
                if (result == null) return false;
                foreach (KeyValuePair<string, string> pair in result)
                    if (!pair.Key.StartsWith(provider.Prefix, StringComparison.Ordinal) || pair.Value == null) return false;
                // LoadAll can ignore missing/unrecognized ItemData types. Reject
                // rather than discard any input key absent from its result.
                foreach (string key in beforeTarget.Keys)
                    if (key.StartsWith(provider.Prefix, StringComparison.Ordinal) && !result.ContainsKey(key)) return false;
                foreach (string key in beforeSource.Keys)
                    if (key.StartsWith(provider.Prefix, StringComparison.Ordinal) && !result.ContainsKey(key)) return false;
                results[i] = new Dictionary<string, string>(result, StringComparer.Ordinal);
            }
            // Foreign virtual Load/TryStack callbacks are not guaranteed pure.
            // Do not overwrite their unexpected changes with stale snapshots.
            if (!ReferenceEquals(receiver.m_itemData, target) || !ReferenceEquals(candidate.m_itemData, source) ||
                target.m_stack != targetCount || source.m_stack != sourceCount ||
                !SameData(target.m_customData, beforeTarget) || !SameData(source.m_customData, beforeSource)) return false;
            for (int i = 0; i < results.Length; i++)
                foreach (KeyValuePair<string, string> pair in results[i]) beforeTarget[pair.Key] = pair.Value;
            target.m_customData = beforeTarget;
            return true;
        }
        catch (Exception)
        {
            // Refuse this pair before FineDining/Epic changes quantities or destroys the
            // source. Arbitrary side effects inside foreign callbacks are theirs.
            return false;
        }
    }

    // These three values are composed separately by FineDining immediately
    // after this guard. Their unequal timers are not an ItemDataManager veto.
    private static bool FineDiningKey(string key) => key == SpoilageClock.ExpiryDataKey ||
        key == FreshnessRuntime.AssignedLifetimeDataKey || key == SpoilageClock.SpoiledDataKey;
    private static bool UnknownManagerKey(string key) => key.IndexOf('#') >= 0 && key != EpicMagicKey;

    private static bool Owned(string key, Provider[] providers)
    {
        foreach (Provider provider in providers)
            if (key.StartsWith(provider.Prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool SameData(Dictionary<string, string>? actual, Dictionary<string, string> expected)
    {
        if (actual == null || actual.Count != expected.Count) return false;
        foreach (KeyValuePair<string, string> pair in expected)
            if (!actual.TryGetValue(pair.Key, out string value) || value != pair.Value) return false;
        return true;
    }

    private static bool HasSignature(MethodInfo method, Type result, params Type[] parameters) => method.IsStatic &&
        !method.ContainsGenericParameters && method.ReturnType == result && method.GetMethodBody() != null &&
        method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters);

    private static MethodInfo Method(Type type, string name, Type result, params Type[] parameters)
    {
        MethodInfo? method = type.GetMethod(name, Declared, null, parameters, null);
        Require(method != null && HasSignature(method, result, parameters), type.FullName + "." + name + " signature");
        return method!;
    }

    private static void Match(MethodInfo method, string expected) => Require(
        EpicLootStackCompatibility.Fingerprint(PatchProcessor.GetOriginalInstructions(method)) == expected,
        "unrecognized relevant method body " + method.DeclaringType?.FullName + "." + method.Name);

    private static string ExceptionFingerprint(MethodInfo method)
    {
        var code = PatchProcessor.GetOriginalInstructions(method);
        var flow = code.Select(i => new CodeInstruction(i)).ToList();
        foreach (CodeInstruction instruction in flow) instruction.blocks.Clear();
        var normalized = new StringBuilder(EpicLootStackCompatibility.Fingerprint(flow)).Append('\n');
        int ordinal = 0;
        foreach (CodeInstruction instruction in code)
        {
            foreach (ExceptionBlock block in instruction.blocks)
                normalized.Append(ordinal).Append(':').Append(block.blockType).Append(':')
                    .Append(block.catchType?.FullName ?? "").Append('\n');
            if (instruction.opcode != OpCodes.Nop) ordinal++;
        }
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized.ToString()))).Replace("-", "");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Unsupported ItemDataManager stack contract: " + reason);
    }
}
