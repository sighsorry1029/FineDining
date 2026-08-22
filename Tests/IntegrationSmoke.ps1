param(
    [string] $AssemblyPath = "$(Split-Path -Parent $PSScriptRoot)\bin\Debug\FineDining.dll",
    [string] $ManagedDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed'
)

$ErrorActionPreference = 'Stop'
$assemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assemblyDirectory = Split-Path -Parent $assemblyPath

[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)

    $fileName = ([Reflection.AssemblyName] $eventArgs.Name).Name + '.dll'
    $candidate = Join-Path $assemblyDirectory $fileName
    if (-not (Test-Path -LiteralPath $candidate) -and $fileName -like 'assembly_*.dll')
    {
        $candidate = Join-Path $assemblyDirectory ($fileName -replace '\.dll$', '_publicized.dll')
    }

    if (-not (Test-Path -LiteralPath $candidate))
    {
        $candidate = Join-Path $ManagedDirectory $fileName
    }

    if (-not (Test-Path -LiteralPath $candidate) -and $fileName -like 'assembly_*.dll')
    {
        $candidate = Join-Path (Join-Path $ManagedDirectory 'publicized_assemblies') ($fileName -replace '\.dll$', '_publicized.dll')
    }

    if (Test-Path -LiteralPath $candidate)
    {
        return [Reflection.Assembly]::LoadFrom($candidate)
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

function Assert-TextOrder([string] $Text, [string[]] $Needles, [string] $Message)
{
    $previousIndex = -1
    foreach ($needle in $Needles)
    {
        $index = $Text.IndexOf($needle, [StringComparison]::Ordinal)
        Assert-True ($index -gt $previousIndex) "$Message Missing or out-of-order text: $needle"
        $previousIndex = $index
    }
}

function Get-DirectCallCount(
    [Reflection.MethodInfo] $Caller,
    [Reflection.MethodInfo] $Callee)
{
    Assert-True ($null -ne $Caller) 'The caller method for an IL call check is missing.'
    Assert-True ($null -ne $Callee) 'The callee method for an IL call check is missing.'

    [byte[]] $il = $Caller.GetMethodBody().GetILAsByteArray()
    [int] $targetToken = $Callee.MetadataToken
    [int] $count = 0
    for ($index = 0; $index -le $il.Length - 5; $index++)
    {
        if (($il[$index] -eq 0x28 -or $il[$index] -eq 0x6F) -and
            [BitConverter]::ToInt32($il, $index + 1) -eq $targetToken)
        {
            $count++
        }
    }

    return $count
}

function Get-FirstDirectCallOffset(
    [Reflection.MethodInfo] $Caller,
    [Reflection.MethodInfo] $Callee)
{
    [byte[]] $il = $Caller.GetMethodBody().GetILAsByteArray()
    [int] $targetToken = $Callee.MetadataToken
    for ($index = 0; $index -le $il.Length - 5; $index++)
    {
        if (($il[$index] -eq 0x28 -or $il[$index] -eq 0x6F) -and
            [BitConverter]::ToInt32($il, $index + 1) -eq $targetToken)
        {
            return $index
        }
    }

    return -1
}

function Get-DirectGenericCallArgumentCount(
    [Reflection.MethodInfo] $Caller,
    [string] $GenericArgumentFullName)
{
    Assert-True ($null -ne $Caller) 'The caller method for a generic IL call check is missing.'

    [byte[]] $il = $Caller.GetMethodBody().GetILAsByteArray()
    [int] $count = 0
    for ($index = 0; $index -le $il.Length - 5; $index++)
    {
        if ($il[$index] -ne 0x28 -and $il[$index] -ne 0x6F)
        {
            continue
        }

        try
        {
            $calledMethod = $Caller.Module.ResolveMethod([BitConverter]::ToInt32($il, $index + 1))
        }
        catch
        {
            continue
        }

        if ($calledMethod -isnot [Reflection.MethodInfo] -or -not $calledMethod.IsGenericMethod)
        {
            continue
        }

        foreach ($argument in $calledMethod.GetGenericArguments())
        {
            if ([string]::Equals($argument.FullName, $GenericArgumentFullName, [StringComparison]::Ordinal))
            {
                $count++
            }
        }
    }

    return $count
}

$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$staticFlags = [Reflection.BindingFlags] 'Static,NonPublic'
$instanceFlags = [Reflection.BindingFlags] 'Instance,NonPublic,Public'
$allStaticFlags = [Reflection.BindingFlags] 'Static,NonPublic,Public'

$pluginType = $assembly.GetType('FineDining.FineDiningPlugin', $true)
$modVersionField = $pluginType.GetField('ModVersion', $allStaticFlags)
Assert-True ($null -ne $modVersionField) 'The plugin version constant is missing.'
Assert-True ([string] $modVersionField.GetRawConstantValue() -eq '1.0.0') 'The plugin version must remain 1.0.0.'
Assert-True ($assembly.GetName().Version -eq [Version] '1.0.0.0') 'The assembly version must remain 1.0.0.0.'
Assert-True ([bool] $pluginType.GetField('DefaultConfigurationLock', $allStaticFlags).GetRawConstantValue()) 'Server configuration locking must default to enabled.'
$configSyncField = $pluginType.GetField('ConfigSync', $allStaticFlags)
Assert-True ($null -ne $configSyncField -and $configSyncField.FieldType.FullName -eq 'ServerSync.ConfigSync') 'The required ServerSync configuration field is missing.'
Assert-True ($null -ne $assembly.GetType('ServerSync.VersionCheck', $false)) 'The merged ServerSync version handshake is missing.'
$localizerType = $assembly.GetType('LocalizationManager.Localizer', $true)
$localizerLoadMethod = $localizerType.GetMethod('Load', $allStaticFlags)
$loadLocalizationLaterMethod = $localizerType.GetMethod('LoadLocalizationLater', $allStaticFlags)
$pluginAwakeMethod = $pluginType.GetMethod('Awake', $instanceFlags)
Assert-True ($null -ne $localizerLoadMethod -and $null -ne $loadLocalizationLaterMethod -and $null -ne $pluginAwakeMethod) 'Localization startup methods are missing.'
Assert-True ($loadLocalizationLaterMethod.GetParameters().Count -eq 0) 'The FejdStartup.SetupGui localization postfix must not request a mismatched Localization __instance.'
$pluginAwakeIl = $pluginAwakeMethod.GetMethodBody().GetILAsByteArray()
$localizerLoadToken = $localizerLoadMethod.MetadataToken
$loadsLocalization = $false
for ($index = 0; $index -le $pluginAwakeIl.Length - 5; $index++)
{
    if ($pluginAwakeIl[$index] -eq 0x28 -and
        [BitConverter]::ToInt32($pluginAwakeIl, $index + 1) -eq $localizerLoadToken)
    {
        $loadsLocalization = $true
        break
    }
}
Assert-True $loadsLocalization 'FineDiningPlugin.Awake must activate the embedded LocalizationManager.'
$englishLocalizationStream = $assembly.GetManifestResourceStream('FineDining.translations.English.yml')
Assert-True ($null -ne $englishLocalizationStream) 'The embedded English localization resource is missing.'
$englishLocalizationReader = [IO.StreamReader]::new($englishLocalizationStream)
try
{
    $englishLocalizationText = $englishLocalizationReader.ReadToEnd()
    Assert-True ($englishLocalizationText.Contains('finedining_tooltip_spoils_in: "Spoils in {0}"')) 'The embedded detailed running tooltip translation is missing.'
    Assert-True ($englishLocalizationText.Contains('finedining_tooltip_paused:')) 'The embedded cold-paused tooltip translation is missing.'
    Assert-True ($englishLocalizationText.Contains('finedining_tooltip_freshness_effect: "Freshness effect: food stats x{0}"')) 'The embedded food-freshness tooltip translation must show only the multiplier.'
    Assert-True (-not $englishLocalizationText.Contains('(-{1}%)')) 'The English freshness tooltip must not repeat the multiplier as a percentage reduction.'
    Assert-True ($englishLocalizationText.Contains('finedining_duration_day: "{0}d"')) 'The embedded detailed day unit is missing.'
    Assert-True ($englishLocalizationText.Contains('finedining_icebox: "Icebox"')) 'The embedded Icebox name is missing.'
    Assert-True ($englishLocalizationText.Contains('finedining_rotten_produce: "Rotten Produce"')) 'The embedded Rotten Produce name is missing.'
    Assert-True ($englishLocalizationText.Contains('finedining_rotten_food: "Rotten Food"')) 'The embedded Rotten Food name is missing.'
    Assert-True ($englishLocalizationText.Contains('finedining_rotten_produce_description: "Spoiled produce. Eating it causes vomiting for 5 seconds."')) 'The Rotten Produce Puke duration description is missing.'
    Assert-True ($englishLocalizationText.Contains('finedining_rotten_food_description: "Thoroughly spoiled food. Eating it causes vomiting for 10 seconds."')) 'The Rotten Food Puke duration description is missing.'
    Assert-True ($englishLocalizationText.Contains('finedining_icebox_limit_reached:') -and $englishLocalizationText.Contains('{0}')) 'The localized Icebox quota message or its limit placeholder is missing.'
}
finally
{
    $englishLocalizationReader.Dispose()
}

$koreanLocalizationStream = $assembly.GetManifestResourceStream('FineDining.translations.Korean.yml')
Assert-True ($null -ne $koreanLocalizationStream) 'The embedded Korean localization resource is missing.'
$koreanLocalizationReader = [IO.StreamReader]::new($koreanLocalizationStream)
try
{
    $koreanLocalizationText = $koreanLocalizationReader.ReadToEnd()
    Assert-True ($koreanLocalizationText.Contains('finedining_tooltip_spoils_in:')) 'The Korean running tooltip translation is missing.'
    Assert-True ($koreanLocalizationText.Contains('finedining_tooltip_paused:')) 'The Korean cold-paused tooltip translation is missing.'
    $koreanFreshnessTranslation = $koreanLocalizationText.Replace("`r`n", "`n").Split("`n") |
        Where-Object { $_.StartsWith('finedining_tooltip_freshness_effect:', [StringComparison]::Ordinal) } |
        Select-Object -First 1
    Assert-True ($null -ne $koreanFreshnessTranslation -and $koreanFreshnessTranslation.Contains('x{0}"')) 'The Korean food-freshness tooltip translation must show the multiplier.'
    Assert-True (-not $koreanFreshnessTranslation.Contains('{1}') -and -not $koreanFreshnessTranslation.Contains('%')) 'The Korean freshness tooltip must not repeat the multiplier as a percentage reduction.'
    Assert-True ($koreanLocalizationText.Contains('finedining_duration_minute:')) 'The Korean detailed time units are missing.'
    Assert-True ($koreanLocalizationText.Contains('finedining_icebox:')) 'The Korean Icebox name is missing.'
    Assert-True ($koreanLocalizationText.Contains('finedining_rotten_produce:')) 'The Korean Rotten Produce name is missing.'
    Assert-True ($koreanLocalizationText.Contains('finedining_rotten_food:')) 'The Korean Rotten Food name is missing.'
    $koreanRottenProduceDescription = $koreanLocalizationText.Replace("`r`n", "`n").Split("`n") |
        Where-Object { $_.StartsWith('finedining_rotten_produce_description:', [StringComparison]::Ordinal) } |
        Select-Object -First 1
    $koreanRottenFoodDescription = $koreanLocalizationText.Replace("`r`n", "`n").Split("`n") |
        Where-Object { $_.StartsWith('finedining_rotten_food_description:', [StringComparison]::Ordinal) } |
        Select-Object -First 1
    Assert-True ($null -ne $koreanRottenProduceDescription -and $koreanRottenProduceDescription.Contains('5')) 'The Korean Rotten Produce Puke duration description is missing.'
    Assert-True ($null -ne $koreanRottenFoodDescription -and $koreanRottenFoodDescription.Contains('10')) 'The Korean Rotten Food Puke duration description is missing.'
    Assert-True ($koreanLocalizationText.Contains('finedining_icebox_limit_reached:')) 'The Korean Icebox quota message is missing.'
}
finally
{
    $koreanLocalizationReader.Dispose()
}

$policyType = $assembly.GetType('FineDining.SpoilagePolicy', $true)
$parseMethod = $policyType.GetMethod('TryParseAndNormalize', $staticFlags)

function Parse-Policy([string] $Yaml)
{
    $arguments = [object[]] @($Yaml, $null, '', '')
    $accepted = [bool] $parseMethod.Invoke($null, $arguments)
    return [pscustomobject] @{
        Accepted = $accepted
        Policy = $arguments[1]
        Normalized = [string] $arguments[2]
        Error = [string] $arguments[3]
    }
}

$defaultLifetimeLine = 'lifetimes: { farmingHarvest: 100, cookingStationInput: 75, cookingStationOutput: 50, fermentedFood: 25, feastMaterial: 125, feastResult: 125, fish: 75, otherEdible: 25 }'
function New-PolicyDocument(
    [string] $Overrides,
    [string] $LifetimeLine = $defaultLifetimeLine,
    [string[]] $ExtraLines = @())
{
    return (@('version: 1', $LifetimeLine) + $ExtraLines + @("overrides: $Overrides", '')) -join "`n"
}

$validYaml = @'
version: 1
lifetimes:
  farmingHarvest: 100
  cookingStationInput: 75
  cookingStationOutput: 50
  fermentedFood: 25
  feastMaterial: 125
  feastResult: 125
  fish: 75
  otherEdible: 25
overrides:
  - RawMeat, 100, RottenMeat
  - DeerMeat, 0
  - TinyFood, 0.01
  - SubsecondFood, 0.0000001
'@

$valid = Parse-Policy $validYaml
Assert-True $valid.Accepted $valid.Error
Assert-TextOrder $valid.Normalized @(
    'farmingHarvest:',
    'cookingStationInput:',
    'cookingStationOutput:',
    'fermentedFood:',
    'feastMaterial:',
    'feastResult:',
    'fish:',
    'otherEdible:'
) 'The normalized policy must preserve all eight runtime lifetime keys.'
Assert-True (-not $valid.Normalized.Contains('defaults:')) 'Fixed group replacements must not be exposed through YAML defaults.'
Assert-True (-not $valid.Normalized.Contains('spoiledPrefab:')) 'Fixed group replacements must not be exposed through YAML.'
Assert-True (-not $valid.Normalized.Contains('rawSingleCookInput:')) 'Legacy lifetime keys must not survive normalization.'
$overrides = $valid.Policy.GetType().GetProperty('Overrides', $instanceFlags).GetValue($valid.Policy)
Assert-True ($overrides.Count -eq 4) 'The valid policy should contain four overrides.'
$tiny = $overrides['TinyFood']
$subsecond = $overrides['SubsecondFood']
$ticksProperty = $tiny.GetType().GetProperty('LifetimeTicks', $instanceFlags)
Assert-True ($ticksProperty.GetValue($tiny) -eq 36L * [TimeSpan]::TicksPerSecond) '0.01 hours should be 36 seconds.'
Assert-True ($ticksProperty.GetValue($subsecond) -eq [TimeSpan]::TicksPerSecond) 'Positive sub-second values should clamp to one second.'
$hasReplacementOverrideProperty = $tiny.GetType().GetProperty('HasReplacementOverride', $instanceFlags)
$replacementPrefabProperty = $tiny.GetType().GetProperty('ReplacementPrefab', $instanceFlags)
Assert-True (-not [bool] $hasReplacementOverrideProperty.GetValue($tiny)) 'A compact two-field override must retain automatic replacement routing.'
Assert-True ([string] $replacementPrefabProperty.GetValue($tiny) -eq 'RottenMeat') 'An unclassifiable two-field override must retain the hidden RottenMeat fallback.'

$invalidDocuments = @(
    (New-PolicyDocument "[ 'DeerMeat, 0, RottenMeat' ]"),
    (New-PolicyDocument "[ 'RawMeat, 1', 'rawmeat, 2' ]"),
    (New-PolicyDocument '[]' 'lifetimes: { farmingHarvest: 5040.01, cookingStationInput: 75, cookingStationOutput: 50, fermentedFood: 25, feastMaterial: 125, feastResult: 125, fish: 75, otherEdible: 25 }'),
    (New-PolicyDocument "[ 'RawMeat, 1, RawMeat' ]"),
    (New-PolicyDocument "[ 'RottenMeat, 1, Coal' ]"),
    (New-PolicyDocument "[ 'FineDining_RottenProduce, 1, Coal' ]"),
    (New-PolicyDocument "[ 'FineDining_RottenFood, 1, Coal' ]"),
    (New-PolicyDocument '[]' $defaultLifetimeLine @('includedItems: [ RawMeat ]')),
    (New-PolicyDocument '[]' $defaultLifetimeLine @('defaults: { spoiledPrefab: RottenMeat }')),
    (New-PolicyDocument "[ 'RawMeat, 1, RottenMeat(Clone)' ]")
)

foreach ($document in $invalidDocuments)
{
    $invalid = Parse-Policy $document
    Assert-True (-not $invalid.Accepted) 'An invalid policy document was accepted.'
}

$resourceName = 'FineDining.Resources.Defaults.FineDining.yml'
Assert-True ($assembly.GetManifestResourceNames() -contains $resourceName) 'The embedded default policy is missing.'
$reader = [IO.StreamReader]::new($assembly.GetManifestResourceStream($resourceName))
try
{
    $embeddedYaml = $reader.ReadToEnd()
}
finally
{
    $reader.Dispose()
}

$embedded = Parse-Policy $embeddedYaml
Assert-True $embedded.Accepted $embedded.Error
$embeddedOverrides = $embedded.Policy.GetType().GetProperty('Overrides', $instanceFlags).GetValue($embedded.Policy)
Assert-True ($embeddedOverrides.Count -eq 0) 'The shipped policy must not force Dandelion, Thistle, or any other exact override.'

$referenceGeneratorType = $assembly.GetType('FineDining.SpoilageReferenceGenerator', $true)
$referenceEntryType = $assembly.GetType('FineDining.SpoilageReferenceEntry', $true)
$referenceSectionType = $assembly.GetType('FineDining.SpoilageReferenceSection', $true)
$referenceFileNameField = $referenceGeneratorType.GetField('ReferenceFileName', $allStaticFlags)
Assert-True ($null -ne $referenceFileNameField) 'The generated reference filename constant is missing.'
Assert-True (
    [string] $referenceFileNameField.GetRawConstantValue() -eq 'FineDining.reference.yml'
) 'The generated reference filename should remain FineDining.reference.yml.'
Assert-True ($null -ne $referenceGeneratorType.GetMethod('Tick', $staticFlags)) 'The reference generator must have a runtime entry point.'
Assert-True ($null -ne $referenceGeneratorType.GetMethod('Invalidate', $staticFlags)) 'The reference generator must be invalidatable after policy or prefab changes.'
Assert-True ($null -ne $referenceGeneratorType.GetMethod('Reset', $staticFlags)) 'The reference generator must reset between sessions.'

$referenceEntryConstructor = $referenceEntryType.GetConstructors($instanceFlags) |
    Where-Object { $_.GetParameters().Count -eq 5 } |
    Select-Object -First 1
Assert-True ($null -ne $referenceEntryConstructor) 'The reference entry constructor was not found.'

function New-ReferenceEntry(
    [string] $PrefabName,
    [string] $OwnerName,
    [string] $Section,
    [double] $LifetimeHours,
    [string] $ReplacementPrefab)
{
    $sectionValue = [Enum]::Parse($referenceSectionType, $Section)
    return $referenceEntryConstructor.Invoke([object[]] @(
        $PrefabName,
        $OwnerName,
        $sectionValue,
        $LifetimeHours,
        $ReplacementPrefab
    ))
}

$referenceEntryValues = @(
    (New-ReferenceEntry 'VanillaBerry' 'Valheim' 'FarmingHarvest' 100 'FineDining_RottenProduce'),
    (New-ReferenceEntry 'ModBerryB' 'Alpha Mod' 'FarmingHarvest' 100 'FineDining_RottenProduce'),
    (New-ReferenceEntry 'ModBerryA' 'Alpha Mod' 'FarmingHarvest' 100 'FineDining_RottenProduce'),
    (New-ReferenceEntry 'ZuluBerry' 'Zulu Mod' 'FarmingHarvest' 100 'FineDining_RottenProduce'),
    (New-ReferenceEntry 'UnknownBerry' '' 'FarmingHarvest' 100 'FineDining_RottenProduce'),
    (New-ReferenceEntry 'RawFood' 'Valheim' 'CookingStationInput' 75 'RottenMeat'),
    (New-ReferenceEntry 'CookedFood' 'Alpha Mod' 'CookingStationOutput' 50 'RottenMeat'),
    (New-ReferenceEntry 'FeastKit' 'Valheim' 'FeastMaterial' 125 'FineDining_RottenFood'),
    (New-ReferenceEntry 'PlacedFeastFood' 'Feaster Mod' 'FeastResult' 125 'FineDining_RottenFood'),
    (New-ReferenceEntry 'FermentedMeadFood' 'Valheim' 'FermentedFood' 25 'FineDining_RottenFood'),
    (New-ReferenceEntry 'WholeFish' 'Valheim' 'Fish' 75 'RottenMeat'),
    (New-ReferenceEntry 'OtherFood' '' 'OtherEdible' 25 'FineDining_RottenFood'),
    (New-ReferenceEntry 'OverrideWinner' 'Valheim' 'OtherEdible' 25 'RottenMeat'),
    (New-ReferenceEntry 'OverrideWinner' 'Alpha Mod' 'OverrideEnabled' 12.5 'CustomRotten'),
    (New-ReferenceEntry 'DisabledWinner' 'Valheim' 'OtherEdible' 25 'RottenMeat'),
    (New-ReferenceEntry 'DisabledWinner' 'Zulu Mod' 'OverrideDisabled' 0 ''),
    (New-ReferenceEntry 'Mod:Food' "Unsafe`r`nOwner" 'OverrideEnabled' 2.5 'Rotten:Food')
)
$referenceEntries = [Array]::CreateInstance($referenceEntryType, $referenceEntryValues.Count)
for ($index = 0; $index -lt $referenceEntryValues.Count; $index++)
{
    $referenceEntries.SetValue($referenceEntryValues[$index], $index)
}

$buildReferenceMethod = $referenceGeneratorType.GetMethod('BuildReferenceContent', $staticFlags)
Assert-True ($null -ne $buildReferenceMethod) 'The deterministic reference serializer is missing.'
$buildReferenceArguments = [object[]]::new(1)
$buildReferenceArguments[0] = $referenceEntries
$referenceContent = [string] $buildReferenceMethod.Invoke($null, $buildReferenceArguments)
Assert-True ($referenceContent.Contains('Generated by FineDining 1.0.0.')) 'The reference header must identify FineDining 1.0.0.'
Assert-True ($referenceContent.Contains('not loaded as configuration or synchronized')) 'The reference header must distinguish the lookup file from synchronized policy.'

Assert-TextOrder $referenceContent @(
    '# ===== automatic: farmingHarvest =====',
    '# ===== automatic: feastMaterial =====',
    '# ===== automatic: feastResult =====',
    '# ===== automatic: fermentedFood =====',
    '# ===== automatic: cookingStationOutput =====',
    '# ===== automatic: cookingStationInput =====',
    '# ===== automatic: fish =====',
    '# ===== automatic: otherEdible =====',
    '# ===== exact overrides: enabled =====',
    '# ===== exact overrides: disabled ====='
) 'Reference classifications must have a stable primary order.'
Assert-True ($referenceContent.Contains('- FeastKit, 125, FineDining_RottenFood')) 'Feast materials must be listed as runtime spoilage entries.'
Assert-True ($referenceContent.Contains('- PlacedFeastFood, 125, FineDining_RottenFood')) 'Placed feast results must use their dedicated runtime lifetime and replacement.'
Assert-True ($referenceContent.Contains('- FermentedMeadFood, 25, FineDining_RottenFood')) 'Directly edible Fermenter outputs must use their dedicated runtime group.'
Assert-True ($referenceContent.Contains('- WholeFish, 75, RottenMeat')) 'Structurally discovered fish must be listed in their runtime group.'

$harvestStart = $referenceContent.IndexOf('# ===== automatic: farmingHarvest =====', [StringComparison]::Ordinal)
$feastMaterialStart = $referenceContent.IndexOf('# ===== automatic: feastMaterial =====', [StringComparison]::Ordinal)
$harvestSection = $referenceContent.Substring($harvestStart, $feastMaterialStart - $harvestStart)
Assert-TextOrder $harvestSection @(
    '# ----- Valheim -----',
    '- VanillaBerry, 100, FineDining_RottenProduce',
    '# ----- Alpha Mod -----',
    '- ModBerryA, 100, FineDining_RottenProduce',
    '- ModBerryB, 100, FineDining_RottenProduce',
    '# ----- Zulu Mod -----',
    '- ZuluBerry, 100, FineDining_RottenProduce',
    '# ----- Unknown / Untracked -----',
    '- UnknownBerry, 100, FineDining_RottenProduce'
) 'Reference owners and prefabs must be sorted within each classification.'

$enabledStart = $referenceContent.IndexOf('# ===== exact overrides: enabled =====', [StringComparison]::Ordinal)
$disabledStart = $referenceContent.IndexOf('# ===== exact overrides: disabled =====', [StringComparison]::Ordinal)
$automaticContent = $referenceContent.Substring(0, $enabledStart)
$enabledContent = $referenceContent.Substring($enabledStart, $disabledStart - $enabledStart)
$disabledContent = $referenceContent.Substring($disabledStart)
Assert-True (-not $automaticContent.Contains('OverrideWinner')) 'An enabled exact override must replace its automatic reference row.'
Assert-True (-not $automaticContent.Contains('DisabledWinner')) 'A disabled exact override must replace its automatic reference row.'
Assert-True (($referenceContent.Split([string[]] @('OverrideWinner'), [StringSplitOptions]::None).Count - 1) -eq 1) 'An enabled exact override should appear exactly once.'
Assert-True (($referenceContent.Split([string[]] @('DisabledWinner'), [StringSplitOptions]::None).Count - 1) -eq 1) 'A disabled exact override should appear exactly once.'
Assert-True ($enabledContent.Contains('- OverrideWinner, 12.5, CustomRotten')) 'Enabled overrides must use the compact prefab, hours, replacement form.'
Assert-True ($disabledContent.Contains('- DisabledWinner, 0')) 'Disabled overrides must use the compact prefab, 0 form.'
Assert-True (-not $disabledContent.Contains('DisabledWinner, 0,')) 'Disabled overrides must not include a replacement prefab.'
Assert-True ($enabledContent.Contains('"Mod:Food, 2.5, Rotten:Food"')) 'Unsafe YAML scalars must be quoted without changing compact override semantics.'
Assert-True ($enabledContent.Contains('# ----- Unsafe  Owner -----')) 'Owner section comments must not permit newline injection.'

$reversedEntries = [Array]::CreateInstance($referenceEntryType, $referenceEntryValues.Count)
for ($index = 0; $index -lt $referenceEntryValues.Count; $index++)
{
    $reversedEntries.SetValue($referenceEntryValues[$referenceEntryValues.Count - 1 - $index], $index)
}
$reversedArguments = [object[]]::new(1)
$reversedArguments[0] = $reversedEntries
$reversedContent = [string] $buildReferenceMethod.Invoke($null, $reversedArguments)
Assert-True ($reversedContent -ceq $referenceContent) 'Reference generation must be deterministic regardless of capture order.'

$referenceRows = @($referenceContent.Replace("`r`n", "`n").Split("`n") |
    Where-Object { $_.StartsWith('- ', [StringComparison]::Ordinal) })
$copiedOverrideBlock = ($referenceRows | ForEach-Object { '  ' + $_ }) -join "`n"
$copiedReferenceYaml = New-PolicyDocument ("`n" + $copiedOverrideBlock)
$copiedReferencePolicy = Parse-Policy $copiedReferenceYaml
Assert-True $copiedReferencePolicy.Accepted "Every generated reference row must be directly copyable into overrides: $($copiedReferencePolicy.Error)"

$isPolicyFilePathMethod = $policyType.GetMethod('IsPolicyFilePath', $staticFlags)
Assert-True ($null -ne $isPolicyFilePathMethod) 'The exact policy watcher path filter is missing.'
Assert-True ([bool] $isPolicyFilePathMethod.Invoke($null, [object[]] @('C:\temp\FineDining.yml'))) 'The primary YAML must remain watched.'
Assert-True (-not [bool] $isPolicyFilePathMethod.Invoke($null, [object[]] @('C:\temp\FineDining.reference.yml'))) 'Writing the generated reference must not trigger a policy reload.'

$ownerResolverType = $assembly.GetType('FineDining.FoodPrefabOwnerResolver', $true)
$normalizeOwnerMethod = $ownerResolverType.GetMethod('NormalizeOwnerName', $staticFlags)
$ownerSortBucketMethod = $ownerResolverType.GetMethod('GetOwnerSortBucket', $staticFlags)
Assert-True ([string] $normalizeOwnerMethod.Invoke($null, [object[]] @('')) -eq 'Unknown / Untracked') 'Empty owner names must use the unknown section.'
$sanitizedOwner = [string] $normalizeOwnerMethod.Invoke($null, [object[]] @("  Unsafe`r`nOwner  "))
Assert-True ($sanitizedOwner -eq 'Unsafe  Owner') 'Owner names must remove line breaks before becoming YAML comments.'
Assert-True (-not ($sanitizedOwner.Contains("`r") -or $sanitizedOwner.Contains("`n"))) 'Sanitized owner names must remain on one line.'
Assert-True ([int] $ownerSortBucketMethod.Invoke($null, [object[]] @('Valheim')) -eq 0) 'Valheim must be the first owner bucket.'
Assert-True ([int] $ownerSortBucketMethod.Invoke($null, [object[]] @('Some Mod')) -eq 1) 'Mod owners must use the middle owner bucket.'
Assert-True ([int] $ownerSortBucketMethod.Invoke($null, [object[]] @('Unknown / Untracked')) -eq 2) 'Unknown owners must be the final owner bucket.'

$writeReferenceMethod = $referenceGeneratorType.GetMethod('WriteTextIfChanged', $staticFlags)
Assert-True ($null -ne $writeReferenceMethod) 'The changed-only reference writer is missing.'
$referenceTempPath = Join-Path ([IO.Path]::GetTempPath()) ('FineDining-reference-smoke-' + [Guid]::NewGuid().ToString('N') + '.yml')
$writeReferenceArguments = [object[]]::new(2)
$writeReferenceArguments[0] = [string] $referenceTempPath
$writeReferenceArguments[1] = [string] $referenceContent
try
{
    Assert-True ([bool] $writeReferenceMethod.Invoke($null, $writeReferenceArguments)) 'The first reference write should create the file.'
    Assert-True ([IO.File]::Exists($referenceTempPath)) 'The reference writer did not create its target file.'
    $referenceBytes = [IO.File]::ReadAllBytes($referenceTempPath)
    $hasUtf8Bom = $referenceBytes.Length -ge 3 -and $referenceBytes[0] -eq 0xEF -and $referenceBytes[1] -eq 0xBB -and $referenceBytes[2] -eq 0xBF
    Assert-True (-not $hasUtf8Bom) 'The generated YAML should use UTF-8 without a BOM.'

    [IO.File]::SetLastWriteTimeUtc($referenceTempPath, [DateTime]::new(2001, 1, 1, 0, 0, 0, [DateTimeKind]::Utc))
    $unchangedTimestamp = [IO.File]::GetLastWriteTimeUtc($referenceTempPath)
    Assert-True (-not [bool] $writeReferenceMethod.Invoke($null, $writeReferenceArguments)) 'Identical reference content must not rewrite the file.'
    Assert-True ([IO.File]::GetLastWriteTimeUtc($referenceTempPath).Ticks -eq $unchangedTimestamp.Ticks) 'An unchanged reference write must preserve LastWriteTimeUtc.'

    $changedReferenceContent = "# changed`n[]`n"
    $writeReferenceArguments[1] = [string] $changedReferenceContent
    Assert-True ([bool] $writeReferenceMethod.Invoke($null, $writeReferenceArguments)) 'Changed reference content must update the file.'
    Assert-True ([IO.File]::ReadAllText($referenceTempPath) -ceq $changedReferenceContent) 'Changed reference content was not written canonically.'
}
finally
{
    if ([IO.File]::Exists($referenceTempPath))
    {
        Remove-Item -LiteralPath $referenceTempPath -Force
    }
}

$uiType = $assembly.GetType('FineDining.InventoryGridSpoilageTimerPatch', $true)
$formatMethod = $uiType.GetMethod('FormatRemainingTime', $staticFlags)
$uiCases = @{
    0 = '1m'
    59 = '1m'
    60 = '1m'
    3599 = '60m'
    3600 = '1h'
    3601 = '2h'
}
foreach ($seconds in $uiCases.Keys)
{
    $actual = [string] $formatMethod.Invoke($null, [object[]] @([double] $seconds))
    Assert-True ($actual -eq $uiCases[$seconds]) "UI value for $seconds seconds should be $($uiCases[$seconds]), not $actual."
}

$runtimeType = $assembly.GetType('FineDining.DecayRuntime', $true)
$expiryKeyField = $runtimeType.GetField('ExpiryDataKey', $allStaticFlags)
Assert-True ($null -ne $expiryKeyField) 'The persisted world expiry key is missing.'
Assert-True (
    [string] $expiryKeyField.GetRawConstantValue() -eq 'sighsorry.FineDining.ExpiryWorldTicks'
) 'Changing the persisted expiry key would orphan existing inventory, container, and world timers.'
$placedAnchorKeyField = $runtimeType.GetField('PlacedAnchorDataKey', $allStaticFlags)
Assert-True ($null -ne $placedAnchorKeyField) 'The pending placed-item anchor key is missing.'
Assert-True (
    [string] $placedAnchorKeyField.GetRawConstantValue() -eq 'sighsorry.FineDining.PlacedWorldTicks'
) 'The placed-item policy-sync anchor must remain stable while pending ZDOs can exist.'

$freshnessType = $assembly.GetType('FineDining.FreshnessRuntime', $true)
$publicFreshnessApiType = $assembly.GetType('FineDining.FineDiningApi', $true)
Assert-True ($publicFreshnessApiType.IsPublic) 'The optional FineDining food-stat API must remain public.'
$foodMultiplierApiMethod = $publicFreshnessApiType.GetMethod('GetFoodStatMultiplier', [Reflection.BindingFlags] 'Static,Public')
Assert-True ($null -ne $foodMultiplierApiMethod) 'GourmetsDiet cannot resolve the public food multiplier API.'
Assert-True ($null -eq $publicFreshnessApiType.GetMethod('CopyFreshnessMetadata', [Reflection.BindingFlags] 'Static,Public')) 'Placed Feast metadata copying must remain an internal implementation detail.'
$copyFreshnessMethod = $freshnessType.GetMethod('CopyFreshnessMetadata', $staticFlags)
Assert-True ($null -ne $copyFreshnessMethod) 'Placed Feast consumption cannot bridge its own clock into edible data.'
$assignedLifetimeKeyField = $freshnessType.GetField('AssignedLifetimeDataKey', $allStaticFlags)
Assert-True ([string] $assignedLifetimeKeyField.GetRawConstantValue() -eq 'sighsorry.FineDining.AssignedLifetimeTicks') 'The assigned-lifetime persistence key changed.'
$tryFreshnessRatioMethod = $freshnessType.GetMethod('TryGetFreshnessRatio', $staticFlags)
$calculateFoodMultiplierMethod = $freshnessType.GetMethod('CalculateFoodStatMultiplier', $staticFlags)
$calculateFoodMultiplierForMinimumMethod = $freshnessType.GetMethod('CalculateFoodStatMultiplierForMinimum', $staticFlags)
Assert-True ($null -ne $tryFreshnessRatioMethod) 'Own-item remaining/lifetime freshness calculation is missing.'
Assert-True ($null -ne $calculateFoodMultiplierForMinimumMethod) 'The configurable linear freshness formula seam is missing.'
Assert-True ($null -eq $freshnessType.GetField('InheritedFreshnessDataKey', $allStaticFlags)) 'Ingredient freshness lineage metadata must not be persisted.'
Assert-True ([single] $calculateFoodMultiplierMethod.Invoke($null, [object[]] @([single] 1)) -eq [single] 1) 'Fully fresh food must retain x1 stats.'
Assert-True ([single] $calculateFoodMultiplierMethod.Invoke($null, [object[]] @([single] 0.5)) -eq [single] 0.875) 'Half freshness must linearly produce x0.875 stats.'
Assert-True ([single] $calculateFoodMultiplierMethod.Invoke($null, [object[]] @([single] 0)) -eq [single] 0.75) 'Expired-bound freshness must clamp at x0.75 stats.'
function Get-ConfiguredFreshnessMultiplier([single] $Freshness, [single] $Minimum)
{
    return [single] $calculateFoodMultiplierForMinimumMethod.Invoke(
        $null,
        [object[]] @($Freshness, $Minimum))
}
Assert-True ((Get-ConfiguredFreshnessMultiplier 0.5 0) -eq [single] 0.5) 'A zero minimum must make the stat multiplier equal freshness.'
Assert-True ((Get-ConfiguredFreshnessMultiplier 0.5 0.25) -eq [single] 0.625) 'A x0.25 minimum must derive the x0.75 slope automatically.'
Assert-True ((Get-ConfiguredFreshnessMultiplier 0.5 0.75) -eq [single] 0.875) 'The default x0.75 minimum must derive the x0.25 slope automatically.'
Assert-True ((Get-ConfiguredFreshnessMultiplier 0 1) -eq [single] 1) 'A x1 minimum must disable freshness stat loss.'
Assert-True ((Get-ConfiguredFreshnessMultiplier 2 -1) -eq [single] 1) 'The pure formula must safely clamp both inputs to the supported range.'

$defaultMinimumField = $freshnessType.GetField('DefaultMinimumFoodMultiplier', $allStaticFlags)
$minimumConfigField = $freshnessType.GetField('_minimumFoodMultiplier', $staticFlags)
$bindMinimumConfigMethod = $freshnessType.GetMethod('BindMinimumFoodMultiplier', $staticFlags)
$initializeFreshnessMethod = $freshnessType.GetMethod('Initialize', $staticFlags)
$shutdownFreshnessMethod = $freshnessType.GetMethod('Shutdown', $staticFlags)
$iceboxSubsystemType = $assembly.GetType('FineDining.IceboxSubsystem', $true)
$bindIceboxRowsMethod = $iceboxSubsystemType.GetMethod('BindStorageRows', $allStaticFlags)
$bindIceboxRecipeMethod = $iceboxSubsystemType.GetMethod('BindRecipe', $allStaticFlags)
$initializeIceboxSubsystemMethod = $iceboxSubsystemType.GetMethod('Initialize', $allStaticFlags)
$bindConfigurationLockMethod = $pluginType.GetMethod('BindConfigurationLock', $allStaticFlags)
Assert-True ([single] $defaultMinimumField.GetRawConstantValue() -eq [single] 0.75) 'The minimum food-effect multiplier default must remain x0.75.'
Assert-True ($null -ne $minimumConfigField -and $null -ne $bindMinimumConfigMethod -and $null -ne $initializeFreshnessMethod -and $null -ne $shutdownFreshnessMethod) 'The synchronized minimum food-effect config lifecycle is incomplete.'
Assert-True ($null -ne $bindIceboxRowsMethod -and $null -ne $bindIceboxRecipeMethod -and $null -ne $initializeIceboxSubsystemMethod) 'The synchronized Icebox gameplay config lifecycle is incomplete.'
Assert-True ($null -ne $bindConfigurationLockMethod) 'The server configuration locking entry is missing.'
$freshnessInitializeToken = $initializeFreshnessMethod.MetadataToken
$initializesFreshnessConfig = $false
for ($index = 0; $index -le $pluginAwakeIl.Length - 5; $index++)
{
    if ($pluginAwakeIl[$index] -eq 0x28 -and
        [BitConverter]::ToInt32($pluginAwakeIl, $index + 1) -eq $freshnessInitializeToken)
    {
        $initializesFreshnessConfig = $true
        break
    }
}
Assert-True $initializesFreshnessConfig 'FineDiningPlugin.Awake must bind and synchronize the minimum food-effect config.'
$freshnessConfigPath = Join-Path ([IO.Path]::GetTempPath()) ("FineDining-freshness-$([Guid]::NewGuid().ToString('N')).cfg")
$configFileType = $bindMinimumConfigMethod.GetParameters()[0].ParameterType
$configFileConstructor = $configFileType.GetConstructor(@([string], [bool]))
if ($null -eq $configFileConstructor)
{
    $configFileConstructor = $configFileType.GetConstructor(@([string], [bool], $assembly.GetType('BepInEx.BepInPlugin', $true)))
}
Assert-True ($null -ne $configFileConstructor) 'The installed BepInEx ConfigFile constructor could not be resolved for config validation.'
$configFileArguments = [object[]]::new($configFileConstructor.GetParameters().Count)
$configFileArguments[0] = [string] $freshnessConfigPath
$configFileArguments[1] = [bool] $false
if ($configFileArguments.Count -eq 3)
{
    $configFileArguments[2] = $null
}
$testConfigFileHolder = [object[]]::new(1)
$testConfigFileHolder[0] = $configFileConstructor.Invoke(
    [Reflection.BindingFlags]::Default,
    $null,
    [object[]] $configFileArguments,
    [Globalization.CultureInfo]::InvariantCulture)
$testConfigFile = $testConfigFileHolder[0]
try
{
    $bindMinimumArguments = [object[]]::new(1)
    $bindMinimumArguments[0] = $testConfigFile
    $minimumEntry = $bindMinimumConfigMethod.Invoke($null, $bindMinimumArguments)
    Assert-True ($null -ne $minimumEntry) 'The minimum food-effect config entry was not bound.'
    Assert-True ($minimumEntry.Definition.Section -eq '01 - Food Effects') 'The minimum food-effect config section changed.'
    Assert-True ($minimumEntry.Definition.Key -eq 'Minimum Food Effect Multiplier') 'The minimum food-effect config key changed.'
    Assert-True ([single] $minimumEntry.DefaultValue -eq [single] 0.75) 'The bound minimum food-effect config default must be x0.75.'
    $acceptableRange = $minimumEntry.Description.AcceptableValues
    Assert-True ($acceptableRange.GetType().GetGenericTypeDefinition().FullName -eq 'BepInEx.Configuration.AcceptableValueRange`1') 'The minimum food-effect config must declare an acceptable range.'
    Assert-True ([single] $acceptableRange.MinValue -eq [single] 0 -and [single] $acceptableRange.MaxValue -eq [single] 1) 'The minimum food-effect config range must be 0..1.'

    $bindRowsArguments = [object[]]::new(1)
    $bindRowsArguments[0] = $testConfigFile
    $rowsEntry = $bindIceboxRowsMethod.Invoke($null, $bindRowsArguments)
    Assert-True ($rowsEntry.Definition.Section -eq '03 - Icebox') 'The Icebox row config section changed.'
    Assert-True ($rowsEntry.Definition.Key -eq 'Storage Rows') 'The Icebox row config key changed.'
    Assert-True ([int] $rowsEntry.DefaultValue -eq 4) 'The Icebox must default to four rows.'
    $rowsRange = $rowsEntry.Description.AcceptableValues
    Assert-True ([int] $rowsRange.MinValue -eq 4 -and [int] $rowsRange.MaxValue -eq 20) 'The Icebox row config range must be 4..20.'

    $bindRecipeArguments = [object[]]::new(1)
    $bindRecipeArguments[0] = $testConfigFile
    $recipeEntry = $bindIceboxRecipeMethod.Invoke($null, $bindRecipeArguments)
    Assert-True ($recipeEntry.Definition.Section -eq '03 - Icebox') 'The Icebox recipe config section changed.'
    Assert-True ($recipeEntry.Definition.Key -eq 'Recipe') 'The Icebox recipe config key changed.'
    Assert-True ([string] $recipeEntry.DefaultValue -eq 'FineWood:10,Iron:2') 'The Icebox recipe default changed.'

    $initializeIl = $initializeFreshnessMethod.GetMethodBody().GetILAsByteArray()
    $calledMethods = [Collections.Generic.List[Reflection.MethodBase]]::new()
    $assignedFields = [Collections.Generic.List[Reflection.FieldInfo]]::new()
    for ($index = 0; $index -le $initializeIl.Length - 5; $index++)
    {
        if ($initializeIl[$index] -eq 0x7d)
        {
            try
            {
                $assignedFields.Add($initializeFreshnessMethod.Module.ResolveField([BitConverter]::ToInt32($initializeIl, $index + 1)))
            }
            catch
            {
                # A byte inside another operand may look like stfld.
            }
            continue
        }

        if ($initializeIl[$index] -ne 0x28 -and $initializeIl[$index] -ne 0x6f)
        {
            continue
        }

        try
        {
            $calledMethods.Add($initializeFreshnessMethod.Module.ResolveMethod([BitConverter]::ToInt32($initializeIl, $index + 1)))
        }
        catch
        {
            # A byte inside another operand may look like a call opcode.
        }
    }
    Assert-True (($calledMethods | Where-Object { $_.DeclaringType.FullName -eq 'ServerSync.ConfigSync' -and $_.Name -eq 'AddConfigEntry' }).Count -eq 1) 'The minimum food-effect config must be registered with ConfigSync.'
    Assert-True (($assignedFields | Where-Object { $_.DeclaringType.FullName -eq 'ServerSync.OwnConfigEntryBase' -and $_.Name -eq 'SynchronizedConfig' }).Count -eq 1) 'The minimum food-effect ConfigSync entry must explicitly enable synchronization.'

    $iceboxInitializeIl = $initializeIceboxSubsystemMethod.GetMethodBody().GetILAsByteArray()
    $iceboxCalledMethods = [Collections.Generic.List[Reflection.MethodBase]]::new()
    $iceboxAssignedFields = [Collections.Generic.List[Reflection.FieldInfo]]::new()
    for ($index = 0; $index -le $iceboxInitializeIl.Length - 5; $index++)
    {
        if ($iceboxInitializeIl[$index] -eq 0x7d)
        {
            try
            {
                $iceboxAssignedFields.Add($initializeIceboxSubsystemMethod.Module.ResolveField([BitConverter]::ToInt32($iceboxInitializeIl, $index + 1)))
            }
            catch
            {
                # A byte inside another operand may look like stfld.
            }
            continue
        }

        if ($iceboxInitializeIl[$index] -ne 0x28 -and $iceboxInitializeIl[$index] -ne 0x6f)
        {
            continue
        }

        try
        {
            $iceboxCalledMethods.Add($initializeIceboxSubsystemMethod.Module.ResolveMethod([BitConverter]::ToInt32($iceboxInitializeIl, $index + 1)))
        }
        catch
        {
            # A byte inside another operand may look like a call opcode.
        }
    }
    Assert-True (($iceboxCalledMethods | Where-Object { $_.DeclaringType.FullName -eq 'ServerSync.ConfigSync' -and $_.Name -eq 'AddConfigEntry' }).Count -eq 2) 'Both Icebox gameplay config entries must be registered with ConfigSync.'
    Assert-True (($iceboxAssignedFields | Where-Object { $_.DeclaringType.FullName -eq 'ServerSync.OwnConfigEntryBase' -and $_.Name -eq 'SynchronizedConfig' }).Count -eq 2) 'Both Icebox gameplay config entries must explicitly enable synchronization.'

    $pluginAwakeCalls = [Collections.Generic.List[Reflection.MethodBase]]::new()
    for ($index = 0; $index -le $pluginAwakeIl.Length - 5; $index++)
    {
        if ($pluginAwakeIl[$index] -ne 0x28 -and $pluginAwakeIl[$index] -ne 0x6f)
        {
            continue
        }

        try
        {
            $pluginAwakeCalls.Add($pluginAwakeMethod.Module.ResolveMethod([BitConverter]::ToInt32($pluginAwakeIl, $index + 1)))
        }
        catch
        {
            # A byte inside another operand may look like a call opcode.
        }
    }
    Assert-True (($pluginAwakeCalls | Where-Object { $_.DeclaringType.FullName -eq 'ServerSync.ConfigSync' -and $_.Name -eq 'AddLockingConfigEntry' }).Count -eq 1) 'FineDining must register exactly one server configuration lock.'
}
finally
{
    if ([IO.File]::Exists($freshnessConfigPath))
    {
        Remove-Item -LiteralPath $freshnessConfigPath -Force
    }
}

function New-FreshnessItem([long] $Clock, [long] $Assigned)
{
    $item = [Activator]::CreateInstance($foodMultiplierApiMethod.GetParameters()[0].ParameterType)
    $dataField = $item.GetType().GetField('m_customData', $instanceFlags)
    $data = $dataField.GetValue($item)
    if ($null -eq $data)
    {
        $data = [Collections.Generic.Dictionary[string,string]]::new()
        $dataField.SetValue($item, $data)
    }
    $data[[string] $expiryKeyField.GetRawConstantValue()] = $Clock.ToString([Globalization.CultureInfo]::InvariantCulture)
    $data[[string] $assignedLifetimeKeyField.GetRawConstantValue()] = $Assigned.ToString([Globalization.CultureInfo]::InvariantCulture)
    return $item
}

$freshnessBridgeSource = New-FreshnessItem 12345L 67890L
$freshnessBridgeDestination = New-FreshnessItem 9L 10L
$freshnessBridgeSourceData = $freshnessBridgeSource.GetType().GetField('m_customData', $instanceFlags).GetValue($freshnessBridgeSource)
$freshnessBridgeDestinationData = $freshnessBridgeDestination.GetType().GetField('m_customData', $instanceFlags).GetValue($freshnessBridgeDestination)
$freshnessBridgeSourceData['sighsorry.FineDining.PlacedWorldTicks'] = '111'
$freshnessBridgeSourceData['third.party.source'] = 'source'
$freshnessBridgeDestinationData['third.party.destination'] = 'keep'
$null = $copyFreshnessMethod.Invoke($null, [object[]] @($freshnessBridgeSource, $freshnessBridgeDestination))
Assert-True ([long] $freshnessBridgeDestinationData[[string] $expiryKeyField.GetRawConstantValue()] -eq 12345L) 'Placed Feast bridging must copy the host clock.'
Assert-True ([long] $freshnessBridgeDestinationData[[string] $assignedLifetimeKeyField.GetRawConstantValue()] -eq 67890L) 'Placed Feast bridging must copy the host lifetime basis.'
Assert-True ($freshnessBridgeDestinationData['third.party.destination'] -eq 'keep') 'Freshness bridging must preserve destination custom data owned by other mods.'
Assert-True (-not $freshnessBridgeDestinationData.ContainsKey('third.party.source')) 'Freshness bridging must not copy unrelated source custom data.'
Assert-True (-not $freshnessBridgeDestinationData.ContainsKey('sighsorry.FineDining.PlacedWorldTicks')) 'Freshness bridging must not copy placement anchors.'

$composeLifetimeMethod = $freshnessType.GetMethod('ComposeAssignedLifetime', $staticFlags)
$captureLifetimeMethod = $freshnessType.GetMethod('CaptureAssignedLifetime', $staticFlags)
Assert-True ($null -ne $composeLifetimeMethod -and $null -ne $captureLifetimeMethod) 'Assigned-lifetime stack composition seams are missing.'
$freshnessNow = 1000L * [TimeSpan]::TicksPerSecond
$destinationFreshnessItem = New-FreshnessItem (($freshnessNow + 600L * [TimeSpan]::TicksPerSecond)) (1200L * [TimeSpan]::TicksPerSecond)
$sourceFreshnessItem = New-FreshnessItem (($freshnessNow + 400L * [TimeSpan]::TicksPerSecond)) (800L * [TimeSpan]::TicksPerSecond)
$destinationSnapshot = $captureLifetimeMethod.Invoke($null, [object[]] @($destinationFreshnessItem))
$sourceSnapshot = $captureLifetimeMethod.Invoke($null, [object[]] @($sourceFreshnessItem))
$null = $composeLifetimeMethod.Invoke($null, [object[]] @($destinationFreshnessItem, $destinationSnapshot, $sourceSnapshot))
$mergedFreshnessData = $destinationFreshnessItem.GetType().GetField('m_customData', $instanceFlags).GetValue($destinationFreshnessItem)
Assert-True ([long] $mergedFreshnessData[[string] $assignedLifetimeKeyField.GetRawConstantValue()] -eq (1200L * [TimeSpan]::TicksPerSecond)) 'A merged stack must retain the conservative larger assigned lifetime.'

Assert-True ($null -eq $assembly.GetType('FineDining.InventoryGuiCraftingFreshnessPatch', $false)) 'General Recipe outputs must not inherit ingredient freshness.'
Assert-True ($null -eq $assembly.GetType('FineDining.CookingStationInputSpoilageSafetyPatch', $false)) 'CookingStation must retain its vanilla input path.'
Assert-True ($null -eq $assembly.GetType('FineDining.FermenterInputSpoilageSafetyPatch', $false)) 'Fermenter must retain its vanilla input path.'
Assert-True ($null -eq $assembly.GetType('FineDining.CookingStationOutputFreshnessPatch', $false)) 'CookingStation output freshness inheritance must remain disabled.'
Assert-True ($null -eq $assembly.GetType('FineDining.FermenterOutputFreshnessPatch', $false)) 'Fermenter output freshness inheritance must remain disabled.'
Assert-True ($null -ne $assembly.GetType('FineDining.FreshnessTooltipPatch', $false)) 'Per-item freshness must adjust edible tooltips.'
Assert-True ($null -ne $assembly.GetType('FineDining.PlayerEatFoodFreshnessPatch', $false)) 'Per-item freshness must adjust vanilla eating.'
Assert-True ($null -ne $assembly.GetType('FineDining.PlacedFeastFreshnessConsumptionPatch', $false)) 'Placed Feast eating must bridge the placed item clock.'

$tryGetSpoilageClockMethod = $runtimeType.GetMethod('TryGetSpoilageClock', $staticFlags)
$composeStackClockValuesMethod = $runtimeType.GetMethod('ComposeStackClockValues', $staticFlags)
$composeClockValuesMethod = $runtimeType.GetMethod('ComposeClockValues', $staticFlags)
Assert-True ($null -ne $tryGetSpoilageClockMethod) 'The signed spoilage-clock query seam is missing.'
Assert-True ($null -ne $composeStackClockValuesMethod) 'Stack metadata merges must expose signed-clock composition.'
Assert-True ($null -ne $composeClockValuesMethod) 'The pure signed-clock composition helper is missing.'
$inventorySlotsCompatibilityType = $assembly.GetType('FineDining.InventorySlotsCompatibility', $true)
$inventorySlotsGuidField = $inventorySlotsCompatibilityType.GetField('PluginGuid', $allStaticFlags)
Assert-True ($null -ne $inventorySlotsGuidField -and [string] $inventorySlotsGuidField.GetRawConstantValue() -eq 'sighsorry.InventorySlots') 'The optional InventorySlots signed-clock API handoff is missing or targets the wrong plugin.'

$clockItemType = $tryGetSpoilageClockMethod.GetParameters()[0].ParameterType
$clockCustomDataField = $clockItemType.GetField('m_customData', $instanceFlags)
$expiryDataKey = [string] $expiryKeyField.GetRawConstantValue()
function Invoke-SpoilageClock([AllowNull()] [string] $ClockValue, [long] $NowTicks)
{
    $clockItem = [Activator]::CreateInstance($clockItemType)
    $clockData = $clockCustomDataField.GetValue($clockItem)
    if ($null -eq $clockData)
    {
        $clockData = [Collections.Generic.Dictionary[string,string]]::new()
        $clockCustomDataField.SetValue($clockItem, $clockData)
    }

    if ($null -ne $ClockValue)
    {
        $clockData[$expiryDataKey] = $ClockValue
    }

    $arguments = [object[]] @($clockItem, $NowTicks, 0L, $false)
    $valid = [bool] $tryGetSpoilageClockMethod.Invoke($null, $arguments)
    return [pscustomobject] @{
        Valid = $valid
        Remaining = [long] $arguments[2]
        Paused = [bool] $arguments[3]
    }
}

$runningClock = Invoke-SpoilageClock '500' 200L
Assert-True ($runningClock.Valid -and -not $runningClock.Paused -and $runningClock.Remaining -eq 300L) 'A positive clock must decode as a running absolute deadline.'
$pausedClock = Invoke-SpoilageClock '-300' 200L
Assert-True ($pausedClock.Valid -and $pausedClock.Paused -and $pausedClock.Remaining -eq 300L) 'A negative clock must decode as frozen remaining time.'
$expiredClock = Invoke-SpoilageClock '100' 200L
Assert-True ($expiredClock.Valid -and -not $expiredClock.Paused -and $expiredClock.Remaining -eq 0L) 'An elapsed positive deadline must decode as an expired running clock.'

$minimumLongText = ([long]::MinValue).ToString([Globalization.CultureInfo]::InvariantCulture)
foreach ($invalidClockValue in @('0', '+1', '01', $minimumLongText))
{
    $invalidClock = Invoke-SpoilageClock $invalidClockValue 200L
    Assert-True (-not $invalidClock.Valid) "Clock metadata '$invalidClockValue' must be rejected unless it is canonical and representable."
}

function Compose-StackClock($Destination, $Source)
{
    return $composeStackClockValuesMethod.Invoke($null, [object[]] @($Destination, $Source))
}

Assert-True ([string] (Compose-StackClock $null '-300') -eq '-300') 'A missing destination must inherit a canonical signed source clock.'
foreach ($invalidSource in @('0', '+1', '01', $minimumLongText))
{
    Assert-True ([string] (Compose-StackClock '500' $invalidSource) -eq '500') "Invalid source clock '$invalidSource' must not alter destination metadata."
}

# ComposeStackClockValues obtains live ZNet time for two valid values. Its pure
# helper is invoked directly here so the smoke test remains deterministic in a
# process without Unity's native player, while the IL assertion keeps the public
# stack seam wired to that exact helper.
$composeStackClockIl = $composeStackClockValuesMethod.GetMethodBody().GetILAsByteArray()
$composeClockValuesToken = $composeClockValuesMethod.MetadataToken
$stackSeamUsesPureComposition = $false
for ($index = 0; $index -le $composeStackClockIl.Length - 5; $index++)
{
    if ($composeStackClockIl[$index] -eq 0x28 -and
        [BitConverter]::ToInt32($composeStackClockIl, $index + 1) -eq $composeClockValuesToken)
    {
        $stackSeamUsesPureComposition = $true
        break
    }
}
Assert-True $stackSeamUsesPureComposition 'ComposeStackClockValues must delegate valid mixed signed clocks to the tested pure composition helper.'

function Compose-PureClock([long] $Destination, [long] $Source, [long] $NowTicks, [bool] $DestinationPaused)
{
    return [long] $composeClockValuesMethod.Invoke(
        $null,
        [object[]] @($Destination, $Source, $NowTicks, $DestinationPaused))
}

Assert-True ((Compose-PureClock 500L (-300L) 0L $false) -eq 300L) 'A paused source with less remaining time must shorten a running destination without changing its sign.'
Assert-True ((Compose-PureClock (-500L) 300L 0L $true) -eq -300L) 'A running source with less remaining time must shorten a paused destination while preserving the destination pause state.'
Assert-True ((Compose-PureClock (-300L) (-500L) 0L $true) -eq -300L) 'A later paused source must not extend a paused destination.'
Assert-True ((Compose-PureClock 300L (-500L) 0L $false) -eq 300L) 'A later paused source must not pause or extend a running destination.'

$expiredSourceComposition = Compose-PureClock 500L 100L 200L $false
Assert-True ($expiredSourceComposition -eq 200L) 'An expired source must compose to an expired running deadline at the supplied world time.'
$expiredSourceIntoPaused = Compose-PureClock (-500L) 100L 200L $true
Assert-True ($expiredSourceIntoPaused -eq 200L) 'An expired source must not create the invalid paused-zero clock when merged into a paused destination.'

$registerGroundMethod = $runtimeType.GetMethod('RegisterGroundDrop', $staticFlags)
$composeGroundMethod = $runtimeType.GetMethod('ComposeGroundStackExpiry', $staticFlags)
$refreshOwnedPlacedMethod = $runtimeType.GetMethod('RefreshOwnedPlacedDrop', $staticFlags)
Assert-True ($null -ne $registerGroundMethod) 'Loaded timestamped ItemDrops must have a registration entry point.'
Assert-True ($null -ne $composeGroundMethod) 'Ground stack merges must retain an expiry composition entry point.'
Assert-True ($null -ne $refreshOwnedPlacedMethod) 'Placed ItemDrops must be recoverable after ZDO ownership handoff.'

$effectiveRemainingMethod = $runtimeType.GetMethod('CalculateEffectiveRemainingTicks', $staticFlags)
$ticksPerSecond = [TimeSpan]::TicksPerSecond
Assert-True ($null -ne $effectiveRemainingMethod) 'Signed placement must expose deterministic remaining-time composition.'
$warmDelayedRemaining = [long] $effectiveRemainingMethod.Invoke(
    $null,
    [object[]] @((20L * $ticksPerSecond), (10L * $ticksPerSecond), (15L * $ticksPerSecond), -1L, $false))
$coldDelayedRemaining = [long] $effectiveRemainingMethod.Invoke(
    $null,
    [object[]] @((20L * $ticksPerSecond), (10L * $ticksPerSecond), (15L * $ticksPerSecond), -1L, $true))
$inheritedShorterRemaining = [long] $effectiveRemainingMethod.Invoke(
    $null,
    [object[]] @((20L * $ticksPerSecond), (20L * $ticksPerSecond), (15L * $ticksPerSecond), (3L * $ticksPerSecond), $false))
$inheritedExpiredRemaining = [long] $effectiveRemainingMethod.Invoke(
    $null,
    [object[]] @((20L * $ticksPerSecond), (20L * $ticksPerSecond), (15L * $ticksPerSecond), 0L, $true))
Assert-True ($warmDelayedRemaining -eq 5L * $ticksPerSecond) 'Warm delayed placement must subtract elapsed world time from the target lifetime.'
Assert-True ($coldDelayedRemaining -eq 15L * $ticksPerSecond) 'Mountain delayed placement must retain the full frozen target lifetime.'
Assert-True ($inheritedShorterRemaining -eq 3L * $ticksPerSecond) 'Placement must retain the shorter inherited remaining duration.'
Assert-True ($inheritedExpiredRemaining -eq 0L) 'An already-expired source must remain due and must not receive a fresh Mountain lifetime.'

$initializePlacedMethod = $runtimeType.GetMethod('ShouldInitializePlacedDeadline', $staticFlags)
Assert-True (-not [bool] $initializePlacedMethod.Invoke($null, [object[]] @(0L, $true, $false))) 'Reloading an existing placed timer must preserve its absolute deadline.'
Assert-True ([bool] $initializePlacedMethod.Invoke($null, [object[]] @(100L, $true, $false))) 'A fresh placement must apply the target prefab lifetime cap.'
Assert-True ([bool] $initializePlacedMethod.Invoke($null, [object[]] @(0L, $true, $true))) 'A policy-sync anchor must finish target lifetime reconciliation after reload.'
Assert-True ([bool] $initializePlacedMethod.Invoke($null, [object[]] @(0L, $false, $false))) 'An existing timerless placed food Piece must receive a timer.'

$inheritSpawnMethod = $runtimeType.GetMethod('ShouldInheritGroundSpawnTime', $staticFlags)
Assert-True ([bool] $inheritSpawnMethod.Invoke($null, [object[]] @($true, $false))) 'Loose auto-destroy drops must preserve their vanilla cleanup age.'
Assert-True (-not [bool] $inheritSpawnMethod.Invoke($null, [object[]] @($true, $true))) 'Placed Pieces should receive a fresh cleanup age after becoming loose rotten items.'
Assert-True (-not [bool] $inheritSpawnMethod.Invoke($null, [object[]] @($false, $false))) 'Items without vanilla auto-destruction must not inherit a cleanup deadline.'

Assert-True ($null -ne $assembly.GetType('FineDining.PlayerPlacePieceSpoilagePatch', $false)) 'Player.PlacePiece must capture the stack that vanilla will consume.'
Assert-True ($null -ne $assembly.GetType('FineDining.ItemDropMakePieceSpoilagePatch', $false)) 'ItemDrop.MakePiece must persist the captured deadline on the placed ZDO owner.'
Assert-True ($null -ne $assembly.GetType('FineDining.PieceDropResourcesSpoilagePatch', $false)) 'Piece deconstruction must open a deadline recovery scope.'
Assert-True ($null -ne $assembly.GetType('FineDining.ItemDropCreateRecoveredSpoilagePatch', $false)) 'Recovered ground resources must inherit the Piece deadline.'
Assert-True ($null -ne $assembly.GetType('FineDining.GameCheckDropConversionSpoilagePatch', $false)) 'Damage-converted Piece resources must inherit the Piece deadline.'
Assert-True ($null -ne $assembly.GetType('FineDining.ItemDropSlowUpdateSpoilageSafetyPatch', $false)) 'Owned placed ItemDrops must be re-registered after ownership handoff.'
Assert-True ($null -ne $assembly.GetType('FineDining.ObjectDbUpdateRegistersSpoilagePatch', $false)) 'Late ObjectDB registration must invalidate direct-scope classification.'
Assert-True ($null -ne $assembly.GetType('FineDining.PlayerSaveSpoilageClockPatch', $false)) 'Player.Save must serialize paused inventory clocks as running deadlines so offline time keeps advancing.'

$replacementAmountMethod = $runtimeType.GetMethod('CalculateReplacementAmount', $staticFlags)
Assert-True ($null -ne $replacementAmountMethod) 'Replacement stack normalization is missing.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @(50, 50, 20)) -eq 20) 'A full 50-stack should become one full 20-stack.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @(25, 50, 20)) -eq 10) 'A half-full source stack should remain half-full after replacement.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @(1, 50, 20)) -eq 1) 'A positive source stack must produce at least one replacement.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @(49, 50, 20)) -eq 20) 'Fractional replacement amounts should round up.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @(50, 50, 100)) -eq 50) 'A roomier replacement stack must not create extra items.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @(100, 50, 20)) -eq 20) 'A modded over-stack must remain contained to one replacement stack.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @(0, 50, 20)) -eq 0) 'An empty source must not produce a replacement.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @(5, 0, 0)) -eq 1) 'Invalid stack capacities should normalize to one safely.'
Assert-True ([int] $replacementAmountMethod.Invoke($null, [object[]] @([int]::MaxValue, [int]::MaxValue, ([int]::MaxValue - 1))) -eq ([int]::MaxValue - 1)) 'Maximum integer stack values must scale without overflow.'
$gameAssembly = [AppDomain]::CurrentDomain.GetAssemblies() |
    Where-Object { $_.GetName().Name -eq 'assembly_valheim' } |
    Select-Object -First 1
