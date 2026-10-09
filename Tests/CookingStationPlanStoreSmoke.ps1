#requires -Version 7.0
#requires -PSEdition Core
param(
    [string] $AssemblyPath = "$(Split-Path -Parent $PSScriptRoot)/bin/Debug/FineDining.dll",
    [string] $ManagedPath = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim/valheim_Data/Managed'
)
$ErrorActionPreference = 'Stop'
$assemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assemblyDirectory = Split-Path $assemblyPath -Parent
$corePath = Join-Path (Split-Path (Split-Path $ManagedPath -Parent) -Parent) 'BepInEx/core'
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $name = ([Reflection.AssemblyName] $eventArgs.Name).Name + '.dll'
    foreach ($directory in @($assemblyDirectory, $corePath, $ManagedPath)) {
        $path = Join-Path $directory $name
        if (Test-Path -LiteralPath $path) { return [Reflection.Assembly]::LoadFrom($path) }
    }
    return $null
})
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$game = [Reflection.Assembly]::LoadFrom((Join-Path $ManagedPath 'assembly_valheim.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $ManagedPath 'assembly_utils.dll'))
$flags = [Reflection.BindingFlags] 'Public,NonPublic,Static,Instance'
$store = $assembly.GetType('FineDining.CookingStationPlanStore', $true)
$planType = $assembly.GetType('FineDining.CookingStationSlotPlan', $true)
$constructor = $planType.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq 5 }
$read = $store.GetMethod('TryRead', $flags)
$write = $store.GetMethod('Write', $flags)
$clear = $store.GetMethod('Clear', $flags)
$reset = $store.GetMethod('Reset', $flags)
$key = [string]$store.GetField('PayloadKey', $flags).GetRawConstantValue()
$hash = [StringExtensionMethods]::GetStableHashCode($key)
$extra = $game.GetType('ZDOExtraData', $true)
$checks = 0
function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
    $script:checks++
}
function New-Plan([bool] $Auto = $false, [bool] $Prepaid = $false, [int] $Bonus = 0,
                  [string] $InputName = 'RawMeat', [string] $OutputName = 'CookedMeat') {
    return $constructor.Invoke([object[]] @($Auto, $Prepaid, $Bonus, $InputName, $OutputName))
}
function Read-Plan($Zdo, [int] $Slot) {
    $arguments = [object[]] @($Zdo, $Slot, [Activator]::CreateInstance($planType))
    if ($read.Invoke($null, $arguments)) { return $arguments[2] }
    return $null
}
function New-Zdo([uint] $Id) {
    # Avoid Unity constructors. These ZDOs are nonpersistent: actual Set/revision
    # behavior is exercised, but sector save dirtiness needs a running world.
    $zdo = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($game.GetType('ZDO', $true))
    $zdo.m_uid = [ZDOID]::new([long]777, $Id)
    return $zdo
}
function Get-Payload($Zdo) {
    return ,$Zdo.GetType().GetMethod('GetByteArray', [type[]] @([int], [byte[]])).Invoke($Zdo, [object[]] @($hash, $null))
}
function Get-Counts($Zdo) {
    $data = [object[]] @($Zdo.m_uid, $null, $null, $null, $null, $null, $null, $null, $null)
    [void]$extra.GetMethod('GetData').Invoke($null, $data)
    return @{ Ints=$data[4].Count; Strings=$data[6].Count; Arrays=$data[7].Count }
}
function Receive-Payload($Zdo, [byte[]] $Payload) {
    # Encode only the native envelope; the plan payload is the actual mod output.
    # Execute original Deserialize without Unity-dependent full Serialize/transport.
    $package = [ZPackage]::new()
    $package.Write([ushort]0x80)
    $package.Write([int]0)
    $package.WriteNumItems(1)
    $package.Write([int]$hash)
    $package.Write($Payload)
    $package.SetPos(0)
    $Zdo.Deserialize($package)
}

