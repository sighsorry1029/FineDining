#requires -Version 7.0
#requires -PSEdition Core
param(
    [Parameter(Mandatory)][string] $PathOfValheimanDll,
    [string] $AssemblyPath = '',
    [string] $ManagedPath = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed',
    [string] $BepInExCorePath = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/core'
)
$ErrorActionPreference = 'Stop'
# Test-only desktop HarmonyX. No input DLL is rewritten or copied to the game.
$testDependencies = @(
    'harmonyx/2.16.0', 'monomod.runtimedetour/25.3.3', 'monomod.utils/25.0.11',
    'monomod.core/1.3.3', 'monomod.backports/1.1.2', 'monomod.ilhelpers/1.1.0', 'mono.cecil/0.11.6'
) | ForEach-Object { "$env:USERPROFILE/.nuget/packages/$_/lib/netstandard2.0" }
if (-not $AssemblyPath) { $AssemblyPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin/Debug/FineDining.dll' }
$AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$PathOfValheimanDll = (Resolve-Path -LiteralPath $PathOfValheimanDll).Path
$assemblyDirectory = Split-Path $AssemblyPath -Parent
$povDirectory = Split-Path $PathOfValheimanDll -Parent
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $name = ([Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
    foreach ($directory in ($testDependencies + @($BepInExCorePath, $ManagedPath, $assemblyDirectory, $povDirectory))) {
        $path = Join-Path $directory $name
        if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::UnsafeLoadFrom($path) }
    }
    return $null
})
$harmonyAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $testDependencies[0] '0Harmony.dll'))
$game = [Reflection.Assembly]::UnsafeLoadFrom((Join-Path $ManagedPath 'assembly_valheim.dll'))
$pov = [Reflection.Assembly]::UnsafeLoadFrom($PathOfValheimanDll)
$plugin = [Reflection.Assembly]::UnsafeLoadFrom($AssemblyPath)
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$compat = $plugin.GetType('FineDining.PathOfValheimanCompatibility', $true)
$bindingType = $compat.GetNestedType('Bindings', [Reflection.BindingFlags]::NonPublic)
$bindings = $bindingType.GetConstructors($instance)[0].Invoke([object[]]@($pov))
function Binding([string] $name) { $bindingType.GetField($name, $instance).GetValue($bindings) }
function Assert([bool] $condition, [string] $message) { if (-not $condition) { throw $message } }
Assert ((Binding FoodValue).IsPrivate) 'Expected original private PoV evaluator.'
Assert ($plugin.GetReferencedAssemblies().Name -notcontains 'PathOfValheiman') 'PoV must remain optional.'
Write-Output 'PASS original PoV runtime signatures and cached delegate bindings; no hard assembly reference.'

# Compile/install all four patches against the supplied DLL in this process.
# No PoV plugin startup or game method is executed by this installation check.
$installHarmony = [HarmonyLib.Harmony]::new('FineDining.PoVInstallTest')
try {
    foreach ($pair in @(@('FoodValue','ScaleFoodValue'), @('SnackEat','ScaleSnack'), @('BatchCook','PlanBatchCooking'))) {
        $null=$installHarmony.Patch((Binding $pair[0]),$null,$null,[HarmonyLib.HarmonyMethod]::new($compat.GetMethod($pair[1],$static)))
    }
    $null=$installHarmony.Patch((Binding BrewPrefix),
        [HarmonyLib.HarmonyMethod]::new($compat.GetMethod('BeforeBrewOutput',$static)),
        [HarmonyLib.HarmonyMethod]::new($compat.GetMethod('AfterBrewOutput',$static)))
} finally { $installHarmony.UnpatchSelf() }
Write-Output 'PASS isolated HarmonyX installation/removal of all four original PoV method patches.'

