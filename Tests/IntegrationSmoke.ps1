param(
    [string] $AssemblyPath = "$(Split-Path -Parent $PSScriptRoot)\bin\Debug\FineDining.dll",
    [string] $ManagedDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed',
    [string] $ThunderstoreZipPath = '',
    [string] $NexusZipPath = ''
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

function Get-MethodRequired([Type] $Type, [string] $Name)
{
    $flags = [Reflection.BindingFlags] 'Static,Instance,Public,NonPublic'
    $methods = @($Type.GetMethods($flags) | Where-Object { $_.Name -eq $Name })
    Assert-True ($methods.Count -eq 1) "Expected exactly one method named $($Type.FullName).$Name"
    return $methods[0]
}

function Get-StreamSha256([IO.Stream] $Stream)
{
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try
    {
        return [BitConverter]::ToString($sha256.ComputeHash($Stream)).Replace('-', '')
    }
    finally
    {
        $sha256.Dispose()
    }
}

function Assert-ZipPackage(
    [string] $Path,
    [hashtable] $ExpectedEntries,
    [string] $ExpectedManifestVersion = '')
{
    if ([string]::IsNullOrWhiteSpace($Path))
    {
        return
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedPath)
    try
    {
        $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        Assert-True ($entries.Count -eq $ExpectedEntries.Count) "Package '$resolvedPath' contains unexpected or missing files."
        foreach ($entryName in $ExpectedEntries.Keys)
        {
            $entry = $archive.GetEntry($entryName)
            Assert-True ($null -ne $entry) "Package '$resolvedPath' is missing '$entryName'."

            $entryStream = $entry.Open()
            try
            {
                $entryHash = Get-StreamSha256 $entryStream
            }
            finally
            {
                $entryStream.Dispose()
            }

            $sourcePath = (Resolve-Path -LiteralPath ([string]$ExpectedEntries[$entryName])).Path
            $sourceStream = [IO.File]::OpenRead($sourcePath)
            try
            {
                $sourceHash = Get-StreamSha256 $sourceStream
            }
            finally
            {
                $sourceStream.Dispose()
            }

            Assert-True ($entryHash -eq $sourceHash) "Package '$resolvedPath' contains a stale '$entryName'."
        }

        if (-not [string]::IsNullOrWhiteSpace($ExpectedManifestVersion))
        {
            $manifestEntry = $archive.GetEntry('manifest.json')
            Assert-True ($null -ne $manifestEntry) "Package '$resolvedPath' is missing manifest.json."
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try
            {
                $manifest = $reader.ReadToEnd() | ConvertFrom-Json
            }
            finally
            {
                $reader.Dispose()
            }

            Assert-True ($manifest.name -eq 'FineDining') "Package '$resolvedPath' has the wrong manifest name."
            Assert-True ($manifest.version_number -eq $ExpectedManifestVersion) "Package '$resolvedPath' has a stale manifest version."
        }
    }
    finally
    {
        $archive.Dispose()
    }
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
    'FineDining.FermenterEnvironmentSpeedSystem'))
{
    $null = Get-TypeRequired $assembly $typeName
}

