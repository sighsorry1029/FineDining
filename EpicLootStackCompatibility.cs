using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FineDining;

// EpicLoot replaces AutoStackItems rather than extending the original method.
// FineDining owns expiry in that replacement; FLG owns only its candidate filter.
internal static class EpicLootStackCompatibility
{
    private const string HarmonyId = "sighsorry.FineDining.EpicLootStacks";
    private const string EntryId = HarmonyId + ".Entry";
    private const string EpicOwner = "randyknapp.mods.epicloot";
    private const string FlgOwner = "sighsorry.FeedLikeGrandma.EpicLootStacks";
    private const string FlgDropOwner = "sighsorry.FeedLikeGrandma.FeedBarrelDrops";
    private const BindingFlags Declared = BindingFlags.Static | BindingFlags.Instance |
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly MethodInfo Target = AccessTools.DeclaredMethod(typeof(ItemDrop), "AutoStackItems", Type.EmptyTypes);
    private static readonly MethodInfo EntryMethod = typeof(EpicLootStackCompatibility).GetMethod(nameof(BeforeAutoStack), Declared)!;
    private static readonly MethodInfo TranspilerMethod = typeof(EpicLootStackCompatibility).GetMethod(nameof(Transpiler), Declared)!;
    private static readonly MethodInfo ComposeMethod = typeof(DecayRuntime).GetMethod(nameof(DecayRuntime.ComposeGroundStackExpiry), Declared)!;
    private static readonly MethodInfo VanillaTranspiler = typeof(ItemDropAutoStackSpoilagePatch).GetMethod("Transpiler", Declared)!;
    private static readonly MethodInfo ClockGuard = typeof(ItemDropClockReadinessPatch).GetMethod("Prefix", Declared)!;
    private static Harmony? _entry, _replacement;
    private static MethodInfo? _prefix;
    private static MethodInfo[] _helpers = Array.Empty<MethodInfo>();
    private static Patch? _orderedEntry, _orderedEpic;
    private static bool _composed, _inserted, _blocked;
    private static string? _lastWarning;
    internal static bool IsInstalled => _replacement != null;

    // Identifies relevant methods only, independent of DLL version/MVID/token or
    // branch offsets. Unrelated updates do not require a new compatibility list.
    private const string PrefixContract = "09BDA7EC447CECFF365BBAAAEECEE508B9E698A57123D74D4D16302FEEA9E8ED";
    private const string StacksContract = "B6C5B4EADEB0350D1986322450BFC704A185049CDE27F5D9F5593A9F255CA982";
    private const string RawContract = "D1499ABD5712E108C56D0A6659725797894024B7ADFC3FDF1B9C46E11355979C";

    internal static void Initialize()
    {
        if (_entry != null) return;
        try
        {
            _entry = new Harmony(EntryId);
            _entry.Patch(Target, prefix: new HarmonyMethod(EntryMethod)
            { priority = Priority.First, before = new[] { EpicOwner } });
        }
        catch (Exception error)
        {
            _blocked = true;
            try { _entry?.UnpatchSelf(); _entry = null; }
            catch (Exception) { }
            try { FineDiningPlugin.Log.LogWarning("EpicLoot spoilage compatibility could not install its stack entry guard: " + error.GetBaseException().Message); }
            catch (Exception) { }
            return;
        }
        try { Ensure(Harmony.GetPatchInfo(Target)); }
        catch (Exception error) { Block(error); }
    }

    // No polling loop: the call boundary also catches EpicLoot registered after
    // Start. Already-bound calls inspect descriptors; IL is cached until rebuild.
    private static bool BeforeAutoStack(bool __runOriginal)
    {
        if (!__runOriginal) return false;
        try
        {
            Patches patches = Harmony.GetPatchInfo(Target);
            // A refusal is scoped to EpicLoot's replacement. If that route has
            // been removed, leave ordinary game auto-stacking available.
            if (!patches.Prefixes.Any(patch => patch.owner == EpicOwner))
            { Ensure(patches); return true; }
            if (_blocked) return false;
            Ensure(patches);
            return true;
        }
        catch (Exception error) { Block(error); return false; }
    }