# Original IL -> Harmony instructions without publicizing inputs or importing
# unrelated Unity/default-interface-method bodies into the desktop runtime.
Add-Type -Path "$env:USERPROFILE/.nuget/packages/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll"
$metadata = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($PathOfValheimanDll)
$opcodes = @{}
foreach ($field in [Reflection.Emit.OpCodes].GetFields($static)) {
    $op = $field.GetValue($null); $opcodes[$op.Name] = $op
}
function Read-Codes([Reflection.MethodInfo] $method, [Reflection.Emit.ILGenerator] $generator) {
    $body = $metadata.MainModule.LookupToken($method.MetadataToken).Body
    Assert ($body.ExceptionHandlers.Count -eq 0) 'Update IL bridge for new exception boundaries.'
    foreach ($variable in $body.Variables) {
        $type = [Type]::GetType($variable.VariableType.FullName.Replace('/', '+'))
        if ($null -eq $type) { $type = $pov.ManifestModule.ResolveType($variable.VariableType.MetadataToken.ToInt32()) }
        $null = $generator.DeclareLocal($type)
    }
    $labels = @{}
    foreach ($instruction in $body.Instructions) { $labels[$instruction.Offset] = $generator.DefineLabel() }
    $codes = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
    foreach ($instruction in $body.Instructions) {
        $operand = $instruction.Operand
        if ($operand -is [Mono.Cecil.Cil.Instruction]) { $operand = $labels[$operand.Offset] }
        elseif ($operand -is [Mono.Cecil.Cil.Instruction[]]) { $operand = [Reflection.Emit.Label[]]@($operand | ForEach-Object { $labels[$_.Offset] }) }
        elseif ($operand -is [Mono.Cecil.Cil.VariableDefinition]) { $operand = [int]$operand.Index }
        elseif ($operand -is [Mono.Cecil.ParameterDefinition]) { $operand = [int]$operand.Index }
        elseif ($operand -is [Mono.Cecil.FieldReference]) { $operand = $pov.ManifestModule.ResolveField($operand.MetadataToken.ToInt32()) }
        elseif ($operand -is [Mono.Cecil.MethodReference]) { $operand = $pov.ManifestModule.ResolveMethod($operand.MetadataToken.ToInt32()) }
        elseif ($operand -is [Mono.Cecil.TypeReference]) { $operand = $pov.ManifestModule.ResolveType($operand.MetadataToken.ToInt32()) }
        $code = [HarmonyLib.CodeInstruction]::new($opcodes[$instruction.OpCode.Name], $operand)
        $code.labels.Add($labels[$instruction.Offset])
        $codes.Add($code)
    }
    return ,$codes
}
function Rewrite([string] $name, $codes, [Reflection.Emit.ILGenerator] $generator, [Reflection.MethodInfo] $method) {
    $args = [object[]]@(,$codes)
    if ($name -eq 'ScaleFoodValue') { $args = [object[]]@($codes, $generator, $method) }
    $result = $compat.GetMethod($name, $static).Invoke($null, $args)
    return ,([Collections.Generic.List[HarmonyLib.CodeInstruction]]::new([Collections.Generic.IEnumerable[HarmonyLib.CodeInstruction]]$result))
}
$rewrites = @{}
foreach ($pair in @(@('FoodValue','ScaleFoodValue'), @('SnackEat','ScaleSnack'), @('BatchCook','PlanBatchCooking'))) {
    $method = Binding $pair[0]
    $scratch = [Reflection.Emit.DynamicMethod]::new('PoVILScratch', [void], [Type[]]@())
    $generator = $scratch.GetILGenerator()
    $original = Read-Codes $method $generator
    $before = ($original | ForEach-Object { $_.ToString() + '/' + ($_.labels -join ',') }) -join "`n"
    $rewritten = Rewrite $pair[1] $original $generator $method
    $after = ($original | ForEach-Object { $_.ToString() + '/' + ($_.labels -join ',') }) -join "`n"
    Assert ($before -eq $after) 'Incoming original IL was mutated.'
    $rewrites[$pair[0]] = @{ Original=$original; Patched=$rewritten; Method=$method }
    Write-Output "PASS $($pair[0]) original IL rewrite: $($original.Count) -> $($rewritten.Count) instructions."
}
$metadata.Dispose()