if ($null -eq $gameAssembly)
{
    $publicizedGameAssembly = Join-Path $assemblyDirectory 'assembly_valheim_publicized.dll'
    if (-not (Test-Path -LiteralPath $publicizedGameAssembly))
    {
        $publicizedGameAssembly = Join-Path (Join-Path $ManagedDirectory 'publicized_assemblies') 'assembly_valheim_publicized.dll'
    }

    $gameAssembly = [Reflection.Assembly]::LoadFrom($publicizedGameAssembly)
}

Assert-True ($null -ne $gameAssembly) 'The Valheim assembly dependency was not loaded.'
$conversionPatchType = $assembly.GetType('FineDining.GameCheckDropConversionSpoilagePatch', $true)
$conversionTargetMethod = $conversionPatchType.GetMethod('TargetMethod', $staticFlags)
$conversionTarget = $conversionTargetMethod.Invoke($null, @())
Assert-True ($null -ne $conversionTarget) 'The exact Game.CheckDropConversion Harmony target was not found.'
Assert-True ($conversionTarget.Name -eq 'CheckDropConversion') 'The recovery conversion patch resolved the wrong method.'
Assert-True ($conversionTarget.GetParameters().Count -eq 4) 'Game.CheckDropConversion no longer has the expected signature.'
$itemDropComponentType = $gameAssembly.GetType('ItemDrop', $true)
$slowUpdateMethod = $itemDropComponentType.GetMethod('SlowUpdate', $instanceFlags)
Assert-True ($null -ne $slowUpdateMethod) 'The owner-handoff safety patch requires ItemDrop.SlowUpdate.'
$slowUpdatePatchType = $assembly.GetType('FineDining.ItemDropSlowUpdateSpoilageSafetyPatch', $true)
$slowUpdatePostfix = $slowUpdatePatchType.GetMethod('Postfix', $staticFlags)
Assert-True ($slowUpdatePostfix.GetParameters().Count -eq 1) 'The ItemDrop.SlowUpdate postfix signature changed unexpectedly.'
$feastComponentType = $gameAssembly.GetType('Feast', $true)
$feastHoverTarget = $feastComponentType.GetMethod('GetHoverText', $instanceFlags)
$itemDropHoverTarget = $itemDropComponentType.GetMethod('GetHoverText', $instanceFlags)
Assert-True ($null -ne $feastHoverTarget) 'Placed feast timer rendering requires Feast.GetHoverText.'
Assert-True ($null -ne $itemDropHoverTarget) 'World-item timer rendering requires ItemDrop.GetHoverText.'
$feastHoverPatchType = $assembly.GetType('FineDining.FeastSpoilageHoverPatch', $true)
$itemDropWorldItemHoverPatchType = $assembly.GetType('FineDining.ItemDropWorldItemSpoilageHoverPatch', $true)
$feastHoverPostfix = $feastHoverPatchType.GetMethod('Postfix', $staticFlags)
Assert-True ($null -ne $feastHoverPostfix) 'The Feast.GetHoverText postfix is missing.'
$itemDropWorldItemHoverPostfix = $itemDropWorldItemHoverPatchType.GetMethod('Postfix', $staticFlags)
Assert-True ($null -ne $itemDropWorldItemHoverPostfix) 'The ItemDrop.GetHoverText postfix is missing.'
$worldItemHoverType = $assembly.GetType('FineDining.WorldItemSpoilageHover', $true)
$appendWorldItemTimerMethod = $worldItemHoverType.GetMethod('Append', $staticFlags)
$tryBuildWorldItemTimerLineMethod = $worldItemHoverType.GetMethod('TryBuildTimerLine', $staticFlags)
Assert-True ($null -ne $appendWorldItemTimerMethod -and $null -ne $tryBuildWorldItemTimerLineMethod) 'The shared world-item hover helper is incomplete.'
$feastHoverIl = $feastHoverPostfix.GetMethodBody().GetILAsByteArray()
$appendWorldItemTimerToken = $appendWorldItemTimerMethod.MetadataToken
$feastHoverCallsWorldItemHelper = $false
for ($index = 0; $index -le $feastHoverIl.Length - 5; $index++)
{
    if ($feastHoverIl[$index] -eq 0x28 -and
        [BitConverter]::ToInt32($feastHoverIl, $index + 1) -eq $appendWorldItemTimerToken)
    {
        $feastHoverCallsWorldItemHelper = $true
        break
    }
}
Assert-True $feastHoverCallsWorldItemHelper 'Placed Feast hover must call the shared world-item helper.'
$itemDropWorldItemHoverIl = $itemDropWorldItemHoverPostfix.GetMethodBody().GetILAsByteArray()
$itemDropHoverCallsWorldItemHelper = $false
for ($index = 0; $index -le $itemDropWorldItemHoverIl.Length - 5; $index++)
{
    if ($itemDropWorldItemHoverIl[$index] -eq 0x28 -and
        [BitConverter]::ToInt32($itemDropWorldItemHoverIl, $index + 1) -eq $appendWorldItemTimerToken)
    {
        $itemDropHoverCallsWorldItemHelper = $true
        break
    }
}
Assert-True $itemDropHoverCallsWorldItemHelper 'Every ItemDrop hover path must call the shared world-item helper.'
$spoilageUiTextType = $assembly.GetType('FineDining.SpoilageUiText', $true)
$formatDetailedRemainingMethod = $spoilageUiTextType.GetMethod('FormatDetailedRemaining', $staticFlags)
$buildStatusLineMethod = $spoilageUiTextType.GetMethod('BuildStatusLine', $staticFlags)
$buildFreshnessEffectLineMethod = $spoilageUiTextType.GetMethod('BuildFreshnessEffectLine', $staticFlags)
Assert-True ($null -ne $formatDetailedRemainingMethod) 'The localized detailed day/hour/minute formatter is missing.'
Assert-True ($null -ne $buildStatusLineMethod) 'The shared running/paused spoilage status-line formatter is missing.'
Assert-True ($null -ne $buildFreshnessEffectLineMethod) 'The shared food-freshness effect formatter is missing.'
Assert-True ([string] $spoilageUiTextType.GetField('EnglishFreshnessEffectLine', $staticFlags).GetRawConstantValue() -eq 'Freshness effect: food stats x{0}') 'The English freshness fallback must show only the multiplier.'

