#requires -Version 7.0
#requires -PSEdition Core
param(
    [string] $AssemblyPath = '',
    [string] $ManagedPath = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed',
    [string] $BepInExCorePath = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core'
)
$ErrorActionPreference = 'Stop'
# Test-only HarmonyX supports this desktop CLR. The mod still builds/deploys
# with the game's Harmony 2.9; no test dependency is copied to the game.
$testDependencies = @(
    'harmonyx/2.16.0', 'monomod.runtimedetour/25.3.3', 'monomod.utils/25.0.11',
    'monomod.core/1.3.3', 'monomod.backports/1.1.2', 'monomod.ilhelpers/1.1.0', 'mono.cecil/0.11.6'
) | ForEach-Object { "$env:USERPROFILE/.nuget/packages/$_/lib/netstandard2.0" }
if (-not $AssemblyPath) { $AssemblyPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin/Debug/FineDining.dll' }
$AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assemblyDirectory = Split-Path $AssemblyPath -Parent
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $name = ([Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
    foreach ($directory in ($testDependencies + @($BepInExCorePath, $ManagedPath, $assemblyDirectory))) {
        $path = Join-Path $directory $name
        if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::UnsafeLoadFrom($path) }
    }
    return $null
})
$game = [Reflection.Assembly]::UnsafeLoadFrom((Join-Path $ManagedPath 'assembly_valheim.dll'))
$harmony = [Reflection.Assembly]::LoadFrom((Join-Path $testDependencies[0] '0Harmony.dll'))
$plugin = [Reflection.Assembly]::UnsafeLoadFrom($AssemblyPath)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
# Read the unchanged original IL with Cecil: desktop CLR GetMethodBody cannot
# load the game's default-interface-method types. This tests the real matcher,
# not Harmony's importer or game detour installation.
$scratch = New-Object Reflection.Emit.DynamicMethod('CookingILScratch', [void], ([Type[]]@()))
$generator = $scratch.GetILGenerator()
Add-Type -Path "$env:USERPROFILE/.nuget/packages/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll"
$metadata = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ManagedPath 'assembly_valheim.dll'))
$body = ($metadata.MainModule.GetType('InventoryGui').Methods | Where-Object Name -eq DoCrafting).Body
if ($body.ExceptionHandlers.Count -ne 0) { throw 'Update the IL bridge to represent new exception boundaries.' }
$opcodes = @{}
foreach ($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) {
    $op = $field.GetValue($null); $opcodes[$op.Name] = $op
}
$labels = @{}
foreach ($instruction in $body.Instructions) { $labels[$instruction.Offset] = $generator.DefineLabel() }
$codes = New-Object 'System.Collections.Generic.List[HarmonyLib.CodeInstruction]'
foreach ($instruction in $body.Instructions) {
    $operand = $instruction.Operand
    if ($operand -is [Mono.Cecil.Cil.Instruction]) { $operand = $labels[$operand.Offset] }
    elseif ($operand -is [Mono.Cecil.Cil.Instruction[]]) { $operand = [Reflection.Emit.Label[]]@($operand | ForEach-Object { $labels[$_.Offset] }) }
    elseif ($operand -is [Mono.Cecil.Cil.VariableDefinition]) { $operand = [int]$operand.Index }
    elseif ($operand -is [Mono.Cecil.ParameterDefinition]) { $operand = [int]($operand.Index + 1) }
    elseif ($operand -is [Mono.Cecil.FieldReference]) { $operand = $game.ManifestModule.ResolveField($operand.MetadataToken.ToInt32()) }
    elseif ($operand -is [Mono.Cecil.MethodReference]) { $operand = $game.ManifestModule.ResolveMethod($operand.MetadataToken.ToInt32()) }
    elseif ($operand -is [Mono.Cecil.TypeReference]) { $operand = $game.ManifestModule.ResolveType($operand.MetadataToken.ToInt32()) }
    $code = [Activator]::CreateInstance($harmony.GetType('HarmonyLib.CodeInstruction'), [object[]]@($opcodes[$instruction.OpCode.Name], $operand))
    $code.labels.Add($labels[$instruction.Offset])
    $codes.Add($code)
}
$metadata.Dispose()
$patch = $plugin.GetType('FineDining.InventoryGuiCookingProductionBonusPatch', $true)
$inject = $patch.GetMethod('TryInject', $flags)
$helper = $patch.GetField('BonusHelperMethod', $flags).GetValue($null)
$originalCount = $codes.Count
# Reject drift atomically, including labels. The original instruction list is
# copied before mutation so each negative case starts with the original game IL.
foreach ($scenario in @('missing amount call', 'changed accumulation', 'missing bonus initialization')) {
    $bad = New-Object 'System.Collections.Generic.List[HarmonyLib.CodeInstruction]'
    foreach ($code in $codes) { $bad.Add([HarmonyLib.CodeInstruction]::new($code)) }
    if ($scenario -eq 'missing amount call') {
        $bad | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.DeclaringType.Name -eq 'Recipe' -and $_.operand.Name -eq 'GetAmount' } | ForEach-Object { $_.opcode = [Reflection.Emit.OpCodes]::Nop; $_.operand = $null }
    } else {
        $amountIndex = 0
        for ($i=0; $i -lt $bad.Count; $i++) {
            if ($bad[$i].operand -is [Reflection.FieldInfo] -and $bad[$i].operand.Name -eq 'm_craftBonusAmount') { $amountIndex=$i; break }
        }
        if ($scenario -eq 'changed accumulation') { $bad[$amountIndex+1].opcode = [Reflection.Emit.OpCodes]::Sub }
        else {
            $bonusSlot = $bad[$amountIndex+2].operand
            for ($i=1; $i -lt $amountIndex; $i++) {
                if ($bad[$i].opcode -eq [Reflection.Emit.OpCodes]::Stloc_S -and $bad[$i].operand -eq $bonusSlot -and $bad[$i-1].opcode -eq [Reflection.Emit.OpCodes]::Ldc_I4_0) {
                    $bad[$i-1].opcode = [Reflection.Emit.OpCodes]::Ldc_I4_1; break
                }
            }
        }
    }
    $before = @($bad | ForEach-Object { $_.ToString() + '/' + ($_.labels -join ',') + '/' + ($_.blocks -join ',') }) -join "`n"
    $badArguments = [object[]]@($bad.PSObject.BaseObject, $generator, '')
    if ($inject.Invoke($null, $badArguments)) { throw "Unsafe acceptance: $scenario" }
    $after = @($bad | ForEach-Object { $_.ToString() + '/' + ($_.labels -join ',') + '/' + ($_.blocks -join ',') }) -join "`n"
    if ($before -ne $after) { throw "Rejected pattern modified incoming IL: $scenario" }
}
$arguments = [object[]]@($codes.PSObject.BaseObject, $generator, '')
if (-not $inject.Invoke($null, $arguments)) { throw "Original DoCrafting rejected: $($arguments[2])" }
if ($codes.Count -ne $originalCount+16) { throw 'Unexpected insertion size.' }
$calls = @($codes | Where-Object { $_.opcode -eq [Reflection.Emit.OpCodes]::Call -and $_.operand -eq $helper })
if ($calls.Count -ne 1) { throw 'Expected exactly one helper call.' }