# Reject changed snack destinations and RPC payloads without altering the input.
foreach ($scenario in @('missing snack stat', 'changed snack destination', 'RPC name', 'RPC count', 'RPC provenance', 'duplicate RPC')) {
    $key = if ($scenario -like '*snack*') { 'SnackEat' } else { 'BatchCook' }
    $codes = [Collections.Generic.List[HarmonyLib.CodeInstruction]]::new()
    foreach ($code in $rewrites[$key].Original) { $codes.Add([HarmonyLib.CodeInstruction]::new($code)) }
    if ($scenario -eq 'missing snack stat') {
        ($codes | Where-Object { $_.operand -is [Reflection.FieldInfo] -and $_.operand.Name -eq 'm_food' }).opcode = [Reflection.Emit.OpCodes]::Nop
    } elseif ($scenario -eq 'changed snack destination') {
        ($codes | Where-Object { $_.opcode -eq [Reflection.Emit.OpCodes]::Stfld -and $_.operand.Name -eq 'Health' }).opcode = [Reflection.Emit.OpCodes]::Nop
    } elseif ($scenario -eq 'RPC name') {
        ($codes | Where-Object { $_.operand -eq 'RPC_AddItem' }).operand = 'OtherRPC'
    } elseif ($scenario -eq 'RPC count') {
        ($codes | Where-Object { $_.opcode -eq [Reflection.Emit.OpCodes]::Ldc_I4_2 }).opcode = [Reflection.Emit.OpCodes]::Ldc_I4_3
    } elseif ($scenario -eq 'RPC provenance') {
        ($codes | Where-Object { $_.operand -is [Reflection.FieldInfo] -and $_.operand.Name -eq 'm_cheated' }).opcode = [Reflection.Emit.OpCodes]::Nop
    } else {
        $rpc = $codes | Where-Object { $_.operand -is [Reflection.MethodInfo] -and $_.operand.Name -eq 'InvokeRPC' }
        $codes.Add([HarmonyLib.CodeInstruction]::new($rpc))
    }
    $before = ($codes | ForEach-Object { $_.ToString() + '/' + ($_.labels -join ',') }) -join "`n"
    $name = if ($key -eq 'SnackEat') { 'ScaleSnack' } else { 'PlanBatchCooking' }
    $rejected = $false
    try { $null = Rewrite $name $codes $generator $rewrites[$key].Method } catch { $rejected = $true }
    Assert $rejected "Unsafe drift acceptance: $scenario"
    $after = ($codes | ForEach-Object { $_.ToString() + '/' + ($_.labels -join ',') }) -join "`n"
    Assert ($before -eq $after) "Rejected rewrite modified input: $scenario"
}
Write-Output 'PASS six atomic drift rejections for snack and cooking payload changes.'