$localizationAssembly = [AppDomain]::CurrentDomain.GetAssemblies() |
    Where-Object { $_.GetName().Name -eq 'assembly_guiutils' } |
    Select-Object -First 1
if ($null -eq $localizationAssembly)
{
    $localizationAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $assemblyDirectory 'assembly_guiutils_publicized.dll'))
}

$localizationType = $localizationAssembly.GetType('Localization', $true)
$localizationInstanceField = $localizationType.GetField('m_instance', $allStaticFlags)
$previousLocalization = $localizationInstanceField.GetValue($null)
$testLocalization = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($localizationType)
$localizationType.GetField('m_stringBuilder', $instanceFlags).SetValue($testLocalization, [Text.StringBuilder]::new())
$localizationType.GetField('m_endChars', $instanceFlags).SetValue($testLocalization, [char[]] @(' '))
$translationField = $localizationType.GetField('m_translations', $instanceFlags)
$translations = [Activator]::CreateInstance($translationField.FieldType)
$translations['finedining_tooltip_spoils_in'] = 'Spoils in {0}'
$translations['finedining_tooltip_paused'] =
    'Cold environment paused spoilage ' + [char] 0x00B7 + ' remaining {0} ' + [char] 0x2744
$translations['finedining_tooltip_freshness_effect'] = 'Freshness effect: food stats x{0}'
$translations['finedining_duration_day'] = '{0}d'
$translations['finedining_duration_hour'] = '{0}h'
$translations['finedining_duration_minute'] = '{0}m'
$translationField.SetValue($testLocalization, $translations)
$cacheField = $localizationType.GetField('m_cache', $instanceFlags)
$cacheField.SetValue($testLocalization, [Activator]::CreateInstance($cacheField.FieldType, [object[]] @(100)))
$localizationInstanceField.SetValue($null, $testLocalization)
try
{
    $detailedTicks = [TimeSpan]::FromMinutes((24 * 60) + (2 * 60) + 3).Ticks
    $detailedTime = [string] $formatDetailedRemainingMethod.Invoke($null, [object[]] @($detailedTicks))
    Assert-True ($detailedTime -eq '1d 2h 3m') 'Detailed tooltip time must retain non-zero day, hour, and minute components.'
    $subMinuteTime = [string] $formatDetailedRemainingMethod.Invoke($null, [object[]] @([TimeSpan]::FromSeconds(1).Ticks))
    Assert-True ($subMinuteTime -eq '1m') 'Detailed tooltip time must retain the one-minute display floor.'
    $exactDayTime = [string] $formatDetailedRemainingMethod.Invoke($null, [object[]] @([TimeSpan]::FromDays(1).Ticks))
    Assert-True ($exactDayTime -eq '1d') 'Detailed tooltip time should omit zero-valued trailing units.'

    $runningStatusLine = [string] $buildStatusLineMethod.Invoke($null, [object[]] @($detailedTicks, $false))
    Assert-True ($runningStatusLine -eq 'Spoils in 1d 2h 3m') 'Running item and feast tooltips must use the detailed shared status line.'
    $pausedStatusLine = [string] $buildStatusLineMethod.Invoke($null, [object[]] @($detailedTicks, $true))
    Assert-True ($pausedStatusLine.Contains('<color=#70C8FF>')) 'Paused item and feast tooltips must use the cold blue color.'
    Assert-True ($pausedStatusLine.Contains('Cold environment paused spoilage')) 'Paused tooltips must explicitly say that cold stopped spoilage.'
    Assert-True ($pausedStatusLine.Contains('1d 2h 3m')) 'Paused tooltips must retain the frozen detailed remaining time.'
    Assert-True ($pausedStatusLine.Contains([string] [char] 0x2744)) 'Paused tooltips must end with a visible snowflake state marker.'

    $freshnessEffectLine = [string] $buildFreshnessEffectLineMethod.Invoke($null, [object[]] @([single] 0.9999433))
    Assert-True ($freshnessEffectLine -eq 'Freshness effect: food stats x1.00') 'Near-full freshness must render x1.00 without a misleading (-0%) suffix.'
    Assert-True (-not $freshnessEffectLine.Contains('%')) 'Freshness effect lines must never repeat the multiplier as a percentage.'

    $worldHoverClockItemType = $foodMultiplierApiMethod.GetParameters()[0].ParameterType
    $worldHoverClockItem = [Activator]::CreateInstance($worldHoverClockItemType)
    $worldHoverClockDataField = $worldHoverClockItemType.GetField('m_customData', $instanceFlags)
    $worldHoverClockData = [Collections.Generic.Dictionary[string,string]]::new()
    $worldHoverClockDataField.SetValue($worldHoverClockItem, $worldHoverClockData)
    $worldHoverLineArguments = [object[]] @($worldHoverClockItem, 200L, '')
    $worldHoverClockData[$expiryDataKey] = '500'
    Assert-True ([bool] $tryBuildWorldItemTimerLineMethod.Invoke($null, $worldHoverLineArguments)) 'A running loose ItemDrop clock must produce hover text.'
    Assert-True ([string] $worldHoverLineArguments[2] -eq 'Spoils in 1m') 'A running loose ItemDrop must use the shared rounded-up timer text.'
    $worldHoverClockData[$expiryDataKey] = '-300'
    $worldHoverLineArguments[2] = ''
    Assert-True ([bool] $tryBuildWorldItemTimerLineMethod.Invoke($null, $worldHoverLineArguments)) 'A frozen loose ItemDrop clock must produce hover text.'
    Assert-True ([string] $worldHoverLineArguments[2] -like '*Cold environment paused spoilage*') 'A frozen loose ItemDrop must show the cold-paused state.'
    $null = $worldHoverClockData.Remove($expiryDataKey)
    $worldHoverLineArguments[2] = 'stale'
    Assert-True (-not [bool] $tryBuildWorldItemTimerLineMethod.Invoke($null, $worldHoverLineArguments)) 'A timerless natural ItemDrop must not produce spoilage hover text.'
    Assert-True ([string] $worldHoverLineArguments[2] -eq '') 'A failed world-item timer lookup must clear its output line.'
    $worldHoverClockData[$expiryDataKey] = 'malformed'
    Assert-True (-not [bool] $tryBuildWorldItemTimerLineMethod.Invoke($null, $worldHoverLineArguments)) 'Malformed ItemDrop clock metadata must not produce hover text.'
}
finally
{
    $localizationInstanceField.SetValue($null, $previousLocalization)
}

