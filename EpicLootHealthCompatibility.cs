using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace FineDining;

// EpicLoot's additive effect normally runs inside vanilla UpdateFood, whose body
// FineDining replaces. The percentage/status-effect path already runs via SEMan.
internal static class EpicLootHealthCompatibility
{
    internal const string PluginGuid = "randyknapp.mods.epicloot";
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private delegate float EffectValue(Player player, string effect, float scale, ItemDrop.ItemData? ignored);
    private static Assembly? _assembly;
    private static EffectValue? _value;
    private static AccessTools.FieldRef<string>? _effect;
    private static MethodInfo? _helper, _transpiler;
    private static readonly MethodInfo UpdateFood = AccessTools.DeclaredMethod(typeof(Player), "UpdateFood", new[] { typeof(float), typeof(bool) });
    private static bool _failed;

    internal static float Add(Player player, float foodRegen)
    {
        if (_failed || !ReferenceEquals(player, Player.m_localPlayer)) return foodRegen;
        try
        {
            if (_value == null)
            {
                if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin)) return foodRegen;
                Bind(plugin.Instance.GetType().Assembly);
            }
            // Preserve an intentionally disabled EpicLoot effect. Recheck patch
            // descriptors on a regen tick, not in the per-frame food update.
            Patches? patches = Harmony.GetPatchInfo(UpdateFood);
            if (patches == null || patches.Transpilers.Count(p => p.owner == PluginGuid &&
                p.PatchMethod == _transpiler && !p.wrapTryCatch) != 1) return foodRegen;
            if (Harmony.GetPatchInfo(_helper!)?.Owners.Any() == true ||
                Harmony.GetPatchInfo(_transpiler!)?.Owners.Any() == true)
                throw new InvalidOperationException("EpicLoot additive helper has an unsupported external patch");
            return foodRegen + _value!(player, _effect!(), 1f, null);
        }
        catch (Exception error)
        {
            _failed = true;
            _value = null; _effect = null;
            FineDiningPlugin.Log.LogWarning("EpicLoot additive food regeneration unavailable; normal food regeneration remains active: " +
                error.GetBaseException().Message);
            return foodRegen;
        }
    }

    internal static void Bind(Assembly assembly)
    {
        if (_assembly != null && _assembly != assembly)
            throw new InvalidOperationException("EpicLoot assembly changed; restart required");
        Type extensions = assembly.GetType("EpicLoot.PlayerExtensions", true)!;
        Type effects = assembly.GetType("EpicLoot.MagicEffectType", true)!;
        Type regen = assembly.GetType("EpicLoot.MagicItemEffects.ModifyPlayerRegen+SEMan_ModifyRegen_Patches", true)!;
        MethodInfo? getter = extensions.GetMethod("GetTotalActiveMagicEffectValue", Static, null,
            new[] { typeof(Player), typeof(string), typeof(float), typeof(ItemDrop.ItemData) }, null);
        FieldInfo? field = effects.GetField("AddHealthRegen", Static);
        MethodInfo? helper = regen.GetMethod("AddHealthTicks", Static, null, new[] { typeof(float) }, null);
        MethodInfo? transpiler = regen.GetMethod("ModifyHealthRegen_Transpiler", Static, null,
            new[] { typeof(System.Collections.Generic.IEnumerable<CodeInstruction>) }, null);
        if (getter == null || !getter.IsPublic || getter.ReturnType != typeof(float) ||
            field == null || !field.IsPublic || field.FieldType != typeof(string) ||
            helper == null || !helper.IsPrivate || helper.ReturnType != typeof(float) ||
            transpiler == null || transpiler.ReturnType != typeof(System.Collections.Generic.IEnumerable<CodeInstruction>))
            throw new InvalidOperationException("EpicLoot health effect API changed");

        // Verify the responsibility of the private helper, then use its public
        // API instead of invoking private code or reimplementing effect caches.
        var code = PatchProcessor.GetOriginalInstructions(helper).Where(i => i.opcode != OpCodes.Nop).ToArray();
        if (code.Length != 8 || code[0].opcode != OpCodes.Ldarg_0 ||
            !code[1].LoadsField(AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer))) ||
            !code[2].LoadsField(field) || code[3].opcode != OpCodes.Ldc_R4 || !Equals(code[3].operand, 1f) ||
            code[4].opcode != OpCodes.Ldnull || !code[5].Calls(getter) ||
            code[6].opcode != OpCodes.Add || code[7].opcode != OpCodes.Ret || code.Any(i => i.blocks.Count != 0))
            throw new InvalidOperationException("EpicLoot additive health calculation changed");
        _value = (EffectValue)Delegate.CreateDelegate(typeof(EffectValue), getter);
        _effect = AccessTools.StaticFieldRefAccess<string>(field);
        _helper = helper; _transpiler = transpiler;
        _assembly = assembly;
    }

    internal static void Shutdown()
    {
        _assembly = null; _value = null; _effect = null; _helper = null; _transpiler = null; _failed = false;
    }
}