# Original ZDO.Set needs these globals. Only this isolated process is affected;
# no game files, publicized inputs, Harmony detours, or live world are used.
$net = $game.GetType('ZNet', $true)
$man = $game.GetType('ZDOMan', $true)
$net.GetField('m_instance', $flags).SetValue($null, [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($net))
$net.GetField('m_isServer', $flags).SetValue($null, $true)
$man.GetField('s_instance', $flags).SetValue($null, [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($man))
[ZDOExtraData]::Reset()
try {
    $zdo = New-Zdo 1
    $manual = New-Plan
    $prepaid = New-Plan $false $true 1
    Assert ($null -eq (Read-Plan $zdo 0)) 'Missing storage must have no plan.'
    Assert ([bool]$clear.Invoke($null, [object[]] @($zdo, 0))) 'Clearing a never-used slot succeeds.'
    Assert ($zdo.DataRevision -eq 0 -and $null -eq (Get-Payload $zdo)) 'Untouched stations need no empty payload.'

    # Every flag combination, including all zero, must survive a cold read.
    for ($bits = 0; $bits -lt 8; $bits++) {
        $plan = New-Plan (($bits -band 1) -ne 0) (($bits -band 2) -ne 0) ([int](($bits -band 4) -ne 0)) '식재료_日本' 'CookedMeat'
        Assert ([bool]$write.Invoke($null, [object[]] @($zdo, $bits, $plan))) 'Write flag combination.'
    }
    $reset.Invoke($null, [object[]] @($null))
    for ($bits = 0; $bits -lt 8; $bits++) {
        $plan = Read-Plan $zdo $bits
        Assert ($null -ne $plan) 'Zero flags are a plan, not an empty slot.'
        Assert ($planType.GetProperty('AutoPop', $flags).GetValue($plan) -eq (($bits -band 1) -ne 0)) 'Auto flag round trip.'
        Assert ($planType.GetProperty('CollectionExperiencePrepaid', $flags).GetValue($plan) -eq (($bits -band 2) -ne 0)) 'Prepaid flag round trip.'
        Assert ($planType.GetProperty('BonusCount', $flags).GetValue($plan) -eq [int](($bits -band 4) -ne 0)) 'Fixed bonus round trip.'
        Assert ($planType.GetProperty('ExpectedInput', $flags).GetValue($plan) -eq '식재료_日本') 'Exact Unicode input round trip.'
        Assert ($planType.GetProperty('ExpectedOutput', $flags).GetValue($plan) -eq 'CookedMeat') 'Exact output round trip.'
    }

    $payload = Get-Payload $zdo
    $revision = $zdo.DataRevision
    $cachedPlans = $store.GetMethod('GetPlans', $flags).Invoke($null, @($zdo))
    $decoded = $cachedPlans.GetType().GetField('Plans', $flags).GetValue($cachedPlans)
    for ($i = 0; $i -lt 20; $i++) {
        $plan = Read-Plan $zdo 0
        Assert ([bool]$write.Invoke($null, [object[]] @($zdo, 0, $plan))) 'Repeated identical write succeeds.'
    }
    Assert ($zdo.DataRevision -eq $revision -and [object]::ReferenceEquals($payload, (Get-Payload $zdo))) 'No-op writes must not allocate stored arrays or advance revision.'
    Receive-Payload $zdo ([byte[]]$payload.Clone())
    [void](Read-Plan $zdo 0)
    Assert ([object]::ReferenceEquals($decoded, $cachedPlans.GetType().GetField('Plans', $flags).GetValue($cachedPlans))) 'An unchanged received payload must reuse its decoded plans.'
    $zdo.OwnerRevision++
    [void](Read-Plan $zdo 0)
    Assert (-not [object]::ReferenceEquals($decoded, $cachedPlans.GetType().GetField('Plans', $flags).GetValue($cachedPlans))) 'Ownership transfer must refresh the snapshot.'

    # Whole-value replacement must remove plans on peers that already saw them.
    $remote = New-Zdo 2
    Receive-Payload $remote (Get-Payload $zdo)
    Assert ($null -ne (Read-Plan $remote 7)) 'Receive active plans.'
    for ($slot = 0; $slot -lt 8; $slot++) {
        Assert ([bool]$clear.Invoke($null, [object[]] @($zdo, $slot))) 'Clear active plan.'
    }
    $empty = Get-Payload $zdo
    Assert ([Convert]::ToHexString($empty) -eq '020000') 'Last removal retains the explicit versioned empty set.'
    Receive-Payload $remote $empty
    Assert ($null -eq (Read-Plan $remote 7)) 'Explicit empty set must replace a previously received active plan.'
    $revision = $zdo.DataRevision
    [void]$clear.Invoke($null, [object[]] @($zdo, 7))
    Assert ($zdo.DataRevision -eq $revision) 'Repeated clear must not advance revision.'

    # Reuse, unrelated slot preservation, reload, and pooled object identity.
    Assert ([bool]$write.Invoke($null, [object[]] @($zdo, 0, $prepaid))) 'Write prepaid manual plan.'
    Assert ([bool]$write.Invoke($null, [object[]] @($zdo, 1, $manual))) 'Write independent slot.'
    [void]$clear.Invoke($null, [object[]] @($zdo, 1))
    $reset.Invoke($null, [object[]] @($null))
    $restored = Read-Plan $zdo 0
    Assert ($planType.GetProperty('CollectionExperiencePrepaid', $flags).GetValue($restored) -and $planType.GetProperty('BonusCount', $flags).GetValue($restored) -eq 1) 'Clearing another slot and a cache reset must preserve prepaid experience and bonus.'
    $replacement = New-Plan $false $false 0 'FishRaw' 'FishCooked'
    [void]$clear.Invoke($null, [object[]] @($zdo, 0))
    Assert ([bool]$write.Invoke($null, [object[]] @($zdo, 0, $replacement))) 'Reuse slot for another conversion.'
    Assert ($planType.GetProperty('ExpectedInput', $flags).GetValue((Read-Plan $zdo 0)) -eq 'FishRaw') 'Reused slot must not retain the old input.'
    $saved = [byte[]](Get-Payload $zdo).Clone()
    $zdo.m_uid = [ZDOID]::new([long]777, [uint]3)
    Assert ($null -eq (Read-Plan $zdo 0)) 'Pooled ZDO identity must not reuse another object plan.'
    [void][ZDOExtraData]::Set($zdo.m_uid, $hash, $saved)
    $reset.Invoke($null, [object[]] @($null))
    Assert ($planType.GetProperty('ExpectedOutput', $flags).GetValue((Read-Plan $zdo 0)) -eq 'FishCooked') 'Persisted bytes survive a new object/cache.'

    # Invalid blocks are not partially read or silently overwritten/cleared.
    $valid = [byte[]]$saved.Clone()
    $badFlags = [byte[]]$valid.Clone(); $badFlags[5] = 8
    $badUtf8 = [byte[]]$valid.Clone(); $badUtf8[8] = 255
    $badLength = [byte[]]$valid.Clone(); $badLength[6] = 255; $badLength[7] = 255
    $duplicate = [byte[]](@(2,2,0) + $valid[3..($valid.Length-1)] + $valid[3..($valid.Length-1)])
    $invalidBlocks = @(
        ,([byte[]]@()); ,([byte[]]@(3,0,0)); ,([byte[]]@(2,1,0));
        ,([byte[]]@(2,0,0,99)); ,$badFlags; ,$badUtf8; ,$badLength; ,$duplicate;
        ,([byte[]]::new(1024*1024+1))
    )
    foreach ($bad in $invalidBlocks) {
        [void][ZDOExtraData]::Set($zdo.m_uid, $hash, $bad)
        $revision = $zdo.DataRevision
        Assert ($null -eq (Read-Plan $zdo 0)) 'Reject invalid, duplicate or unsupported plan block.'
        Assert (-not [bool]$write.Invoke($null, [object[]] @($zdo, 0, $manual))) 'Do not overwrite an invalid block.'
        Assert (-not [bool]$clear.Invoke($null, [object[]] @($zdo, 0))) 'Do not erase an invalid block.'
        Assert ([object]::ReferenceEquals($bad, (Get-Payload $zdo)) -and $zdo.DataRevision -eq $revision) 'Invalid bytes must remain unchanged.'
    }
    [void][ZDOExtraData]::Set($zdo.m_uid, $hash, $valid)
    Assert ($null -ne (Read-Plan $zdo 0)) 'A valid replacement must recover an invalid cached block.'
    Assert (-not [bool]$write.Invoke($null, [object[]] @($zdo, -1, $manual))) 'Reject negative slot.'
    Assert (-not [bool]$write.Invoke($null, [object[]] @($zdo, 65536, $manual))) 'Reject unrepresentable slot.'
    Assert (-not [bool]$write.Invoke($null, [object[]] @($zdo, 1, (New-Plan $false $false 0 '' 'CookedMeat')))) 'Reject empty prefab name.'

    # The observed station sizes stay at one byte-array entry, even after reuse.
    foreach ($count in @(28,23)) {
        $station = New-Zdo ([uint](100+$count))
        for ($cycle = 0; $cycle -lt 3; $cycle++) {
            for ($slot = 0; $slot -lt $count; $slot++) { Assert ([bool]$write.Invoke($null, [object[]] @($station, $slot, $manual))) 'Write crowded station.' }
            $counts = Get-Counts $station
            Assert ($counts.Ints -eq 0 -and $counts.Strings -eq 0 -and $counts.Arrays -eq 1) 'New plans must use exactly one byte-array entry.'
            $activeBytes = (Get-Payload $station).Length
            for ($slot = 0; $slot -lt $count; $slot++) { Assert ([bool]$clear.Invoke($null, [object[]] @($station, $slot))) 'Clear crowded station.' }
            Assert ((Get-Payload $station).Length -eq 3 -and (Get-Counts $station).Arrays -eq 1) 'Repeated cycles must return to a three-byte empty block.'
        }
        Write-Output "Slots=$count; active payload=$activeBytes bytes; empty payload=3 bytes; one byte-array key; no per-slot Int32/String keys."
    }

    # Existing keys (including unknown contributors) remain byte-for-byte intact.
    $legacy = New-Zdo 500
    $oldInts = @{}
    $oldStrings = @{}
    for ($slot = 0; $slot -lt 28; $slot++) {
        foreach ($field in @('auto','prepaid','bonus','version')) { $oldInts[[StringExtensionMethods]::GetStableHashCode("sighsorry.FineDining.CookingStation.$slot.$field")] = 1 }
        $oldStrings[[StringExtensionMethods]::GetStableHashCode("sighsorry.FineDining.CookingStation.$slot.input")] = 'RawMeat'
        $oldStrings[[StringExtensionMethods]::GetStableHashCode("sighsorry.FineDining.CookingStation.$slot.output")] = 'CookedMeat'
        $oldInts[[StringExtensionMethods]::GetStableHashCode("slotstatus$slot")] = 1
        $oldInts[([StringExtensionMethods]::GetStableHashCode('cheatedQueued') + $slot)] = 0
    }
    $oldInts[-1449744342] = 10
    foreach ($entry in $oldInts.GetEnumerator()) { [void][ZDOExtraData]::Set($legacy.m_uid, [int]$entry.Key, [int]$entry.Value) }
    foreach ($entry in $oldStrings.GetEnumerator()) { [void][ZDOExtraData]::Set($legacy.m_uid, [int]$entry.Key, [string]$entry.Value) }
    Assert ($null -eq (Read-Plan $legacy 0)) 'Version 1 keys must not be interpreted as current plans.'
    [void]$write.Invoke($null, [object[]] @($legacy, 0, $manual))
    [void]$clear.Invoke($null, [object[]] @($legacy, 0))
    Assert ((Get-Counts $legacy).Ints -eq 169 -and (Get-Counts $legacy).Strings -eq 56) 'Legacy key counts deliberately remain unchanged.'
    $getInt = $legacy.GetType().GetMethod('GetInt', [type[]] @([int], [int]))
    $getString = $legacy.GetType().GetMethod('GetString', [type[]] @([int], [string]))
    foreach ($entry in $oldInts.GetEnumerator()) { Assert ($getInt.Invoke($legacy, [object[]] @([int]$entry.Key, [int]-1)) -eq $entry.Value) 'Preserve existing int values.' }
    foreach ($entry in $oldStrings.GetEnumerator()) { Assert ($getString.Invoke($legacy, [object[]] @([int]$entry.Key, '')) -eq $entry.Value) 'Preserve existing strings.' }
    Write-Output "PASS: $checks CookingStation plan storage assertions against original game DLLs."
    Write-Output 'Isolated managed checks only: no Unity lifecycle, live save chunks, RPC transport, item spawning, or multiplayer execution.'
}
finally {
    $reset.Invoke($null, [object[]] @($null))
    [ZDOExtraData]::Reset()
    $net.GetField('m_instance', $flags).SetValue($null, $null)
    $net.GetField('m_isServer', $flags).SetValue($null, $false)
    $man.GetField('s_instance', $flags).SetValue($null, $null)
}