$appendWorldItemTimerIl = $appendWorldItemTimerMethod.GetMethodBody().GetILAsByteArray()
$tryBuildWorldItemTimerLineIl = $tryBuildWorldItemTimerLineMethod.GetMethodBody().GetILAsByteArray()
$buildStatusLineToken = $buildStatusLineMethod.MetadataToken
$callsSharedTimerFormatter = $false
$tryGetSpoilageClockToken = $tryGetSpoilageClockMethod.MetadataToken
$timerLineBuilderUsesClock = $false
for ($index = 0; $index -le $tryBuildWorldItemTimerLineIl.Length - 5; $index++)
{
    if ($tryBuildWorldItemTimerLineIl[$index] -ne 0x28)
    {
        continue
    }

    $methodToken = [BitConverter]::ToInt32($tryBuildWorldItemTimerLineIl, $index + 1)
    if ($methodToken -eq $buildStatusLineToken)
    {
        $callsSharedTimerFormatter = $true
    }
    elseif ($methodToken -eq $tryGetSpoilageClockToken)
    {
        $timerLineBuilderUsesClock = $true
    }
}

$worldItemHoverCalls = [Collections.Generic.List[Reflection.MethodBase]]::new()
for ($index = 0; $index -le $appendWorldItemTimerIl.Length - 5; $index++)
{
    if ($appendWorldItemTimerIl[$index] -ne 0x28 -and
        $appendWorldItemTimerIl[$index] -ne 0x6f)
    {
        continue
    }

    $methodToken = [BitConverter]::ToInt32($appendWorldItemTimerIl, $index + 1)
    try
    {
        $worldItemHoverCalls.Add($appendWorldItemTimerMethod.Module.ResolveMethod($methodToken))
    }
    catch
    {
        # A byte inside another operand may look like a call opcode.
    }
}
Assert-True $callsSharedTimerFormatter 'World-item hover text must reuse the detailed running/paused SpoilageUiText formatter.'
Assert-True $timerLineBuilderUsesClock 'World-item hover text must require a valid spoilage clock so timerless natural drops stay unchanged.'
Assert-True ($null -eq $worldItemHoverType.GetMethod('IsPlacedPiece', $staticFlags)) 'World-item hover text must not exclude loose timestamped ItemDrops behind a Piece gate.'
Assert-True (($worldItemHoverCalls | Where-Object { $_.DeclaringType.FullName -eq 'FineDining.FoodClassifier' }).Count -eq 0) 'World-item hover text must not be restricted by a food-classification cache.'
Assert-True (($worldItemHoverCalls | Where-Object { ($_.DeclaringType.FullName -eq 'ItemDrop' -and $_.Name -eq 'Save') -or ($_.DeclaringType.FullName -eq 'ZDO' -and $_.Name -eq 'Set') -or ($_.DeclaringType.FullName -eq 'ZNetView' -and $_.Name -like 'InvokeRPC*') }).Count -eq 0) 'World-item hover rendering must not save or mutate network state.'
$containsLineMethod = $spoilageUiTextType.GetMethod('ContainsLine', $staticFlags)
Assert-True ([bool] $containsLineMethod.Invoke($null, [object[]] @("Item name`n$runningStatusLine", $runningStatusLine))) 'World-item hover rendering must recognize an existing timer line and remain idempotent.'
Assert-True ($null -ne $assembly.GetType('FineDining.ItemDataSpoilageTooltipPatch', $false)) 'Real ItemData tooltips must receive the shared spoilage status line.'
$itemType = $gameAssembly.GetType('ItemDrop+ItemData', $true)
$sharedType = $gameAssembly.GetType('ItemDrop+ItemData+SharedData', $true)
$itemTypeEnum = $gameAssembly.GetType('ItemDrop+ItemData+ItemType', $true)
$freshnessEffectsType = $assembly.GetType('FineDining.FreshnessFoodEffects', $true)
$createFoodSnapshotMethod = $freshnessEffectsType.GetMethod('CreateFoodSnapshot', $staticFlags)
$createTooltipSnapshotMethod = $freshnessEffectsType.GetMethod('CreateTooltipSnapshot', $staticFlags)
$roundTooltipStatMethod = $freshnessEffectsType.GetMethod('RoundTooltipStat', $staticFlags)
Assert-True ($null -ne $createFoodSnapshotMethod -and $null -ne $createTooltipSnapshotMethod -and $null -ne $roundTooltipStatMethod) 'Food-effect snapshots must expose separate consumed and tooltip paths.'
Assert-True ([single] $roundTooltipStatMethod.Invoke($null, [object[]] @([single] 0)) -eq [single] 0) 'A true zero food stat must remain zero in the tooltip.'
Assert-True ([single] $roundTooltipStatMethod.Invoke($null, [object[]] @([single] 0.01)) -eq [single] 0.1) 'A small positive food stat must retain a visible x0.1 tooltip floor.'

