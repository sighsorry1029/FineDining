param(
    [string] $AssemblyPath = "$(Split-Path -Parent $PSScriptRoot)\bin\Debug\FineDining.dll",
    [string] $ManagedDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assemblyDirectory = Split-Path -Parent $assemblyPath

[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)

    $fileName = ([Reflection.AssemblyName] $eventArgs.Name).Name + '.dll'
    $candidates = @(
        (Join-Path $assemblyDirectory $fileName),
        (Join-Path $ManagedDirectory $fileName),
        (Join-Path (Join-Path $ManagedDirectory 'publicized_assemblies') $fileName),
        (Join-Path (Join-Path $ManagedDirectory 'publicized_assemblies') ($fileName -replace '\.dll$', '_publicized.dll'))
    )

    foreach ($candidate in $candidates)
    {
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

function Get-TypeRequired([Reflection.Assembly] $Assembly, [string] $Name)
{
    $type = $Assembly.GetType($Name, $false)
    Assert-True ($null -ne $type) "Required type is missing: $Name"
    return $type
}

function Get-Constant([Type] $Type, [string] $Name)
{
    $flags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
    $field = $Type.GetField($Name, $flags)
    Assert-True ($null -ne $field -and $field.IsLiteral) "Required constant is missing: $($Type.FullName).$Name"
    return $field.GetRawConstantValue()
}

function Assert-Method([Type] $Type, [string] $Name)
{
    $flags = [Reflection.BindingFlags] 'Static,Instance,Public,NonPublic'
    $method = $Type.GetMethods($flags) | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
    Assert-True ($null -ne $method) "Required method is missing: $($Type.FullName).$Name"
}

$assembly = [Reflection.Assembly]::UnsafeLoadFrom($assemblyPath)
Assert-True ($assembly.GetName().Name -eq 'FineDining') 'Assembly name must be FineDining.'
Assert-True ($assembly.GetName().Version -eq [Version] '1.0.0.0') 'Assembly version must remain 1.0.0.0.'

$pluginType = Get-TypeRequired $assembly 'FineDining.FineDiningPlugin'
Assert-True ((Get-Constant $pluginType 'ModName') -eq 'FineDining') 'Plugin name must be FineDining.'
Assert-True ((Get-Constant $pluginType 'ModVersion') -eq '1.0.0') 'Plugin version must remain 1.0.0.'
Assert-True ((Get-Constant $pluginType 'Author') -eq 'sighsorry') 'Plugin author must be sighsorry.'
Assert-True ((Get-Constant $pluginType 'ModGUID') -eq 'sighsorry.FineDining') 'Plugin GUID must be sighsorry.FineDining.'
Assert-True ([bool](Get-Constant $pluginType 'DefaultConfigurationLock')) 'Server configuration lock must default to enabled.'

$basePluginType = [BepInEx.BaseUnityPlugin]
$pluginTypes = @($assembly.GetTypes() | Where-Object {
    -not $_.IsAbstract -and $basePluginType.IsAssignableFrom($_)
})
Assert-True ($pluginTypes.Count -eq 1) 'FineDining must contain exactly one BepInEx plugin class.'

$incompatibilities = @(
    $pluginType.GetCustomAttributesData() |
        Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInIncompatibility' } |
        ForEach-Object { [string]$_.ConstructorArguments[0].Value }
)
foreach ($legacyGuid in @('sighsorry.BeingSpoiled', 'blizz.GourmetsDiet', 'sighsorry.InputHoverHints'))
{
    Assert-True ($incompatibilities -contains $legacyGuid) "Missing old-plugin incompatibility: $legacyGuid"
}

foreach ($typeName in @(
    'FineDining.FineDiningLocalization',
    'FineDining.FoodIdentity',
    'FineDining.FreshnessRuntime',
    'FineDining.DecayRuntime',
    'FineDining.DietModule',
    'FineDining.StationModule',
    'FineDining.FermenterEnvironmentSpeedSystem',
    'FineDining.DietTooltipPatch',
    'FineDining.DietPlayerFoodPatches'))
{
    $null = Get-TypeRequired $assembly $typeName
}

foreach ($contract in @(
    @('FineDining.DietModule', @('Initialize', 'Tick', 'Shutdown')),
    @('FineDining.StationModule', @('Initialize', 'Shutdown', 'GetFermenterBatchToken', 'CheckpointFermenterBeforeCompletion', 'NotifyFermenterBatchCleared')),
    @('FineDining.FoodIdentity', @('GetCanonicalPrefabName', 'IsDirectlyEdible'))))
{
    $type = Get-TypeRequired $assembly $contract[0]
    foreach ($methodName in $contract[1])
    {
        Assert-Method $type $methodName
    }
}

Assert-True ($null -eq $assembly.GetType('FineDining.FineDiningApi', $false)) 'No public compatibility API should remain.'
Assert-True ($null -eq $assembly.GetType('GourmetsDiet.GourmetsDietApi', $false)) 'Old GourmetsDiet API must not be included.'
Assert-True ($null -eq $assembly.GetType('LocalizationManager.Localizer', $false)) 'The old standalone localization helper must not remain.'

$decayType = Get-TypeRequired $assembly 'FineDining.DecayRuntime'
$freshnessType = Get-TypeRequired $assembly 'FineDining.FreshnessRuntime'
$stateStoreType = Get-TypeRequired $assembly 'FineDining.FoodStateStore'
$generatedType = Get-TypeRequired $assembly 'FineDining.GeneratedPrefabRegistry'
$environmentType = Get-TypeRequired $assembly 'FineDining.FermenterEnvironmentSpeedSystem'

Assert-True ((Get-Constant $decayType 'ExpiryDataKey') -eq 'sighsorry.FineDining.ExpiryWorldTicks') 'Expiry key is not the new FineDining key.'
Assert-True ((Get-Constant $freshnessType 'AssignedLifetimeDataKey') -eq 'sighsorry.FineDining.AssignedLifetimeTicks') 'Assigned-lifetime key is not the new FineDining key.'
Assert-True ((Get-Constant $stateStoreType 'CustomDataKey') -eq 'sighsorry.FineDining.DietState') 'Diet state key is not the new FineDining key.'
Assert-True ((Get-Constant $generatedType 'IceboxPrefabName') -eq 'FineDining_Icebox') 'Icebox prefab ID is incorrect.'
Assert-True ((Get-Constant $generatedType 'RottenProducePrefabName') -eq 'FineDining_RottenProduce') 'Rotten Produce prefab ID is incorrect.'
Assert-True ((Get-Constant $generatedType 'RottenFoodPrefabName') -eq 'FineDining_RottenFood') 'Rotten Food prefab ID is incorrect.'

foreach ($fieldName in @('BatchTokenKey', 'AccumulatedBonusTicksKey', 'LastCheckpointTicksKey', 'BonusRateKey'))
{
    $value = [string](Get-Constant $environmentType $fieldName)
    Assert-True ($value.StartsWith('FineDining_FermenterEnv_', [StringComparison]::Ordinal)) "Fermenter state key is not FineDining-owned: $fieldName"
}

$resources = @($assembly.GetManifestResourceNames())
foreach ($resource in @(
    'FineDining.Resources.Defaults.FineDining.yml',
    'FineDining.translations.English.yml',
    'FineDining.translations.Korean.yml'))
{
    Assert-True ($resources -contains $resource) "Embedded resource is missing: $resource"
}

$english = Get-Content -LiteralPath (Join-Path $projectRoot 'translations\English.yml') -Raw
$korean = Get-Content -LiteralPath (Join-Path $projectRoot 'translations\Korean.yml') -Raw
foreach ($key in @(
    'finedining_tooltip_spoils_in',
    'finedining_tooltip_freshness_effect',
    'finedining_diet_full_straight_title',
    'finedining_diet_tooltip_chef_multiplier',
    'finedining_station_cover',
    'finedining_station_timer'))
{
    Assert-True ($english.Contains($key + ':')) "English localization is missing $key."
    Assert-True ($korean.Contains($key + ':')) "Korean localization is missing $key."
}

$sourceFiles = Get-ChildItem -LiteralPath $projectRoot -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
$legacyMatches = @($sourceFiles | Select-String -Pattern 'BeingSpoiled|GourmetsDiet|InputHoverHints|IHH_' |
    Where-Object {
        -not ($_.Path.EndsWith('Plugin.cs', [StringComparison]::OrdinalIgnoreCase) -and
              $_.Line.Contains('BepInIncompatibility'))
    })
Assert-True ($legacyMatches.Count -eq 0) 'Old runtime identifiers remain outside intentional incompatibility declarations.'

$startTimeWrites = @($sourceFiles | Select-String -Pattern '\.Set\s*\([^\r\n]*s_startTime')
Assert-True ($startTimeWrites.Count -eq 0) 'FineDining must never write vanilla fermenter s_startTime.'

$harmonyOwners = @($sourceFiles | Select-String -Pattern 'new\s+Harmony\s*\(|Harmony\s+_harmony\s*=')
Assert-True ($harmonyOwners.Count -eq 1 -and $harmonyOwners[0].Path.EndsWith('Plugin.cs')) 'FineDining must own exactly one Harmony instance.'

Write-Host 'FineDining integration smoke checks passed.'