# Run the actual inserted value/packet IL. Only the diet/freshness inputs and
# native name lookup / RPC transport are substituted; arithmetic, payload
# construction, argument order and return-struct field writes are emitted.
Add-Type @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
public static class PoVFragments {
    public static float ScaleValue;
    public static int Calls;
    public static object[] Packet;
    public static float Scale(object ignored) { Calls++; return ScaleValue; }
    public static string Name(object ignored) { return "DeerMeat"; }
    public static void Send(object view, string rpc, object[] args, object user, object station) {
        Calls++; Packet=new object[]{view,rpc,args,user,station};
    }
    static OpCode Op(object c) { return (OpCode)c.GetType().GetField("opcode").GetValue(c); }
    static object Operand(object c) { return c.GetType().GetField("operand").GetValue(c); }
    static void Emit(ILGenerator il, OpCode op, object arg, Dictionary<int,LocalBuilder> locals) {
        if(arg is LocalBuilder) {
            var old=(LocalBuilder)arg;
            if(!locals.ContainsKey(old.LocalIndex)) locals[old.LocalIndex]=il.DeclareLocal(old.LocalType);
            il.Emit(op,locals[old.LocalIndex]);
        } else if(arg is FieldInfo) il.Emit(op,(FieldInfo)arg);
        else if(arg is MethodInfo) il.Emit(op,(MethodInfo)arg);
        else if(arg is Type) il.Emit(op,(Type)arg);
        else if(arg is string) il.Emit(op,(string)arg);
        else if(arg==null) il.Emit(op);
        else throw new Exception("Unexpected fragment operand: "+arg);
    }
    public static object Food(IList codes, int start, int end, object input, float scale) {
        Calls=0; ScaleValue=scale;
        var dm=new DynamicMethod("PoVFoodReturn",typeof(object),new[]{typeof(object)},typeof(PoVFragments),true);
        var il=dm.GetILGenerator(); var locals=new Dictionary<int,LocalBuilder>();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Unbox_Any,input.GetType());
        for(int i=start;i<=end;i++) {
            var op=Op(codes[i]); var operand=Operand(codes[i]);
            if(operand is MethodInfo && ((MethodInfo)operand).Name=="DietScale") operand=typeof(PoVFragments).GetMethod("Scale");
            if(op==OpCodes.Ret) il.Emit(OpCodes.Box,input.GetType());
            Emit(il,op,operand,locals);
        }
        return ((Func<object,object>)dm.CreateDelegate(typeof(Func<object,object>)))(input);
    }
    public static float Snack(IList codes, int start, int end, object shared, float share, float freshness) {
        Calls=0; ScaleValue=freshness;
        var dm=new DynamicMethod("PoVSnackValue",typeof(float),new[]{typeof(object),typeof(object)},typeof(PoVFragments),true);
        var il=dm.GetILGenerator(); var locals=new Dictionary<int,LocalBuilder>();
        il.DeclareLocal(typeof(object)); var shareLocal=il.DeclareLocal(typeof(float));
        il.Emit(OpCodes.Ldc_R4,share); il.Emit(OpCodes.Stloc,shareLocal);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass,shared.GetType());
        for(int i=start;i<end;i++) {
            var operand=Operand(codes[i]);
            if(operand is MethodInfo && ((MethodInfo)operand).Name=="GetFoodStatMultiplier") operand=typeof(PoVFragments).GetMethod("Scale");
            Emit(il,Op(codes[i]),operand,locals);
        }
        il.Emit(OpCodes.Ret);
        return ((Func<object,object,float>)dm.CreateDelegate(typeof(Func<object,object,float>)))(shared,null);
    }
    public static void Batch(IList codes, int start, int end, object station, object user, object item) {
        Calls=0; Packet=null;
        var dm=new DynamicMethod("PoVCookPacket",typeof(void),new[]{typeof(object),typeof(object),typeof(object)},typeof(PoVFragments),true);
        var il=dm.GetILGenerator(); var locals=new Dictionary<int,LocalBuilder>();
        var stationLocal=il.DeclareLocal(station.GetType());
        il.DeclareLocal(typeof(object)); il.DeclareLocal(typeof(int)); var itemLocal=il.DeclareLocal(item.GetType());
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass,station.GetType()); il.Emit(OpCodes.Stloc,stationLocal);
        il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Castclass,item.GetType()); il.Emit(OpCodes.Stloc,itemLocal);
        for(int i=start;i<=end;i++) {
            var op=Op(codes[i]); var operand=Operand(codes[i]);
            if(operand is MethodInfo) {
                var method=(MethodInfo)operand;
                if(method.Name=="get_name") { operand=typeof(PoVFragments).GetMethod("Name"); op=OpCodes.Call; }
                else if(method.Name=="RequestAdd") operand=typeof(PoVFragments).GetMethod("Send");
                else throw new Exception("Unexpected packet call: "+method);
            }
            Emit(il,op,operand,locals);
        }
        il.Emit(OpCodes.Ret);
        ((Action<object,object,object>)dm.CreateDelegate(typeof(Action<object,object,object>)))(station,user,item);
    }
}
'@
$foodCodes = $rewrites.FoodValue.Patched
$valuesType = $rewrites.FoodValue.Method.ReturnType
$inputValue = [Activator]::CreateInstance($valuesType)
$valuesType.GetField('Health').SetValue($inputValue,[float]65)
$valuesType.GetField('Stamina').SetValue($inputValue,[float]40)
$valuesType.GetField('Eitr').SetValue($inputValue,[float]20)
$returnCount = 0
for ($i=0; $i -lt $foodCodes.Count; $i++) {
    if ($foodCodes[$i].operand -isnot [Reflection.MethodInfo] -or $foodCodes[$i].operand.Name -ne 'DietScale') { continue }
    $start=$i-2; $end=$i
    while ($foodCodes[$end].opcode -ne [Reflection.Emit.OpCodes]::Ret) { $end++ }
    foreach ($scale in @(0,0.25,1,1.8)) {
        $result = [PoVFragments]::Food($foodCodes,$start,$end,$inputValue,$scale)
        foreach ($field in $valuesType.GetFields($instance)) {
            Assert ([Math]::Abs($field.GetValue($result)-$field.GetValue($inputValue)*$scale) -lt 0.0001) 'Food scale applied incorrectly.'
        }
        Assert ([PoVFragments]::Calls -eq 1) 'Food return applied scale more than once.'
    }
    $returnCount++
}
Assert ($returnCount -eq @($rewrites.FoodValue.Original | Where-Object opcode -eq ([Reflection.Emit.OpCodes]::Ret)).Count) 'Not all return paths were tested.'
$shared = [Activator]::CreateInstance($game.GetType('ItemDrop+ItemData+SharedData'))
$snackCodes = $rewrites.SnackEat.Patched
foreach ($name in @('m_food','m_foodStamina','m_foodEitr')) {
    $shared.GetType().GetField($name).SetValue($shared,[float]80)
    $start=0
    while (-not ($snackCodes[$start].operand -is [Reflection.FieldInfo] -and $snackCodes[$start].operand.Name -eq $name)) { $start++ }
    $end=$start
    while ($snackCodes[$end].opcode -ne [Reflection.Emit.OpCodes]::Stfld) { $end++ }
    foreach ($freshness in @(0.25,1)) {
        $result = [PoVFragments]::Snack($snackCodes,$start,$end,$shared,0.5,$freshness)
        Assert ([Math]::Abs($result-80*0.5*$freshness) -lt 0.0001 -and [PoVFragments]::Calls -eq 1) 'Snack freshness/share composition failed.'
    }
}
$station = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($game.GetType('CookingStation'))
$view = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($game.GetType('ZNetView'))
$station.GetType().GetField('m_nview',$instance).SetValue($station,$view)
$item = [Activator]::CreateInstance($game.GetType('ItemDrop+ItemData'))
$user = [object]::new()
$batchCodes = $rewrites.BatchCook.Patched
$rpcStart=0
while ($batchCodes[$rpcStart].operand -ne 'RPC_AddItem') { $rpcStart++ }
$rpcEnd=$rpcStart
while (-not ($batchCodes[$rpcEnd].operand -is [Reflection.MethodInfo] -and $batchCodes[$rpcEnd].operand.Name -eq 'RequestAdd')) { $rpcEnd++ }
foreach ($cheated in @($false,$true)) {
    $item.m_cheated=$cheated
    [PoVFragments]::Batch($batchCodes,$rpcStart-2,$rpcEnd,$station,$user,$item)
    $packet=[PoVFragments]::Packet
    Assert ([PoVFragments]::Calls -eq 1 -and [object]::ReferenceEquals($packet[0],$view) -and $packet[1] -eq 'RPC_AddItem') 'Batch changed destination or RPC count.'
    Assert ($packet[2].Count -eq 2 -and $packet[2][0] -eq 'DeerMeat' -and $packet[2][1] -eq $cheated) 'Batch changed ingredient/provenance.'
    Assert ([object]::ReferenceEquals($packet[3],$user) -and [object]::ReferenceEquals($packet[4],$station)) 'Batch changed user/station argument order.'
}
Write-Output "PASS emitted IL: $returnCount food return paths with four scales, six snack values, two batch provenance payloads."