$tooltipSource = [Activator]::CreateInstance($itemType)
$tooltipSourceShared = [Activator]::CreateInstance($sharedType)
$itemType.GetField('m_shared').SetValue($tooltipSource, $tooltipSourceShared)
$tooltipSourceData = $itemType.GetField('m_customData').GetValue($tooltipSource)
if ($null -eq $tooltipSourceData)
{
    $tooltipSourceData = [Collections.Generic.Dictionary[string,string]]::new()
    $itemType.GetField('m_customData').SetValue($tooltipSource, $tooltipSourceData)
}
$tooltipStats = @{
    m_food = [single] 14.99915
    m_foodStamina = [single] 7.4444
    m_foodEitr = [single] 5.5555
    m_foodRegen = [single] 0.9999431
}
foreach ($fieldName in $tooltipStats.Keys)
{
    $sharedType.GetField($fieldName).SetValue($tooltipSourceShared, $tooltipStats[$fieldName])
}

$freshTooltipSnapshot = $createTooltipSnapshotMethod.Invoke($null, [object[]] @($tooltipSource, [single] 0.875))
$freshTooltipShared = $itemType.GetField('m_shared').GetValue($freshTooltipSnapshot)
Assert-True ([single] $sharedType.GetField('m_food').GetValue($freshTooltipShared) -eq [single] 13.1) 'An uneaten food tooltip must apply freshness once and round health to one decimal.'
Assert-True ([single] $sharedType.GetField('m_foodRegen').GetValue($freshTooltipShared) -eq [single] 0.9) 'An uneaten food tooltip must round health regeneration to one decimal.'

$consumedSnapshot = $createFoodSnapshotMethod.Invoke($null, [object[]] @($tooltipSource, [single] 0.875))
$consumedShared = $itemType.GetField('m_shared').GetValue($consumedSnapshot)
Assert-True ([Math]::Abs([single] $sharedType.GetField('m_food').GetValue($tooltipSourceShared) - [single] 14.99915) -lt 0.000001) 'Consumed and tooltip snapshots must not mutate prefab/source food stats.'
Assert-True ([Math]::Abs([single] $sharedType.GetField('m_food').GetValue($consumedShared) - [single] 13.1) -gt 0.001) 'Consumed food snapshots must retain full calculation precision instead of tooltip rounding.'

$tooltipSnapshot = $createTooltipSnapshotMethod.Invoke($null, [object[]] @($consumedSnapshot, [single] 0.5))
$tooltipShared = $itemType.GetField('m_shared').GetValue($tooltipSnapshot)
Assert-True (-not [object]::ReferenceEquals($tooltipShared, $consumedShared)) 'Rounding an active-food tooltip must clone SharedData rather than mutating consumed state.'
Assert-True ([single] $sharedType.GetField('m_food').GetValue($tooltipShared) -eq [single] 13.1) 'Tooltip health must round to at most one decimal without applying freshness twice.'
Assert-True ([single] $sharedType.GetField('m_foodStamina').GetValue($tooltipShared) -eq [single] 6.5) 'Tooltip stamina must round to at most one decimal.'
Assert-True ([single] $sharedType.GetField('m_foodEitr').GetValue($tooltipShared) -eq [single] 4.9) 'Tooltip eitr must round to at most one decimal.'
Assert-True ([single] $sharedType.GetField('m_foodRegen').GetValue($tooltipShared) -eq [single] 0.9) 'Tooltip health regeneration must round to at most one decimal.'
Assert-True ([Math]::Abs([single] $sharedType.GetField('m_food').GetValue($consumedShared) - [single] 13.1) -gt 0.001) 'Tooltip rendering must leave the active consumed snapshot at full precision.'

$recoveryComposeMethod = $runtimeType.GetMethod('ComposeDirectRecoveryMergeExpiry', $staticFlags)
$applyEarlierClockMethod = $runtimeType.GetMethod('ApplyEarlierClock', $staticFlags)
Assert-True ($null -ne $recoveryComposeMethod) 'Optional inventory recovery merges must retain an expiry composition seam.'
Assert-True ($recoveryComposeMethod.GetParameters().Count -eq 3) 'The recovery merge composition seam must receive target, source, and moved amount.'
Assert-True ($null -ne $applyEarlierClockMethod) 'Direct recovery merges must retain their pure signed-clock application helper.'
$recoveryComposeIl = $recoveryComposeMethod.GetMethodBody().GetILAsByteArray()
$applyEarlierClockToken = $applyEarlierClockMethod.MetadataToken
$recoverySeamUsesSignedClock = $false
for ($index = 0; $index -le $recoveryComposeIl.Length - 5; $index++)
{
    if ($recoveryComposeIl[$index] -eq 0x28 -and
        [BitConverter]::ToInt32($recoveryComposeIl, $index + 1) -eq $applyEarlierClockToken)
    {
        $recoverySeamUsesSignedClock = $true
        break
    }
}
Assert-True $recoverySeamUsesSignedClock 'The optional direct-recovery seam must delegate to signed remaining-time composition.'

$stackField = $itemType.GetField('m_stack')
$customDataField = $itemType.GetField('m_customData')
$expiryDataKey = [string] $expiryKeyField.GetRawConstantValue()

function New-RecoveryMergeItem([int] $Stack, [string] $Expiry = '')
{
    $mergeItem = [Activator]::CreateInstance($itemType)
    $stackField.SetValue($mergeItem, $Stack)
    $customData = $customDataField.GetValue($mergeItem)
    if ($null -eq $customData)
    {
        $customData = [Collections.Generic.Dictionary[string,string]]::new()
        $customDataField.SetValue($mergeItem, $customData)
    }

    if ($Expiry.Length -gt 0)
    {
        $customData[$expiryDataKey] = $Expiry
    }

    return $mergeItem
}