foreach ($contract in @(
    @('FineDining.DietModule', @('Initialize', 'Tick', 'Shutdown')),
    @('FineDining.StationModule', @('Initialize', 'Shutdown')),
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

$calculateReplacementAmount = Get-MethodRequired $decayType 'CalculateReplacementAmount'
$replacementAmount = [int] $calculateReplacementAmount.Invoke($null, [object[]] @(50))
Assert-True ($replacementAmount -eq 50) 'Spoilage replacement must preserve a 50-item source stack as one 50/20 over-stack.'
Assert-True ([int]$calculateReplacementAmount.Invoke($null, [object[]] @(-1)) -eq 0) 'Spoilage replacement must reject negative source counts.'

$iceboxType = Get-TypeRequired $assembly 'FineDining.IceboxSubsystem'
$parseIceboxRecipe = Get-MethodRequired $iceboxType 'TryParseRecipe'
$recipeArguments = [object[]] @('FineWood:10,Iron:2', $null, $null)
Assert-True ([bool]$parseIceboxRecipe.Invoke($null, $recipeArguments)) 'The default-shaped Icebox recipe must parse.'
Assert-True ($recipeArguments[1].Count -eq 2 -and [string]::IsNullOrEmpty($recipeArguments[2])) 'Icebox recipe parsing must preserve both requirements.'
$invalidRecipeArguments = [object[]] @('FineWood:10,FineWood:2', $null, $null)
Assert-True (-not [bool]$parseIceboxRecipe.Invoke($null, $invalidRecipeArguments)) 'Duplicate Icebox recipe prefabs must be rejected.'
Assert-True (-not [string]::IsNullOrWhiteSpace($invalidRecipeArguments[2])) 'Invalid Icebox recipes must explain the failure.'

$freshnessMethod = Get-MethodRequired $freshnessType 'CalculateFoodStatMultiplierForMinimum'
$freshnessFull = [float] $freshnessMethod.Invoke($null, [object[]] @([float]1, [float]0.75))
$freshnessHalf = [float] $freshnessMethod.Invoke($null, [object[]] @([float]0.5, [float]0.75))
$freshnessEmpty = [float] $freshnessMethod.Invoke($null, [object[]] @([float]0, [float]0.75))
Assert-True ([Math]::Abs($freshnessFull - 1) -lt 0.0001) 'Full freshness must retain x1 food stats.'
Assert-True ([Math]::Abs($freshnessHalf - 0.875) -lt 0.0001) 'Half freshness with a 0.75 minimum must produce x0.875.'
Assert-True ([Math]::Abs($freshnessEmpty - 0.75) -lt 0.0001) 'Zero freshness must use the configured minimum.'

$classifierType = Get-TypeRequired $assembly 'FineDining.FoodClassifier'
$selectGroup = Get-MethodRequired $classifierType 'TrySelectGroup'
function Assert-Classification(
    [bool] $Farming,
    [bool] $CookingInput,
    [bool] $CookingOutput,
    [bool] $Fermented,
    [bool] $FeastMaterial,
    [bool] $FeastResult,
    [bool] $Fish,
    [bool] $Edible,
    [bool] $ExpectedTracked,
    [string] $ExpectedGroup)
{
    $arguments = [object[]] @(
        $Farming,
        $CookingInput,
        $CookingOutput,
        $Fermented,
        $FeastMaterial,
        $FeastResult,
        $Fish,
        $Edible,
        $null)
    $tracked = [bool] $selectGroup.Invoke($null, $arguments)
    Assert-True ($tracked -eq $ExpectedTracked) "Unexpected tracked state for classification '$ExpectedGroup'."
    if ($ExpectedTracked)
    {
        Assert-True ($arguments[8].ToString() -eq $ExpectedGroup) "Expected classification '$ExpectedGroup', got '$($arguments[8])'."
    }
}

Assert-Classification $true $false $true $false $false $false $false $true $true 'FarmingHarvest'
Assert-Classification $false $true $false $false $false $false $false $true $true 'CookingStationOutput'
Assert-Classification $false $true $false $false $false $false $false $false $true 'CookingStationInput'
Assert-Classification $false $false $false $false $false $false $false $false $false 'OtherEdible'

$spoilageGroupType = Get-TypeRequired $assembly 'FineDining.SpoilageGroup'
$spoilageDefaultsType = Get-TypeRequired $assembly 'FineDining.SpoilageDefaults'
$getReplacementPrefab = Get-MethodRequired $spoilageDefaultsType 'GetReplacementPrefab'
$expectedReplacements = @{
    FarmingHarvest = 'FineDining_RottenProduce'
    CookingStationInput = 'RottenMeat'
    CookingStationOutput = 'RottenMeat'
    FermentedFood = 'FineDining_RottenFood'
    FeastMaterial = 'FineDining_RottenFood'
    FeastResult = 'FineDining_RottenFood'
    Fish = 'RottenMeat'
    OtherEdible = 'FineDining_RottenFood'
}
foreach ($group in [Enum]::GetValues($spoilageGroupType))
{
    $groupName = $group.ToString()
    Assert-True ($expectedReplacements.ContainsKey($groupName)) "Spoilage group '$groupName' is missing a replacement contract."
    $replacement = [string] $getReplacementPrefab.Invoke($null, [object[]] @($group))
    Assert-True ($replacement -eq $expectedReplacements[$groupName]) "Unexpected default replacement for '$groupName': '$replacement'."
}

$includePickable = Get-MethodRequired $classifierType 'ShouldIncludePickableOutput'
Assert-True (-not [bool]$includePickable.Invoke($null, [object[]] @($true, $false, $true))) 'Cultivated seed outputs must be excluded.'
Assert-True ([bool]$includePickable.Invoke($null, [object[]] @($true, $false, $false))) 'Cultivated non-seed outputs must be included.'
Assert-True ([bool]$includePickable.Invoke($null, [object[]] @($false, $true, $false))) 'Wild edible outputs must be included.'
Assert-True (-not [bool]$includePickable.Invoke($null, [object[]] @($false, $false, $false))) 'Wild non-edible outputs must be excluded.'

$stateType = Get-TypeRequired $assembly 'FineDining.PlayerFoodStateData'
$historyType = Get-TypeRequired $assembly 'FineDining.HistoryEntryData'
$historyServiceType = Get-TypeRequired $assembly 'FineDining.RecentHistoryService'
$state = [Activator]::CreateInstance($stateType, $true)
$recentField = $stateType.GetField('Recent', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $recentField) 'Diet recent-history field is missing.'
$recent = $recentField.GetValue($state)
foreach ($key in @('A', 'B', 'C'))
{
    $entry = [Activator]::CreateInstance($historyType, $true)
    $historyType.GetField('Key').SetValue($entry, $key)
    $historyType.GetField('Stack').SetValue($entry, 1)
    $recent.Add($entry)
}
$registerConsumption = Get-MethodRequired $historyServiceType 'RegisterConsumption'
$newStack = [int] $registerConsumption.Invoke($null, [object[]] @($state, 'A', $false))
$recentKeys = @($recent | ForEach-Object { [string]$historyType.GetField('Key').GetValue($_) })
Assert-True (($recentKeys -join ',') -eq 'B,C,A') 'Re-eaten food must move to the newest recent-history position.'
Assert-True ($newStack -eq 2) 'Re-eaten regular food must increment its diminishing stack.'

Assert-True ((Get-Constant $stateStoreType 'StatePrefix') -eq 'v1:') 'Diet state must use the v1 JSON prefix.'
Assert-True ($stateType.IsSerializable -and $historyType.IsSerializable) 'Diet state models must remain compatible with Unity JsonUtility.'
Assert-Method $stateStoreType 'SerializeState'
Assert-Method $stateStoreType 'DeserializeState'

$encodeClock = Get-MethodRequired $decayType 'EncodeClockValue'
$decodeClock = Get-MethodRequired $decayType 'TryDecodeClockValue'
$composeClock = Get-MethodRequired $decayType 'ComposeClockValues'
$nowTicks = [DateTime]::UtcNow.Ticks
$tenSeconds = [TimeSpan]::TicksPerSecond * 10L
$twentySeconds = [TimeSpan]::TicksPerSecond * 20L
$pausedClock = [long] $encodeClock.Invoke($null, [object[]] @($nowTicks, $tenSeconds, $true))
$runningClock = [long] $encodeClock.Invoke($null, [object[]] @($nowTicks, $twentySeconds, $false))
$decodeArguments = [object[]] @($pausedClock, $nowTicks, [long]0, $false)
Assert-True ([bool]$decodeClock.Invoke($null, $decodeArguments)) 'Paused spoilage clock must decode.'
Assert-True ([bool]$decodeArguments[3]) 'Paused spoilage clock must retain its paused state.'
Assert-True ([long]$decodeArguments[2] -eq $tenSeconds) 'Paused spoilage clock must retain its remaining duration.'
$composedClock = [long] $composeClock.Invoke($null, [object[]] @($runningClock, $pausedClock, $nowTicks, $false))
$composedArguments = [object[]] @($composedClock, $nowTicks, [long]0, $false)
Assert-True ([bool]$decodeClock.Invoke($null, $composedArguments)) 'Composed spoilage clock must decode.'
Assert-True ([long]$composedArguments[2] -eq $tenSeconds) 'Stack composition must keep the lower remaining spoilage time.'

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

Assert-ZipPackage $ThunderstoreZipPath @{
    'FineDining.dll' = $assemblyPath
    'README.md' = (Join-Path $projectRoot 'README.md')
    'LICENSE.txt' = (Join-Path $projectRoot 'LICENSE.txt')
    'THIRD-PARTY-NOTICES.txt' = (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt')
    'manifest.json' = (Join-Path $projectRoot 'Thunderstore\manifest.json')
    'CHANGELOG.md' = (Join-Path $projectRoot 'Thunderstore\CHANGELOG.md')
    'icon.png' = (Join-Path $projectRoot 'Thunderstore\icon.png')
} '1.0.0'
Assert-ZipPackage $NexusZipPath @{
    'FineDining.dll' = $assemblyPath
    'README.md' = (Join-Path $projectRoot 'README.md')
    'LICENSE.txt' = (Join-Path $projectRoot 'LICENSE.txt')
    'THIRD-PARTY-NOTICES.txt' = (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt')
}

Write-Host 'FineDining integration smoke checks passed.'
