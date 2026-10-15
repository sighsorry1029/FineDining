#requires -Version 7.0
param(
    [string] $AssemblyPath = "$(Split-Path -Parent $PSScriptRoot)/bin/Debug/FineDining.dll",
    [string] $ManagedPath = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed',
    [string] $CecilPath = "$env:USERPROFILE/.nuget/packages/mono.cecil/0.11.6/lib/netstandard2.0/Mono.Cecil.dll"
)
$ErrorActionPreference = 'Stop'
$AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$ManagedPath = (Resolve-Path -LiteralPath $ManagedPath).Path
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $path = Join-Path $ManagedPath (([Reflection.AssemblyName]$eventArgs.Name).Name + '.dll')
    if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::LoadFrom($path) }
    return $null
})
Add-Type -Path $CecilPath
$game = [Reflection.Assembly]::LoadFrom((Join-Path $ManagedPath 'assembly_valheim.dll'))
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$zdoType = $game.GetType('ZDO', $true)
$nativeCheck = $game.GetType('CheatUtils', $true).GetMethod('IsCheated', $flags, $null, [type[]]@($zdoType), $null)
$metadata = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($AssemblyPath)
try {
    $method = $metadata.MainModule.GetType('FineDining.FermenterCookingBonusSystem').Methods | Where-Object Name -eq CompleteDelayedTap
    $instructions = @($method.Body.Instructions)
    $keyLoads = @($instructions | Where-Object { $_.OpCode.Name -eq 'ldsfld' -and $_.Operand.FullName -eq 'System.Int32 ZDOVars::s_cheated' })
    if ($keyLoads.Count -ne 1) { throw 'Expected one barrel cheated-key lookup.' }
    $start = $keyLoads[0]
    # Replace only the Unity object lookup and the two external bool inputs.
    # Keep the compiled mod predicate, branch targets and bypass negation intact.
    $getZdo = $start.Previous
    $getView = $getZdo.Previous
    $delayedBranch = $getView.Previous.Previous
    if ($getZdo.Operand.Name -ne 'GetZDO' -or $getView.Operand.Name -ne 'GetNView' -or
        $getView.Previous.OpCode.Name -ne 'ldarg.0' -or $delayedBranch.OpCode.Name -notin @('brtrue', 'brtrue.s')) {
        throw 'Delayed-input or ZDO lookup contract changed; review the extraction boundary.'
    }
    $region = [Collections.Generic.List[Mono.Cecil.Cil.Instruction]]::new()
    $cursor = $start
    while ($null -ne $cursor -and $cursor.Operand.Name -ne 'OnCreateNew') {
        $region.Add($cursor)
        $cursor = $cursor.Next
    }
    if ($null -eq $cursor -or $cursor.Operand.DeclaringType.FullName -ne 'ItemDrop') { throw 'Missing item creation boundary.' }
    $end = $cursor
    if ($delayedBranch.Operand.Operand.Name -ne 'get_s_bypassCheatChecks') { throw 'Delayed input no longer joins the bypass check.' }
    $dynamic = [Reflection.Emit.DynamicMethod]::new('BuiltFermenterCheatedPredicate', [bool], [type[]]@([bool], $zdoType, [bool]))
    $il = $dynamic.GetILGenerator()
    $labels = @{}
    foreach ($instruction in @($region.ToArray()) + @($end)) { $labels[$instruction.Offset] = $il.DefineLabel() }
    $opcodes = @{}
    foreach ($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static')) {
        $opcode = $field.GetValue($null)
        $opcodes[$opcode.Name] = $opcode
    }
    $il.Emit([Reflection.Emit.OpCodes]::Ldarg_0)
    $il.Emit([Reflection.Emit.OpCodes]::Brtrue, $labels[$delayedBranch.Operand.Offset])
    $il.Emit([Reflection.Emit.OpCodes]::Ldarg_1)
    foreach ($instruction in $region) {
        $il.MarkLabel($labels[$instruction.Offset])
        $operand = $instruction.Operand
        $opcode = $opcodes[$instruction.OpCode.Name]
        if ($operand -is [Mono.Cecil.Cil.Instruction]) {
            if (-not $labels.ContainsKey($operand.Offset)) { throw 'Branch leaves the predicate.' }
            $il.Emit($opcode, $labels[$operand.Offset])
        } elseif ($operand -is [Mono.Cecil.FieldReference]) {
            $il.Emit($opcode, $game.GetType($operand.DeclaringType.FullName, $true).GetField($operand.Name, $flags))
        } elseif ($operand -is [Mono.Cecil.MethodReference]) {
            if ($operand.FullName -eq 'System.Boolean PlayerProfile::get_s_bypassCheatChecks()') {
                $il.Emit([Reflection.Emit.OpCodes]::Ldarg_2)
            } elseif ($operand.DeclaringType.FullName -eq 'ZDO' -and $operand.Name -in @('GetInt', 'GetBool')) {
                $defaultType = if ($operand.Name -eq 'GetInt') { [int] } else { [bool] }
                $il.Emit($opcode, $zdoType.GetMethod($operand.Name, [type[]]@([int], $defaultType)))
            } else { throw "Unexpected predicate call: $operand" }
        } elseif ($null -eq $operand) { $il.Emit($opcode) }
        elseif ($operand -is [sbyte]) { $il.Emit($opcode, [sbyte]$operand) }
        elseif ($operand -is [int]) { $il.Emit($opcode, [int]$operand) }
        else { throw "Unexpected predicate operand: $operand" }
    }
    $il.MarkLabel($labels[$end.Offset])
    $il.Emit([Reflection.Emit.OpCodes]::Ret)
    $delegateType = [Func[bool,object,bool,bool]].GetGenericTypeDefinition().MakeGenericType([bool], $zdoType, [bool], [bool])
    $predicate = $dynamic.CreateDelegate($delegateType)
} finally { $metadata.Dispose() }

# Nonpersistent original ZDO fixtures. No live world, Unity initialization or RPCs.
$net = $game.GetType('ZNet', $true)
$man = $game.GetType('ZDOMan', $true)
$net.GetField('m_instance', $flags).SetValue($null, [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($net))
$net.GetField('m_isServer', $flags).SetValue($null, $true)
$man.GetField('s_instance', $flags).SetValue($null, [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($man))
[ZDOExtraData]::Reset()
$checks = 0
try {
    [uint]$id = 1
    foreach ($revision in @($null, 0, 1, 2, -1, 3)) {
        $zdo = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($zdoType)
        $zdo.m_uid = [ZDOID]::new([long]777, $id++)
        if ($null -ne $revision) { $zdo.Set([ZDOVars]::s_cheated, [int]$revision) }
        $native = [bool]$nativeCheck.Invoke($null, [object[]]@($zdo))
        foreach ($delayed in @($false, $true)) {
            foreach ($bypass in @($false, $true)) {
                $expected = ($delayed -or $native) -and -not $bypass
                $actual = [bool]$predicate.DynamicInvoke([object[]]@($delayed, $zdo, $bypass))
                if ($actual -ne $expected) { throw "Mismatch: revision=$revision delayed=$delayed bypass=$bypass native=$expected mod=$actual" }
                $checks++
            }
        }
    }
} finally { [ZDOExtraData]::Reset() }
Write-Output "PASS $checks compiled bonus predicates match original CheatUtils and vanilla delayed/bypass policy."
Write-Output 'Isolated managed IL fragment and original ZDO execution; no item spawning, Unity world or multiplayer.'