function Invoke-RecoveryMerge($Target, $Source, [int] $Moved)
{
    # Valid composition normally asks ZNet for current world time. Exercise the
    # exact pure helper with now=0 outside Unity; early no-op/invalid branches
    # still invoke the public seam directly.
    if ($Moved -le 0 -or [object]::ReferenceEquals($Target, $Source))
    {
        return [bool] $recoveryComposeMethod.Invoke($null, [object[]] @($Target, $Source, $Moved))
    }

    $sourceData = $customDataField.GetValue($Source)
    if (-not $sourceData.ContainsKey($expiryDataKey))
    {
        return [bool] $recoveryComposeMethod.Invoke($null, [object[]] @($Target, $Source, $Moved))
    }

    $sourceText = [string] $sourceData[$expiryDataKey]
    $sourceClock = 0L
    $canonicalSource = [long]::TryParse(
        $sourceText,
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref] $sourceClock) -and
        $sourceClock -ne 0L -and
        $sourceClock -ne [long]::MinValue -and
        $sourceText -ceq $sourceClock.ToString([Globalization.CultureInfo]::InvariantCulture)
    if (-not $canonicalSource)
    {
        return [bool] $recoveryComposeMethod.Invoke($null, [object[]] @($Target, $Source, $Moved))
    }

    $targetData = $customDataField.GetValue($Target)
    $targetClock = 0L
    $targetPaused = $sourceClock -lt 0L
    if ($targetData.ContainsKey($expiryDataKey) -and
        [long]::TryParse(
            [string] $targetData[$expiryDataKey],
            [Globalization.NumberStyles]::Integer,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref] $targetClock) -and
        $targetClock -ne 0L -and
        $targetClock -ne [long]::MinValue)
    {
        $targetPaused = $targetClock -lt 0L
    }

    return [bool] $applyEarlierClockMethod.Invoke(
        $null,
        [object[]] @($Target, $sourceClock, 0L, $targetPaused))
}

$laterTarget = New-RecoveryMergeItem 8 '500'
$earlierSource = New-RecoveryMergeItem 2 '300'
$laterTargetData = $customDataField.GetValue($laterTarget)
$earlierSourceData = $customDataField.GetValue($earlierSource)
$laterTargetData['TargetOnly'] = 'preserve-target'
$earlierSourceData['SourceOnly'] = 'preserve-source'
Assert-True (Invoke-RecoveryMerge $laterTarget $earlierSource 2) 'A recovered direct merge must inherit the source deadline when it is earlier.'
Assert-True ($laterTargetData[$expiryDataKey] -eq '300') 'The direct merge target did not receive the earlier source deadline.'
Assert-True ($laterTargetData['TargetOnly'] -eq 'preserve-target') 'Recovery composition must preserve unrelated target custom data.'
Assert-True ($earlierSourceData[$expiryDataKey] -eq '300' -and $earlierSourceData['SourceOnly'] -eq 'preserve-source') 'Recovery composition must not mutate source custom data.'
Assert-True ([int] $stackField.GetValue($laterTarget) -eq 8 -and [int] $stackField.GetValue($earlierSource) -eq 2) 'FineDining must not change stack counts owned by the inventory mod.'

$earlierTarget = New-RecoveryMergeItem 9 '100'
$laterSource = New-RecoveryMergeItem 4 '900'
Assert-True (-not (Invoke-RecoveryMerge $earlierTarget $laterSource 1)) 'A later source deadline must not extend the target deadline.'
Assert-True ($customDataField.GetValue($earlierTarget)[$expiryDataKey] -eq '100') 'The earlier target deadline must remain unchanged.'

$invalidTarget = New-RecoveryMergeItem 9 'garbage'
$validSource = New-RecoveryMergeItem 4 '400'
Assert-True (Invoke-RecoveryMerge $invalidTarget $validSource 1) 'A valid source should replace a malformed target deadline.'
Assert-True ($customDataField.GetValue($invalidTarget)[$expiryDataKey] -eq '400') 'Malformed target deadline recovery was not canonicalized.'

$validTarget = New-RecoveryMergeItem 9 '500'
$invalidSource = New-RecoveryMergeItem 4 'garbage'
Assert-True (-not (Invoke-RecoveryMerge $validTarget $invalidSource 1)) 'A malformed source deadline must not propagate.'
Assert-True (-not (Invoke-RecoveryMerge $validTarget $validSource 0)) 'A zero-item operation is not a merge and must remain a no-op.'
Assert-True (-not (Invoke-RecoveryMerge $validTarget $validTarget 1)) 'A stack must never compose a deadline with itself.'

$chainTarget = New-RecoveryMergeItem 1 '900'
$chainSourceOne = New-RecoveryMergeItem 1 '600'
$chainSourceTwo = New-RecoveryMergeItem 1 '700'
$chainSourceThree = New-RecoveryMergeItem 1 '200'
Assert-True (Invoke-RecoveryMerge $chainTarget $chainSourceOne 1) 'The first direct recovery donor should shorten the target deadline.'
Assert-True (-not (Invoke-RecoveryMerge $chainTarget $chainSourceTwo 1)) 'A later donor in a recovery merge chain must not extend the target.'
Assert-True (Invoke-RecoveryMerge $chainTarget $chainSourceThree 1) 'The earliest donor in a recovery merge chain should win.'
Assert-True ($customDataField.GetValue($chainTarget)[$expiryDataKey] -eq '200') 'A recovery merge chain must retain the minimum participating deadline.'
Assert-True ($customDataField.GetValue($validTarget)[$expiryDataKey] -eq '500') 'A merge into another target must not bleed into an unrelated stack.'

$azuCompatibilityType = $assembly.GetType('FineDining.AzuExtendedPlayerInventoryCompatibility', $true)
$azuGuidField = $azuCompatibilityType.GetField('PluginGuid', $allStaticFlags)
Assert-True ($null -ne $azuGuidField) 'The optional AzuExtendedPlayerInventory GUID is missing.'
Assert-True ([string] $azuGuidField.GetRawConstantValue() -eq 'Azumatt.AzuExtendedPlayerInventory') 'The AzuExtendedPlayerInventory soft-dependency GUID changed.'
Assert-True ($null -ne $azuCompatibilityType.GetMethod('TryInstall', $staticFlags)) 'The optional AzuExtendedPlayerInventory compatibility installer is missing.'
Assert-True ($null -ne $azuCompatibilityType.GetMethod('Transpiler', $staticFlags)) 'The verified direct-merge transpiler is missing.'

$item = [Activator]::CreateInstance($itemType)
$shared = [Activator]::CreateInstance($sharedType)
$itemType.GetField('m_shared').SetValue($item, $shared)
$sharedType.GetField('m_itemType').SetValue($shared, [Enum]::Parse($itemTypeEnum, 'Material'))
$sharedType.GetField('m_food').SetValue($shared, [single] 100)
$classifierType = $assembly.GetType('FineDining.FoodClassifier', $true)
$isEdibleMethod = $classifierType.GetMethod('IsEdible', $staticFlags)
$runtimeFoodMultiplierMethod = $freshnessType.GetMethod('GetFoodStatMultiplier', $staticFlags)
$runtimeFoodMultiplierIl = $runtimeFoodMultiplierMethod.GetMethodBody().GetILAsByteArray()
$runtimeUsesClassifierEdibility = $false
for ($index = 0; $index -le $runtimeFoodMultiplierIl.Length - 5; $index++)
{
    if ($runtimeFoodMultiplierIl[$index] -eq 0x28 -and
        [BitConverter]::ToInt32($runtimeFoodMultiplierIl, $index + 1) -eq $isEdibleMethod.MetadataToken)
    {
        $runtimeUsesClassifierEdibility = $true
        break
    }
}
Assert-True $runtimeUsesClassifierEdibility 'Food freshness effects must use the same direct-edibility predicate as food classification.'
Assert-True (-not [bool] $isEdibleMethod.Invoke($null, [object[]] @($item))) 'A Material with food stats should not be directly edible.'
$sharedType.GetField('m_itemType').SetValue($shared, [Enum]::Parse($itemTypeEnum, 'Consumable'))
Assert-True ([bool] $isEdibleMethod.Invoke($null, [object[]] @($item))) 'A Consumable with direct food stats should be edible.'
$sharedType.GetField('m_food').SetValue($shared, [single] 0)
$sharedType.GetField('m_foodRegen').SetValue($shared, [single] 10)
Assert-True (-not [bool] $isEdibleMethod.Invoke($null, [object[]] @($item))) 'Health regeneration alone must not turn a Consumable into direct food.'
$hasFoodEffectMethod = $freshnessEffectsType.GetMethod('HasFoodEffect', $staticFlags)
Assert-True ($null -ne $hasFoodEffectMethod) 'The tooltip freshness qualifier is missing.'
Assert-True (-not [bool] $hasFoodEffectMethod.Invoke($null, [object[]] @($item))) 'A regeneration-only Consumable must not receive a freshness effect line.'
Assert-True ([single] $foodMultiplierApiMethod.Invoke($null, [object[]] @($item)) -eq [single] 1) 'A regeneration-only Consumable must expose a neutral freshness multiplier to integrations.'
$sharedType.GetField('m_foodStamina').SetValue($shared, [single] 1)
Assert-True ([bool] $isEdibleMethod.Invoke($null, [object[]] @($item))) 'A primary food stat must admit a Consumable even when it also has regeneration.'
Assert-True ([bool] $hasFoodEffectMethod.Invoke($null, [object[]] @($item))) 'A directly edible Consumable must retain freshness scaling for its regeneration stat.'
$looksLikeFeastRoutingFoodMethod = $classifierType.GetMethod('LooksLikeFeastRoutingFood', $staticFlags)
Assert-True ($null -ne $looksLikeFeastRoutingFoodMethod) 'Feast-result discovery must retain its Feaster-compatible routing predicate.'
$sharedType.GetField('m_foodStamina').SetValue($shared, [single] 0)
$sharedType.GetField('m_isDrink').SetValue($shared, $true)
Assert-True ([bool] $looksLikeFeastRoutingFoodMethod.Invoke($null, [object[]] @($shared))) 'Feaster-compatible routing must recognize a drink-like linked result without widening direct-food scope.'
$sharedType.GetField('m_isDrink').SetValue($shared, $false)
Assert-True (-not [bool] $looksLikeFeastRoutingFoodMethod.Invoke($null, [object[]] @($shared))) 'Regeneration alone must not look like a Feast routing result.'

$shouldIncludePickableOutputMethod = $classifierType.GetMethod('ShouldIncludePickableOutput', $allStaticFlags)
Assert-True ($null -ne $shouldIncludePickableOutputMethod) 'The output-level Pickable classification seam is missing.'
function Should-IncludePickableOutput(
    [bool] $CultivatedRoot,
    [bool] $DirectlyEdible,
    [bool] $HasSeedPrefabSuffix)
{
    return [bool] $shouldIncludePickableOutputMethod.Invoke(
        $null,
        [object[]] @($CultivatedRoot, $DirectlyEdible, $HasSeedPrefabSuffix))
}

Assert-True (Should-IncludePickableOutput $true $false $false) 'A non-seed output from a Plant.m_grownPrefabs root must be included even when it is not edible.'
Assert-True (-not (Should-IncludePickableOutput $true $false $true)) 'A non-edible cultivated output with a Seed or Seeds prefab suffix must be excluded.'
Assert-True (Should-IncludePickableOutput $true $true $true) 'Direct edibility must win over the cultivated Seed or Seeds suffix exclusion.'
Assert-True (Should-IncludePickableOutput $false $true $false) 'A directly edible output from any other Pickable must be included.'
Assert-True (Should-IncludePickableOutput $false $true $true) 'A directly edible non-cultivated output must remain included even when its prefab name has a Seed or Seeds suffix.'
Assert-True (-not (Should-IncludePickableOutput $false $false $false)) 'A non-edible output from a non-cultivated Pickable must be excluded.'
Assert-True (
    (Should-IncludePickableOutput $false $true $false) -and
    -not (Should-IncludePickableOutput $false $false $false)
) 'An edible main output must not pull in a non-edible extra output.'
Assert-True (
    -not (Should-IncludePickableOutput $false $false $false) -and
    (Should-IncludePickableOutput $false $true $false)
) 'An edible extra output must not cause a non-edible main output to be included.'

$hasSeedPrefabSuffixMethod = $classifierType.GetMethod('HasSeedPrefabSuffix', $allStaticFlags)
Assert-True ($null -ne $hasSeedPrefabSuffixMethod) 'The cultivated seed-output suffix predicate is missing.'
function Has-SeedPrefabSuffix([string] $PrefabName)
{
    return [bool] $hasSeedPrefabSuffixMethod.Invoke($null, [object[]] @($PrefabName))
}

Assert-True (Has-SeedPrefabSuffix 'CarrotSeeds') 'Plural Seeds suffixes must be recognized.'
Assert-True (Has-SeedPrefabSuffix 'AncientSeed') 'Singular Seed suffixes must be recognized.'
Assert-True (Has-SeedPrefabSuffix 'vinegreenseeds(Clone)') 'Seed suffix matching must ignore case and the runtime Clone suffix.'
Assert-True (-not (Has-SeedPrefabSuffix 'SeedOil')) 'Seed at the start of a prefab name must not trigger the suffix rule.'
Assert-True (-not (Has-SeedPrefabSuffix 'SeedBread')) 'Seed inside a prefab name must not trigger the suffix rule.'
Assert-True (-not (Has-SeedPrefabSuffix 'Barley')) 'Non-seed cultivated harvests must not be filtered by name.'
Assert-True (-not (Has-SeedPrefabSuffix 'Flax')) 'Non-seed cultivated harvests must not be filtered by name.'

$spoilageGroupType = $assembly.GetType('FineDining.SpoilageGroup', $true)
Assert-True (
    ([Enum]::GetNames($spoilageGroupType) -join ',') -eq
    'FarmingHarvest,CookingStationInput,CookingStationOutput,FermentedFood,FeastMaterial,FeastResult,Fish,OtherEdible'
) 'The automatic lifetime groups must contain all eight direct runtime classifications.'
$selectGroupMethod = $classifierType.GetMethod('TrySelectGroup', $staticFlags)
Assert-True ($null -ne $selectGroupMethod) 'The automatic classification priority seam is missing.'

function Select-AutomaticGroup(
    [bool] $FarmingHarvest,
    [bool] $CookingInput,
    [bool] $CookingOutput,
    [bool] $FermentedFood,
    [bool] $FeastMaterial,
    [bool] $FeastResult,
    [bool] $Fish,
    [bool] $Edible)
{
    $arguments = [object[]] @(
        $FarmingHarvest,
        $CookingInput,
        $CookingOutput,
        $FermentedFood,
        $FeastMaterial,
        $FeastResult,
        $Fish,
        $Edible,
        $null)
    $tracked = [bool] $selectGroupMethod.Invoke($null, $arguments)
    return [pscustomobject] @{
        Tracked = $tracked
        Group = if ($null -eq $arguments[8]) { '' } else { $arguments[8].ToString() }
    }
}

$unrelated = Select-AutomaticGroup $false $false $false $false $false $false $false $false
$directEdible = Select-AutomaticGroup $false $false $false $false $false $false $false $true
$fish = Select-AutomaticGroup $false $false $false $false $false $false $true $true
$nonEdibleInput = Select-AutomaticGroup $false $true $false $false $false $false $false $false
$edibleInput = Select-AutomaticGroup $false $true $false $false $false $false $false $true
$nonEdibleOutput = Select-AutomaticGroup $false $false $true $false $false $false $false $false
$fermentedOverlap = Select-AutomaticGroup $false $true $true $true $false $false $true $true
$feastResultOverlap = Select-AutomaticGroup $false $true $true $true $false $true $true $true
$feastMaterialOverlap = Select-AutomaticGroup $false $true $true $true $true $true $true $true
$harvestOverlap = Select-AutomaticGroup $true $true $true $true $true $true $true $true
Assert-True (-not $unrelated.Tracked) 'A non-edible item with no direct Farming or CookingStation relationship must be excluded.'
Assert-True ($directEdible.Tracked -and $directEdible.Group -eq 'OtherEdible') 'A directly edible item should use otherEdible.'
Assert-True ($fish.Tracked -and $fish.Group -eq 'Fish') 'A Fish component or pickup endpoint must use the fish group before broad edibility.'
Assert-True ($nonEdibleInput.Tracked -and $nonEdibleInput.Group -eq 'CookingStationInput') 'A non-edible CookingStation input must remain an explicit decay exception.'
Assert-True ($edibleInput.Tracked -and $edibleInput.Group -eq 'CookingStationOutput') 'An edible CookingStation input must use the output lifetime.'
Assert-True ($nonEdibleOutput.Tracked -and $nonEdibleOutput.Group -eq 'CookingStationOutput') 'A non-edible CookingStation output must remain an explicit decay exception.'
Assert-True ($fermentedOverlap.Tracked -and $fermentedOverlap.Group -eq 'FermentedFood') 'A structural Fermenter output must win cooking, fish, and edible overlaps.'
Assert-True ($feastResultOverlap.Tracked -and $feastResultOverlap.Group -eq 'FeastResult') 'A structural feast result must win fermenter, cooking, fish, and edible overlaps.'
Assert-True ($feastMaterialOverlap.Tracked -and $feastMaterialOverlap.Group -eq 'FeastMaterial') 'A structural feast material must win other structural detail groups.'
Assert-True ($harvestOverlap.Tracked -and $harvestOverlap.Group -eq 'FarmingHarvest') 'A Farming harvest output must win every automatic overlap.'