    internal static bool Ensure(Patches patches)
    {
        Patch[] epic = patches.Prefixes.Where(patch => patch.owner == EpicOwner).ToArray();
        if (epic.Length == 0)
        {
            if (_prefix != null) RemoveReplacement();
            return false;
        }
        Require(epic.Length == 1, "ambiguous EpicLoot replacement");
        MethodInfo prefix = epic[0].PatchMethod;
        Require(!epic[0].wrapTryCatch, "EpicLoot exception policy changed");
        Patch[] clockGuards = patches.Prefixes.Where(patch => patch.PatchMethod == ClockGuard).ToArray();
        Require(clockGuards.Length == 1 && clockGuards.All(patch =>
            patch.owner == FineDiningPlugin.ModGUID && !patch.wrapTryCatch && patch.priority == Priority.First &&
            patch.before.SequenceEqual(new[] { EntryId, EpicOwner }) && patch.after.Length == 0),
            "clock readiness guard changed");
        Require(patches.Prefixes.Count == 2 + clockGuards.Length && patches.Prefixes.Count(patch =>
            patch.owner == EntryId && patch.PatchMethod == EntryMethod && !patch.wrapTryCatch &&
            patch.priority == Priority.First && patch.before.SequenceEqual(new[] { EpicOwner }) && patch.after.Length == 0) == 1,
            "missing entry guard or additional original-method prefix");
        Patch entry = patches.Prefixes.Single(patch => patch.owner == EntryId);
        Require(!epic[0].before.Contains(EntryId), "EpicLoot prefix precedes its safety guard");
        if (!ReferenceEquals(_orderedEntry, entry) || !ReferenceEquals(_orderedEpic, epic[0]))
        {
            var ordered = PatchProcessor.GetSortedPatchMethods(Target, patches.Prefixes.ToArray());
            Require(ordered[clockGuards.Length] == EntryMethod &&
                (clockGuards.Length == 0 || ordered[0] == ClockGuard),
                "EpicLoot entry guard ordering changed");
            _orderedEntry = entry; _orderedEpic = epic[0];
        }
        Require(patches.Postfixes.Count == 0 && patches.Finalizers.Count == 0 && patches.ILManipulators.Count == 0,
            "uninspected original-method wrapper");
        Require(patches.Transpilers.Count(patch => patch.owner == FineDiningPlugin.ModGUID &&
            patch.PatchMethod == VanillaTranspiler && !patch.wrapTryCatch) == 1,
            "original FineDining expiry patch missing");
        Require(patches.Transpilers.Count(patch => patch.owner == FlgDropOwner) <= 1, "duplicated FLG original stack patch");
        foreach (Patch patch in patches.Transpilers)
        {
            if (patch.owner == FineDiningPlugin.ModGUID && patch.PatchMethod == VanillaTranspiler && !patch.wrapTryCatch) continue;
            Require(patch.owner == FlgDropOwner && patch.PatchMethod.DeclaringType?.FullName == "FeedLikeGrandma.FeedBarrelDropProtection" &&
                patch.PatchMethod.Name == "Transpiler" && !patch.wrapTryCatch,
                "original-method transpiler bypassed by EpicLoot");
        }
        if (_prefix != null)
        {
            Require(_prefix == prefix, "running EpicLoot replacement changed; restart required");
            ValidateInstalled();
            return true;
        }
        MethodInfo[] helpers = ValidateEpicContract(prefix);
        foreach (MethodInfo method in helpers) RequireUnpatched(method);
        RequireUnpatched(ComposeMethod);
        ValidateReplacementPatches(prefix, false);
        _prefix = prefix;
        _helpers = helpers;
        try
        {
            _replacement = new Harmony(HarmonyId);
            _replacement.Patch(prefix, transpiler: new HarmonyMethod(TranspilerMethod)
            { priority = Priority.Last, after = new[] { FlgOwner } });
            ValidateInstalled();
        }
        catch
        {
            RemoveReplacement();
            throw;
        }
        return true;
    }