# Execute the emitted, actual patched bonus region in isolation. Only the gui
# fields, helper result and random source are stubs; the branch/loop and amount
# updates are the instructions read from the game plus the real TryInject output.
Add-Type @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
public class CookingBonusFragment {
    public float chance = 1;
    public int amount = 1;
    public static int Result, Calls, RandomCalls;
    public static int Bonus(object gui, object station, int count, float skill) { Calls++; return Result; }
    public static float Random() { RandomCalls++; return 0; }
    public static int[] Run(IList codes, int start, int end, int totalSlot, int bonusSlot,
        int stationSlot, int skillSlot, int batchSlot, int total, int batches, int result) {
        Result=result; Calls=RandomCalls=0;
        var dm=new DynamicMethod("PatchedBonusFragment",typeof(int[]),new[]{typeof(CookingBonusFragment)},typeof(CookingBonusFragment),true);
        var il=dm.GetILGenerator();
        // The original slot numbers are preserved; unused locals carry no state.
        var locals=new LocalBuilder[64];
        for(int i=0;i<locals.Length;i++) locals[i]=il.DeclareLocal(i==stationSlot?typeof(object):i==skillSlot?typeof(float):typeof(int));
        il.Emit(OpCodes.Ldc_I4,total); il.Emit(OpCodes.Stloc,locals[totalSlot]);
        il.Emit(OpCodes.Ldc_I4,batches); il.Emit(OpCodes.Stloc,locals[batchSlot]);
        il.Emit(OpCodes.Ldc_R4,1f); il.Emit(OpCodes.Stloc,locals[skillSlot]);
        var labels=new Dictionary<Label,Label>();
        var type=codes[0].GetType();
        var opField=type.GetField("opcode"); var operandField=type.GetField("operand"); var labelField=type.GetField("labels");
        for(int i=start;i<=end;i++) foreach(Label old in (IEnumerable)labelField.GetValue(codes[i])) labels[old]=il.DefineLabel();
        for(int i=start;i<=end;i++) {
            object code=codes[i];
            foreach(Label old in (IEnumerable)labelField.GetValue(code)) il.MarkLabel(labels[old]);
            if(i==end) break;
            OpCode op=(OpCode)opField.GetValue(code); object operand=operandField.GetValue(code);
            if(operand is MethodInfo) {
                var method=(MethodInfo)operand;
                if(method.Name=="CalculateCookingSkillBonusOrUseVanilla") il.Emit(op,typeof(CookingBonusFragment).GetMethod("Bonus"));
                else if(method.Name=="get_value" && method.DeclaringType.FullName=="UnityEngine.Random") il.Emit(op,typeof(CookingBonusFragment).GetMethod("Random"));
                else throw new Exception("Unexpected fragment call: "+method);
            } else if(operand is FieldInfo) {
                var field=(FieldInfo)operand;
                string name=field.Name=="m_craftBonusChance"?"chance":field.Name=="m_craftBonusAmount"?"amount":null;
                if(name==null) throw new Exception("Unexpected fragment field: "+field);
                il.Emit(op,typeof(CookingBonusFragment).GetField(name));
            } else if(operand is Label) { il.Emit(op,labels[(Label)operand]); }
            else if(op.OperandType==OperandType.ShortInlineVar || op.OperandType==OperandType.InlineVar) { il.Emit(op,locals[Convert.ToInt32(operand)]); }
            else if(operand==null) il.Emit(op);
            else if(operand is int) il.Emit(op,(int)operand);
            else throw new Exception("Unexpected operand: "+operand);
        }
        il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Newarr,typeof(int));
        il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ldloc,locals[totalSlot]); il.Emit(OpCodes.Stelem_I4);
        il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ldloc,locals[bonusSlot]); il.Emit(OpCodes.Stelem_I4); il.Emit(OpCodes.Ret);
        return ((Func<CookingBonusFragment,int[]>)dm.CreateDelegate(typeof(Func<CookingBonusFragment,int[]>)))(new CookingBonusFragment());
    }
}
'@
$helperIndex = $codes.IndexOf($calls[0]); $start = $helperIndex-4
$localIndexMethod = $patch.GetMethod('TryGetLocalIndex', $flags)
function Get-LocalIndex($instruction, [bool]$load) {
    $args = [object[]]@($instruction, $load, -1)
    if (-not $localIndexMethod.Invoke($null,$args)) { throw 'Cannot read fragment local.' }
    return [int]$args[2]
}
$stationSlot = Get-LocalIndex $codes[$start+1] $true
$totalSlot = Get-LocalIndex $codes[$start+2] $true
$skillSlot = Get-LocalIndex $codes[$start+3] $true
$bonusSlot = Get-LocalIndex $codes[$helperIndex+1] $false
$endLabel = $codes[$start+13].operand
$end = -1
for ($i=$start+16; $i -lt $codes.Count; $i++) { if ($codes[$i].labels.Contains($endLabel)) { $end=$i; break } }
if ($end -lt 0) { throw 'Missing original capacity-check exit.' }
$batchSlot = Get-LocalIndex $codes[$end-2] $true
foreach ($case in @(@(2,1,1,3,1,0), @(6,3,2,8,2,0), @(6,3,0,6,0,0), @(2,1,-1,3,1,1), @(6,3,-1,12,3,3))) {
    $result = [CookingBonusFragment]::Run($codes,$start,$end,$totalSlot,$bonusSlot,$stationSlot,$skillSlot,$batchSlot,$case[0],$case[1],$case[2])
    if ($result[0] -ne $case[3] -or $result[1] -ne $case[4] -or [CookingBonusFragment]::Calls -ne 1 -or [CookingBonusFragment]::RandomCalls -ne $case[5]) {
        throw "Wrong amount/control flow for $case : $result"
    }
}
Write-Output "PASS original DoCrafting IL: $originalCount -> $($codes.Count); one helper; 3 rejected drift cases; 5 emitted bonus/fallback cases."
Write-Output 'Isolated test uses test-only HarmonyX 2.16 and stubbed helper/random inputs; Unity/Mono detour installation and full crafting are not executed.'