$spoilageDefaultsType = $assembly.GetType('FineDining.SpoilageDefaults', $true)
$getReplacementPrefabMethod = $spoilageDefaultsType.GetMethod('GetReplacementPrefab', $staticFlags)
Assert-True ($null -ne $getReplacementPrefabMethod) 'The fixed group replacement routing method is missing.'
function Get-GroupReplacement([string] $Group)
{
    $value = [Enum]::Parse($spoilageGroupType, $Group)
    return [string] $getReplacementPrefabMethod.Invoke($null, [object[]] @($value))
}
Assert-True ((Get-GroupReplacement 'FarmingHarvest') -eq 'FineDining_RottenProduce') 'Farming harvests must become RottenProduce.'
Assert-True ((Get-GroupReplacement 'CookingStationInput') -eq 'RottenMeat') 'CookingStation inputs must become RottenMeat.'
Assert-True ((Get-GroupReplacement 'CookingStationOutput') -eq 'RottenMeat') 'CookingStation outputs must become RottenMeat.'
Assert-True ((Get-GroupReplacement 'Fish') -eq 'RottenMeat') 'Fish must become RottenMeat.'
foreach ($groupName in @('FermentedFood', 'FeastMaterial', 'FeastResult', 'OtherEdible'))
{
    Assert-True ((Get-GroupReplacement $groupName) -eq 'FineDining_RottenFood') "$groupName must become RottenFood."
}

$unknownGroupRejected = $false
try
{
    $unknownGroup = [Enum]::ToObject($spoilageGroupType, 999)
    $null = $getReplacementPrefabMethod.Invoke($null, [object[]] @($unknownGroup))
}
catch
{
    $exception = $_.Exception
    while ($null -ne $exception.InnerException)
    {
        $exception = $exception.InnerException
    }
    $unknownGroupRejected = $exception -is [ArgumentOutOfRangeException]
}
Assert-True $unknownGroupRejected 'An unmapped spoilage group must fail fast instead of silently using a replacement.'

$generatedPrefabType = $assembly.GetType('FineDining.GeneratedPrefabRegistry', $true)
Assert-True ([string] $generatedPrefabType.GetField('IceboxPrefabName', $allStaticFlags).GetRawConstantValue() -eq 'FineDining_Icebox') 'The Icebox prefab id must remain stable.'
Assert-True ([string] $generatedPrefabType.GetField('RottenProducePrefabName', $allStaticFlags).GetRawConstantValue() -eq 'FineDining_RottenProduce') 'The Rotten Produce prefab id must remain stable.'
Assert-True ([string] $generatedPrefabType.GetField('RottenFoodPrefabName', $allStaticFlags).GetRawConstantValue() -eq 'FineDining_RottenFood') 'The Rotten Food prefab id must remain stable.'
Assert-True ([string] $generatedPrefabType.GetField('RottenProducePukeStatusEffectName', $allStaticFlags).GetRawConstantValue() -eq 'FineDining_PukeRottenProduce') 'The Rotten Produce Puke status-effect id must remain stable.'
Assert-True ([string] $generatedPrefabType.GetField('RottenFoodPukeStatusEffectName', $allStaticFlags).GetRawConstantValue() -eq 'FineDining_PukeRottenFood') 'The Rotten Food Puke status-effect id must remain stable.'
Assert-True ([string] $generatedPrefabType.GetField('PukeSourceItemPrefabName', $staticFlags).GetRawConstantValue() -eq 'RottenMeat') 'Generated Puke effects must retain RottenMeat as their semantic source item.'
Assert-True ([string] $generatedPrefabType.GetField('PukeSourceStatusEffectName', $staticFlags).GetRawConstantValue() -eq 'Puke') 'Generated Puke effects must clone the vanilla Puke status effect.'
Assert-True ([string] $generatedPrefabType.GetField('GeneratedPukeStatusEffectCategory', $staticFlags).GetRawConstantValue() -eq 'FineDining_Puke') 'Generated Puke variants must share a private non-stacking category.'
Assert-True ([single] $generatedPrefabType.GetField('RottenProducePukeDurationSeconds', $staticFlags).GetRawConstantValue() -eq [single] 5) 'Rotten Produce must apply Puke for five seconds.'
Assert-True ([single] $generatedPrefabType.GetField('RottenFoodPukeDurationSeconds', $staticFlags).GetRawConstantValue() -eq [single] 10) 'Rotten Food must apply Puke for ten seconds.'
Assert-True ([int] $iceboxSubsystemType.GetField('StorageColumns', $allStaticFlags).GetRawConstantValue() -eq 8) 'The generated Icebox must have eight columns.'
Assert-True ([int] $iceboxSubsystemType.GetField('DefaultStorageRows', $allStaticFlags).GetRawConstantValue() -eq 4) 'The generated Icebox must default to four rows.'
Assert-True ([int] $iceboxSubsystemType.GetField('MinimumStorageRows', $allStaticFlags).GetRawConstantValue() -eq 4) 'The generated Icebox row minimum must remain four.'
Assert-True ([int] $iceboxSubsystemType.GetField('MaximumStorageRows', $allStaticFlags).GetRawConstantValue() -eq 20) 'The generated Icebox row maximum must remain twenty.'
Assert-True ([string] $iceboxSubsystemType.GetField('DefaultRecipe', $allStaticFlags).GetRawConstantValue() -eq 'FineWood:10,Iron:2') 'The generated Icebox recipe default changed.'
Assert-True ([single] $generatedPrefabType.GetField('IceboxHealth', $staticFlags).GetRawConstantValue() -eq [single] 1000) 'The generated Icebox must have 1000 health.'
Assert-True ([string] $generatedPrefabType.GetField('IceboxSourcePrefabName', $staticFlags).GetRawConstantValue() -eq 'piece_chest') 'The Icebox must retain its restart-safe vanilla clone source.'
Assert-True ([string] $generatedPrefabType.GetField('RottenProduceSourcePrefabName', $staticFlags).GetRawConstantValue() -eq 'Resin') 'Rotten Produce must retain its independent Resin clone source.'
Assert-True ([string] $generatedPrefabType.GetField('RottenFoodSourcePrefabName', $staticFlags).GetRawConstantValue() -eq 'BreadDough') 'Rotten Food must retain its independent BreadDough clone source.'
Assert-True ([string] $generatedPrefabType.GetField('IceboxMaterialName', $staticFlags).GetRawConstantValue() -eq 'antifreezegland') 'The Icebox material override changed.'
Assert-True ([string] $generatedPrefabType.GetField('RottenMaterialName', $staticFlags).GetRawConstantValue() -eq 'LoxMeatRotten') 'The generated rotten material override changed.'
Assert-True ([bool] $generatedPrefabType.GetMethod('IsIceboxPrefabName', $allStaticFlags).Invoke($null, [object[]] @('FineDining_Icebox(Clone)'))) 'Runtime clone suffixes must resolve to the stable Icebox identity.'
Assert-True (-not [bool] $generatedPrefabType.GetMethod('IsIceboxPrefabName', $allStaticFlags).Invoke($null, [object[]] @('piece_chest'))) 'The vanilla chest must not be treated as an Icebox.'

$isGeneratedReplacementMethod = $generatedPrefabType.GetMethod('IsGeneratedReplacementPrefabName', $allStaticFlags)
$ensureGeneratedReplacementMethod = $generatedPrefabType.GetMethod('EnsureGeneratedReplacementAvailable', $allStaticFlags)
$queueGeneratedRegistrationMethod = $generatedPrefabType.GetMethod('QueueRegistrationRetry', $allStaticFlags)
$registerConfiguredContentMethod = $generatedPrefabType.GetMethod('RegisterConfiguredContent', $allStaticFlags)
$tryCreateIceboxRequirementsMethod = $generatedPrefabType.GetMethod('TryCreateIceboxRequirements', $staticFlags)
$tryParseIceboxRecipeMethod = $generatedPrefabType.GetMethod('TryParseIceboxRecipe', $staticFlags)
$serializeIceboxRequirementsMethod = $generatedPrefabType.GetMethod('SerializeIceboxRequirements', $staticFlags)
$registerIceboxBuildContentMethod = $generatedPrefabType.GetMethod('RegisterIceboxBuildContent', $staticFlags)
$applyStoredIceboxRecipesMethod = $iceboxSubsystemType.GetMethod('ApplyStoredRecipesToLoadedIceboxes', $allStaticFlags)
$registerPersistentPrefabsMethod = $generatedPrefabType.GetMethod('RegisterPersistentPrefabsCore', $staticFlags)
$tryRegisterGeneratedReplacementMethod = $generatedPrefabType.GetMethod('TryRegisterGeneratedReplacement', $staticFlags)
$tryRegisterGeneratedItemMethod = $generatedPrefabType.GetMethod('TryRegisterGeneratedItem', $staticFlags)
$ensureGeneratedItemMethod = $generatedPrefabType.GetMethod('EnsureGeneratedItem', $staticFlags)
$findItemCloneSourceMethod = $generatedPrefabType.GetMethod('FindItemCloneSource', $staticFlags)
$hasSpawnableItemShapeMethod = $generatedPrefabType.GetMethod('HasSpawnableItemShape', $staticFlags)
$registerGeneratedItemMethod = $generatedPrefabType.GetMethod('RegisterGeneratedItem', $staticFlags)
$addItemToObjectDbMethod = $generatedPrefabType.GetMethod('AddItemToObjectDb', $staticFlags)
$addPrefabToZNetSceneMethod = $generatedPrefabType.GetMethod('AddPrefabToZNetScene', $staticFlags)
$verifyGeneratedItemRegistrationMethod = $generatedPrefabType.GetMethod('VerifyGeneratedItemRegistration', $staticFlags)
$verifyGeneratedObjectDbMethod = $generatedPrefabType.GetMethod('IsGeneratedItemRegisteredWithObjectDb', $staticFlags)
$verifyGeneratedZNetSceneMethod = $generatedPrefabType.GetMethod('IsGeneratedItemRegisteredWithZNetScene', $staticFlags)
$verifyGeneratedPukeMethod = $generatedPrefabType.GetMethod('IsGeneratedPukeStatusEffectRegistered', $staticFlags)
$sanitizeRottenConsumableMethod = $generatedPrefabType.GetMethod('SanitizeAsRottenConsumable', $staticFlags)
$configureGeneratedPukeMethod = $generatedPrefabType.GetMethod('ConfigureGeneratedPukeStatusEffect', $staticFlags)
$registerGeneratedPukeMethod = $generatedPrefabType.GetMethod('RegisterGeneratedPukeStatusEffect', $staticFlags)
$ensureGeneratedPukeMethod = $generatedPrefabType.GetMethods($staticFlags) |
    Where-Object { $_.Name -eq 'EnsureGeneratedPukeStatusEffect' -and $_.GetParameters().Count -eq 2 } |
    Select-Object -First 1
$ensureGeneratedPukeCoreMethod = $generatedPrefabType.GetMethods($staticFlags) |
    Where-Object { $_.Name -eq 'EnsureGeneratedPukeStatusEffect' -and $_.GetParameters().Count -eq 4 } |
    Select-Object -First 1
$isGeneratedItemRegistrationCompleteMethod = $generatedPrefabType.GetMethod('IsGeneratedItemRegistrationComplete', $staticFlags)
$tryGetGeneratedKindMethod = $generatedPrefabType.GetMethod('TryGetGeneratedReplacementKind', $staticFlags)
$retryRegistrationNextFrameMethod = $generatedPrefabType.GetMethod('RetryRegistrationNextFrame', $staticFlags)
Assert-True ($null -ne $isGeneratedReplacementMethod -and $null -ne $ensureGeneratedReplacementMethod) 'Generated replacement recovery APIs are missing.'
Assert-True ($null -ne $queueGeneratedRegistrationMethod -and $null -ne $registerConfiguredContentMethod) 'Deferred generated-prefab registration is missing.'
Assert-True ($null -ne $tryCreateIceboxRequirementsMethod -and $null -ne $tryParseIceboxRecipeMethod -and $null -ne $serializeIceboxRequirementsMethod -and $null -ne $registerIceboxBuildContentMethod -and $null -ne $applyStoredIceboxRecipesMethod) 'The configurable Icebox recipe pipeline is incomplete.'
Assert-True ((Get-DirectCallCount $registerIceboxBuildContentMethod $tryCreateIceboxRequirementsMethod) -eq 1) 'Icebox recipe requirements must be resolved atomically before replacing the active recipe.'
Assert-True ((Get-DirectCallCount $registerConfiguredContentMethod $applyStoredIceboxRecipesMethod) -eq 1) 'Generated-content refreshes must restore the construction recipe recorded by loaded Iceboxes.'

$validIceboxRecipeArguments = [object[]] @(' FineWood:10, Iron:2 ', $null, $null)
Assert-True ([bool] $tryParseIceboxRecipeMethod.Invoke($null, $validIceboxRecipeArguments)) 'A valid Icebox recipe must parse.'
$validIceboxIngredients = $validIceboxRecipeArguments[1]
Assert-True ($validIceboxIngredients.Count -eq 2) 'The valid Icebox recipe must retain both ingredients.'
Assert-True ([string] $validIceboxIngredients[0].Key -eq 'FineWood' -and [int] $validIceboxIngredients[0].Value -eq 10) 'The first Icebox recipe ingredient changed during parsing.'
Assert-True ([string] $validIceboxIngredients[1].Key -eq 'Iron' -and [int] $validIceboxIngredients[1].Value -eq 2) 'The second Icebox recipe ingredient changed during parsing.'
foreach ($invalidIceboxRecipe in @(
    '',
    'FineWood',
    'FineWood:',
    ':10',
    'FineWood:0',
    'FineWood:-1',
    'FineWood:not-a-number',
    'FineWood:10:2',
    'FineWood:10,FineWood:2',
    'FineWood:10,'))
{
    $invalidIceboxRecipeArguments = [object[]] @($invalidIceboxRecipe, $null, $null)
    Assert-True (-not [bool] $tryParseIceboxRecipeMethod.Invoke($null, $invalidIceboxRecipeArguments)) "Invalid Icebox recipe '$invalidIceboxRecipe' must be rejected atomically."
}
Assert-True ([bool] $isGeneratedReplacementMethod.Invoke($null, [object[]] @('FineDining_RottenProduce'))) 'Rotten Produce must be recognized as a recoverable generated replacement.'
Assert-True ([bool] $isGeneratedReplacementMethod.Invoke($null, [object[]] @('FineDining_RottenFood'))) 'Rotten Food must be recognized as a recoverable generated replacement.'
Assert-True (-not [bool] $isGeneratedReplacementMethod.Invoke($null, [object[]] @('RottenMeat'))) 'Vanilla replacements must not enter the generated-prefab recovery path.'
Assert-True (-not [bool] $isGeneratedReplacementMethod.Invoke($null, [object[]] @('CustomRottenFood'))) 'Custom replacements must retain their existing missing-prefab policy.'
$produceKindArguments = [object[]] @('FineDining_RottenProduce', $null)
$foodKindArguments = [object[]] @('FineDining_RottenFood', $null)
Assert-True ([bool] $tryGetGeneratedKindMethod.Invoke($null, $produceKindArguments)) 'Rotten Produce must map to a generated-prefab kind.'
Assert-True ([bool] $tryGetGeneratedKindMethod.Invoke($null, $foodKindArguments)) 'Rotten Food must map to a generated-prefab kind.'
Assert-True ([string] $produceKindArguments[1] -eq 'RottenProduce') 'Rotten Produce must use its own registration branch.'
Assert-True ([string] $foodKindArguments[1] -eq 'RottenFood') 'Rotten Food must use its own registration branch.'
Assert-True ((Get-DirectCallCount $registerPersistentPrefabsMethod $tryRegisterGeneratedReplacementMethod) -ge 2) 'Rotten Produce and Rotten Food must be attempted independently during persistent registration.'
Assert-True ($tryRegisterGeneratedReplacementMethod.GetMethodBody().ExceptionHandlingClauses.Count -ge 1) 'Puke preparation and item registration failures must remain isolated per generated replacement.'
Assert-True ((Get-DirectCallCount $tryRegisterGeneratedReplacementMethod $ensureGeneratedPukeMethod) -ge 1) 'A generated Puke effect must be ready before its rotten item is registered.'
Assert-True ((Get-DirectCallCount $ensureGeneratedPukeCoreMethod $configureGeneratedPukeMethod) -ge 1) 'Generated Puke clones must receive their stable identity and duration.'
Assert-True ((Get-DirectCallCount $ensureGeneratedPukeCoreMethod $registerGeneratedPukeMethod) -ge 1) 'Generated Puke clones must be registered with ObjectDB.'
Assert-True ($tryRegisterGeneratedItemMethod.GetMethodBody().ExceptionHandlingClauses.Count -ge 1) 'A generated-item failure must be isolated so the other generated prefabs can still register.'
Assert-True ($null -ne $ensureGeneratedItemMethod -and $null -ne $findItemCloneSourceMethod -and $null -ne $hasSpawnableItemShapeMethod) 'Generated item source lookup and validation seams are missing.'
Assert-True ((Get-DirectCallCount $findItemCloneSourceMethod $hasSpawnableItemShapeMethod) -eq 0) 'Generated item lookup must not hide a present source by prefiltering its component shape.'
Assert-True ((Get-DirectCallCount $ensureGeneratedItemMethod $findItemCloneSourceMethod) -eq 1) 'A generated item must resolve its clone source exactly once.'
Assert-True ((Get-DirectCallCount $ensureGeneratedItemMethod $hasSpawnableItemShapeMethod) -eq 1) 'A generated item must validate the selected clone source exactly once.'
Assert-True ((Get-FirstDirectCallOffset $ensureGeneratedItemMethod $findItemCloneSourceMethod) -lt (Get-FirstDirectCallOffset $ensureGeneratedItemMethod $hasSpawnableItemShapeMethod)) 'Generated item source lookup must precede component validation.'
$floatingShapeCallCount = Get-DirectGenericCallArgumentCount $hasSpawnableItemShapeMethod 'Floating'
$itemDropShapeCallCount = Get-DirectGenericCallArgumentCount $hasSpawnableItemShapeMethod 'ItemDrop'
Assert-True ($floatingShapeCallCount -eq 0) "Vanilla item clone sources must not require the optional Floating component; found $floatingShapeCallCount checks."
Assert-True ($itemDropShapeCallCount -eq 1) "Generated item clone sources must still require ItemDrop exactly once; found $itemDropShapeCallCount checks."
Assert-True ((Get-DirectCallCount $registerGeneratedItemMethod $addItemToObjectDbMethod) -ge 1) 'Generated items must be written to ObjectDB indexes.'
Assert-True ((Get-DirectCallCount $registerGeneratedItemMethod $addPrefabToZNetSceneMethod) -ge 1) 'Generated items must be written to ZNetScene indexes.'
Assert-True ((Get-DirectCallCount $tryRegisterGeneratedItemMethod $verifyGeneratedItemRegistrationMethod) -ge 1) 'Every generated replacement registration must be verified.'
Assert-True ((Get-DirectCallCount $verifyGeneratedItemRegistrationMethod $verifyGeneratedObjectDbMethod) -ge 1) 'Generated item verification must cover ObjectDB.'
Assert-True ((Get-DirectCallCount $verifyGeneratedItemRegistrationMethod $verifyGeneratedZNetSceneMethod) -ge 1) 'Generated item verification must cover ZNetScene.'
Assert-True ((Get-DirectCallCount $verifyGeneratedItemRegistrationMethod $verifyGeneratedPukeMethod) -ge 1) 'Generated item verification must cover its Puke status effect.'
Assert-True ((Get-DirectCallCount $isGeneratedItemRegistrationCompleteMethod $verifyGeneratedPukeMethod) -ge 1) 'On-demand replacement recovery must reject an item whose Puke effect is missing from ObjectDB.'