# Exercise the real cancellation guards with HarmonyX, not just direct calls.
# The synthetic original has the game parameter types but no Unity body.
Add-Type @'
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
public static class PoVOrdering {
    public static bool Result;
    public static int Originals, PrefixEffects, WrappedEffects;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool Cancel(ref bool __result) { PrefixEffects++; __result=Result; return false; }
    public static void Wrapped() { WrappedEffects++; }
    public static Type Target(Type item) {
        var a=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("PoVOrderingTarget"),AssemblyBuilderAccess.Run);
        var t=a.DefineDynamicModule("main").DefineType("FoodTarget",TypeAttributes.Public);
        foreach(var name in new[]{"CanEat","EatFood","UpdateFood"}) {
            Type[] args=name=="CanEat"?new[]{item,typeof(bool)}:name=="EatFood"?new[]{item}:new[]{typeof(float),typeof(bool)};
            var method=t.DefineMethod(name,MethodAttributes.Public|MethodAttributes.Static,typeof(bool),args);
            for(int i=0;i<args.Length;i++) method.DefineParameter(i+1,ParameterAttributes.None,name=="UpdateFood"?(i==0?"dt":"forceUpdate"):(i==0?"item":"showMessages"));
            method.SetImplementationFlags(MethodImplAttributes.NoInlining);
            var il=method.GetILGenerator();
            il.Emit(OpCodes.Ldsfld,typeof(PoVOrdering).GetField("Originals")); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stsfld,typeof(PoVOrdering).GetField("Originals"));
            il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
        }
        return t.CreateType();
    }
}
'@
$target = [PoVOrdering]::Target($game.GetType('ItemDrop+ItemData'))
$fdHarmony = [HarmonyLib.Harmony]::new('FineDining.PoVCompatibilityTest')
$povHarmony = [HarmonyLib.Harmony]::new('treextr.pathofvalheiman')
$fdPatches = $plugin.GetType('FineDining.DietPlayerFoodPatches')
$cancel = [PoVOrdering].GetMethod('Cancel')
try {
    foreach ($name in @('CanEat','EatFood','UpdateFood')) {
        $method=$target.GetMethod($name)
        $fdPatch=[HarmonyLib.HarmonyMethod]::new($fdPatches.GetMethod($name+'Prefix',$static))
        $null=$fdHarmony.Patch($method,$fdPatch)
        $povPatch=[HarmonyLib.HarmonyMethod]::new($cancel); $povPatch.priority=800
        $null=$povHarmony.Patch($method,$povPatch)
    }
    # These PoV prefixes were registered first. Patch the prefix method afterward,
    # as the actual compatibility module does, and check its new body is called.
    $null=$fdHarmony.Patch($cancel,$null,[HarmonyLib.HarmonyMethod]::new([PoVOrdering].GetMethod('Wrapped')))
    foreach ($result in @($false,$true)) {
        [PoVOrdering]::Result=$result
        foreach ($name in @('CanEat','EatFood','UpdateFood')) {
            $args=[object[]]@($null,$false)
            if ($name -eq 'EatFood') { $args=[object[]]@(,$null) }
            if ($name -eq 'UpdateFood') { $args=[object[]]@([float]0.1,$false) }
            Assert ($target.GetMethod($name).Invoke($null,$args) -eq $result) "FD replaced PoV's $name result."
        }
    }
    Assert ([PoVOrdering]::Originals -eq 0 -and [PoVOrdering]::PrefixEffects -eq 6 -and [PoVOrdering]::WrappedEffects -eq 6) 'Harmony cancellation or patch-method wrapping failed.'
} finally {
    $fdHarmony.UnpatchSelf(); $povHarmony.UnpatchSelf()
}
Write-Output 'PASS HarmonyX ordering, six cancelled food calls, preserved results, and wrapping an already registered prefix.'