    internal static MethodInfo[] ValidateEpicContract(MethodInfo prefix)
    {
        Require(prefix.IsPrivate && prefix.Name == "Prefix" &&
            prefix.DeclaringType?.FullName == "EpicLoot.MagicItemData+ItemDrop_AutoStackItems_Patch" &&
            HasSignature(prefix, typeof(bool), typeof(ItemDrop), typeof(bool)), "EpicLoot replacement signature/access");
        ParameterInfo[] parameters = prefix.GetParameters();
        Require(parameters[0].Name == "__instance" && parameters[1].Name == "__runOriginal", "EpicLoot Harmony binding");
        Require(!prefix.GetCustomAttributes(typeof(HarmonyArgument), false).Any() &&
            !prefix.DeclaringType!.GetCustomAttributes(typeof(HarmonyArgument), false).Any() &&
            parameters.All(parameter => !parameter.GetCustomAttributes(typeof(HarmonyArgument), false).Any()),
            "EpicLoot argument remapping");
        Type owner = prefix.DeclaringType!.DeclaringType!;
        MethodInfo stacks = Method(owner, "StacksWith", typeof(bool), typeof(ItemDrop.ItemData), typeof(ItemDrop.ItemData));
        MethodInfo raw = Method(owner, "RawOf", typeof(string), typeof(ItemDrop.ItemData));
        Require(stacks.IsAssembly && raw.IsPublic, "EpicLoot helper access");
        Match(prefix, PrefixContract); Match(stacks, StacksContract); Match(raw, RawContract);
        return new[] { stacks, raw };
    }

    private static MethodInfo? ValidateReplacementPatches(MethodInfo prefix, bool requireOwn)
    {
        Patches? patches = Harmony.GetPatchInfo(prefix);
        if (patches == null)
        {
            Require(!requireOwn, "missing expiry patch");
            return null;
        }
        Require(patches.Prefixes.Count == 0 && patches.Postfixes.Count == 0 && patches.Finalizers.Count == 0 &&
            patches.ILManipulators.Count == 0, "uninspected replacement wrapper");
        int own = 0;
        MethodInfo? filter = null;
        foreach (Patch patch in patches.Transpilers)
        {
            if (patch.owner == HarmonyId && patch.PatchMethod == TranspilerMethod && !patch.wrapTryCatch &&
                patch.after.Contains(FlgOwner)) { own++; continue; }
            Require(filter == null && patch.owner == FlgOwner &&
                patch.PatchMethod.DeclaringType?.FullName == "FeedLikeGrandma.FeedEpicLootStackCompatibility" &&
                patch.PatchMethod.Name == "Transpiler" && !patch.wrapTryCatch && patch.before.Contains(HarmonyId),
                "unrecognized replacement transpiler; update FLG and FineDining together");
            Type? type = patch.PatchMethod.DeclaringType!.Assembly.GetType("FeedLikeGrandma.FeedBarrelDropProtection");
            Require(type != null, "missing FLG filter owner");
            filter = type!.GetMethod("FilterStackCandidate", Declared, null,
                new[] { typeof(ItemDrop), typeof(ItemDrop) }, null);
            // FLG deliberately catches marker read failures and rejects that
            // candidate. Its filter is not subject to EpicLoot's no-EH contract.
            Require(filter != null && filter.IsStatic && filter.ReturnType == typeof(ItemDrop), "FLG filter signature");
        }
        Require(own == (requireOwn ? 1 : 0), "missing or duplicated expiry patch");
        return filter;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        _composed = false; _inserted = false;
        List<CodeInstruction> code = instructions.ToList();
        // Inspect a copy: rejection cannot damage another mod's registration.
        var baseline = new List<CodeInstruction>(code);
        try
        {
            StripFilter(baseline, null);
            if (Fingerprint(baseline) != PrefixContract) return code;
        }
        catch (Exception) { return code; }
        int lookup = code.FindIndex(IsCandidateLookup);
        int candidateLoad = lookup + 2;
        if (code[lookup + 1].opcode == OpCodes.Ldarg_0) candidateLoad += 2;
        Require(code[candidateLoad].IsLdloc(), "EpicLoot candidate load");
        int store = code.FindIndex(IsStackStore);
        int start = store - 7;
        Require(start >= 0 && code[start].IsLdloc() && code[start + 1].opcode == OpCodes.Dup,
            "EpicLoot increment insertion point");
        var receiver = new CodeInstruction(OpCodes.Ldarg_0);
        receiver.labels.AddRange(code[start].labels); code[start].labels.Clear();
        code.InsertRange(start, new[] { receiver,
            new CodeInstruction(code[candidateLoad].opcode, code[candidateLoad].operand),
            new CodeInstruction(OpCodes.Call, ComposeMethod) });
        _inserted = true;
        return code;
    }

    private static bool IsStackStore(CodeInstruction instruction) => instruction.opcode == OpCodes.Stfld &&
        instruction.operand is FieldInfo field && field.DeclaringType == typeof(ItemDrop.ItemData) && field.Name == "m_stack";

