param(
    [string] $AzuEpiAssemblyPath = '',
    [string] $AssemblyPath = '',
    [string] $GameDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -and $env:OS -eq 'Windows_NT')
{
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf))
    {
        throw 'AzuEPI IL smoke requires Windows PowerShell because Valheim ships Harmony 2.9 on .NET Framework.'
    }

    $relayArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if (-not [string]::IsNullOrWhiteSpace($AzuEpiAssemblyPath))
    {
        $relayArguments += @('-AzuEpiAssemblyPath', $AzuEpiAssemblyPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($AssemblyPath))
    {
        $relayArguments += @('-AssemblyPath', $AssemblyPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($GameDirectory))
    {
        $relayArguments += @('-GameDirectory', $GameDirectory)
    }

    & $windowsPowerShell @relayArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Windows PowerShell AzuEPI smoke failed with exit code $LASTEXITCODE."
    }

    exit 0
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AzuEpiAssemblyPath))
{
    $AzuEpiAssemblyPath = Join-Path $projectRoot 'LocalReferences\AzuExtendedPlayerInventory.dll'
}

if (-not (Test-Path -LiteralPath $AzuEpiAssemblyPath -PathType Leaf))
{
    throw "AzuEPI reference was not found at '$AzuEpiAssemblyPath'. Copy it to LocalReferences\AzuExtendedPlayerInventory.dll or pass -AzuEpiAssemblyPath."
}

if ([string]::IsNullOrWhiteSpace($AssemblyPath))
{
    $AssemblyPath = Join-Path $projectRoot 'bin\Debug\FineDining.dll'
}

$assemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$azuEpiAssemblyPath = (Resolve-Path -LiteralPath $AzuEpiAssemblyPath).Path
$assemblyDirectory = Split-Path -Parent $assemblyPath
$azuEpiDirectory = Split-Path -Parent $azuEpiAssemblyPath
$managedDirectory = Join-Path $GameDirectory 'valheim_Data\Managed'
$bepInExCoreDirectory = Join-Path $GameDirectory 'BepInEx\core'

[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)

    $fileName = ([Reflection.AssemblyName] $eventArgs.Name).Name + '.dll'
    foreach ($directory in @(
        $assemblyDirectory,
        $azuEpiDirectory,
        $bepInExCoreDirectory,
        $managedDirectory))
    {
        $candidate = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $candidate)
        {
            return [Reflection.Assembly]::UnsafeLoadFrom($candidate)
        }


    }

    return $null
})

function Assert-True([bool] $Condition, [string] $Message)
{
    if (-not $Condition)
    {
        throw "Assertion failed: $Message"
    }
}

# Diagnose stale optional DLLs before Harmony's IL importer obscures a missing member.
$cecilPath = Join-Path $env:USERPROFILE '.nuget\packages\mono.cecil\0.11.6\lib\netstandard2.0\Mono.Cecil.dll'
Add-Type -Path $cecilPath
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managedDirectory)
$resolver.AddSearchDirectory($bepInExCoreDirectory)
$resolver.AddSearchDirectory($azuEpiDirectory)
$reader = New-Object Mono.Cecil.ReaderParameters
$reader.AssemblyResolver = $resolver
$azuMetadata = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($azuEpiAssemblyPath, $reader)
try {
    $owner = $azuMetadata.MainModule.GetType('AzuEPI.Game.Patches.InventoryPatches')
    $recovery = $owner.NestedTypes | Where-Object Name -eq 'Load_TrackAndFixHiddenItems_Patch'
    $postfix = $recovery.Methods | Where-Object Name -eq 'Postfix'
    foreach ($instruction in $postfix.Body.Instructions) {
        $member = $instruction.Operand
        if ($member -is [Mono.Cecil.MemberReference] -and $member.DeclaringType.Scope.Name -like 'assembly_*' -and -not $member.Resolve()) {
            throw "Optional AzuEPI reference is incompatible with this game: $member at IL offset $($instruction.Offset). Update the external reference before running its transpiler smoke test."
        }
    }
} finally { $azuMetadata.Dispose(); $resolver.Dispose() }

