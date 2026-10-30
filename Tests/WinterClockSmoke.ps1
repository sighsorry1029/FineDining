#requires -Version 7.0
param([string] $AssemblyPath = "$PSScriptRoot/../bin/Debug/FineDining.dll")
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath))
$flags = [Reflection.BindingFlags]'Public,NonPublic,Instance,Static'
$timeline = $assembly.GetType('FineDining.SpoilageTimeline', $true)
$recordType = $assembly.GetType('FineDining.SpoilageClockRecord', $true)
$constructor = $timeline.GetConstructor($flags, $null, [type[]]@([long],[string],[long]), $null)
$recordConstructor = $recordType.GetConstructor($flags, $null, [type[]]@([long],$timeline), $null)
$epoch = '11111111111111111111111111111111'
$checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }; $script:checks++
}
function New-Time([long]$World = 7, [long]$Raw = 1000) {
    $constructor.Invoke([object[]]@($World,$epoch,$Raw))
}
function Field($Value,[string]$Name) { $Value.GetType().GetField($Name,$flags).GetValue($Value) }
function Set-Field($Value,[string]$Name,$Data) { $Value.GetType().GetField($Name,$flags).SetValue($Value,$Data); return $Value }
function Advance($Value,[long]$Raw) { Check ([bool]$timeline.GetMethod('Advance',$flags).Invoke($Value,@($Raw))) 'Valid time advance rejected'; return $Value }
function Text($Value) { $Value.GetType().GetMethod('Serialize',$flags).Invoke($Value,@()) }
function Parse($Type,[string]$Text) {
    $a = [object[]]@($Text,[Activator]::CreateInstance($Type))
    if (!$Type.GetMethod('TryParse',$flags).Invoke($null,$a)) { return $null }; $a[1]
}
function Resolve($Record,$Time,[bool]$Timestamp = $false) {
    $a = [object[]]@($Time,$Timestamp,[long]0)
    if (!$recordType.GetMethod('TryResolve',$flags).Invoke($Record,$a)) { return $null }; $a[2]
}

$hour = [long][TimeSpan]::TicksPerHour
$t = New-Time
$start = [long](Field $t 'ClockTicks')
$deadline = $start + 48*$hour
$food = $recordConstructor.Invoke([object[]]@($deadline,$t))
$t = Advance $t (1000+6*$hour)
$t = Set-Field $t Frozen $true
$t = Advance $t (1000+78*$hour)
$saved = Text $t
$t = Parse $timeline $saved
Check ($null -ne $t -and (Text $t) -ceq $saved) 'World save/reload changed its winter clock'
$t = Set-Field $t Frozen $false
$t = Advance $t (1000+86*$hour)
Check (((Resolve $food $t) - (Field $t ClockTicks)) -eq 34*$hour) 'Unloaded/offline food lost winter protection'
Check ((Field $food Value) -eq $deadline) 'Advancing the world required touching an offline item'

# Repeated winters need one bounded checkpoint, not a growing interval list.
$t = New-Time
$expected = $start
$world = [long]1000
for ($i=0; $i -lt 200; $i++) {
    $t = Set-Field $t Frozen $false
    $world += 3*$hour; $expected += 3*$hour; $t = Advance $t $world
    $t = Set-Field $t Frozen $true
    $world += 40*$hour; $t = Advance $t $world
    $t = Advance $t $world # repeated same-time transition notification
    $t = Parse $timeline (Text $t)
    Check ((Field $t ClockTicks) -eq $expected) 'Repeated winter/save duplicated or lost time'
    Check ((Text $t).Length -le 180) 'World record grows with the number of winters'
}

$t = New-Time
$t = Advance $t 2000
$t = Set-Field $t Frozen $true
$t = Advance $t 9000
$t = Advance $t 7000
Check ((Field $t ClockTicks) -eq $start+1000) 'Winter rewind changed spoilage time'
$t = Set-Field $t Frozen $false # disable in the middle of winter; do not reset earned protection
$t = Advance $t 7500
Check ((Field $t ClockTicks) -eq $start+1500) 'Disabling winter discarded earlier protection'
$t = Advance $t 6500
Check ((Field $t ClockTicks) -eq $start+500) 'Nonwinter rewind no longer follows the existing policy'
$zero = New-Time 7 0
$zero = Advance $zero 0
Check ($null -ne (Parse $timeline (Text $zero))) 'A newly created world at time zero cannot restore its clock'

$t = New-Time
foreach ($value in @([long]-500,[long]$start,[long]($start+500))) {
    $r = $recordConstructor.Invoke([object[]]@($value,$t))
    $decoded = Parse $recordType (Text $r)
    Check ((Resolve $decoded $t) -eq $value) 'Atomic record round trip changed a timer'
}
$r = $recordConstructor.Invoke([object[]]@([long]($start+500),$t))
$other = New-Time 8 999999
Check ((Resolve $r $other) - (Field $other ClockTicks) -eq 500) 'Cross-world transfer did not retain its checkpoint'
$other = Set-Field $other WorldUid ([long]7)
$other = Set-Field $other Epoch '22222222222222222222222222222222'
Check ($null -eq (Resolve $r $other)) 'Lost/replaced same-world history granted a new lifetime'
Check ($null -eq (Resolve $r (New-Time 8) $true)) 'Foreign placement timestamp was silently reinterpreted'

$legacy = Parse $recordType '1500'
Check ((Resolve $legacy $t) - (Field $t ClockTicks) -eq 500) 'Legacy adoption changed current remaining time'
$oldAnchor = Parse $recordType '900'
Check ((Resolve $oldAnchor $t $true) - (Field $t ClockTicks) -eq -100) 'A past placement anchor was clamped to now'
$t = Set-Field $t Frozen $true
$t = Advance $t 2000
Check ((Resolve $legacy $t) -eq (Field $t ClockTicks)) 'Unrecorded legacy history was retroactively invented'

foreach ($bad in @('', '0','-9223372036854775808','+12','012','1e3','2|7|bad|10|20',
    "2|7|$epoch|-50|49", "2|7|$epoch|10|-1", "2|7|$epoch|0|0", "3|7|$epoch|10|20", ('x'*181))) {
    Check ($null -eq (Parse $recordType $bad)) "Accepted malformed/unknown item record: $bad"
}
foreach ($bad in @('',"1|0|$epoch|100|100|0", "1|7|$epoch|-1|100|0", "1|7|$epoch|1|0|0",
    "1|7|$epoch|1|100|2", "2|7|$epoch|1|100|0")) {
    Check ($null -eq (Parse $timeline $bad)) 'Accepted malformed world checkpoint'
}
$before = Text $t
Check (-not $timeline.GetMethod('Advance',$flags).Invoke($t,@([long]-1))) 'Negative raw time accepted'
Check ((Text $t) -eq $before) 'Rejected time advance mutated the checkpoint'
$culture = [Globalization.CultureInfo]::CurrentCulture
try {
    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
    Check ((Text $t) -eq $before) 'World serialization is culture-dependent'
} finally { [Globalization.CultureInfo]::CurrentCulture = $culture }
Write-Output "PASS $checks winter timeline/atomic-record checks against the built DLL (no game execution)."