    // With no expected argument this recognizes only the supported insertion
    // shape during a Harmony rebuild; final validation also checks its owner.
    private static MethodInfo? StripFilter(List<CodeInstruction> code, MethodInfo? expected)
    {
        int lookup = code.FindIndex(IsCandidateLookup);
        Require(lookup >= 0, "missing candidate lookup");
        MethodInfo? filter = code.Select(instruction => instruction.operand).OfType<MethodInfo>().FirstOrDefault(method =>
            method.DeclaringType?.FullName == "FeedLikeGrandma.FeedBarrelDropProtection" && method.Name == "FilterStackCandidate");
        if (filter == null) { Require(expected == null, "FLG candidate guard missing"); return null; }
        Require(filter.IsStatic && filter.ReturnType == typeof(ItemDrop) &&
            filter.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(ItemDrop), typeof(ItemDrop) }) &&
            (expected == null || expected == filter) && code.Count(instruction => instruction.Calls(filter)) == 1 &&
            code[lookup + 1].opcode == OpCodes.Ldarg_0 && code[lookup + 2].Calls(filter), "FLG candidate guard shape");
        RequireNoEntry(code[lookup + 1]); RequireNoEntry(code[lookup + 2]);
        code.RemoveRange(lookup + 1, 2);
        return filter;
    }

    private static void ValidateInstalled()
    {
        Require(_prefix != null, "missing EpicLoot binding");
        MethodInfo? filter = ValidateReplacementPatches(_prefix!, true);
        foreach (MethodInfo helper in _helpers) RequireUnpatched(helper);
        RequireUnpatched(ComposeMethod);
        if (_composed) return;
        List<CodeInstruction> code = PatchProcessor.GetCurrentInstructions(_prefix!);
        Require(_inserted, "expiry insertion failed; an older FLG bridge may already own expiry");
        int call = code.FindIndex(instruction => instruction.Calls(ComposeMethod));
        int store = code.FindIndex(IsStackStore);
        Require(call >= 2 && code.Count(instruction => instruction.Calls(ComposeMethod)) == 1 && call == store - 8 &&
            code[call - 2].opcode == OpCodes.Ldarg_0 && code[call - 1].IsLdloc(), "expiry call missing, duplicated or moved");
        int lookup = code.FindIndex(IsCandidateLookup);
        int candidateStore = lookup + (filter == null ? 1 : 3);
        Require(code[candidateStore].IsStloc() && LocalIndex(code[call - 1]) == LocalIndex(code[candidateStore]), "expiry candidate changed");
        RequireNoEntry(code[call - 1]); RequireNoEntry(code[call]);
        Require(code[call - 2].blocks.Count == 0, "expiry exception entry");
        code[call + 1].labels.AddRange(code[call - 2].labels); code.RemoveRange(call - 2, 3);
        MethodInfo? actualFilter = StripFilter(code, filter);
        Require(actualFilter == filter && Fingerprint(code) == PrefixContract, "replacement flow changed");
        _composed = true;
    }

    private static void Block(Exception error)
    {
        _blocked = true;
        string reason = error.GetBaseException().Message;
        if (_lastWarning == reason) return;
        _lastWarning = reason;
        try { FineDiningPlugin.Log.LogWarning("EpicLoot ground auto-stacking paused before item changes; other storage and spoilage remain available. " + reason); }
        catch (Exception) { }
    }

    private static void RemoveReplacement()
    {
        _replacement?.UnpatchSelf();
        _replacement = null; _prefix = null; _helpers = Array.Empty<MethodInfo>();
        _composed = false; _inserted = false;
    }

    internal static void Shutdown()
    {
        _blocked = true;
        // If a detour cannot be removed, retain its guard and state rather than
        // leave a live callback behind an apparently completed shutdown.
        RemoveReplacement();
        _entry?.UnpatchSelf();
        _entry = null;
        _blocked = false; _lastWarning = null;
        _orderedEntry = null; _orderedEpic = null;
    }

    private static bool IsCandidateLookup(CodeInstruction instruction) => instruction.operand is MethodInfo method &&
        (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && method.DeclaringType == typeof(Component) &&
        method.Name == "GetComponent" && method.IsGenericMethod && method.GetGenericArguments().SequenceEqual(new[] { typeof(ItemDrop) });
    private static int LocalIndex(CodeInstruction instruction)
    {
        if (instruction.opcode == OpCodes.Stloc_0 || instruction.opcode == OpCodes.Ldloc_0) return 0;
        if (instruction.opcode == OpCodes.Stloc_1 || instruction.opcode == OpCodes.Ldloc_1) return 1;
        if (instruction.opcode == OpCodes.Stloc_2 || instruction.opcode == OpCodes.Ldloc_2) return 2;
        if (instruction.opcode == OpCodes.Stloc_3 || instruction.opcode == OpCodes.Ldloc_3) return 3;
        return instruction.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(instruction.operand, CultureInfo.InvariantCulture);
    }
    private static bool HasSignature(MethodInfo method, Type result, params Type[] args) => method.IsStatic &&
        method.ReturnType == result && !method.ContainsGenericParameters && method.GetMethodBody() != null &&
        method.GetMethodBody()!.ExceptionHandlingClauses.Count == 0 && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(args);
    private static MethodInfo Method(Type type, string name, Type result, params Type[] args)
    {
        MethodInfo? method = type.GetMethod(name, Declared, null, args, null);
        Require(method != null && HasSignature(method, result, args), type.FullName + "." + name + " signature");
        return method!;
    }
    private static void RequireUnpatched(MethodInfo method)
    {
        Patches? info = Harmony.GetPatchInfo(method);
        Require(info == null || !info.Owners.Any(), "uninspected patch on " + method.DeclaringType?.FullName + "." + method.Name);
    }
    private static void RequireNoEntry(CodeInstruction instruction) => Require(instruction.labels.Count == 0 && instruction.blocks.Count == 0,
        "branch/exception entry inside replacement guard");
    private static void Match(MethodInfo method, string expected) => Require(Fingerprint(PatchProcessor.GetOriginalInstructions(method)) == expected,
        "unrecognized relevant method body " + method.DeclaringType?.FullName + "." + method.Name);
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Unsupported EpicLoot spoilage contract: " + reason);
    }

    internal static string Fingerprint(List<CodeInstruction> code)
    {
        Require(code.All(instruction => instruction.blocks.Count == 0), "exception flow in relevant method");
        var ordinal = new int[code.Count + 1];
        int count = 0;
        for (int i = 0; i < code.Count; i++) { ordinal[i] = count; if (code[i].opcode != OpCodes.Nop) count++; }
        ordinal[code.Count] = count;
        int Target(Label label)
        {
            int index = code.FindIndex(instruction => instruction.labels.Contains(label));
            Require(index >= 0, "unresolved branch target");
            return ordinal[index];
        }
        string TypeName(Type type)
        {
            if (type.IsGenericParameter) return "!" + type.GenericParameterPosition;
            if (type.IsGenericType) return type.GetGenericTypeDefinition().FullName + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
            return type.FullName ?? type.Name;
        }
        string Operand(object? value)
        {
            if (value is Label label) return "@" + Target(label);
            if (value is Label[] labels) return string.Join(",", labels.Select(branchLabel => "@" + Target(branchLabel)));
            if (value is LocalVariableInfo local) return local.LocalIndex.ToString(CultureInfo.InvariantCulture);
            if (value is MethodBase method) return TypeName(method.DeclaringType!) + "::" + method.Name +
                (method is MethodInfo generic && generic.IsGenericMethod ? "<" + string.Join(",", generic.GetGenericArguments().Select(TypeName)) + ">" : "") +
                "(" + string.Join(",", method.GetParameters().Select(parameter => TypeName(parameter.ParameterType))) + ")" +
                (method is MethodInfo returns ? ":" + TypeName(returns.ReturnType) : "");
            if (value is FieldInfo field) return TypeName(field.DeclaringType!) + "::" + field.Name + ":" + TypeName(field.FieldType);
            if (value is Type type) return TypeName(type);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        var text = new StringBuilder();
        foreach (CodeInstruction instruction in code)
        {
            if (instruction.opcode == OpCodes.Nop) continue;
            string name = instruction.opcode.Name!;
            string operand = Operand(instruction.operand);
            if (instruction.IsLdloc() || instruction.IsStloc())
            { name = instruction.IsLdloc() ? "ldloc" : "stloc"; operand = LocalIndex(instruction).ToString(CultureInfo.InvariantCulture); }
            else if (name.EndsWith(".s", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 2);
            text.Append(name).Append(' ').Append(operand).Append('\n');
        }
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
    }
}