$fineDiningAssembly = [Reflection.Assembly]::UnsafeLoadFrom($assemblyPath)
$azuEpiAssembly = [Reflection.Assembly]::UnsafeLoadFrom($azuEpiAssemblyPath)
$targetType = $azuEpiAssembly.GetType(
    'AzuEPI.Game.Patches.InventoryPatches+Load_TrackAndFixHiddenItems_Patch',
    $true)
$staticNonPublic = [Reflection.BindingFlags] 'Static,NonPublic'
$target = $targetType.GetMethod('Postfix', $staticNonPublic)
Assert-True ($null -ne $target) 'The AzuEPI hidden-slot recovery postfix was not found.'
Assert-True ($target.ReturnType -eq [void]) 'The AzuEPI recovery postfix must return void.'
Assert-True ($target.GetParameters().Count -eq 1) 'The AzuEPI recovery postfix signature changed.'
Assert-True ($target.GetParameters()[0].ParameterType.Name -eq 'Inventory') 'The AzuEPI recovery postfix no longer receives Inventory.'

$harmonyAssembly = [AppDomain]::CurrentDomain.GetAssemblies() |
    Where-Object { $_.GetName().Name -eq '0Harmony' } |
    Select-Object -First 1
if ($null -eq $harmonyAssembly)
{
    $harmonyAssembly = [Reflection.Assembly]::UnsafeLoadFrom(
        (Join-Path $bepInExCoreDirectory '0Harmony.dll'))
}
Assert-True ($null -ne $harmonyAssembly) 'Harmony was not loaded.'
$patchProcessorType = $harmonyAssembly.GetType('HarmonyLib.PatchProcessor', $true)
$getOriginalInstructions = $patchProcessorType.GetMethods([Reflection.BindingFlags] 'Static,Public') |
    Where-Object {
        $_.Name -eq 'GetOriginalInstructions' -and
        $_.GetParameters().Count -eq 2 -and
        -not $_.GetParameters()[1].IsOut
    } |
    Select-Object -First 1
Assert-True ($null -ne $getOriginalInstructions) 'Harmony original-instruction reader was not found.'
$originalInstructionArguments = [object[]]::new(2)
$originalInstructionArguments[0] = $target
$originalInstructionArguments[1] = $null
try { $originalInstructions = $getOriginalInstructions.Invoke($null, $originalInstructionArguments) }
catch { throw $_.Exception.InnerException.ToString() }

$compatibilityType = $fineDiningAssembly.GetType(
    'FineDining.AzuExtendedPlayerInventoryCompatibility',
    $true)
$transpiler = $compatibilityType.GetMethod('Transpiler', $staticNonPublic)
$composeMethod = $compatibilityType.GetMethod(
    'ComposeDirectRecoveryMergeExpirySafe',
    $staticNonPublic)
$matchedSitesField = $compatibilityType.GetField('_matchedMergeSites', $staticNonPublic)
Assert-True ($null -ne $transpiler -and $null -ne $composeMethod -and $null -ne $matchedSitesField) 'FineDining compatibility internals are incomplete.'

$transpilerArguments = [object[]]::new(1)
$transpilerArguments[0] = $originalInstructions
$transpiledInstructions = @($transpiler.Invoke($null, $transpilerArguments))
$injectedCalls = @($transpiledInstructions | Where-Object {
    $_.opcode -eq [Reflection.Emit.OpCodes]::Call -and
    [object]::Equals($_.operand, $composeMethod)
})
$matchedSites = [int] $matchedSitesField.GetValue($null)
Assert-True ($matchedSites -eq 1) "Expected one verified AzuEPI direct merge site, found $matchedSites."
Assert-True ($injectedCalls.Count -eq 1) "Expected one expiry composition call, found $($injectedCalls.Count)."

Write-Output "PASS AzuEPI actual IL; version=$($azuEpiAssembly.GetName().Version); original=$($originalInstructions.Count); transpiled=$($transpiledInstructions.Count); matched=1; injected=1"