# Exact temporary +1 conversion handling. Only the native prefab lookup result
# is supplied by the fixture; push/capture/count validation/pop are built code.
$compat.GetField('_bindings',$static).SetValue($null,$bindings)
$fermenter=[Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($game.GetType('Fermenter'))
$conversion=[Activator]::CreateInstance($game.GetType('Fermenter+ItemConversion'))
$otherConversion=[Activator]::CreateInstance($game.GetType('Fermenter+ItemConversion'))
$beforeBrew=$compat.GetMethod('BeforeBrewOutput',$static)
$afterBrew=$compat.GetMethod('AfterBrewOutput',$static)
$acceptBrew=$compat.GetMethod('IsBrewOutputCount',$static)
$endBrew=$compat.GetMethod('EndBrewOutput',$static)
function Begin-Brew([int] $count, [int] $after) {
    $args=[object[]]@($fermenter,$null)
    $null=$beforeBrew.Invoke($null,$args)
    $state=$args[1]
    $state.GetType().GetField('Conversion',$instance).SetValue($state,$conversion)
    $state.GetType().GetField('Before',$instance).SetValue($state,$count)
    $conversion.m_producedItems=$after
    $null=$afterBrew.Invoke($null,$args)
}
function Accept-Brew([int] $count, $recipe=$conversion) {
    $acceptBrew.Invoke($null,[object[]]@($fermenter,$recipe,$count))
}
foreach ($case in @(@(6,7,$true), @(6,6,$false), @(6,8,$false), @(2147483647,-2147483648,$false))) {
    Begin-Brew $case[0] $case[1]
    Assert ((Accept-Brew $case[0]) -eq $case[2]) 'Wrong temporary conversion acceptance.'
    Assert (-not (Accept-Brew $case[0] $otherConversion)) 'Different conversion was accepted.'
    $null=$endBrew.Invoke($null,[object[]]@($fermenter))
    Assert (-not (Accept-Brew $case[0])) 'Completed tap state leaked.'
}
Begin-Brew 6 7
Begin-Brew 7 8
Assert (Accept-Brew 7) 'Nested tap did not capture its count.'
$null=$endBrew.Invoke($null,[object[]]@($fermenter)); $conversion.m_producedItems=7
Assert (Accept-Brew 6) 'Nested tap did not restore outer state.'
$args=[object[]]@($fermenter,$null)
$null=$beforeBrew.Invoke($null,$args) # simulated prefix failure before its postfix
$null=$endBrew.Invoke($null,[object[]]@($fermenter))
Assert (Accept-Brew 6) 'Failed nested prefix removed the outer state.'
$null=$endBrew.Invoke($null,[object[]]@($fermenter))
Assert (-not (Accept-Brew 6)) 'Outer tap leaked.'
$null=$compat.GetMethod('Shutdown',$static).Invoke($null,@())
Assert ($compat.GetMethod('RegenInterval',$static).Invoke($null,[object[]]@(,$null)) -eq 10) 'No-PoV interval changed.'
Assert ($compat.GetMethod('AdvanceRegen',$static).Invoke($null,[object[]]@(,$null)) -eq 0) 'No-PoV timer reset changed.'
$math=$pov.GetType('PassiveTree.HealthRegenClockMath')
foreach ($factor in @(0.5,1,2,4)) {
    $interval=$math.GetMethod('Interval',$static).Invoke($null,[object[]]@([float]10,[float]$factor))
    $packet=$math.GetMethod('FoodPacket',$static).Invoke($null,[object[]]@([float]20,[float]$factor))
    $remainder=$math.GetMethod('Remainder',$static).Invoke($null,[object[]]@([float]($interval+0.2),[float]10,[float]$factor))
    Assert ([Math]::Abs($packet/$interval-2) -lt 0.0001 -and [Math]::Abs($remainder-0.2) -lt 0.0001) 'PoV regen cadence changed total recovery/remainder.'
}
Write-Output 'PASS strict fermenter count/identity, nested/failed-prefix cleanup, no-PoV defaults and original PoV regen math.'
Write-Output 'Isolated desktop checks only. Unity, full food consumption, RPC transport, item spawning and multiplayer were not executed.'