$rottenShared = [Activator]::CreateInstance($sharedType)
$sePukeType = $gameAssembly.GetType('SE_Puke', $true)
$placeholderPuke = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($sePukeType)
$sanitizeRottenConsumableMethod.Invoke(
    $null,
    [object[]] @(
        $rottenShared,
        '$finedining_rotten_produce',
        '$finedining_rotten_produce_description',
        $placeholderPuke))
Assert-True ([string] $sharedType.GetField('m_itemType').GetValue($rottenShared) -eq 'Consumable') 'Generated rotten items must be consumable.'
Assert-True ([object]::ReferenceEquals($sharedType.GetField('m_consumeStatusEffect').GetValue($rottenShared), $placeholderPuke)) 'A generated rotten item must retain its dedicated Puke effect.'
Assert-True ($null -eq $sharedType.GetField('m_appendToolTip').GetValue($rottenShared)) 'Generated rotten items must not inherit an unrelated appended tooltip.'
foreach ($zeroFoodField in @('m_food', 'm_foodStamina', 'm_foodEitr', 'm_foodBurnTime', 'm_foodRegen'))
{
    Assert-True ([single] $sharedType.GetField($zeroFoodField).GetValue($rottenShared) -eq [single] 0) "Generated rotten item field $zeroFoodField must remain zero."
}
Assert-True (-not [bool] $sharedType.GetField('m_isDrink').GetValue($rottenShared)) 'Generated rotten items must not become drinks.'
$rottenItem = [Activator]::CreateInstance($itemType)
$itemType.GetField('m_shared').SetValue($rottenItem, $rottenShared)
Assert-True (-not [bool] $isEdibleMethod.Invoke($null, [object[]] @($rottenItem))) 'A zero-stat rotten consumable must not re-enter direct-food classification.'
Assert-True (-not [bool] $hasFoodEffectMethod.Invoke($null, [object[]] @($rottenItem))) 'A zero-stat rotten consumable must not receive a freshness multiplier.'
Assert-True ([single] $foodMultiplierApiMethod.Invoke($null, [object[]] @($rottenItem)) -eq [single] 1) 'A zero-stat rotten consumable must expose a neutral multiplier to GourmetsDiet.'
Assert-True ((Get-DirectCallCount $queueGeneratedRegistrationMethod $retryRegistrationNextFrameMethod) -ge 1) 'The coalesced lifecycle queue must start the deferred registration coroutine.'
$retryIteratorAttribute = $retryRegistrationNextFrameMethod.GetCustomAttributes($false) |
    Where-Object { $_.GetType().FullName -eq 'System.Runtime.CompilerServices.IteratorStateMachineAttribute' } |
    Select-Object -First 1
Assert-True ($null -ne $retryIteratorAttribute) 'The generated-prefab retry must defer through an iterator instead of rerunning in the same callback.'
$retryMoveNextMethod = $retryIteratorAttribute.StateMachineType.GetMethod('MoveNext', $instanceFlags)
Assert-True ((Get-DirectCallCount $retryMoveNextMethod $registerConfiguredContentMethod) -ge 1) 'The next-frame retry must rerun configured-content registration.'

$resolveReplacementMethod = $runtimeType.GetMethod('ResolveReplacement', $staticFlags)
$resolveItemPrefabMethod = $runtimeType.GetMethod('ResolveItemPrefab', $staticFlags)
Assert-True ((Get-DirectCallCount $resolveReplacementMethod $isGeneratedReplacementMethod) -ge 1) 'Missing replacements must distinguish FineDining-owned generated prefabs.'
Assert-True ((Get-DirectCallCount $resolveReplacementMethod $ensureGeneratedReplacementMethod) -ge 1) 'A missing FineDining generated replacement must synchronously retry registration before expiry proceeds.'
Assert-True ((Get-FirstDirectCallOffset $resolveReplacementMethod $ensureGeneratedReplacementMethod) -lt (Get-FirstDirectCallOffset $resolveReplacementMethod $resolveItemPrefabMethod)) 'Generated replacements must verify both registries before accepting an ObjectDB lookup.'

foreach ($lifecyclePatchName in @(
    'FineDining.ObjectDbAwakeSpoilagePatch',
    'FineDining.ObjectDbCopySpoilagePatch',
    'FineDining.ObjectDbUpdateRegistersSpoilagePatch',
    'FineDining.ZNetSceneAwakeSpoilagePatch'))
{
    $lifecyclePostfix = $assembly.GetType($lifecyclePatchName, $true).GetMethod('Postfix', $staticFlags)
    Assert-True ((Get-DirectCallCount $lifecyclePostfix $registerConfiguredContentMethod) -ge 1) "$lifecyclePatchName must retain synchronous generated-prefab registration."
    Assert-True ((Get-DirectCallCount $lifecyclePostfix $queueGeneratedRegistrationMethod) -ge 1) "$lifecyclePatchName must queue a coalesced next-frame registration retry."
}

$gameStartRegistrationPatch = $assembly.GetType('FineDining.GameStartGeneratedPrefabRegistrationPatch', $true)
$gameStartRegistrationPostfix = $gameStartRegistrationPatch.GetMethod('Postfix', $staticFlags)
Assert-True ((Get-DirectCallCount $gameStartRegistrationPostfix $registerConfiguredContentMethod) -ge 1) 'Game.Start must provide a final client/dedicated-server generated-prefab registration checkpoint.'

$lateObjectDbPatchType = $assembly.GetType('FineDining.ObjectDbUpdateRegistersSpoilagePatch', $true)
Assert-True ($null -ne $lateObjectDbPatchType.GetMethod('Postfix', $staticFlags)) 'Late ObjectDB register rebuilds must retain generated content and the Icebox Hammer recipe.'

$iceboxSubsystemType = $assembly.GetType('FineDining.IceboxSubsystem', $true)
$normalizeAccountIdMethod = $iceboxSubsystemType.GetMethod('NormalizeAccountId', $allStaticFlags)
$applyIceboxStorageSizeMethod = $iceboxSubsystemType.GetMethod('ApplyConfiguredStorageSize', $allStaticFlags)
$prepareIceboxStorageLoadMethod = $iceboxSubsystemType.GetMethod('PrepareStorageLoad', $allStaticFlags)
$resolveSafeIceboxRowsMethod = $iceboxSubsystemType.GetMethod('ResolveSafeStorageRows', $staticFlags)
$capturePlacedIceboxRecipeMethod = $iceboxSubsystemType.GetMethod('CapturePlacedRecipe', $allStaticFlags)
$applyStoredIceboxRecipeMethod = $iceboxSubsystemType.GetMethod('ApplyStoredRecipe', $allStaticFlags)
$handleIceboxInventoryChangedMethod = $iceboxSubsystemType.GetMethod('HandleInventoryChanged', $allStaticFlags)
$iceboxGameplayConfigChangedMethod = $iceboxSubsystemType.GetMethod('OnGameplayConfigChanged', $staticFlags)
Assert-True ([string] $iceboxSubsystemType.GetField('PlacedRecipeKey', $allStaticFlags).GetRawConstantValue() -eq 'sighsorry.FineDining.IceboxPlacedRecipe') 'The placed Icebox recipe persistence key changed.'
Assert-True ([string] $normalizeAccountIdMethod.Invoke($null, [object[]] @(' Steam_76561198000000000 ')) -eq '76561198000000000') 'Steam account ids must normalize consistently for quota and pins.'
Assert-True ([string] $normalizeAccountIdMethod.Invoke($null, [object[]] @('76561198000000000')) -eq '76561198000000000') 'Unprefixed Steam64 ids must remain unchanged.'
Assert-True ($null -ne $applyIceboxStorageSizeMethod -and $null -ne $prepareIceboxStorageLoadMethod -and $null -ne $resolveSafeIceboxRowsMethod -and $null -ne $capturePlacedIceboxRecipeMethod -and $null -ne $applyStoredIceboxRecipeMethod -and $null -ne $handleIceboxInventoryChangedMethod -and $null -ne $iceboxGameplayConfigChangedMethod) 'Icebox storage and placed-recipe changes cannot be applied safely to loaded containers.'
Assert-True ((Get-DirectCallCount $iceboxGameplayConfigChangedMethod $registerConfiguredContentMethod) -eq 1) 'Icebox gameplay config changes must rebuild the generated prefab and recipe.'
Assert-True ((Get-DirectCallCount $iceboxGameplayConfigChangedMethod $queueGeneratedRegistrationMethod) -eq 1) 'Icebox gameplay config changes must queue a late-content registration retry.'

$inventoryTypeForSizeTest = $gameAssembly.GetType('Inventory', $true)
$inventoryConstructor = $inventoryTypeForSizeTest.GetConstructors() |
    Where-Object { $_.GetParameters().Count -eq 4 } |
    Select-Object -First 1
Assert-True ($null -ne $inventoryConstructor) 'The Inventory constructor needed for Icebox row safety validation is missing.'
$sizedInventory = $inventoryConstructor.Invoke([object[]] @('$finedining_test', $null, 8, 20))
Assert-True ([int] $resolveSafeIceboxRowsMethod.Invoke($null, [object[]] @($sizedInventory)) -eq 4) 'An empty Icebox must use the configured four-row default.'
$highRowItem = [Activator]::CreateInstance($itemType)
$gridPositionField = $itemType.GetField('m_gridPos')
$gridPosition = [Activator]::CreateInstance($gridPositionField.FieldType)
$gridPositionField.FieldType.GetField('x').SetValue($gridPosition, 0)
$gridPositionField.FieldType.GetField('y').SetValue($gridPosition, 12)
$gridPositionField.SetValue($highRowItem, $gridPosition)
$sizedInventory.GetAllItems().Add($highRowItem)
Assert-True ([int] $resolveSafeIceboxRowsMethod.Invoke($null, [object[]] @($sizedInventory)) -eq 13) 'Reducing the row config must retain every occupied Icebox row.'

$containerAwakePatchType = $assembly.GetType('FineDining.ContainerAwakeSpoilagePatch', $true)
$containerAwakePrefix = $containerAwakePatchType.GetMethod('Prefix', $staticFlags)
$containerAwakePostfix = $containerAwakePatchType.GetMethod('Postfix', $staticFlags)
Assert-True ((Get-DirectCallCount $containerAwakePrefix $applyIceboxStorageSizeMethod) -eq 1) 'Icebox dimensions must be applied before Container creates its Inventory.'
Assert-True ((Get-DirectCallCount $containerAwakePostfix $applyIceboxStorageSizeMethod) -eq 1) 'Icebox dimensions must also reach the newly created Inventory.'
Assert-True ((Get-DirectCallCount $containerAwakePostfix $applyStoredIceboxRecipeMethod) -eq 1) 'Loaded Iceboxes must restore their recorded construction recipe.'
$containerLoadPatchType = $assembly.GetType('FineDining.ContainerLoadSpoilagePatch', $true)
$containerLoadPrefix = $containerLoadPatchType.GetMethod('Prefix', $staticFlags)
$containerLoadPostfix = $containerLoadPatchType.GetMethod('Postfix', $staticFlags)
Assert-True ((Get-DirectCallCount $containerLoadPrefix $prepareIceboxStorageLoadMethod) -eq 1) 'Icebox deserialization must temporarily admit every supported row before loading saved items.'
Assert-True ((Get-DirectCallCount $containerLoadPostfix $applyIceboxStorageSizeMethod) -eq 1) 'Icebox deserialization must finish at the configured or highest occupied safe row.'
$inventoryChangedPatchType = $assembly.GetType('FineDining.InventoryChangedSpoilagePatch', $true)
$inventoryChangedPostfix = $inventoryChangedPatchType.GetMethod('Postfix', $staticFlags)
Assert-True ((Get-DirectCallCount $inventoryChangedPostfix $handleIceboxInventoryChangedMethod) -eq 1) 'Clearing high Icebox rows must reapply the configured safe height.'

$iceboxQuotaServiceType = $assembly.GetType('FineDining.IceboxQuotaService', $true)
$notifyPlacedIceboxMethod = $iceboxQuotaServiceType.GetMethod('NotifyLocallyPlacedIcebox', $allStaticFlags)
$resolveIceboxRefundRequirementsMethod = $iceboxQuotaServiceType.GetMethod('ResolveCurrentRequirements', $staticFlags)
$pieceSetCreatorPatchType = $assembly.GetType('FineDining.PieceSetCreatorIceboxSubsystemPatch', $true)
$pieceSetCreatorPostfix = $pieceSetCreatorPatchType.GetMethod('Postfix', $staticFlags)
Assert-True ((Get-DirectCallCount $pieceSetCreatorPostfix $capturePlacedIceboxRecipeMethod) -eq 1) 'A newly placed Icebox must snapshot its paid recipe.'
Assert-True ((Get-FirstDirectCallOffset $pieceSetCreatorPostfix $capturePlacedIceboxRecipeMethod) -lt (Get-FirstDirectCallOffset $pieceSetCreatorPostfix $notifyPlacedIceboxMethod)) 'The paid Icebox recipe must be persisted before placement-limit validation can refund it.'
Assert-True ((Get-DirectCallCount $capturePlacedIceboxRecipeMethod $serializeIceboxRequirementsMethod) -eq 1) 'The placed Icebox recipe snapshot must use the same canonical recipe format as config parsing.'
Assert-True ((Get-DirectCallCount $applyStoredIceboxRecipeMethod $tryCreateIceboxRequirementsMethod) -eq 1) 'Loaded Iceboxes must rebuild recovery requirements from their stored recipe.'
Assert-True ((Get-DirectCallCount $resolveIceboxRefundRequirementsMethod $tryCreateIceboxRequirementsMethod) -eq 2) 'Placement-limit refunds must resolve both the stored recipe and the server recipe before refunding.'
Assert-True ((Get-DirectCallCount $resolveIceboxRefundRequirementsMethod $serializeIceboxRequirementsMethod) -eq 2) 'Placement-limit refunds must compare canonical stored and server recipes instead of trusting a client-owned ZDO.'

$iceboxLimitPolicyType = $assembly.GetType('FineDining.IceboxLimitPolicy', $true)
$parseIceboxLimitsMethod = $iceboxLimitPolicyType.GetMethod('TryParse', $staticFlags)
function Parse-IceboxLimits([string] $Yaml)
{
    $arguments = [object[]] @($Yaml, $null, '')
    $accepted = [bool] $parseIceboxLimitsMethod.Invoke($null, $arguments)
    return [pscustomobject] @{
        Accepted = $accepted
        Snapshot = $arguments[1]
        Error = [string] $arguments[2]
    }
}

$validIceboxLimits = Parse-IceboxLimits @'
defaultLimit: 2
overrides:
  "76561198000000000": 6
  "Steam_76561198000000001": -1
'@
Assert-True $validIceboxLimits.Accepted $validIceboxLimits.Error
$limitSnapshotType = $validIceboxLimits.Snapshot.GetType()
Assert-True ([int] $limitSnapshotType.GetProperty('DefaultLimit', $instanceFlags).GetValue($validIceboxLimits.Snapshot) -eq 2) 'The Icebox default quota did not parse.'
$getIceboxLimitMethod = $limitSnapshotType.GetMethod('GetLimit', $instanceFlags)
Assert-True ([int] $getIceboxLimitMethod.Invoke($validIceboxLimits.Snapshot, [object[]] @('Steam_76561198000000000')) -eq 6) 'A Steam64 Icebox quota override did not normalize.'
Assert-True ([int] $getIceboxLimitMethod.Invoke($validIceboxLimits.Snapshot, [object[]] @('76561198000000001')) -eq -1) 'The unlimited Icebox quota override did not parse.'
Assert-True (-not (Parse-IceboxLimits "defaultLimit: -2`noverrides: {}`n").Accepted) 'Icebox limits below -1 must be rejected atomically.'
Assert-True (-not (Parse-IceboxLimits "defaultLimit: 2`noverrides: { not-a-steamid: 1 }`n").Accepted) 'Non-Steam64 quota keys must be rejected atomically.'

$preservationConfigType = $assembly.GetType('FineDining.PreservationConfig', $true)
Assert-True ([string] $preservationConfigType.GetField('DefaultBiomeList', $staticFlags).GetRawConstantValue() -eq 'Mountain, DeepNorth') 'The synchronized no-spoil biome default changed.'

Write-Output "PASS version/policy/reference/persistence/UI/freshness/placement/world-stack/replacement-scale/recovery-merge/classifier/generated-prefabs/icebox-policy/biomes; assembly=$($assembly.GetName().Version); overrides=$($overrides.Count); referenceRows=$($referenceRows.Count); embeddedResource=true"
