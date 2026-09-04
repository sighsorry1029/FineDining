param(
    [string] $AssemblyPath = "$(Split-Path -Parent $PSScriptRoot)\bin\Debug\FineDining.dll",
    [string] $ManagedDirectory = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed',
    [string] $BepInExCoreDirectory = '',
    [string] $JotunnAssemblyPath = '',
    [string] $ThunderstoreZipPath = '',
    [string] $NexusZipPath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assemblyDirectory = Split-Path -Parent $assemblyPath
if ([string]::IsNullOrWhiteSpace($BepInExCoreDirectory))
{
    $valheimDataDirectory = Split-Path -Parent $ManagedDirectory
    $gameDirectory = Split-Path -Parent $valheimDataDirectory
    $BepInExCoreDirectory = Join-Path $gameDirectory 'BepInEx\core'
}

$jotunnCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($JotunnAssemblyPath))
{
    $jotunnCandidates += (Resolve-Path -LiteralPath $JotunnAssemblyPath).Path
}

$jotunnCandidates += Join-Path $env:USERPROFILE '.nuget\packages\jotunnlib\2.29.2\lib\net462\Jotunn.dll'

[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)

    $fileName = ([Reflection.AssemblyName] $eventArgs.Name).Name + '.dll'
    $candidates = @(
        (Join-Path $assemblyDirectory $fileName),
        (Join-Path $BepInExCoreDirectory $fileName),
        (Join-Path $ManagedDirectory $fileName),
        (Join-Path (Join-Path $ManagedDirectory 'publicized_assemblies') $fileName),
        (Join-Path (Join-Path $ManagedDirectory 'publicized_assemblies') ($fileName -replace '\.dll$', '_publicized.dll'))
    )
    if ($fileName -eq 'Jotunn.dll')
    {
        $candidates = @($jotunnCandidates) + $candidates
    }

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

function Assert-HarmonyPatchTarget(
    [Reflection.Assembly] $Assembly,
    [string] $PatchTypeName,
    [string] $TargetTypeName,
    [string] $TargetMethodName)
{
    $patchType = Get-TypeRequired $Assembly $PatchTypeName
    $attributes = @($patchType.GetCustomAttributesData() | Where-Object {
        $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch'
    })
    $values = @($attributes | ForEach-Object {
        $_.ConstructorArguments | ForEach-Object { $_.Value }
        $_.NamedArguments | ForEach-Object { $_.TypedValue.Value }
    })
    $typeNames = @($values | Where-Object { $_ -is [Type] } | ForEach-Object { $_.FullName })
    $methodNames = @($values | Where-Object { $_ -is [string] })
    Assert-True ($typeNames -contains $TargetTypeName) "$PatchTypeName does not target $TargetTypeName."
    Assert-True ($methodNames -contains $TargetMethodName) "$PatchTypeName does not target $TargetMethodName."
}

function Assert-HarmonyPatchMethodAttribute(
    [Reflection.Assembly] $Assembly,
    [string] $PatchTypeName,
    [string] $AttributeName)
{
    $patchType = Get-TypeRequired $Assembly $PatchTypeName
    $flags = [Reflection.BindingFlags] 'Static,Instance,Public,NonPublic'
    $hasAttribute = @($patchType.GetMethods($flags) | Where-Object {
        @($_.GetCustomAttributesData() | Where-Object {
            $_.AttributeType.FullName -eq "HarmonyLib.$AttributeName"
        }).Count -gt 0
    }).Count -gt 0
    Assert-True $hasAttribute "$PatchTypeName has no $AttributeName method."
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
            Assert-True (@($manifest.dependencies) -contains 'ValheimModding-Jotunn-2.29.2') "Package '$resolvedPath' is missing the required Jotunn dependency."
        }
    }
    finally
    {
        $archive.Dispose()
    }
}

$assembly = [Reflection.Assembly]::UnsafeLoadFrom($assemblyPath)
Assert-True ($assembly.GetName().Name -eq 'FineDining') 'Assembly name must be FineDining.'
Assert-True ($assembly.GetName().Version -eq [Version] '1.0.5.0') 'Assembly version must be 1.0.5.0.'

$pluginType = Get-TypeRequired $assembly 'FineDining.FineDiningPlugin'
Assert-True ((Get-Constant $pluginType 'ModName') -eq 'FineDining') 'Plugin name must be FineDining.'
Assert-True ((Get-Constant $pluginType 'ModVersion') -eq '1.0.5') 'Plugin version must be 1.0.5.'
Assert-True ((Get-Constant $pluginType 'Author') -eq 'sighsorry') 'Plugin author must be sighsorry.'
Assert-True ((Get-Constant $pluginType 'ModGUID') -eq 'sighsorry.FineDining') 'Plugin GUID must be sighsorry.FineDining.'
Assert-True ([bool](Get-Constant $pluginType 'DefaultConfigurationLock')) 'Server configuration lock must default to enabled.'

$configPresentationType = Get-TypeRequired $assembly 'FineDining.ConfigPresentation'
$configManagerAttributesType = Get-TypeRequired $assembly 'FineDining.ConfigurationManagerAttributes'
$configFlags = [Reflection.BindingFlags] 'Static,Instance,Public,NonPublic'
$expectedConfigSections = [ordered]@{
    General = @('1 - General', 500)
    ClientSection = @('2 - Client', 400)
    Diet = @('3 - Diet', 300)
    ChefChoice = @('4 - Chef Choice', 200)
    Spoilage = @('5 - Spoilage', 100)
}
$sectionNameProperty = $null
$sectionOrderProperty = $null
foreach ($sectionFieldName in $expectedConfigSections.Keys)
{
    $sectionField = $configPresentationType.GetField($sectionFieldName, $configFlags)
    Assert-True ($null -ne $sectionField) "Config section definition is missing: $sectionFieldName"
    $sectionValue = $sectionField.GetValue($null)
    if ($null -eq $sectionNameProperty)
    {
        $sectionNameProperty = $sectionValue.GetType().GetProperty('Name', $configFlags)
        $sectionOrderProperty = $sectionValue.GetType().GetProperty('CategoryOrder', $configFlags)
    }
    $sectionName = [string]$sectionNameProperty.GetValue($sectionValue)
    Assert-True ($sectionName -eq [string]$expectedConfigSections[$sectionFieldName][0]) "Config section name is incorrect: $sectionFieldName"
    foreach ($invalidCharacter in @('=', "`n", "`t", '\', '"', "'", '[', ']'))
    {
        Assert-True (-not $sectionName.Contains($invalidCharacter)) "Config section '$sectionFieldName' contains a character forbidden by BepInEx: $invalidCharacter"
    }
    Assert-True ([int]$sectionOrderProperty.GetValue($sectionValue) -eq [int]$expectedConfigSections[$sectionFieldName][1]) "Config section order is incorrect: $sectionFieldName"
}
$sectionDefinitionType = $configPresentationType.GetNestedType('SectionDefinition', $configFlags)
$actualConfigSectionFields = @($configPresentationType.GetFields($configFlags) | Where-Object {
    $_.IsStatic -and $_.FieldType -eq $sectionDefinitionType
})
Assert-True ($actualConfigSectionFields.Count -eq $expectedConfigSections.Count) 'FineDining must expose exactly the five public config sections.'

$expectedConfigManagerPropertyTypes = @{
    CategoryOrder = [Nullable[int]]
    Order = [Nullable[int]]
}
foreach ($propertyName in $expectedConfigManagerPropertyTypes.Keys)
{
    $metadataProperty = $configManagerAttributesType.GetProperty($propertyName, $configFlags)
    Assert-True ($null -ne $metadataProperty) "ConfigManager metadata property is missing: $propertyName"
    Assert-True ($metadataProperty.PropertyType -eq $expectedConfigManagerPropertyTypes[$propertyName]) "ConfigManager metadata property type is incorrect: $propertyName"
}
$configAssemblyReferences = @($assembly.GetReferencedAssemblies() | Where-Object {
    $_.Name -match 'Config(uration)?Manager'
})
Assert-True ($configAssemblyReferences.Count -eq 0) 'ConfigManager ordering metadata must not add a hard assembly dependency.'

$spoilageSection = $configPresentationType.GetField('Spoilage', $configFlags).GetValue($null)
$syncedDescriptionMethod = Get-MethodRequired $configPresentationType 'Synced'
$clientDescriptionMethod = Get-MethodRequired $configPresentationType 'Client'
$acceptableConfigRange = [BepInEx.Configuration.AcceptableValueRange[int]]::new(1, 5)
$syncedDescription = $syncedDescriptionMethod.Invoke(
    $null,
    [object[]] @('Synchronized test.', $spoilageSection, 321, $acceptableConfigRange))
Assert-True ([string]$syncedDescription.Description -eq 'Synchronized test. [Synced with Server]') 'Synchronized config descriptions must expose their scope uniformly.'
Assert-True ([object]::ReferenceEquals($syncedDescription.AcceptableValues, $acceptableConfigRange)) 'Config presentation must preserve acceptable-value constraints.'
$syncedTag = @($syncedDescription.Tags)[0]
Assert-True ($syncedTag.GetType() -eq $configManagerAttributesType) 'Config descriptions must carry the dependency-free ConfigManager metadata shape.'
Assert-True ([int]$syncedTag.CategoryOrder -eq 100 -and [int]$syncedTag.Order -eq 321) 'Config description metadata must preserve section and option order.'
$clientDescription = $clientDescriptionMethod.Invoke(
    $null,
    [object[]] @('Client test.', $spoilageSection, 123, $null))
Assert-True ([string]$clientDescription.Description -eq 'Client test. [Client Only]') 'Client config descriptions must expose their scope uniformly.'

$configBindingSource = (@(
    'Plugin.cs',
    'FreshnessRuntime.cs',
    'PreservationConfig.cs',
    'IceboxSubsystem.cs',
    'Diet\DietConfig.cs',
    'Stations\StationModule.cs') |
    ForEach-Object { Get-Content -LiteralPath (Join-Path $projectRoot $_) -Raw }) -join "`n"
$expectedConfigEntries = @(
    [pscustomobject]@{ File = 'Plugin.cs'; Section = 'General'; Key = 'Lock Server Configuration'; Order = 500; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'General'; Key = 'Cooking Experience per Food Eaten'; Order = 450; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'General'; Key = 'Production Bonus Chance at Max Cooking (%)'; Order = 400; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'General'; Key = 'Fermenter Output Bonus Chance at Max Cooking (%)'; Order = 350; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'General'; Key = 'Production Bonus Excluded Output Prefabs'; Order = 300; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'General'; Key = 'Fermenter Bonus Excluded Prefabs'; Order = 250; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'General'; Key = 'Fermenter Cover Maximum Multiplier'; Order = 200; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'General'; Key = 'Fermenter Depth Maximum Multiplier'; Order = 100; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'ClientSection'; Key = 'Station Icon Scale'; Order = 600; Scope = 'Client' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'ClientSection'; Key = 'Station Icon Rows - Cooking Station'; Order = 500; Scope = 'Client' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'ClientSection'; Key = 'Station Icon Rows - Smelter'; Order = 400; Scope = 'Client' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'ClientSection'; Key = 'Station Icon Rows - Windmill'; Order = 300; Scope = 'Client' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'ClientSection'; Key = 'Station Icon Rows - Fermenter'; Order = 200; Scope = 'Client' },
    [pscustomobject]@{ File = 'Stations\StationModule.cs'; Section = 'ClientSection'; Key = 'Station Icon Rows - Grimpy Box'; Order = 100; Scope = 'Client' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'Diet'; Key = 'Maximum Food Slots'; Order = 500; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'Diet'; Key = 'Food Stat Scale'; Order = 450; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'Diet'; Key = 'Full Course Multiplier'; Order = 350; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'Diet'; Key = 'Recent Food History Size'; Order = 300; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'Diet'; Key = 'Diminishing Returns Start Count'; Order = 200; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'Diet'; Key = 'Diminishing Returns Multiplier'; Order = 100; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'Diet'; Key = 'Puke Food Removal Order'; Order = 50; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'ChefChoice'; Key = 'List Size'; Order = 600; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'ChefChoice'; Key = 'Minimum Multiplier'; Order = 500; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'ChefChoice'; Key = 'Most Likely Multiplier at Max Cooking Level'; Order = 450; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'ChefChoice'; Key = 'Maximum Multiplier'; Order = 400; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'ChefChoice'; Key = 'High-Tier Selection Strength'; Order = 300; Scope = 'Synced' },
    [pscustomobject]@{ File = 'Diet\DietConfig.cs'; Section = 'ChefChoice'; Key = 'Recent Food-Type Preference (%)'; Order = 100; Scope = 'Synced' },
    [pscustomobject]@{ File = 'PreservationConfig.cs'; Section = 'Spoilage'; Key = 'No-Spoil Biomes'; Order = 600; Scope = 'Synced' },
    [pscustomobject]@{ File = 'FreshnessRuntime.cs'; Section = 'Spoilage'; Key = 'Stale Food Minimum Multiplier'; Order = 500; Scope = 'Synced' },
    [pscustomobject]@{ File = 'IceboxSubsystem.cs'; Section = 'Spoilage'; Key = 'Icebox Default Placement Limit'; Order = 450; Scope = 'Synced' },
    [pscustomobject]@{ File = 'IceboxSubsystem.cs'; Section = 'Spoilage'; Key = 'Icebox Storage Rows'; Order = 400; Scope = 'Synced' },
    [pscustomobject]@{ File = 'IceboxSubsystem.cs'; Section = 'Spoilage'; Key = 'Icebox Build Recipe'; Order = 300; Scope = 'Synced' },
    [pscustomobject]@{ File = 'IceboxSubsystem.cs'; Section = 'Spoilage'; Key = 'Icebox Map Pins'; Order = 200; Scope = 'Client' })
$configEntryGuard = 'ConfigPresentation\.(?:General|ClientSection|Diet|ChefChoice|Spoilage)(?:\.Name)?,\s*"[^"]+"'
foreach ($configEntry in $expectedConfigEntries)
{
    $configKey = [string]$configEntry.Key
    $quotedKeyPattern = '"' + [regex]::Escape($configKey) + '"'
    Assert-True ([regex]::Matches($configBindingSource, $quotedKeyPattern).Count -eq 1) "Expected exactly one config binding for key: $configKey"

    $entrySource = Get-Content -LiteralPath (Join-Path $projectRoot ([string]$configEntry.File)) -Raw
    $sectionPattern = 'ConfigPresentation\.' + [regex]::Escape([string]$configEntry.Section)
    $scopePattern = 'ConfigPresentation\.' + [regex]::Escape([string]$configEntry.Scope)
    $bindingPattern =
        '(?s)' + $sectionPattern + '(?:\.Name)?,\s*' + $quotedKeyPattern + ',' +
        '(?:(?!' + $configEntryGuard + ').)*?' + $scopePattern + '\(' +
        '(?:(?!' + $configEntryGuard + ').)*?' + $sectionPattern + ',\s*' +
        [regex]::Escape([string]$configEntry.Order) + '(?:\s*,|\s*\))'
    Assert-True ([regex]::IsMatch($entrySource, $bindingPattern)) (
        "Config binding contract is incorrect: [$($configEntry.Section)] $configKey " +
        "(order $($configEntry.Order), $($configEntry.Scope)).")
}

$obsoleteConfigNames = @(
    '00 - Server',
    '10 - Gameplay - Spoilage & Preservation',
    '20 - Gameplay - Diet',
    '30 - Gameplay - Chef Choice',
    '40 - Gameplay - Cooking & Fermentation',
    '80 - Client - Display',
    '01 - Food Effects',
    '02 - Preservation',
    '03 - Icebox',
    '10 - Diet - Food Slots',
    '11 - Diet - Diminishing Returns',
    '12 - Diet - Chef Choice',
    '13 - Diet - Cooking Production Bonus',
    '20 - Station Hints',
    '21 - Station Hint UI',
    '23 - Station Diagnostics',
    '24 - Fermentation Environment',
    'Lock Configuration',
    'Minimum Food Effect Multiplier',
    'Show Icebox Map Pins',
    'CookingStation Rows',
    'GrimpyBox Rows',
    'Icon Group Scale',
    'Enable Diagnostics',
    'Station Hint Diagnostics',
    'Cover Maximum Speed Multiplier',
    'Depth Maximum Speed Multiplier',
    'Full Straight Multiplier',
    'High-Tier Selection Bias',
    'High-Multiplier Roll Bias',
    'Cooking Level 100 Multiplier Mode',
    'Icebox Map Pin Scale')
foreach ($obsoleteConfigName in $obsoleteConfigNames)
{
    Assert-True (-not $configBindingSource.Contains('"' + $obsoleteConfigName + '"')) "Obsolete config binding remains: $obsoleteConfigName"
}

$pluginConfigSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Plugin.cs') -Raw
$freshnessConfigSource = Get-Content -LiteralPath (Join-Path $projectRoot 'FreshnessRuntime.cs') -Raw
$preservationConfigSource = Get-Content -LiteralPath (Join-Path $projectRoot 'PreservationConfig.cs') -Raw
$iceboxConfigSource = Get-Content -LiteralPath (Join-Path $projectRoot 'IceboxSubsystem.cs') -Raw
$stationConfigSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\StationModule.cs') -Raw
Assert-True ($pluginConfigSource.Contains('ConfigSync.AddLockingConfigEntry(BindConfigurationLock(Config))')) 'The server lock must remain registered as the ServerSync locking entry.'
Assert-True ($freshnessConfigSource.Contains('configSync.AddConfigEntry(_minimumFoodMultiplier)') -and $freshnessConfigSource.Contains('syncedEntry.SynchronizedConfig = true;')) 'Stale-food gameplay configuration must remain server-synchronized.'
Assert-True ($preservationConfigSource.Contains('configSync.AddConfigEntry(_noSpoilBiomes)') -and $preservationConfigSource.Contains('syncedEntry.SynchronizedConfig = true;')) 'No-spoil biome configuration must remain server-synchronized.'
Assert-True ($iceboxConfigSource.Contains('FineDiningPlugin.ConfigSync.AddConfigEntry(_defaultPlacementLimit)') -and $iceboxConfigSource.Contains('FineDiningPlugin.ConfigSync.AddConfigEntry(_storageRows)') -and $iceboxConfigSource.Contains('FineDiningPlugin.ConfigSync.AddConfigEntry(_recipe)')) 'Icebox gameplay configuration must remain server-synchronized.'
Assert-True (-not $iceboxConfigSource.Contains('AddConfigEntry(_showMapPins)')) 'Icebox map pins must remain client-only.'
Assert-True (-not $iceboxConfigSource.Contains('_mapPinScale') -and -not $iceboxConfigSource.Contains('MapPinScale')) 'The removed Icebox map-pin scale configuration and accessor must not return.'
Assert-True (-not $stationConfigSource.Contains('BindLocal')) 'Station client config must not regain a pass-through binding wrapper.'
Assert-True ($stationConfigSource.Contains('configSync.AddConfigEntry(entry)') -and $stationConfigSource.Contains('syncedEntry.SynchronizedConfig = true;')) 'Fermentation gameplay settings must remain server-synchronized.'
$fermenterExclusionConfigStart = $stationConfigSource.IndexOf('FermenterBonusExcludedPrefabs = BindSynced(', [StringComparison]::Ordinal)
$fermenterCoverConfigStart = $stationConfigSource.IndexOf('FermenterCoverMaxSpeedMultiplier = BindSynced(', [StringComparison]::Ordinal)
Assert-True ($fermenterExclusionConfigStart -ge 0 -and $fermenterCoverConfigStart -gt $fermenterExclusionConfigStart) 'The Fermenter exclusion config must precede the cover multiplier config.'
$fermenterExclusionConfigSource = $stationConfigSource.Substring(
    $fermenterExclusionConfigStart,
    $fermenterCoverConfigStart - $fermenterExclusionConfigStart)
Assert-True ([regex]::IsMatch(
    $fermenterExclusionConfigSource,
    '(?s)"Fermenter Bonus Excluded Prefabs",\s*(?:string\.Empty|""),\s*ConfigPresentation\.Synced\(')) 'The Fermenter exclusion list must default to empty and remain synchronized.'
Assert-True ([regex]::IsMatch(
    $stationConfigSource,
    "(?s)Split\(\s*new\[\]\s*\{\s*','\s*,\s*';'\s*,\s*'\\r'\s*,\s*'\\n'\s*\}\s*,\s*StringSplitOptions\.RemoveEmptyEntries")) 'Fermenter exclusion entries must accept comma, semicolon, CR, and LF separators.'
Assert-True ($stationConfigSource.Contains('StringComparer.OrdinalIgnoreCase')) 'Fermenter exclusion matching must be exact and case-insensitive.'
Assert-True ($stationConfigSource.Contains('FoodIdentity.NormalizePrefabName(token)')) 'Fermenter exclusion entries must normalize whitespace and Clone suffixes before matching.'
Assert-True ([regex]::IsMatch(
    $stationConfigSource,
    '(?s)IsFermenterBonusExcluded\(Fermenter\? fermenter\).*?\.Contains\(\s*(?:GetFermenterPrefabName\(fermenter\)|prefabName)\s*\)')) 'Fermenter exclusion matching must use exact HashSet membership on the resolved prefab identity.'
$fermenterPrefabResolverStart = $stationConfigSource.IndexOf('internal static string GetFermenterPrefabName(', [StringComparison]::Ordinal)
$stationShutdownStart = $stationConfigSource.IndexOf('internal static void Shutdown(', $fermenterPrefabResolverStart, [StringComparison]::Ordinal)
Assert-True ($fermenterPrefabResolverStart -ge 0 -and $stationShutdownStart -gt $fermenterPrefabResolverStart) 'Fermenter prefab identity source boundaries were not found.'
$fermenterPrefabResolverSource = $stationConfigSource.Substring(
    $fermenterPrefabResolverStart,
    $stationShutdownStart - $fermenterPrefabResolverStart)
$networkPrefabLookupIndex = $fermenterPrefabResolverSource.IndexOf('ZNetScene.instance.GetPrefab(zdo.GetPrefab())', [StringComparison]::Ordinal)
$viewPrefabFallbackIndex = $fermenterPrefabResolverSource.IndexOf('FoodIdentity.NormalizePrefabName(view.gameObject.name)', [StringComparison]::Ordinal)
$componentPrefabFallbackIndex = $fermenterPrefabResolverSource.IndexOf('FoodIdentity.NormalizePrefabName(fermenter.gameObject.name)', [StringComparison]::Ordinal)
Assert-True ($networkPrefabLookupIndex -ge 0 -and
             $viewPrefabFallbackIndex -gt $networkPrefabLookupIndex -and
             $componentPrefabFallbackIndex -gt $viewPrefabFallbackIndex) 'Fermenter identity must prefer the authoritative ZDO prefab, then fall back to the ZNetView root and component object.'
Assert-True ([regex]::Matches($fermenterPrefabResolverSource, 'FoodIdentity\.NormalizePrefabName\(').Count -eq 3) 'Every Fermenter identity source must share whitespace and Clone normalization.'

$basePluginType = [BepInEx.BaseUnityPlugin]
$pluginTypes = @($assembly.GetTypes() | Where-Object {
    -not $_.IsAbstract -and $basePluginType.IsAssignableFrom($_)
})
Assert-True ($pluginTypes.Count -eq 1) 'FineDining must contain exactly one BepInEx plugin class.'

$dependencies = @(
    $pluginType.GetCustomAttributesData() |
        Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInDependency' }
)
$jotunnDependency = @($dependencies | Where-Object {
    [string]$_.ConstructorArguments[0].Value -eq 'com.jotunn.jotunn'
})
Assert-True ($jotunnDependency.Count -eq 1) 'FineDining must declare one hard Jotunn runtime dependency.'
Assert-True ([string]$jotunnDependency[0].ConstructorArguments[1].Value -eq '2.29.2') 'The Jotunn runtime dependency must require version 2.29.2 or newer.'
$azuCraftyBoxesDependency = @($dependencies | Where-Object {
    [string]$_.ConstructorArguments[0].Value -eq 'Azumatt.AzuCraftyBoxes'
})
Assert-True ($azuCraftyBoxesDependency.Count -eq 1) 'FineDining must declare AzuCraftyBoxes as one optional dependency.'
Assert-True ([int]$azuCraftyBoxesDependency[0].ConstructorArguments[1].Value -eq 2) 'AzuCraftyBoxes must remain a soft dependency.'
$valheimCuisineDependency = @($dependencies | Where-Object {
    [string]$_.ConstructorArguments[0].Value -eq 'XutzBR.ValheimCuisine'
})
Assert-True ($valheimCuisineDependency.Count -eq 1) 'FineDining must declare ValheimCuisine as one optional dependency.'
Assert-True ([int]$valheimCuisineDependency[0].ConstructorArguments[1].Value -eq 2) 'ValheimCuisine must remain a soft dependency.'

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

$localizationType = Get-TypeRequired $assembly 'FineDining.FineDiningLocalization'
$localizationMethods = @($localizationType.GetMethods([Reflection.BindingFlags] 'Static,Public,NonPublic'))
$simpleFallback = @($localizationMethods | Where-Object {
    $_.Name -eq 'LocalizeOrFallback' -and $_.GetParameters().Count -eq 2
})[0]
$indexedFallback = @($localizationMethods | Where-Object {
    $_.Name -eq 'LocalizeOrFallback' -and $_.GetParameters().Count -eq 3
})[0]
$formattedFallback = Get-MethodRequired $localizationType 'FormatOrFallback'
Assert-True ($null -ne $simpleFallback -and
             $simpleFallback.ReturnType -eq [string]) 'Simple localization fallback contract is missing.'
Assert-True ($null -ne $indexedFallback -and
             $indexedFallback.GetParameters()[2].ParameterType -eq [string[]]) 'Valheim-style indexed localization fallback contract is missing.'
Assert-True ($formattedFallback.ReturnType -eq [string] -and
             $formattedFallback.GetParameters()[2].ParameterType -eq [object[]]) 'Invariant formatted localization fallback contract is missing.'

$stationModuleType = Get-TypeRequired $assembly 'FineDining.StationModule'
$stationHintUiType = Get-TypeRequired $assembly 'FineDining.StationHintUi'
$stationPropertyFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
Assert-True ($null -ne $stationModuleType.GetProperty('IconGroupScale', $stationPropertyFlags)) 'Station icon scale config must remain available.'
$fermenterExcludedPrefabsProperty = $stationModuleType.GetProperty(
    'FermenterBonusExcludedPrefabs',
    $stationPropertyFlags)
Assert-True ($null -ne $fermenterExcludedPrefabsProperty) 'The synchronized Fermenter exclusion config is missing.'
$fermenterExcludedPrefabsPropertyType = $fermenterExcludedPrefabsProperty.PropertyType
Assert-True ($fermenterExcludedPrefabsPropertyType.IsGenericType -and
             $fermenterExcludedPrefabsPropertyType.GetGenericTypeDefinition().FullName -eq 'BepInEx.Configuration.ConfigEntry`1' -and
             $fermenterExcludedPrefabsPropertyType.GetGenericArguments()[0] -eq [string]) 'The Fermenter exclusion config must be a string ConfigEntry.'
$isFermenterBonusExcluded = Get-MethodRequired $stationModuleType 'IsFermenterBonusExcluded'
$getFermenterPrefabName = Get-MethodRequired $stationModuleType 'GetFermenterPrefabName'
Assert-True ($isFermenterBonusExcluded.IsStatic -and
             $isFermenterBonusExcluded.ReturnType -eq [bool] -and
             $isFermenterBonusExcluded.GetParameters().Count -eq 1 -and
             $isFermenterBonusExcluded.GetParameters()[0].ParameterType.FullName -eq 'Fermenter') 'The Fermenter exclusion matcher must accept exactly one Fermenter and return bool.'
Assert-True ($getFermenterPrefabName.IsStatic -and
             $getFermenterPrefabName.ReturnType -eq [string] -and
             $getFermenterPrefabName.GetParameters().Count -eq 1 -and
             $getFermenterPrefabName.GetParameters()[0].ParameterType.FullName -eq 'Fermenter') 'The Fermenter prefab resolver must accept exactly one Fermenter and return its stable prefab identity.'
Assert-True ([Math]::Abs([float](Get-Constant $stationHintUiType 'CookingProgressTextScale') - 1.5) -lt 0.0001) 'CookingStation progress status text must use the fixed x1.50 emphasis.'
foreach ($rowProperty in @('CookingStationRows', 'SmelterRows', 'WindmillRows', 'FermenterRows', 'GrimpyBoxRows'))
{
    Assert-True ($null -ne $stationModuleType.GetProperty($rowProperty, $stationPropertyFlags)) "Station row config is missing: $rowProperty"
}
foreach ($removedStationProperty in @('EnableDisplay', 'CookingStationMax', 'SmelterMax', 'WindmillMax', 'FermenterMax', 'Diagnostics'))
{
    Assert-True ($null -eq $stationModuleType.GetProperty($removedStationProperty, $stationPropertyFlags)) "Removed station config remains exposed: $removedStationProperty"
}
Assert-True ([int](Get-Constant $stationModuleType 'HintColumns') -eq 5) 'Station hint grids must retain five columns.'
Assert-True ([int](Get-Constant $stationModuleType 'DefaultHintRows') -eq 2) 'Station hint rows must default to two.'
Assert-True ([int](Get-Constant $stationModuleType 'MaxHintRows') -eq 4) 'Station hint rows must be capped at four.'
Assert-True ([int](Get-Constant $stationModuleType 'MaxHints') -eq 20) 'Four station hint rows must provide twenty cards.'
$getHintLimit = Get-MethodRequired $stationModuleType 'GetHintLimit'
Assert-True ([int]$getHintLimit.Invoke($null, [object[]] @(-1)) -eq 0) 'Negative station rows must clamp to zero cards.'
Assert-True ([int]$getHintLimit.Invoke($null, [object[]] @(0)) -eq 0) 'Zero station rows must hide all cards.'
Assert-True ([int]$getHintLimit.Invoke($null, [object[]] @(2)) -eq 10) 'Two station rows must provide ten cards.'
Assert-True ([int]$getHintLimit.Invoke($null, [object[]] @(4)) -eq 20) 'Four station rows must provide twenty cards.'
Assert-True ([int]$getHintLimit.Invoke($null, [object[]] @(10)) -eq 20) 'Out-of-range station rows must clamp to four.'
foreach ($removedLayoutProperty in @('IconGroupOffsetX', 'IconGroupOffsetY', 'CookingGroupGap'))
{
    Assert-True ($null -eq $stationModuleType.GetProperty($removedLayoutProperty, $stationPropertyFlags)) "Removed station layout config remains exposed: $removedLayoutProperty"
}
Assert-True ($null -eq $stationModuleType.GetProperty('NearbyContainerRange', $stationPropertyFlags)) 'FineDining must not expose a duplicate nearby-container range config.'

$azuCraftyBoxesCompatibilityType = Get-TypeRequired $assembly 'FineDining.AzuCraftyBoxesCompatibility'
Assert-True ((Get-Constant $azuCraftyBoxesCompatibilityType 'PluginGuid') -eq 'Azumatt.AzuCraftyBoxes') 'AzuCraftyBoxes compatibility GUID is incorrect.'
Assert-True ((Get-Constant $azuCraftyBoxesCompatibilityType 'ContainerRangeSection') -eq '2 - CraftyBoxes') 'AzuCraftyBoxes Container Range section contract is incorrect.'
Assert-True ((Get-Constant $azuCraftyBoxesCompatibilityType 'ContainerRangeKey') -eq 'Container Range') 'AzuCraftyBoxes Container Range key contract is incorrect.'
$createNearbyQuery = Get-MethodRequired $azuCraftyBoxesCompatibilityType 'CreateNearbyQuery'
Assert-True ($createNearbyQuery.GetParameters().Count -eq 1) 'FineDining must not pass its own range into AzuCraftyBoxes nearby queries.'
Assert-True ($createNearbyQuery.GetParameters()[0].ParameterType.FullName -eq 'UnityEngine.Component') 'AzuCraftyBoxes nearby queries must remain anchored to the hovered station component.'

$inventorySlotsCompatibilityType = Get-TypeRequired $assembly 'FineDining.InventorySlotsCompatibility'
Assert-True ((Get-Constant $inventorySlotsCompatibilityType 'PluginGuid') -eq 'sighsorry.InventorySlots') 'InventorySlots compatibility GUID is incorrect.'
$compatibilityFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
$minimumInventorySlotsVersionField = $inventorySlotsCompatibilityType.GetField(
    'MinimumSupportedVersion',
    $compatibilityFlags)
Assert-True ($null -ne $minimumInventorySlotsVersionField) 'InventorySlots minimum supported version contract is missing.'
Assert-True ([Version]$minimumInventorySlotsVersionField.GetValue($null) -eq [Version]'1.3.7') 'FineDining must require InventorySlots 1.3.7 or newer.'
$inventorySlotsRegister = Get-MethodRequired $inventorySlotsCompatibilityType 'Register'
$inventorySlotsRegisterParameters = @($inventorySlotsRegister.GetParameters())
Assert-True ($inventorySlotsRegisterParameters.Count -eq 4) 'InventorySlots registration must require a guarded metadata predicate.'
Assert-True ($inventorySlotsRegisterParameters[0].ParameterType.FullName -eq 'System.Reflection.MethodInfo') 'InventorySlots registration must receive the discovered guarded API method.'
Assert-True ($inventorySlotsRegisterParameters[1].ParameterType -eq [string]) 'InventorySlots registration must receive the metadata key.'
Assert-True ($inventorySlotsRegisterParameters[2].ParameterType.FullName.StartsWith('System.Func`3', [StringComparison]::Ordinal)) 'InventorySlots registration must receive a metadata merger.'
Assert-True ($inventorySlotsRegisterParameters[3].ParameterType.FullName.StartsWith('System.Func`3', [StringComparison]::Ordinal)) 'InventorySlots registration must require fail-closed metadata validation.'
$inventorySlotsCompatibilitySource = Get-Content -LiteralPath (Join-Path $projectRoot 'InventorySlotsCompatibility.cs') -Raw
Assert-True (-not $inventorySlotsCompatibilitySource.Contains('legacyRegister')) 'FineDining must not retain an unsupported InventorySlots legacy API path.'

$valheimCuisineCompatibilityType = Get-TypeRequired $assembly 'FineDining.ValheimCuisineCompatibility'
Assert-True ((Get-Constant $valheimCuisineCompatibilityType 'PluginGuid') -eq 'XutzBR.ValheimCuisine') 'ValheimCuisine compatibility GUID is incorrect.'
Assert-True ((Get-Constant $valheimCuisineCompatibilityType 'GrimpyConverterTypeName') -eq 'ValheimCuisine.ValheimCuisinePlugin+GrimpyBoxConverter') 'Grimpy Box converter type contract is incorrect.'
Assert-True ((Get-Constant $valheimCuisineCompatibilityType 'FreydisCollectorTypeName') -eq 'ValheimCuisine.ValheimCuisinePlugin+FreydisCollector') 'Freydis collector type contract is incorrect.'
Assert-True ([Math]::Abs([float](Get-Constant $valheimCuisineCompatibilityType 'CandidateCacheSeconds') - 0.2) -lt 0.0001) 'ValheimCuisine candidate cache must remain bounded to 0.2 seconds.'
foreach ($methodName in @(
    'Initialize',
    'Shutdown',
    'TryShow',
    'TryParseGrimpyRecipe',
    'CalculateFreydisRemainingSeconds'))
{
    Assert-Method $valheimCuisineCompatibilityType $methodName
}

Assert-True ($null -eq $assembly.GetType('FineDining.FineDiningApi', $false)) 'No public compatibility API should remain.'
Assert-True ($null -eq $assembly.GetType('GourmetsDiet.GourmetsDietApi', $false)) 'Old GourmetsDiet API must not be included.'
Assert-True ($null -eq $assembly.GetType('LocalizationManager.Localizer', $false)) 'The old standalone localization helper must not remain.'

$decayType = Get-TypeRequired $assembly 'FineDining.DecayRuntime'
$spoilageClockType = Get-TypeRequired $assembly 'FineDining.SpoilageClock'
$spoilageDefaultsType = Get-TypeRequired $assembly 'FineDining.SpoilageDefaults'
$iceboxSubsystemType = Get-TypeRequired $assembly 'FineDining.IceboxSubsystem'
$freshnessType = Get-TypeRequired $assembly 'FineDining.FreshnessRuntime'
$stateStoreType = Get-TypeRequired $assembly 'FineDining.FoodStateStore'
$generatedType = Get-TypeRequired $assembly 'FineDining.GeneratedPrefabRegistry'
$environmentType = Get-TypeRequired $assembly 'FineDining.FermenterEnvironmentSpeedSystem'
$foodEffectUiTextType = Get-TypeRequired $assembly 'FineDining.FoodEffectUiText'
$hudFoodPanelsType = Get-TypeRequired $assembly 'FineDining.HudFoodPanels'
$stationTextType = Get-TypeRequired $assembly 'FineDining.StationText'
$fermenterHoverPatchType = Get-TypeRequired $assembly 'FineDining.StationFermenterHoverTimePatch'
$autoPopCoreType = Get-TypeRequired $assembly 'FineDining.CookingStationAutoPopCore'
$autoPopSystemType = Get-TypeRequired $assembly 'FineDining.CookingStationAutoPopSystem'
$productionBonusSystemType = Get-TypeRequired $assembly 'FineDining.CookingProductionBonusSystem'

Assert-True ((Get-Constant $spoilageClockType 'ExpiryDataKey') -eq 'sighsorry.FineDining.ExpiryWorldTicks') 'Expiry key is not owned by the FineDining spoilage clock.'
Assert-True ((Get-Constant $spoilageClockType 'SpoiledDataKey') -eq 'sighsorry.FineDining.Spoiled') 'The permanent spoiled-state marker key must remain stable.'
Assert-True ((Get-Constant $freshnessType 'AssignedLifetimeDataKey') -eq 'sighsorry.FineDining.AssignedLifetimeTicks') 'Assigned-lifetime key is not the new FineDining key.'
Assert-True ((Get-Constant $stateStoreType 'CustomDataKey') -eq 'sighsorry.FineDining.DietState') 'Diet state key is not the new FineDining key.'
Assert-True ((Get-Constant $iceboxSubsystemType 'PrefabName') -eq 'FineDining_Icebox') 'Icebox prefab ID is incorrect.'
Assert-True ((Get-Constant $spoilageDefaultsType 'RottenProducePrefabName') -eq 'FineDining_RottenProduce') 'Rotten Produce prefab ID is incorrect.'
Assert-True ((Get-Constant $spoilageDefaultsType 'RottenFoodPrefabName') -eq 'FineDining_RottenFood') 'Rotten Food prefab ID is incorrect.'
$isGeneratedReplacement = Get-MethodRequired $generatedType 'IsGeneratedReplacementPrefabName'
Assert-True ([bool]$isGeneratedReplacement.Invoke($null, [object[]] @('finedining_rottenproduce'))) 'Generated replacement recognition must be case-insensitive.'
Assert-True (-not [bool]$isGeneratedReplacement.Invoke($null, [object[]] @('FineDining_NotRotten'))) 'Generated replacement recognition must reject unrelated prefabs.'
$foodIdentityType = Get-TypeRequired $assembly 'FineDining.FoodIdentity'
$normalizePrefabName = Get-MethodRequired $foodIdentityType 'NormalizePrefabName'
Assert-True ([string]$normalizePrefabName.Invoke($null, [object[]] @('  ModFood(Clone)  ')) -eq 'ModFood') 'Shared prefab normalization must trim whitespace and the Clone suffix without game runtime state.'
$generatedRegistrySource = Get-Content -LiteralPath (Join-Path $projectRoot 'GeneratedPrefabRegistry.cs') -Raw
$configureIceboxStart = $generatedRegistrySource.IndexOf('private static void ConfigureIceboxPrefab(', [StringComparison]::Ordinal)
$refreshIceboxStart = $generatedRegistrySource.IndexOf('private static void RefreshIceboxBuildContent(', $configureIceboxStart, [StringComparison]::Ordinal)
Assert-True ($configureIceboxStart -ge 0 -and $refreshIceboxStart -gt $configureIceboxStart) 'Icebox configuration source section was not found.'
$configureIceboxSource = $generatedRegistrySource.Substring($configureIceboxStart, $refreshIceboxStart - $configureIceboxStart)
Assert-True ($configureIceboxSource.Contains('piece.m_category = Piece.PieceCategory.Misc;', [StringComparison]::Ordinal)) 'Icebox must be registered in the Hammer Misc category.'
Assert-True ([Math]::Abs([float](Get-Constant $autoPopCoreType 'CookingExperienceOnAdd') - 0.4) -lt 0.0001) 'CookingStation add experience must remain 0.4.'
Assert-True ([Math]::Abs([float](Get-Constant $autoPopCoreType 'CookingExperienceOnCollect') - 0.6) -lt 0.0001) 'CookingStation collect experience must remain 0.6.'
Assert-True ([Math]::Abs(
    [float](Get-Constant $autoPopCoreType 'CookingExperienceOnAdd') +
    [float](Get-Constant $autoPopCoreType 'CookingExperienceOnCollect') - 1.0) -lt 0.0001) 'Accepted auto-pop experience must remain the sum of add and collect experience.'
$requestPlanRpc = [string](Get-Constant $autoPopSystemType 'RequestPlanRpc')
$autoPopBonusEffectRpc = [string](Get-Constant $autoPopSystemType 'AutoPopBonusEffectRpc')
$slotStateKeyPrefix = [string](Get-Constant $autoPopSystemType 'SlotStateKeyPrefix')
Assert-True ($requestPlanRpc.StartsWith('FineDining_CookingStation_', [StringComparison]::Ordinal)) 'CookingStation plan RPC is not FineDining-owned.'
Assert-True ($autoPopBonusEffectRpc -eq 'FineDining_CookingStation_AutoPopBonusEffect') 'CookingStation auto-eject bonus-effect RPC is incorrect.'
Assert-True ($autoPopBonusEffectRpc -ne $requestPlanRpc) 'CookingStation planning and auto-eject bonus feedback must use distinct RPCs.'
Assert-True ($slotStateKeyPrefix.StartsWith('sighsorry.FineDining.CookingStation.', [StringComparison]::Ordinal)) 'CookingStation slot state key is not FineDining-owned.'
Assert-True ([int](Get-Constant $autoPopSystemType 'SlotPlanVersion') -eq 1) 'CookingStation slot plan version must be 1.'
$registerAutoPopRpcs = Get-MethodRequired $autoPopSystemType 'RegisterRpcs'
$broadcastAutoPopBonusEffect = Get-MethodRequired $autoPopSystemType 'BroadcastBonusEffect'
$receiveAutoPopBonusEffect = Get-MethodRequired $autoPopSystemType 'ReceiveBonusEffect'
Assert-True ($registerAutoPopRpcs.IsStatic -and
             $registerAutoPopRpcs.GetParameters().Count -eq 1 -and
             $registerAutoPopRpcs.GetParameters()[0].ParameterType.FullName -eq 'CookingStation') 'CookingStation RPC registration must accept exactly one station.'
Assert-True ($broadcastAutoPopBonusEffect.IsStatic -and
             $broadcastAutoPopBonusEffect.IsPrivate -and
             $broadcastAutoPopBonusEffect.GetParameters().Count -eq 2 -and
             $broadcastAutoPopBonusEffect.GetParameters()[0].ParameterType.FullName -eq 'CookingStation' -and
             $broadcastAutoPopBonusEffect.GetParameters()[1].ParameterType -eq [int]) 'CookingStation auto-eject bonus broadcast must accept one station and one count.'
Assert-True ($receiveAutoPopBonusEffect.IsStatic -and
             $receiveAutoPopBonusEffect.IsPrivate -and
             $receiveAutoPopBonusEffect.GetParameters().Count -eq 3 -and
             $receiveAutoPopBonusEffect.GetParameters()[0].ParameterType.FullName -eq 'CookingStation' -and
             $receiveAutoPopBonusEffect.GetParameters()[1].ParameterType -eq [long] -and
             $receiveAutoPopBonusEffect.GetParameters()[2].ParameterType -eq [int]) 'CookingStation auto-eject bonus receiver must accept station, sender, and count.'
Assert-Method $generatedType 'Initialize'
Assert-Method $generatedType 'RefreshConfiguredContent'
Assert-Method $generatedType 'RefreshIceboxConfiguredContent'
Assert-True ($iceboxConfigSource.Contains('GeneratedPrefabRegistry.RefreshIceboxConfiguredContent();')) 'Icebox config changes must use the Icebox-only generated-content refresh.'
Assert-True (-not $iceboxConfigSource.Contains('GeneratedPrefabRegistry.RefreshConfiguredContent();')) 'Icebox config changes must not refresh unrelated rotten items.'
Assert-True ($null -eq $generatedType.GetMethod('RegisterConfiguredContent', [Reflection.BindingFlags] 'Static,NonPublic')) 'The removed manual prefab registration path must not return.'
Assert-True ($null -eq $generatedType.GetMethod('QueueRegistrationRetry', [Reflection.BindingFlags] 'Static,NonPublic')) 'The removed next-frame registration retry must not return.'
Assert-True ($null -eq $assembly.GetType('FineDining.FineDiningGeneratedPrefabMarker', $false)) 'Generated content must not retain the old ownership marker.'

$jotunnReference = @($assembly.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'Jotunn' })
Assert-True ($jotunnReference.Count -eq 1) 'FineDining must reference Jotunn as its generated-content runtime.'
Assert-True ($jotunnReference[0].Version -eq [Version] '2.29.2.0') 'FineDining must compile against Jotunn 2.29.2.'
Assert-True (@($assembly.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'AzuCraftyBoxes' }).Count -eq 0) 'Optional AzuCraftyBoxes compatibility must not add a hard assembly reference.'
Assert-True (@($assembly.GetReferencedAssemblies() | Where-Object { $_.Name -eq 'ValheimCuisine' }).Count -eq 0) 'Optional ValheimCuisine compatibility must not add a hard assembly reference.'
Assert-True ($null -eq $assembly.GetType('FineDining.SpoilageReferenceSection', $false)) 'The mirrored spoilage reference-section enum must remain removed.'
$ownerResolverSource = Get-Content -LiteralPath (Join-Path $projectRoot 'FoodPrefabOwnerResolver.cs') -Raw
foreach ($registryCall in @('ModRegistry.GetPrefabs()', 'ModRegistry.GetItems()', 'ModRegistry.GetPieces()'))
{
    Assert-True ($ownerResolverSource.Contains($registryCall)) "Jotunn owner resolution must use the public registry API: $registryCall"
}
Assert-True (-not $ownerResolverSource.Contains('PrefabManager') -and -not $ownerResolverSource.Contains('AccessTools')) 'Jotunn owner resolution must not return to private manager reflection.'

$calculateReplacementAmount = Get-MethodRequired $decayType 'CalculateReplacementAmount'
$replacementAmount = [int] $calculateReplacementAmount.Invoke($null, [object[]] @(50))
Assert-True ($replacementAmount -eq 50) 'Spoilage replacement must preserve a 50-item source stack as one 50/20 over-stack.'
Assert-True ([int]$calculateReplacementAmount.Invoke($null, [object[]] @(-1)) -eq 0) 'Spoilage replacement must reject negative source counts.'

$iceboxType = Get-TypeRequired $assembly 'FineDining.IceboxSubsystem'
$iceboxLimitPolicyType = Get-TypeRequired $assembly 'FineDining.IceboxLimitPolicy'
$iceboxLimitSnapshotType = Get-TypeRequired $assembly 'FineDining.IceboxLimitSnapshot'
Assert-True ((Get-Constant $iceboxLimitPolicyType 'FileName') -eq 'Icebox.yml') 'Icebox limit policy filename is incorrect.'
Assert-True ([int](Get-Constant $iceboxType 'DefaultPlacementLimit') -eq 2) 'Icebox default placement limit config must default to two.'
Assert-True ($null -eq $iceboxLimitPolicyType.GetField('BuiltInDefaultLimit', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'The legacy YAML default-limit constant must remain removed.'
Assert-True ($null -eq $iceboxLimitSnapshotType.GetProperty('DefaultLimit', [Reflection.BindingFlags] 'Instance,Public,NonPublic')) 'Icebox limit snapshots must contain only per-account overrides.'
$placementLimitAcceptableType = Get-TypeRequired $assembly 'FineDining.IceboxSubsystem+PlacementLimitAcceptableValue'
$placementLimitAcceptable = [Activator]::CreateInstance($placementLimitAcceptableType, $true)
Assert-True ($placementLimitAcceptable.IsValid(-1) -and $placementLimitAcceptable.IsValid(0) -and $placementLimitAcceptable.IsValid(1)) 'Icebox placement limits must accept unlimited, denied, and positive maximum values.'
Assert-True (-not $placementLimitAcceptable.IsValid(-2) -and [int]$placementLimitAcceptable.Clamp(-2) -eq 2) 'Invalid Icebox placement limits must fall back to the safe default instead of unlimited placement.'
Assert-True ([bool](Get-Constant $iceboxType 'DefaultShowMapPins')) 'Icebox map and minimap pins must default to enabled.'
Assert-True ((Get-Constant $iceboxType 'DefaultRecipe') -eq 'TrophySGolem:1,Obsidian:8,Crystal:16,Silver:32') 'Icebox default recipe is incorrect.'
$iceboxLimitPolicySource = Get-Content -LiteralPath (Join-Path $projectRoot 'IceboxLimitPolicy.cs') -Raw
Assert-True ($iceboxLimitPolicySource.Contains('"overrides: {}\n"') -and -not $iceboxLimitPolicySource.Contains('"defaultLimit: ')) 'Generated Icebox.yml must contain only per-Steam64 overrides.'
$tryParseIceboxLimits = Get-MethodRequired $iceboxLimitPolicyType 'TryParse'
$overrideOnlyIceboxYaml = "overrides:`n  `"76561198000000000`": 6`n"
$overrideOnlyIceboxArguments = [object[]] @($overrideOnlyIceboxYaml, $null, '')
Assert-True ([bool]$tryParseIceboxLimits.Invoke($null, $overrideOnlyIceboxArguments)) 'An overrides-only Icebox.yml must parse.'
$iceboxLimitSnapshot = $overrideOnlyIceboxArguments[1]
$getIceboxLimit = Get-MethodRequired $iceboxLimitSnapshot.GetType() 'GetLimit'
Assert-True ([int]$getIceboxLimit.Invoke($iceboxLimitSnapshot, [object[]] @('Steam_76561198000000000', 2)) -eq 6) 'An exact Steam64 Icebox.yml override must beat the config default.'
Assert-True ([int]$getIceboxLimit.Invoke($iceboxLimitSnapshot, [object[]] @('76561198000000001', 2)) -eq 2) 'An unlisted Steam64 account must use the synchronized config default.'
$iceboxQuotaSource = Get-Content -LiteralPath (Join-Path $projectRoot 'IceboxQuotaService.cs') -Raw
Assert-True ($iceboxQuotaSource.Contains('IceboxLimitPolicy.Current.GetLimit(') -and $iceboxQuotaSource.Contains('IceboxSubsystem.PlacementLimit')) 'Authoritative Icebox placement checks must combine YAML overrides with the live synchronized config default.'
$legacyIceboxArguments = [object[]] @("defaultLimit: 7`noverrides: {}`n", $null, '')
Assert-True (-not [bool]$tryParseIceboxLimits.Invoke($null, $legacyIceboxArguments)) 'The removed Icebox.yml defaultLimit field must not be accepted or migrated.'
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$legacyIceboxArguments[2])) 'Rejected legacy Icebox.yml must explain its invalid schema.'
$iceboxStaticFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
foreach ($removedScaleConstant in @('DefaultMapPinScale', 'MinimumMapPinScale', 'MaximumMapPinScale'))
{
    Assert-True ($null -eq $iceboxType.GetField($removedScaleConstant, $iceboxStaticFlags)) "Removed Icebox map-pin scale constant remains: $removedScaleConstant"
}
Assert-True ($null -eq $iceboxType.GetField('_mapPinScale', $iceboxStaticFlags)) 'The removed Icebox map-pin scale ConfigEntry must not return.'
Assert-True ($null -eq $iceboxType.GetProperty('MapPinScale', $iceboxStaticFlags)) 'The removed Icebox map-pin scale accessor must not return.'
Assert-True ($null -eq $iceboxType.GetMethod('OnMapPinScaleChanged', $iceboxStaticFlags)) 'The removed Icebox map-pin scale event handler must not return.'

$iceboxMapPinsType = Get-TypeRequired $assembly 'FineDining.IceboxMapPins'
Assert-True ($null -eq $iceboxMapPinsType.GetMethod('HandleScaleChanged', $iceboxStaticFlags)) 'The removed live map-pin scale refresh path must not return.'
Assert-Method $iceboxMapPinsType 'ApplyPinScale'
$iceboxMapPinsSource = Get-Content -LiteralPath (Join-Path $projectRoot 'IceboxMapPins.cs') -Raw
Assert-True ($iceboxMapPinsSource.Contains('foreach (Minimap.PinData pin in LocalPins.Values)')) 'Pin sizing must iterate only FineDining-owned Icebox pins.'
Assert-True ($iceboxMapPinsSource.Contains('minimap.m_mode == Minimap.MapMode.Large') -and $iceboxMapPinsSource.Contains('minimap.m_pinSizeLarge') -and $iceboxMapPinsSource.Contains('minimap.m_pinSizeSmall')) 'Icebox pin sizing must preserve distinct world-map and minimap base sizes.'
Assert-True ([regex]::IsMatch($iceboxMapPinsSource, 'float\s+pixels\s*=\s*baseSize\s*;')) 'Icebox pins must use the unscaled map or minimap base size.'
Assert-True (-not $iceboxMapPinsSource.Contains('IceboxSubsystem.MapPinScale') -and -not $iceboxMapPinsSource.Contains('baseSize * 2f')) 'Icebox pin rendering must not retain configurable or double-size scaling.'
Assert-True ($iceboxMapPinsSource.Contains('SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pixels)') -and $iceboxMapPinsSource.Contains('SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, pixels)')) 'Icebox pin sizing must apply equally to both RectTransform axes.'
Assert-HarmonyPatchTarget $assembly 'FineDining.MinimapUpdatePinsIceboxMapPinsPatch' 'Minimap' 'UpdatePins'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.MinimapUpdatePinsIceboxMapPinsPatch' 'HarmonyPostfix'
Assert-True ($iceboxMapPinsSource.Contains('IceboxMapPins.ApplyPinScale(__instance);')) 'Minimap.UpdatePins must reapply the fixed Icebox pin size after vanilla lays out pins.'
$parseIceboxRecipe = Get-MethodRequired $iceboxType 'TryParseRecipe'
$recipeArguments = [object[]] @('TrophySGolem:1,Obsidian:8,Crystal:16,Silver:32', $null, $null)
Assert-True ([bool]$parseIceboxRecipe.Invoke($null, $recipeArguments)) 'The default-shaped Icebox recipe must parse.'
Assert-True ($recipeArguments[1].Count -eq 4 -and [string]::IsNullOrEmpty($recipeArguments[2])) 'Icebox recipe parsing must preserve all four default requirements.'
Assert-True ($recipeArguments[1][0].Key -eq 'TrophySGolem' -and $recipeArguments[1][0].Value -eq 1 -and
             $recipeArguments[1][1].Key -eq 'Obsidian' -and $recipeArguments[1][1].Value -eq 8 -and
             $recipeArguments[1][2].Key -eq 'Crystal' -and $recipeArguments[1][2].Value -eq 16 -and
             $recipeArguments[1][3].Key -eq 'Silver' -and $recipeArguments[1][3].Value -eq 32) 'Icebox recipe parsing must preserve the default prefab order and amounts.'
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

$roundMultiplierForDisplay = Get-MethodRequired $foodEffectUiTextType 'RoundMultiplierForDisplay'
$getChefChoiceModifierColor = Get-MethodRequired $foodEffectUiTextType 'GetChefChoiceModifierColor'
$tryGetPenaltyDisplayMultiplier = Get-MethodRequired $foodEffectUiTextType 'TryGetPenaltyDisplayMultiplier'
$positiveModifierColor = [string](Get-Constant $foodEffectUiTextType 'PositiveModifierColorHex')
$penaltyModifierColor = [string](Get-Constant $foodEffectUiTextType 'PenaltyModifierColorHex')
$neutralModifierColor = [string](Get-Constant $foodEffectUiTextType 'NeutralModifierColorHex')
$runningSpoilageColor = [string](Get-Constant $foodEffectUiTextType 'RunningColorHex')
$pausedSpoilageColor = [string](Get-Constant $foodEffectUiTextType 'PausedColorHex')
Assert-True ([Math]::Abs([float]$roundMultiplierForDisplay.Invoke($null, [object[]] @([float]0.9999)) - 1) -lt 0.0001) 'A near-neutral modifier must display as x1.00.'
Assert-True ([Math]::Abs([float]$roundMultiplierForDisplay.Invoke($null, [object[]] @([float]0.994)) - 0.99) -lt 0.0001) 'An active penalty must round to two displayed decimals.'
Assert-True ([string]$getChefChoiceModifierColor.Invoke($null, [object[]] @([float]1.05)) -eq $positiveModifierColor) 'An active Chef modifier must use the positive color.'
Assert-True ([string]$getChefChoiceModifierColor.Invoke($null, [object[]] @([float]1)) -eq $neutralModifierColor) 'A neutral Chef Choice entry must use the neutral color.'
$neutralPenaltyArguments = [object[]] @([float]0.9999, [float]0)
Assert-True (-not [bool]$tryGetPenaltyDisplayMultiplier.Invoke($null, $neutralPenaltyArguments)) 'A penalty displayed as x1.00 must be hidden.'
Assert-True ([Math]::Abs([float]$neutralPenaltyArguments[1] - 1) -lt 0.0001) 'A hidden penalty must report the neutral displayed multiplier.'
$activePenaltyArguments = [object[]] @([float]0.994, [float]0)
Assert-True ([bool]$tryGetPenaltyDisplayMultiplier.Invoke($null, $activePenaltyArguments)) 'A penalty displayed below x1.00 must be visible.'
Assert-True ([Math]::Abs([float]$activePenaltyArguments[1] - 0.99) -lt 0.0001) 'A visible penalty must report its rounded multiplier.'
Assert-True ($penaltyModifierColor -eq '#FFB454') 'Active penalties must retain the warning color.'
Assert-True ($positiveModifierColor -eq '#9FE870') 'Active Chef modifiers must retain the positive color.'
Assert-True ($neutralModifierColor -eq '#B8B8B8') 'Neutral Chef entries must retain the neutral color.'
Assert-True ($runningSpoilageColor -eq '#FFD138') 'Running spoilage text must match the gold inventory timer.'
Assert-True ($pausedSpoilageColor -eq '#70C8FF') 'Paused spoilage text must retain its cold blue color.'
$buildSpoilageStatusLine = Get-MethodRequired $foodEffectUiTextType 'BuildStatusLine'
$getMinimumFreshnessLabel = Get-MethodRequired $foodEffectUiTextType 'GetMinimumFreshnessLabel'
$getMinimumFreshnessSlotLabel = Get-MethodRequired $foodEffectUiTextType 'GetMinimumFreshnessSlotLabel'
$buildMinimumFreshnessLine = Get-MethodRequired $foodEffectUiTextType 'BuildMinimumFreshnessLine'
Assert-True ($buildSpoilageStatusLine.GetParameters().Count -eq 3 -and
             $buildSpoilageStatusLine.GetParameters()[2].ParameterType -eq [bool]) 'Spoilage status formatting must receive the resolved minimum-freshness endpoint.'
Assert-True ($getMinimumFreshnessLabel.ReturnType -eq [string] -and
             $getMinimumFreshnessSlotLabel.ReturnType -eq [string] -and
             $buildMinimumFreshnessLine.ReturnType -eq [string]) 'Minimum-freshness UI formatter contracts are incomplete.'
Assert-True ((Get-Constant $foodEffectUiTextType 'EnglishMinimumFreshnessRunningLine') -eq 'Minimum freshness in {0}') 'Keep expiry must describe its minimum-freshness endpoint instead of claiming that the item becomes rotten.'
Assert-True ((Get-Constant $foodEffectUiTextType 'EnglishMinimumFreshnessPausedLine') -eq 'Cold preservation paused freshness loss · minimum freshness in {0} ❄') 'Paused Keep expiry must describe paused freshness loss rather than paused spoilage.'
Assert-True ((Get-Constant $foodEffectUiTextType 'EnglishMinimumFreshnessLine') -eq 'Minimum freshness reached') 'Keep expiry detail text must use the explicit minimum-freshness endpoint.'
Assert-True ((Get-Constant $foodEffectUiTextType 'EnglishMinimumFreshnessSlotLabel') -eq 'Min') 'The narrow inventory overlay must use the compact minimum-freshness label.'
Assert-Method $foodEffectUiTextType 'BuildChefChoiceModifierLine'
Assert-Method $foodEffectUiTextType 'TryBuildDiminishingReturnsLine'
Assert-Method $foodEffectUiTextType 'TryBuildStalenessLine'
$uiTextFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
Assert-True ($null -eq $foodEffectUiTextType.GetMethod('BuildFreshnessEffectLine', $uiTextFlags)) 'The obsolete Freshness effect UI builder must not remain.'
$dietTooltipSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\TooltipAndConsumePatches.cs') -Raw
$chefModifierIndex = $dietTooltipSource.IndexOf('FoodEffectUiText.BuildChefChoiceModifierLine(', [StringComparison]::Ordinal)
$diminishingModifierIndex = $dietTooltipSource.IndexOf('FoodEffectUiText.TryBuildDiminishingReturnsLine(', [StringComparison]::Ordinal)
$stalenessModifierIndex = $dietTooltipSource.IndexOf('FoodEffectUiText.TryBuildStalenessLine(', [StringComparison]::Ordinal)
Assert-True ($chefModifierIndex -ge 0 -and $chefModifierIndex -lt $diminishingModifierIndex -and $diminishingModifierIndex -lt $stalenessModifierIndex) 'Item tooltip modifiers must remain ordered as Chef Choice, diminishing returns, then staleness.'
$fineDiningUiSource = Get-Content -LiteralPath (Join-Path $projectRoot 'FineDiningUi.cs') -Raw
Assert-True ($fineDiningUiSource.Contains('string color = paused ? PausedColorHex : RunningColorHex;')) 'Spoilage status text must select a color for both running and paused clocks.'
Assert-True ($fineDiningUiSource.Contains('if (reachesMinimumFreshness)') -and
             $fineDiningUiSource.Contains('EnglishMinimumFreshnessPausedLine') -and
             $fineDiningUiSource.Contains('EnglishMinimumFreshnessRunningLine')) 'Keep countdown formatting must select dedicated running and paused minimum-freshness wording.'
Assert-True ([regex]::IsMatch(
    $fineDiningUiSource,
    '(?s)ReachesMinimumFreshness\(ItemData\? item\).*?rule\.State == SpoilageRuleState\.Enabled\s*&&\s*rule\.ExpiryAction == SpoilageExpiryAction\.KeepOriginal')) 'Keep-specific wording must require an enabled KeepOriginal policy rule.'
Assert-True ($fineDiningUiSource.Contains('? FoodEffectUiText.GetMinimumFreshnessSlotLabel()')) 'Completed Keep expiry must use the compact label in inventory and container slots.'
Assert-True ($fineDiningUiSource.Contains('FoodEffectUiText.BuildMinimumFreshnessLine()')) 'Completed Keep expiry must use the shared detailed minimum-freshness line in tooltips and world hover.'
Assert-True ($fineDiningUiSource.Contains('FoodEffectUiText.ReachesMinimumFreshness(__instance)')) 'Item tooltips must select Keep-specific minimum-freshness wording before expiry.'
Assert-True (-not $fineDiningUiSource.Contains('WaterPausedLineKey') -and -not $fineDiningUiSource.Contains('EnglishWaterPausedLine')) 'Water preservation must not add a dedicated hover label.'
$statusColorReturn = 'return $"<color={color}>{line}</color>";'
Assert-True ($fineDiningUiSource.Contains($statusColorReturn)) 'Spoilage status text must colorize the complete localized line.'
$worldHoverStart = $fineDiningUiSource.IndexOf('internal static class WorldItemSpoilageHover', [StringComparison]::Ordinal)
$worldHoverEnd = $fineDiningUiSource.IndexOf('[HarmonyPatch(typeof(Feast)', $worldHoverStart, [StringComparison]::Ordinal)
Assert-True ($worldHoverStart -ge 0 -and $worldHoverEnd -gt $worldHoverStart) 'World-item hover implementation boundaries are missing.'
$worldHoverSource = $fineDiningUiSource.Substring($worldHoverStart, $worldHoverEnd - $worldHoverStart)
Assert-True ($worldHoverSource.Contains('FoodEffectUiText.TryBuildStalenessLine(')) 'Dropped and placed food hover must use the shared staleness formatter.'
Assert-True ($worldHoverSource.Contains('FoodEffectUiText.ReachesMinimumFreshness(worldDrop?.m_itemData)')) 'Dropped and placed food hover must select Keep-specific minimum-freshness wording before expiry.'
Assert-True (-not $worldHoverSource.Contains('IsFishPreservedInWater')) 'World-item hover must not expose a water-specific preservation reason.'
Assert-True (-not $fineDiningUiSource.Contains('[HarmonyPatch(typeof(Fish), nameof(Fish.GetHoverText))]')) 'FineDining must leave vanilla underwater Fish hover text unchanged.'
$decayRuntimeSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DecayRuntime.cs') -Raw
$creatorlessCheckStart = $decayRuntimeSource.IndexOf('internal static bool IsCreatorlessPlacedDrop(', [StringComparison]::Ordinal)
$creatorlessReconcileStart = $decayRuntimeSource.IndexOf('private static bool ReconcileCreatorlessPlacedDrop(', $creatorlessCheckStart, [StringComparison]::Ordinal)
$creatorlessReconcileEnd = $decayRuntimeSource.IndexOf('private static bool TryGetPositiveCustomTicks(', $creatorlessReconcileStart, [StringComparison]::Ordinal)
Assert-True ($creatorlessCheckStart -ge 0 -and $creatorlessReconcileStart -gt $creatorlessCheckStart -and $creatorlessReconcileEnd -gt $creatorlessReconcileStart) 'NoCreator placed-food source boundaries are missing.'
$creatorlessCheckSource = $decayRuntimeSource.Substring($creatorlessCheckStart, $creatorlessReconcileStart - $creatorlessCheckStart)
$creatorlessPlacedGate = $creatorlessCheckSource.IndexOf('IsPlacedGroundDrop(drop)', [StringComparison]::Ordinal)
$creatorlessZdoRead = $creatorlessCheckSource.IndexOf('GetLong(ZDOVars.s_creator, 0L)', [StringComparison]::Ordinal)
Assert-True ($creatorlessPlacedGate -ge 0 -and $creatorlessZdoRead -gt $creatorlessPlacedGate) 'NoCreator exemption must first require a placed Piece ItemDrop, leaving ordinary loose ItemDrops eligible for spoilage.'
Assert-True ($creatorlessCheckSource.Contains('drop.m_nview.GetZDO().GetLong(ZDOVars.s_creator, 0L) == 0L')) 'NoCreator detection must read the persisted ZDO creator field directly.'
Assert-True (-not $creatorlessCheckSource.Contains('GetCreator(')) 'NoCreator detection must not rely on Piece.GetCreator, whose local cache can be stale after Infinity Hammer removes the ZDO field.'
$creatorlessReconcileSource = $decayRuntimeSource.Substring($creatorlessReconcileStart, $creatorlessReconcileEnd - $creatorlessReconcileStart)
$creatorlessOwnerGate = $creatorlessReconcileSource.IndexOf('IsOwnedGroundDrop(', [StringComparison]::Ordinal)
$creatorlessAuthoritativeClear = $creatorlessReconcileSource.IndexOf('ClearOwnedGroundExpiry(', [StringComparison]::Ordinal)
Assert-True ($creatorlessOwnerGate -ge 0 -and $creatorlessAuthoritativeClear -gt $creatorlessOwnerGate) 'Only the authoritative world-item owner may persist NoCreator spoilage cleanup.'
Assert-True ($creatorlessReconcileSource.Contains('UnregisterGroundDrop(drop);')) 'A non-owner must stop locally tracking a NoCreator placed item without mutating its ZDO.'
$registerGroundDropStart = $decayRuntimeSource.IndexOf('internal static void RegisterGroundDrop(', [StringComparison]::Ordinal)
$refreshPlacedDropStart = $decayRuntimeSource.IndexOf('internal static void RefreshOwnedPlacedDrop(', $registerGroundDropStart, [StringComparison]::Ordinal)
$initializePlacedDropStart = $decayRuntimeSource.IndexOf('internal static void InitializePlacedDrop(', $refreshPlacedDropStart, [StringComparison]::Ordinal)
$shouldInitializePlacedStart = $decayRuntimeSource.IndexOf('internal static bool ShouldInitializePlacedDeadline(', $initializePlacedDropStart, [StringComparison]::Ordinal)
Assert-True ($registerGroundDropStart -ge 0 -and $refreshPlacedDropStart -gt $registerGroundDropStart -and $initializePlacedDropStart -gt $refreshPlacedDropStart -and $shouldInitializePlacedStart -gt $initializePlacedDropStart) 'Placed-world registration source boundaries are missing.'
$registerGroundDropSource = $decayRuntimeSource.Substring($registerGroundDropStart, $refreshPlacedDropStart - $registerGroundDropStart)
Assert-True ($registerGroundDropSource.IndexOf('ReconcileCreatorlessPlacedDrop(drop)', [StringComparison]::Ordinal) -ge 0 -and
             $registerGroundDropSource.IndexOf('ReconcileCreatorlessPlacedDrop(drop)', [StringComparison]::Ordinal) -lt $registerGroundDropSource.IndexOf('TryGetExpiryTicks(', [StringComparison]::Ordinal)) 'Registration must clean a persisted NoCreator clock before deciding whether to track the world item.'
$initializePlacedDropSource = $decayRuntimeSource.Substring($initializePlacedDropStart, $shouldInitializePlacedStart - $initializePlacedDropStart)
Assert-True ($initializePlacedDropSource.IndexOf('ReconcileCreatorlessPlacedDrop(drop)', [StringComparison]::Ordinal) -ge 0 -and
             $initializePlacedDropSource.IndexOf('ReconcileCreatorlessPlacedDrop(drop)', [StringComparison]::Ordinal) -lt $initializePlacedDropSource.IndexOf('InitializeOwnedWorldDrop(', [StringComparison]::Ordinal)) 'Fresh NoCreator placement must be rejected before FineDining creates a spoilage clock.'
$clearGroundExpiryStart = $decayRuntimeSource.IndexOf('private static void ClearOwnedGroundExpiry(', [StringComparison]::Ordinal)
$initializeWorldDropStart = $decayRuntimeSource.IndexOf('private static void InitializeOwnedWorldDrop(', $clearGroundExpiryStart, [StringComparison]::Ordinal)
Assert-True ($clearGroundExpiryStart -ge 0 -and $initializeWorldDropStart -gt $clearGroundExpiryStart) 'Authoritative ground-spoilage cleanup source boundaries are missing.'
$clearGroundExpirySource = $decayRuntimeSource.Substring($clearGroundExpiryStart, $initializeWorldDropStart - $clearGroundExpiryStart)
$clearGroundOwnerGate = $clearGroundExpirySource.IndexOf('IsOwnedGroundDrop(drop)', [StringComparison]::Ordinal)
$clearGroundClock = $clearGroundExpirySource.IndexOf('Remove(ExpiryDataKey)', [StringComparison]::Ordinal)
Assert-True ($clearGroundOwnerGate -ge 0 -and $clearGroundClock -gt $clearGroundOwnerGate) 'Ground clock cleanup must verify ownership before removing persisted metadata.'
Assert-True ($clearGroundExpirySource.Contains('Remove(PlacedAnchorDataKey)') -and
             $clearGroundExpirySource.Contains('FreshnessRuntime.ClearTrackedMetadata(drop.m_itemData)') -and
             $clearGroundExpirySource.Contains('drop.Save();') -and
             $clearGroundExpirySource.Contains('UnregisterGroundDrop(drop);')) 'NoCreator cleanup must remove the expiry, placement anchor, and freshness basis, save authoritative state, and stop tracking the item.'
$evaluateGroundExpiryStart = $decayRuntimeSource.IndexOf('private static bool TryEvaluateGroundExpiry(', [StringComparison]::Ordinal)
$clearGroundExpiryAfterEvaluate = $decayRuntimeSource.IndexOf('private static void ClearOwnedGroundExpiry(', $evaluateGroundExpiryStart, [StringComparison]::Ordinal)
Assert-True ($evaluateGroundExpiryStart -ge 0 -and $clearGroundExpiryAfterEvaluate -gt $evaluateGroundExpiryStart) 'Ground expiry-evaluation source boundaries are missing.'
$evaluateGroundExpirySource = $decayRuntimeSource.Substring($evaluateGroundExpiryStart, $clearGroundExpiryAfterEvaluate - $evaluateGroundExpiryStart)
$evaluateCreatorlessGate = $evaluateGroundExpirySource.IndexOf('ReconcileCreatorlessPlacedDrop(drop)', [StringComparison]::Ordinal)
$evaluateEnsureState = $evaluateGroundExpirySource.IndexOf('EnsureItemState(', [StringComparison]::Ordinal)
Assert-True ($evaluateCreatorlessGate -ge 0 -and $evaluateEnsureState -gt $evaluateCreatorlessGate) 'NoCreator status must be reconciled before expiry evaluation can recreate or advance a clock.'
$expireGroundStackStart = $decayRuntimeSource.IndexOf('private static void ExpireGroundStack(', [StringComparison]::Ordinal)
$groundSourceMatchStart = $decayRuntimeSource.IndexOf('private static bool GroundSourceStillMatches(', $expireGroundStackStart, [StringComparison]::Ordinal)
$groundSourceMatchEnd = $decayRuntimeSource.IndexOf('internal static bool ShouldInheritGroundSpawnTime(', $groundSourceMatchStart, [StringComparison]::Ordinal)
Assert-True ($expireGroundStackStart -ge 0 -and $groundSourceMatchStart -gt $expireGroundStackStart -and $groundSourceMatchEnd -gt $groundSourceMatchStart) 'Ground replacement source boundaries are missing.'
$expireGroundStackSource = $decayRuntimeSource.Substring($expireGroundStackStart, $groundSourceMatchStart - $expireGroundStackStart)
$creatorlessReplacementGate = $expireGroundStackSource.IndexOf('ReconcileCreatorlessPlacedDrop(drop)', [StringComparison]::Ordinal)
$spawnGroundReplacement = $expireGroundStackSource.IndexOf('ItemDrop.DropItem(', [StringComparison]::Ordinal)
Assert-True ($creatorlessReplacementGate -ge 0 -and $spawnGroundReplacement -gt $creatorlessReplacementGate) 'Ground expiry must re-check and clean NoCreator state before spawning a rotten replacement.'
$groundSourceMatchSource = $decayRuntimeSource.Substring($groundSourceMatchStart, $groundSourceMatchEnd - $groundSourceMatchStart)
Assert-True ($groundSourceMatchSource.Contains('IsCreatorlessPlacedDrop(drop)')) 'The final source-identity check must reject an item changed to NoCreator while a replacement was being prepared.'
$hoverCreatorlessGate = $worldHoverSource.IndexOf('DecayRuntime.IsCreatorlessPlacedDrop(worldDrop)', [StringComparison]::Ordinal)
$hoverClockRead = $worldHoverSource.IndexOf('DecayRuntime.TryGetSpoilageClock(', $hoverCreatorlessGate, [StringComparison]::Ordinal)
Assert-True ($hoverCreatorlessGate -ge 0 -and $hoverClockRead -gt $hoverCreatorlessGate) 'Placed NoCreator items must suppress their stale persisted spoilage hover before the clock is read.'
Assert-Method $decayType 'IsCreatorlessPlacedDrop'
$worldPauseStart = $decayRuntimeSource.IndexOf('private static bool ResolveWorldDropPausedState(', [StringComparison]::Ordinal)
$worldPauseCache = $decayRuntimeSource.IndexOf('GroundEnvironmentSamples.TryGetValue(', $worldPauseStart, [StringComparison]::Ordinal)
$fishWaterCheck = $decayRuntimeSource.IndexOf('IsFishPreservedInWater(drop, knownGroup)', $worldPauseStart, [StringComparison]::Ordinal)
Assert-True ($worldPauseStart -ge 0 -and $fishWaterCheck -gt $worldPauseStart -and $fishWaterCheck -lt $worldPauseCache) 'Dynamic Fish water state must be sampled before the position-based biome cache.'
Assert-True ($decayRuntimeSource.Contains('group != SpoilageGroup.Fish') -and $decayRuntimeSource.Contains('!fish.IsOutOfWater()')) 'Water preservation must be limited to classified Fish and use vanilla Fish water state.'
Assert-True ($decayRuntimeSource.Contains('ResolveWorldDropPausedState(drop, rule.Group)')) 'The one-second ground-item path must reuse its resolved spoilage group.'
Assert-True ($decayRuntimeSource.Contains('floating.m_waterLevel') -and $decayRuntimeSource.Contains('do not treat tar as preservation')) 'Modded Fish pickup items must use the existing water-only Floating fallback.'
$formatFoodNameForTooltip = Get-MethodRequired $hudFoodPanelsType 'FormatFoodNameForTooltip'
Assert-True ([string]$formatFoodNameForTooltip.Invoke($null, [object[]] @('Carrot')) -eq '<color=orange>Carrot</color>') 'HUD food hover names must use Valheim orange markup.'
Assert-True ([string]::IsNullOrEmpty([string]$formatFoodNameForTooltip.Invoke($null, [object[]] @('   ')))) 'An empty HUD food name must not produce empty color markup.'
$nullFoodNameArguments = New-Object 'System.Object[]' 1
Assert-True ([string]::IsNullOrEmpty([string]$formatFoodNameForTooltip.Invoke($null, $nullFoodNameArguments))) 'A null HUD food name must not produce empty color markup.'
$hudFoodPanelsSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\HudFoodPanels.cs') -Raw
$hudFoodSlotsSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\HudFoodSlots.cs') -Raw
Assert-True ($hudFoodPanelsSource.Contains('string foodName = FormatFoodNameForTooltip(')) 'Recent-history and Chef Choice hover must color localized food names.'
$updateRecentSlotsStart = $hudFoodPanelsSource.IndexOf(
    'private static void UpdateRecentSlots(',
    [StringComparison]::Ordinal)
$updateChefSlotsStart = $hudFoodPanelsSource.IndexOf(
    'private static void UpdateChefSlots(',
    $updateRecentSlotsStart,
    [StringComparison]::Ordinal)
Assert-True ($updateRecentSlotsStart -ge 0 -and $updateChefSlotsStart -gt $updateRecentSlotsStart) 'Recent-history HUD source boundaries are missing.'
$updateRecentSlotsSource = $hudFoodPanelsSource.Substring(
    $updateRecentSlotsStart,
    $updateChefSlotsStart - $updateRecentSlotsStart)
Assert-True ([regex]::IsMatch(
    $updateRecentSlotsSource,
    '(?s)int\s+regularNextStack\s*=\s*RecentHistoryService\.GetNextStack\(\s*state,\s*entry\.Key,\s*isChef:\s*false\s*\);.*?FoodRules\.CalculateDiminishingScale\(regularNextStack\)')) 'Recent-history HUD penalties must preview the next regular consumption rather than the completed history count.'
Assert-True (-not $updateRecentSlotsSource.Contains('CalculateDiminishingScale(entry.Stack)')) 'Recent-history HUD penalties must not lag one consumption behind by using entry.Stack directly.'
Assert-True ([regex]::IsMatch(
    $updateRecentSlotsSource,
    'bool\s+isChef\s*=\s*ChefCollectionService\.GetEntry\(\s*state,\s*entry\.Key\s*\)\s*!=\s*null\s*;')) 'Recent-history HUD preview must resolve whether the next consumption is protected by Chef Choice.'
Assert-True ([regex]::IsMatch(
    $updateRecentSlotsSource,
    'bool\s+chefExemptsDiminishing\s*=\s*isChef\s*&&\s*wouldDiminish\s*;')) 'Chef exemption must apply only when the next regular consumption would diminish.'
Assert-True ([regex]::IsMatch(
    $updateRecentSlotsSource,
    '(?s)wouldDiminish\s*&&\s*!chefExemptsDiminishing\s*\?.*?x\{.*?:\s*string\.Empty')) 'A Chef-exempt recent-history slot must hide the regular diminishing multiplier footer.'
Assert-True ([regex]::Matches($updateRecentSlotsSource, 'entry\.Stack').Count -ge 2) 'Recent-history corner and hover text must retain the completed consumption count while the footer previews the next one.'
Assert-True (-not $hudFoodSlotsSource.Contains('UpdateTooltips(') -and
             -not $hudFoodSlotsSource.Contains('UITooltip')) 'Eaten-food icons must no longer own a cursor-following tooltip update path.'
Assert-True ($hudFoodPanelsSource.Contains('Localization.instance.Localize(hoveredFood.m_item.m_shared.m_name)')) 'Eaten-food hover must localize its item name before adding rich-text color.'
Assert-True ($hudFoodPanelsSource.Contains('FormatFoodNameForTooltip(foodName)')) 'Eaten-food hover must color localized food names.'
Assert-True ([Math]::Abs([float](Get-Constant $hudFoodPanelsType 'HoverPanelShowDelay') - 0.5) -lt 0.0001) 'Fixed diet hover panels must retain the standard half-second tooltip delay.'
Assert-True ([Math]::Abs([float](Get-Constant $hudFoodPanelsType 'HoverPanelHeight') - 48) -lt 0.0001) 'Fixed diet hover panels must retain their two-line height.'
Assert-True ([Math]::Abs([float](Get-Constant $hudFoodPanelsType 'HoverPanelMinimumWidth') - 460) -lt 0.0001) 'Fixed diet hover panels must retain their requested minimum width.'
Assert-True ([Math]::Abs([float](Get-Constant $hudFoodPanelsType 'HoverPanelFontSize') - 15) -lt 0.0001) 'Fixed diet hover panels must retain their preferred font size.'
Assert-True ([Math]::Abs([float](Get-Constant $hudFoodPanelsType 'HoverPanelMinimumFontSize') - 10) -lt 0.0001) 'Fixed diet hover panels must retain their legible auto-size floor.'

$layoutRootStart = $hudFoodPanelsSource.IndexOf('private static void LayoutRoot(', [StringComparison]::Ordinal)
$createRowStart = $hudFoodPanelsSource.IndexOf('private static RectTransform CreateRow(', $layoutRootStart, [StringComparison]::Ordinal)
Assert-True ($layoutRootStart -ge 0 -and $createRowStart -gt $layoutRootStart) 'Diet HUD layout source boundaries are missing.'
$layoutRootSource = $hudFoodPanelsSource.Substring($layoutRootStart, $createRowStart - $layoutRootStart)
Assert-True ([regex]::IsMatch(
    $layoutRootSource,
    '(?s)LayoutHoverPanel\(\s*context\.RecentHover,\s*hoverWidth,\s*new Vector2\(0f, HoverPanelGap\),\s*new Vector2\(0f, 0f\)\)')) 'Recent-history hover guidance must be anchored immediately above its row.'
Assert-True ([regex]::IsMatch(
    $layoutRootSource,
    '(?s)LayoutHoverPanel\(\s*context\.ChefHover,\s*hoverWidth,\s*new Vector2\(0f, chefRowOffset - slotSize\.y - HoverPanelGap\),\s*new Vector2\(0f, 1f\)\)')) 'Chef Choice hover guidance must be anchored immediately below its row.'
Assert-True ([regex]::IsMatch(
    $layoutRootSource,
    '(?s)LayoutHoverPanel\(\s*context\.EatenHover,\s*hoverWidth,\s*new Vector2\(0f, HoverPanelGap\),\s*new Vector2\(0f, 0f\)\)')) 'Eaten-food hover must use the same fixed area immediately above recent history, independent of the hovered icon location.'

$createHoverPanelStart = $hudFoodPanelsSource.IndexOf('private static HoverPanelContext CreateHoverPanel(', [StringComparison]::Ordinal)
$ensureSlotsStart = $hudFoodPanelsSource.IndexOf('private static void EnsureSlots(', $createHoverPanelStart, [StringComparison]::Ordinal)
Assert-True ($createHoverPanelStart -ge 0 -and $ensureSlotsStart -gt $createHoverPanelStart) 'Fixed diet hover panel creation source boundaries are missing.'
$createHoverPanelSource = $hudFoodPanelsSource.Substring($createHoverPanelStart, $ensureSlotsStart - $createHoverPanelStart)
Assert-True ($createHoverPanelSource.Contains('background.raycastTarget = false;')) 'Fixed diet hover panel backgrounds must not intercept pointer input.'
Assert-True ($createHoverPanelSource.Contains('text.enableAutoSizing = true;') -and
             $createHoverPanelSource.Contains('text.fontSizeMin = HoverPanelMinimumFontSize;') -and
             $createHoverPanelSource.Contains('text.fontSizeMax = HoverPanelFontSize;')) 'Fixed diet hover panels must auto-size within the intended font range.'
Assert-True ($createHoverPanelSource.Contains('text.maxVisibleLines = 2;') -and
             $createHoverPanelSource.Contains('text.textWrappingMode = TextWrappingModes.NoWrap;') -and
             $createHoverPanelSource.Contains('text.overflowMode = TextOverflowModes.Ellipsis;')) 'Fixed diet hover panels must preserve exactly two unwrapped, ellipsized message lines.'
$createTextStart = $hudFoodPanelsSource.IndexOf('private static TextMeshProUGUI CreateText(', [StringComparison]::Ordinal)
$updateHoverGuidanceStart = $hudFoodPanelsSource.IndexOf('private static void UpdateHoverGuidance(', $createTextStart, [StringComparison]::Ordinal)
Assert-True ($createTextStart -ge 0 -and $updateHoverGuidanceStart -gt $createTextStart) 'Diet HUD text helper source boundaries are missing.'
$createTextSource = $hudFoodPanelsSource.Substring($createTextStart, $updateHoverGuidanceStart - $createTextStart)
Assert-True ($createTextSource.Contains('text.raycastTarget = false;')) 'Fixed diet hover panel text must not intercept pointer input.'

$createSlotStart = $hudFoodPanelsSource.IndexOf('private static SlotContext CreateSlot(', [StringComparison]::Ordinal)
$formatFoodNameStart = $hudFoodPanelsSource.IndexOf('internal static string FormatFoodNameForTooltip(', $createSlotStart, [StringComparison]::Ordinal)
Assert-True ($createSlotStart -ge 0 -and $formatFoodNameStart -gt $createSlotStart) 'Diet HUD slot creation source boundaries are missing.'
$createSlotSource = $hudFoodPanelsSource.Substring($createSlotStart, $formatFoodNameStart - $createSlotStart)
Assert-True (-not $createSlotSource.Contains('UITooltip') -and -not $createSlotSource.Contains('GetOrCreateTooltip(')) 'Recent-history and Chef Choice slots must use fixed panels instead of per-slot UITooltip components.'

$updateHoverGuidanceEnd = $hudFoodPanelsSource.IndexOf('private static void MarkHoverTextDirty(', $updateHoverGuidanceStart, [StringComparison]::Ordinal)
Assert-True ($updateHoverGuidanceEnd -gt $updateHoverGuidanceStart) 'Diet HUD setting-aware guidance source boundary is missing.'
$updateHoverGuidanceSource = $hudFoodPanelsSource.Substring($updateHoverGuidanceStart, $updateHoverGuidanceEnd - $updateHoverGuidanceStart)
Assert-True ($updateHoverGuidanceSource.Contains('DietConfig.GetChefRecentFoodPreferencePercent() > 0f')) 'Recent-history guidance must report whether food-type preference is enabled.'
Assert-True ($updateHoverGuidanceSource.Contains('DietConfig.GetChefHighTierSelectionStrength() > 0f')) 'Chef guidance must report whether Cooking tier weighting is enabled.'
Assert-True ($updateHoverGuidanceSource.Contains('chefMultiplierMode > chefMultiplierMinimum') -and
             $updateHoverGuidanceSource.Contains('!Mathf.Approximately(')) 'Chef guidance must only claim multiplier progression when the configured level-100 mode exceeds the minimum.'
foreach ($guidanceToken in @(
    '$finedining_diet_recent_chef_guidance',
    '$finedining_diet_recent_chef_guidance_disabled',
    '$finedining_diet_chef_cooking_guidance_both',
    '$finedining_diet_chef_cooking_guidance_tier',
    '$finedining_diet_chef_cooking_guidance_multiplier',
    '$finedining_diet_chef_cooking_guidance_disabled'))
{
    Assert-True ($updateHoverGuidanceSource.Contains($guidanceToken)) "Diet HUD setting branch is missing localization token $guidanceToken."
}

$updateSlotHoverTextStart = $hudFoodPanelsSource.IndexOf('private static void UpdateSlotHoverText(', [StringComparison]::Ordinal)
$hideSlotStart = $hudFoodPanelsSource.IndexOf('private static void HideSlot(', $updateSlotHoverTextStart, [StringComparison]::Ordinal)
Assert-True ($updateSlotHoverTextStart -ge 0 -and $hideSlotStart -gt $updateSlotHoverTextStart) 'Diet HUD hover text composition source boundaries are missing.'
$updateSlotHoverTextSource = $hudFoodPanelsSource.Substring($updateSlotHoverTextStart, $hideSlotStart - $updateSlotHoverTextStart)
Assert-True ($updateSlotHoverTextSource.Contains('$finedining_diet_recent_chef_exempt')) 'Recent-history hover must explain a Chef Choice exemption with its dedicated localization token.'
Assert-True ([regex]::IsMatch(
    $updateSlotHoverTextSource,
    '(?s)chefExemptsDiminishing.*?\$finedining_diet_recent_chef_exempt')) 'Chef exemption hover text must take precedence over the ordinary prospective diminishing message.'
Assert-True ($updateSlotHoverTextSource.Contains('slot.HoverChefExemptsDiminishing == chefExemptsDiminishing') -and
             $updateSlotHoverTextSource.Contains('slot.HoverChefExemptsDiminishing = chefExemptsDiminishing')) 'Recent-history hover caching must invalidate when Chef exemption state changes.'
Assert-True ($hudFoodPanelsSource.Contains('public bool HoverChefExemptsDiminishing;')) 'Recent-history slot state must cache the Chef exemption flag.'
Assert-True ([regex]::IsMatch(
    $updateSlotHoverTextSource,
    '\?\s*description\s*\+\s*"\\n"\s*\+\s*guidance\s*:\s*guidance\s*\+\s*"\\n"\s*\+\s*description')) 'History hover must order guidance before the existing description, while Chef hover must order the existing description before guidance.'

$updateHoverPanelStart = $hudFoodPanelsSource.IndexOf('private static void UpdateHoverPanel(', [StringComparison]::Ordinal)
$updateEatenHoverStart = $hudFoodPanelsSource.IndexOf('private static void UpdateEatenHover(', $updateHoverPanelStart, [StringComparison]::Ordinal)
Assert-True ($updateHoverPanelStart -ge 0 -and $updateEatenHoverStart -gt $updateHoverPanelStart) 'Fixed diet hover visibility source boundaries are missing.'
$updateHoverPanelSource = $hudFoodPanelsSource.Substring($updateHoverPanelStart, $updateEatenHoverStart - $updateHoverPanelStart)
Assert-True ($updateHoverPanelSource.Contains('!ReferenceEquals(panel.HoveredSlot, hoveredSlot)') -and
             $updateHoverPanelSource.Contains('panel.HoverStartedAt = Time.unscaledTime;') -and
             $updateHoverPanelSource.Contains('panel.Root.gameObject.SetActive(false);')) 'Moving between diet icons must hide the panel and restart its hover delay.'
Assert-True ($updateHoverPanelSource.Contains('Time.unscaledTime - panel.HoverStartedAt >= HoverPanelShowDelay')) 'Fixed diet hover panels must wait for the standard delay before appearing.'

$disableEatenTooltipStart = $hudFoodPanelsSource.IndexOf('private static void DisableEatenCursorTooltip(', $updateEatenHoverStart, [StringComparison]::Ordinal)
$calculateExtraEffectStart = $hudFoodPanelsSource.IndexOf('internal static float CalculateExtraEffectScale(', $disableEatenTooltipStart, [StringComparison]::Ordinal)
$formatEatenFoodHoverStart = $hudFoodPanelsSource.IndexOf('internal static string FormatEatenFoodHover(', $calculateExtraEffectStart, [StringComparison]::Ordinal)
Assert-True ($disableEatenTooltipStart -gt $updateEatenHoverStart -and
             $calculateExtraEffectStart -gt $disableEatenTooltipStart -and
             $formatEatenFoodHoverStart -gt $calculateExtraEffectStart) 'Eaten-food hover and net-effect source boundaries are missing.'
$updateEatenHoverSource = $hudFoodPanelsSource.Substring($updateEatenHoverStart, $disableEatenTooltipStart - $updateEatenHoverStart)
$disableEatenTooltipSource = $hudFoodPanelsSource.Substring($disableEatenTooltipStart, $calculateExtraEffectStart - $disableEatenTooltipStart)
$calculateExtraEffectSource = $hudFoodPanelsSource.Substring($calculateExtraEffectStart, $formatEatenFoodHoverStart - $calculateExtraEffectStart)
Assert-True ($hudFoodPanelsSource.Contains('UpdateEatenHover(context, hud, player, state);') -and
             $hudFoodPanelsSource.Contains('eatenHover.Text.alignment = TextAlignmentOptions.TopLeft;')) 'Eaten-food hover must update with the existing HUD and use a left-aligned fixed panel.'
Assert-True ($updateEatenHoverSource.Contains('!ReferenceEquals(context.EatenHoveredFood, hoveredFood)') -and
             $updateEatenHoverSource.Contains('context.EatenHoveredIcon != hoveredIcon') -and
             $updateEatenHoverSource.Contains('panel.HoverStartedAt = Time.unscaledTime;') -and
             $updateEatenHoverSource.Contains('Time.unscaledTime - panel.HoverStartedAt >= HoverPanelShowDelay')) 'Switching eaten-food identities or icons must restart the normal hover delay.'
Assert-True ($updateEatenHoverSource.Contains('icon.isActiveAndEnabled && icon.gameObject.activeInHierarchy') -and
             $updateEatenHoverSource.Contains('IsHovered(icon, canHover: true)') -and
             $updateEatenHoverSource.Contains('panel.Root.gameObject.SetActive(visible);') -and
             $updateEatenHoverSource.Contains('context.EatenArrowRoot.gameObject.SetActive(visible);')) 'Only the hovered, currently visible eaten-food icon may show the panel and matching arrow.'
Assert-True ($updateEatenHoverSource.Contains('context.RecentHover.Root.gameObject.SetActive(false);') -and
             $updateEatenHoverSource.Contains('context.ChefHover.Root.gameObject.SetActive(false);')) 'The eaten-food panel must suppress overlapping recent-history and Chef guidance while visible.'
Assert-True ($updateEatenHoverSource.Contains('FoodRules.GetAppliedScale(player, state, hoveredFood)') -and
             $updateEatenHoverSource.Contains('DietConfig.GetBaseSlotScale(FoodSlotProgression.GetCurrentSlots(player, state))') -and
             $updateEatenHoverSource.Contains('FoodRules.GetFullCourseScale(player, state, FoodRules.CountActiveDietFoods(foods))') -and
             $updateEatenHoverSource.Contains('FoodRules.GetActiveFood(state, FoodIdentity.GetCanonicalPrefabName(hoveredFood))')) 'Eaten-food summary must use the applied snapshot divided by the current base slot scale, with live Full Course and consumed component metadata.'
Assert-True (-not $updateEatenHoverSource.Contains('PreviewNextFoodEffect(') -and
             -not $updateEatenHoverSource.Contains('FreshnessRuntime.') -and
             -not $updateEatenHoverSource.Contains('CalculateDiminishingScale(') -and
             -not $updateEatenHoverSource.Contains('.m_time') -and
             -not $calculateExtraEffectSource.Contains('.m_time')) 'Eaten-food hover must not forecast a new consumption or include vanilla time decay in its net-effect multiplier.'
Assert-True ($calculateExtraEffectSource.Contains('appliedScale / baseSlotScale * fullCourseScale')) 'The net-effect summary must remove only the configured slot baseline from AppliedScale.'
Assert-True ($updateEatenHoverSource.Contains('FineDiningLocalization.LocalizeOrFallback("$finedining_diet_extra_effect", "Net effect")')) 'Eaten-food hover must retain its localization key with the visible Net effect fallback.'
Assert-True ($disableEatenTooltipSource.Contains('icon.GetComponent<UITooltip>()') -and
             $disableEatenTooltipSource.Contains('if (CurrentTooltipField() == tooltip)') -and
             $disableEatenTooltipSource.Contains('UITooltip.HideTooltip();') -and
             $disableEatenTooltipSource.Contains('tooltip.enabled = false;')) 'Cursor-tooltip suppression must target the eaten icon tooltip and hide the global tooltip only when that exact component owns it.'
foreach ($removedHudTooltipMethod in @('GetOrCreateTooltip', 'UpdateTooltipHover', 'GetCurrentTooltip'))
{
    Assert-True ($null -eq $hudFoodPanelsType.GetMethod($removedHudTooltipMethod, [Reflection.BindingFlags] 'Static,Public,NonPublic')) "Removed cursor-following HUD tooltip helper must not return: $removedHudTooltipMethod"
}
$arrowCreateStart = $hudFoodPanelsSource.IndexOf('RectTransform arrowRoot = CreateRow(root, "EatenFoodArrow");', [StringComparison]::Ordinal)
$eatenPanelCreateStart = $hudFoodPanelsSource.IndexOf('HoverPanelContext eatenHover = CreateHoverPanel(', $arrowCreateStart, [StringComparison]::Ordinal)
Assert-True ($arrowCreateStart -ge 0 -and $eatenPanelCreateStart -gt $arrowCreateStart) 'Eaten-food arrow creation boundaries are missing.'
$arrowCreateSource = $hudFoodPanelsSource.Substring($arrowCreateStart, $eatenPanelCreateStart - $arrowCreateStart)
Assert-True ($arrowCreateSource.Contains('new RectTransform[3]') -and
             $arrowCreateSource.Contains('arrowRoot.sizeDelta = Vector2.zero;') -and
             $arrowCreateSource.Contains('typeof(Image)') -and
             $arrowCreateSource.Contains('line.color = Color.white;') -and
             $arrowCreateSource.Contains('line.raycastTarget = false;')) 'The eaten-food pointer must be a white three-segment arrow that does not intercept input.'
$layoutEatenArrowStart = $hudFoodPanelsSource.IndexOf('private static void LayoutEatenArrow(', [StringComparison]::Ordinal)
$layoutArrowSegmentStart = $hudFoodPanelsSource.IndexOf('private static void LayoutArrowSegment(', $layoutEatenArrowStart, [StringComparison]::Ordinal)
Assert-True ($layoutEatenArrowStart -ge 0 -and $layoutArrowSegmentStart -gt $layoutEatenArrowStart) 'Eaten-food arrow layout boundaries are missing.'
$layoutEatenArrowSource = $hudFoodPanelsSource.Substring($layoutEatenArrowStart, $layoutArrowSegmentStart - $layoutEatenArrowStart)
Assert-True ($updateEatenHoverSource.Contains('LayoutEatenArrow(context, hoveredIcon!);') -and
             $layoutEatenArrowSource.Contains('GetRectInParent(icon.rectTransform, root)') -and
             $layoutEatenArrowSource.Contains('GetRectInParent(context.EatenHover.Root, root)') -and
             [regex]::Matches($layoutEatenArrowSource, 'LayoutArrowSegment\(').Count -eq 3) 'Exactly one arrow must connect the current hovered icon to the fixed panel, with a two-part arrowhead.'

Assert-True ($hudFoodPanelsSource.Contains('UpdateFullCourseIndicator(context, hud, player);')) 'Full Course indicator updates must remain independent of the new fixed diet hover panels.'
$fullCourseStart = $hudFoodPanelsSource.IndexOf('private static void UpdateFullCourseIndicator(', [StringComparison]::Ordinal)
$fullCourseEnd = $hudFoodPanelsSource.IndexOf('private static bool ShouldRefreshChefCollection(', $fullCourseStart, [StringComparison]::Ordinal)
Assert-True ($fullCourseStart -ge 0 -and $fullCourseEnd -gt $fullCourseStart) 'Full Course indicator source boundaries are missing.'
$fullCourseSource = $hudFoodPanelsSource.Substring($fullCourseStart, $fullCourseEnd - $fullCourseStart)
Assert-True ((Get-Constant $hudFoodPanelsType 'FullCourseIconResourceName') -eq 'FineDining.Resources.UI.FullCourseIcon.png') 'Full Course must load its embedded tableware icon by the stable manifest resource ID.'
Assert-True ([Math]::Abs([float](Get-Constant $hudFoodPanelsType 'FullCourseIconSize') - 52) -lt 0.0001) 'Full Course must retain the legible 52px tableware icon size.'
Assert-True ([Math]::Abs([float](Get-Constant $hudFoodPanelsType 'FullCourseTooltipGap') - 6) -lt 0.0001) 'Full Course tooltip must remain six pixels from the icon.'
Assert-True ($fullCourseSource.Contains('icon.sprite = LoadFullCourseIconSprite();') -and
             $fullCourseSource.Contains('icon.preserveAspect = true;') -and
             $hudFoodPanelsSource.Contains('public Image Icon = null!;')) 'Full Course must render the embedded tableware sprite as an aspect-preserving Image.'
Assert-True ($fullCourseSource.Contains('background.color = Color.clear;')) 'The Full Course icon hit area must remain fully transparent behind the tableware sprite.'
Assert-True ([regex]::IsMatch(
    $fullCourseSource,
    '(?s)TextMeshProUGUI multiplier = CreateText\(.*?"Multiplier".*?TextAlignmentOptions\.Center')) 'Full Course multiplier must remain centered over the tableware icon.'
Assert-True (-not $fullCourseSource.Contains('multiplier.outlineWidth') -and
             -not $fullCourseSource.Contains('multiplier.outlineColor')) 'Full Course multiplier must not touch TMP outline material properties before the new text component is initialized.'
Assert-True ($fullCourseSource.Contains('Outline multiplierOutline = multiplier.gameObject.AddComponent<Outline>();') -and
             $fullCourseSource.Contains('multiplierOutline.effectColor = new Color(0f, 0f, 0f, 0.9f);') -and
             $fullCourseSource.Contains('multiplierOutline.useGraphicAlpha = true;')) 'Full Course multiplier must use the material-independent Unity UI Outline effect.'
Assert-True (-not $fullCourseSource.Contains('"Star"') -and
             -not $fullCourseSource.Contains('FullCourseStarColor') -and
             -not $fullCourseSource.Contains("HasCharacter('★'")) 'The replaced TMP star glyph must not return to the Full Course indicator.'
Assert-True ($fullCourseSource.Contains('CreateFullCourseTooltipPanel(parent, template)') -and
             $fullCourseSource.Contains('UpdateFullCourseTooltipPanel(indicator);') -and
             $fullCourseSource.Contains('LayoutFullCourseTooltipPanel(indicator);')) 'Full Course hover must use its dedicated fixed panel lifecycle.'
Assert-True (-not $fullCourseSource.Contains('UITooltip') -and
             -not $fullCourseSource.Contains('UpdateTooltipHover(') -and
             -not $fullCourseSource.Contains('GetOrCreateTooltip(')) 'Full Course must not return to the cursor-following UITooltip path.'

$fullCoursePanelStart = $fullCourseSource.IndexOf('private static HoverPanelContext CreateFullCourseTooltipPanel(', [StringComparison]::Ordinal)
$fullCoursePanelUpdateStart = $fullCourseSource.IndexOf('private static void UpdateFullCourseTooltipPanel(', $fullCoursePanelStart, [StringComparison]::Ordinal)
$fullCoursePanelLayoutStart = $fullCourseSource.IndexOf('private static void LayoutFullCourseTooltipPanel(', $fullCoursePanelUpdateStart, [StringComparison]::Ordinal)
$fullCourseIconLoadStart = $fullCourseSource.IndexOf('private static Sprite? LoadFullCourseIconSprite(', $fullCoursePanelLayoutStart, [StringComparison]::Ordinal)
Assert-True ($fullCoursePanelStart -ge 0 -and
             $fullCoursePanelUpdateStart -gt $fullCoursePanelStart -and
             $fullCoursePanelLayoutStart -gt $fullCoursePanelUpdateStart -and
             $fullCourseIconLoadStart -gt $fullCoursePanelLayoutStart) 'Full Course fixed-panel implementation boundaries are missing.'
$fullCoursePanelSource = $fullCourseSource.Substring($fullCoursePanelStart, $fullCoursePanelUpdateStart - $fullCoursePanelStart)
$fullCoursePanelUpdateSource = $fullCourseSource.Substring($fullCoursePanelUpdateStart, $fullCoursePanelLayoutStart - $fullCoursePanelUpdateStart)
$fullCoursePanelLayoutSource = $fullCourseSource.Substring($fullCoursePanelLayoutStart, $fullCourseIconLoadStart - $fullCoursePanelLayoutStart)
Assert-True ($fullCoursePanelSource.Contains('background.raycastTarget = false;') -and
             $fullCoursePanelSource.Contains('background.color = DefaultBackground;') -and
             $fullCoursePanelSource.Contains('TextAlignmentOptions.TopLeft')) 'Full Course fixed panel must be non-interactive and keep its localized copy top-left aligned.'
Assert-True ($fullCoursePanelUpdateSource.Contains('Time.unscaledTime - indicator.HoverStartedAt >= HoverPanelShowDelay')) 'Full Course fixed panel must wait for the standard half-second hover delay.'
Assert-True ($fullCoursePanelLayoutSource.Contains('float rightX = iconBounds.xMax + FullCourseTooltipGap;') -and
             $fullCoursePanelLayoutSource.Contains('float leftX = iconBounds.xMin - FullCourseTooltipGap - width;') -and
             $fullCoursePanelLayoutSource.Contains('? rightX') -and
             $fullCoursePanelLayoutSource.Contains(': leftX')) 'Full Course fixed panel must prefer the icon right side and fall back to its left when needed.'
Assert-True ([regex]::Matches($fullCoursePanelLayoutSource, 'Mathf\.Clamp\(').Count -ge 2 -and
             $fullCoursePanelLayoutSource.Contains('FullCourseTooltipCanvasMargin')) 'Full Course fixed panel must clamp both axes inside the root canvas.'
Assert-True ($fullCourseSource.Contains('localization.Localize("$finedining_diet_full_course_title")') -and
             $fullCourseSource.Contains('"$finedining_diet_full_course_description"')) 'Full Course fixed panel must retain its localized title and description.'
$formatStationDuration = Get-MethodRequired $stationTextType 'FormatDuration'
$formatStationSecondsCore = Get-MethodRequired $stationTextType 'FormatSecondsCore'
$colorizeStationTimer = Get-MethodRequired $stationTextType 'ColorizeTimer'
$formatFermenterMultiplier = Get-MethodRequired $fermenterHoverPatchType 'FormatMultiplier'
$parseGrimpyRecipe = Get-MethodRequired $valheimCuisineCompatibilityType 'TryParseGrimpyRecipe'
$calculateFreydisRemaining = Get-MethodRequired $valheimCuisineCompatibilityType 'CalculateFreydisRemainingSeconds'
Assert-True ([string]$formatStationDuration.Invoke($null, [object[]] @([double]0, $false)) -eq '00:00') 'A completed station duration must display only 00:00.'
Assert-True ([string]$formatStationDuration.Invoke($null, [object[]] @([double]0, $true)) -eq '00:01') 'An active sub-second station duration must keep a one-second minimum.'
Assert-True ([string]$formatStationDuration.Invoke($null, [object[]] @([double]65.1, $false)) -eq '01:06') 'A station duration must round up and omit its label.'
Assert-True ([string]$formatStationDuration.Invoke($null, [object[]] @([double]3661, $false)) -eq '1:01:01') 'An hour-long station duration must use h:mm:ss.'
Assert-True ([string]$formatStationSecondsCore.Invoke($null, [object[]] @([double]0, $false, '{0}s')) -eq '0s') 'A completed non-fermenter duration must display 0s.'
Assert-True ([string]$formatStationSecondsCore.Invoke($null, [object[]] @([double]0, $true, '{0}s')) -eq '1s') 'An active non-fermenter duration must keep a one-second minimum.'
Assert-True ([string]$formatStationSecondsCore.Invoke($null, [object[]] @([double]65.1, $false, '{0}s')) -eq '66s') 'A non-fermenter duration must round up to total seconds.'
Assert-True ([string]$formatStationSecondsCore.Invoke($null, [object[]] @([double]3661, $false, '{0}s')) -eq '3661s') 'A non-fermenter duration must not switch to clock formatting.'
Assert-True ([string]$formatStationSecondsCore.Invoke($null, [object[]] @([double]65.1, $false, '{0}초')) -eq '66초') 'The total-seconds formatter must accept a localized unit format.'
Assert-True ([string]::IsNullOrEmpty([string]$formatStationSecondsCore.Invoke($null, [object[]] @([double]-1, $false, '{0}s')))) 'A negative station duration must not be displayed.'
Assert-True ([string]::IsNullOrEmpty([string]$formatStationSecondsCore.Invoke($null, [object[]] @([double]::NaN, $false, '{0}s')))) 'A NaN station duration must not be displayed.'
Assert-True ([string]::IsNullOrEmpty([string]$formatStationSecondsCore.Invoke($null, [object[]] @([double]::PositiveInfinity, $false, '{0}s')))) 'An infinite station duration must not be displayed.'
Assert-True ([string]$colorizeStationTimer.Invoke($null, [object[]] @('42s')) -eq '<color=#FFD138>42s</color>') 'Station remaining time must use the shared gold markup.'
Assert-True ([string]::IsNullOrEmpty([string]$colorizeStationTimer.Invoke($null, [object[]] @('')))) 'An empty station timer must not create empty rich-text markup.'
Assert-True ([string]$formatFermenterMultiplier.Invoke($null, [object[]] @([float]1)) -eq 'x1') 'The neutral Fermenter speed must remain compact.'
Assert-True ([string]$formatFermenterMultiplier.Invoke($null, [object[]] @([float]1.044)) -eq 'x1.04') 'Fermenter speed must retain meaningful hundredths.'
Assert-True ([string]$formatFermenterMultiplier.Invoke($null, [object[]] @([float]4)) -eq 'x4') 'Whole Fermenter speeds must not gain trailing zeroes.'
$grimpyRecipeArguments = [object[]] @(' Honey : 10 : 2 ', '', 0, 0)
Assert-True ([bool]$parseGrimpyRecipe.Invoke($null, $grimpyRecipeArguments)) 'A valid whitespace-padded Grimpy recipe must parse.'
Assert-True ([string]$grimpyRecipeArguments[1] -eq 'Honey' -and [int]$grimpyRecipeArguments[2] -eq 10 -and [int]$grimpyRecipeArguments[3] -eq 2) 'A parsed Grimpy recipe must preserve prefab, required amount, and output amount.'
foreach ($invalidGrimpyRecipe in @(
    '',
    'Honey:10',
    'Honey:10:1:extra',
    ':10:1',
    'Honey:0:1',
    'Honey:-1:1',
    'Honey:1:0',
    'Honey:one:1',
    'Honey:999999999999:1'))
{
    $invalidGrimpyArguments = [object[]] @($invalidGrimpyRecipe, '', 0, 0)
    Assert-True (-not [bool]$parseGrimpyRecipe.Invoke($null, $invalidGrimpyArguments)) "Invalid Grimpy recipe must be rejected: '$invalidGrimpyRecipe'"
}
$freydisLastTicks = [long](100 * [TimeSpan]::TicksPerSecond)
$freydisNowTicks = [long](120 * [TimeSpan]::TicksPerSecond)
Assert-True ([Math]::Abs([double]$calculateFreydisRemaining.Invoke($null, [object[]] @([double]60, [double]10, $freydisLastTicks, $freydisNowTicks)) - 30) -lt 0.0001) 'Freydis remaining time must combine accumulated and server elapsed seconds.'
Assert-True ([Math]::Abs([double]$calculateFreydisRemaining.Invoke($null, [object[]] @([double]60, [double]50, $freydisLastTicks, $freydisNowTicks))) -lt 0.0001) 'A due Freydis collection must display zero remaining seconds.'
Assert-True ([Math]::Abs([double]$calculateFreydisRemaining.Invoke($null, [object[]] @([double]60, [double]10, $freydisNowTicks, $freydisLastTicks)) - 50) -lt 0.0001) 'A future Freydis checkpoint must not subtract time.'
Assert-True ([Math]::Abs([double]$calculateFreydisRemaining.Invoke($null, [object[]] @([double]60, [double]10, [long]0, $freydisNowTicks)) - 50) -lt 0.0001) 'A missing Freydis checkpoint must not invent elapsed time.'
Assert-True ([double]::IsNaN([double]$calculateFreydisRemaining.Invoke($null, [object[]] @([double]0, [double]10, $freydisLastTicks, $freydisNowTicks)))) 'An invalid Freydis collection interval must not produce a countdown.'
$stationTextFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
Assert-True ($null -eq $stationTextType.GetMethod('FormatTimer', $stationTextFlags)) 'The redundant labeled station timer formatter must not remain.'
Assert-True ($null -eq $stationTextType.GetField('TimerToken', $stationTextFlags)) 'The removed station timer localization token must not remain.'
Assert-True ($null -eq $stationTextType.GetProperty('TimerLabel', $stationTextFlags)) 'The removed station timer label must not remain.'
Assert-True ((Get-Constant $stationTextType 'TimerColorHex') -eq '#FFD138') 'The station remaining-time color must match running spoilage gold.'
Assert-True ((Get-Constant $stationTextType 'SecondsToken') -eq '$finedining_station_seconds') 'The station seconds token is incorrect.'
Assert-True ((Get-Constant $stationTextType 'AutoEjectToken') -eq '$finedining_station_auto_eject') 'The station auto-eject token is incorrect.'
Assert-True ((Get-Constant $stationTextType 'FermentationSpeedToken') -eq '$finedining_station_fermentation_speed') 'The Fermenter speed token is incorrect.'
Assert-True ((Get-Constant $stationTextType 'FermentationGuidanceToken') -eq '$finedining_station_fermentation_guidance') 'The Fermenter guidance token is incorrect.'
Assert-True ($null -ne $stationTextType.GetProperty('FermentationGuidanceLabel', $stationTextFlags)) 'The localized Fermenter guidance property is missing.'
$tryGetFermenterRemaining = Get-MethodRequired $environmentType 'TryGetRemainingSeconds'
$fermenterRemainingParameters = @($tryGetFermenterRemaining.GetParameters())
Assert-True ($fermenterRemainingParameters.Count -eq 3) 'Fermenter remaining-time resolution must also return the effective speed.'
Assert-True ($fermenterRemainingParameters[2].IsOut -and $fermenterRemainingParameters[2].ParameterType.GetElementType() -eq [float]) 'Fermenter remaining-time speed must be returned as an out float.'
$cookingProgressSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\CookingProgressResolver.cs') -Raw
$stationHoverTextSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\StationHoverTextPatches.cs') -Raw
$fermenterEnvironmentSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\FermenterEnvironmentSpeedSystem.cs') -Raw
$stationHintUiSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\StationHintUi.cs') -Raw
$stationModuleSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\StationModule.cs') -Raw
$stationInputResolverSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\StationInputResolver.cs') -Raw
$stationHoverPatchSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\StationHoverPatches.cs') -Raw
$valheimCuisineCompatibilitySource = Get-Content -LiteralPath (Join-Path $projectRoot 'Stations\ValheimCuisineCompatibility.cs') -Raw
$grimpyShowStart = $valheimCuisineCompatibilitySource.IndexOf('private static void ShowGrimpy(', [StringComparison]::Ordinal)
$freydisShowStart = $valheimCuisineCompatibilitySource.IndexOf('private static void ShowFreydis(', $grimpyShowStart, [StringComparison]::Ordinal)
$localizedCuisineItemStart = $valheimCuisineCompatibilitySource.IndexOf('private static string GetLocalizedItemName(', $freydisShowStart, [StringComparison]::Ordinal)
Assert-True ($grimpyShowStart -ge 0 -and $freydisShowStart -gt $grimpyShowStart -and $localizedCuisineItemStart -gt $freydisShowStart) 'Grimpy and Freydis compatibility source boundaries were not found.'
$grimpyCompatibilitySource = $valheimCuisineCompatibilitySource.Substring($grimpyShowStart, $freydisShowStart - $grimpyShowStart)
$freydisCompatibilitySource = $valheimCuisineCompatibilitySource.Substring($freydisShowStart, $localizedCuisineItemStart - $freydisShowStart)
Assert-True (-not $grimpyCompatibilitySource.Contains('ColorizeTimer(') -and -not $grimpyCompatibilitySource.Contains('FormatDuration(') -and -not $grimpyCompatibilitySource.Contains('FormatSeconds(')) 'Grimpy Box recipe cards must remain timerless and must not receive invented remaining-time markup.'
Assert-True ($freydisCompatibilitySource.Contains('StationText.ColorizeTimer(duration)')) 'Freydis must color only its projected remaining duration gold.'
Assert-True ($freydisCompatibilitySource.Contains('"Stored $1/$2 · next collection $3"') -and -not $freydisCompatibilitySource.Contains('next collection <color=')) 'Freydis fallback text must leave labels uncolored and accept an already-colorized duration.'
$iconScaleConfigStart = $stationModuleSource.IndexOf('IconGroupScale = config.Bind(', [StringComparison]::Ordinal)
$cookingRowsConfigStart = $stationModuleSource.IndexOf('CookingStationRows = config.Bind(', $iconScaleConfigStart, [StringComparison]::Ordinal)
Assert-True ($iconScaleConfigStart -ge 0 -and $cookingRowsConfigStart -gt $iconScaleConfigStart) 'Station Icon Scale config source boundaries were not found.'
$iconScaleConfigSource = $stationModuleSource.Substring($iconScaleConfigStart, $cookingRowsConfigStart - $iconScaleConfigStart)
Assert-True ([regex]::IsMatch($iconScaleConfigSource, '(?s)"Station Icon Scale",\s*1f,\s*ConfigPresentation\.Client\(')) 'Station Icon Scale must default to x1.00.'
$stationHintCreateStart = $stationHintUiSource.IndexOf('private void Create(', [StringComparison]::Ordinal)
$stationHintCreateEnd = $stationHintUiSource.IndexOf('private static GameObject CreateGrid(', $stationHintCreateStart, [StringComparison]::Ordinal)
Assert-True ($stationHintCreateStart -ge 0 -and $stationHintCreateEnd -gt $stationHintCreateStart) 'Station hint creation source boundaries were not found.'
$stationHintCreateSource = $stationHintUiSource.Substring($stationHintCreateStart, $stationHintCreateEnd - $stationHintCreateStart)
Assert-True ($stationHintCreateSource.Contains('emphasizeStatusText: false') -and $stationHintCreateSource.Contains('emphasizeStatusText: true')) 'Only CookingStation progress elements must opt into emphasized status text.'
$stationHintElementStart = $stationHintUiSource.IndexOf('private sealed class Element', [StringComparison]::Ordinal)
Assert-True ($stationHintElementStart -ge 0) 'Station hint element source boundary was not found.'
$stationHintElementSource = $stationHintUiSource.Substring($stationHintElementStart)
Assert-True ($stationHintElementSource.Contains('bool emphasizeStatusText')) 'Station hint elements must receive an explicit status-text emphasis flag.'
Assert-True ($stationHintElementSource.Contains('float statusTextScale = emphasizeStatusText ? CookingProgressTextScale : 1f;')) 'Station hint status-text emphasis must fall back to x1.00 outside CookingStation progress cards.'
foreach ($scaledProgressStatusContract in @(
    '_status.fontSize = 12f * statusTextScale;',
    '_status.fontSizeMin = 7f * statusTextScale;',
    '_status.fontSizeMax = 12f * statusTextScale;',
    '-1f * statusTextScale',
    '24f * statusTextScale',
    '_secondaryStatus.fontSize = 10f * statusTextScale;',
    '_secondaryStatus.fontSizeMin = 7f * statusTextScale;',
    '_secondaryStatus.fontSizeMax = 10f * statusTextScale;',
    '20f * statusTextScale',
    '-19f * statusTextScale'))
{
    Assert-True ($stationHintElementSource.Contains($scaledProgressStatusContract)) "CookingStation progress status sizing is missing: $scaledProgressStatusContract"
}
Assert-True (-not [regex]::IsMatch($stationHintElementSource, '(?m)_label\.[^;\r\n]*statusTextScale|_icon\.[^;\r\n]*statusTextScale')) 'CookingStation progress emphasis must not enlarge card icons or item names.'
$smelterHoverStart = $stationHoverTextSource.IndexOf('internal static class StationSmelterHoverTimePatch', [StringComparison]::Ordinal)
$fermenterHoverStart = $stationHoverTextSource.IndexOf('internal static class StationFermenterHoverTimePatch', [StringComparison]::Ordinal)
$stationHoverTimeStart = $stationHoverTextSource.IndexOf('internal static class StationHoverTime', [StringComparison]::Ordinal)
Assert-True ($smelterHoverStart -ge 0 -and $fermenterHoverStart -gt $smelterHoverStart -and $stationHoverTimeStart -gt $fermenterHoverStart) 'Station hover formatter sections were not found.'
$smelterHoverSource = $stationHoverTextSource.Substring($smelterHoverStart, $fermenterHoverStart - $smelterHoverStart)
$fermenterHoverSource = $stationHoverTextSource.Substring($fermenterHoverStart, $stationHoverTimeStart - $fermenterHoverStart)
Assert-True ($cookingProgressSource.Contains('StationText.FormatSeconds(') -and -not $cookingProgressSource.Contains('StationText.FormatDuration(')) 'CookingStation progress must use total seconds only.'
Assert-True ($smelterHoverSource.Contains('StationText.FormatSeconds(') -and -not $smelterHoverSource.Contains('StationText.FormatDuration(')) 'Smelter and Windmill hover must use total seconds only.'
Assert-True ($smelterHoverSource.Contains('keepAtLeastOneSecond: true')) 'An active Smelter or Windmill queue must never display 0 seconds.'
Assert-True ($smelterHoverSource.Contains('StationText.ColorizeTimer(') -and ([regex]::Matches($smelterHoverSource, 'StationText\.ColorizeTimer\(')).Count -eq 1) 'Smelter and Windmill must color only their inserted remaining-seconds value gold.'
Assert-True ($fermenterHoverSource.Contains('StationText.FormatDuration(') -and -not $fermenterHoverSource.Contains('StationText.FormatSeconds(')) 'Fermenter hover must retain clock-formatted durations.'
Assert-True ($fermenterHoverSource.Contains('StationText.FermentationSpeedLabel') -and $fermenterHoverSource.Contains('FormatMultiplier(speedMultiplier)')) 'Fermenter hover must append its effective speed beside the countdown.'
Assert-True ($fermenterHoverSource.Contains('$"{environment.DepthMeters.ToString("0.0", CultureInfo.InvariantCulture)} m') -and -not $fermenterHoverSource.Contains('MaximumDepthMeters')) 'Fermenter hover depth must show only the current depth in metres without a maximum-depth denominator.'
Assert-True ($fermenterHoverSource.Contains('detailText += "\n" + ColorizeDetailLine(') -and $fermenterHoverSource.Contains('StationText.FermentationGuidanceLabel);') -and -not $fermenterHoverSource.Contains('$" · {StationText.FermentationGuidanceLabel})"')) 'Fermenter hover must place the localized environment guidance on its own row below the countdown.'
Assert-True ($fermenterHoverSource.Contains('string timerText = StationText.ColorizeTimer(duration);') -and $fermenterHoverSource.Contains('StationText.ColorizeTimer(FormatMultiplier(speedMultiplier))')) 'Fermenter must color its duration and only the combined speed-multiplier value gold.'
Assert-True (([regex]::Matches($fermenterHoverSource, 'StationText\.ColorizeTimer\(')).Count -eq 3) 'Fermenter hover must color the excluded timer once and the normal timer plus combined speed once each, without coloring environment details.'
Assert-True ($fermenterHoverSource.Contains('private const string DetailColor = "orange";') -and $fermenterHoverSource.Contains('ColorizeDetailLine(timerText)')) 'Fermenter timer labels and environment details must retain their outer orange styling.'
Assert-True (-not [regex]::IsMatch($fermenterHoverSource, 'ColorizeTimer\(FormatMultiplier\((cover|depth)Multiplier\)\)')) 'Fermenter cover and depth rate values must remain orange.'
Assert-True ($fermenterEnvironmentSource.Contains('speedMultiplier = Math.Max(1f, 1f + bonusRate);') -and $fermenterEnvironmentSource.Contains('/ speedMultiplier);')) 'The displayed Fermenter speed must be the same multiplier used by the remaining-time calculation.'
$excludedHoverCheckIndex = $fermenterHoverSource.IndexOf('StationModule.IsFermenterBonusExcluded(__instance)', [StringComparison]::Ordinal)
$excludedHoverEnvironmentIndex = $fermenterHoverSource.IndexOf('FermenterEnvironmentSpeedSystem.GetEnvironmentStatus(__instance)', [StringComparison]::Ordinal)
$excludedHoverReturnIndex = if ($excludedHoverCheckIndex -ge 0) {
    $fermenterHoverSource.IndexOf('return;', $excludedHoverCheckIndex, [StringComparison]::Ordinal)
} else {
    -1
}
Assert-True ($excludedHoverCheckIndex -ge 0 -and
             $excludedHoverReturnIndex -gt $excludedHoverCheckIndex -and
             $excludedHoverEnvironmentIndex -gt $excludedHoverReturnIndex) 'An excluded Fermenter must finish its time-only hover branch before environment details are resolved.'
$excludedHoverBranchSource = $fermenterHoverSource.Substring(
    $excludedHoverCheckIndex,
    $excludedHoverEnvironmentIndex - $excludedHoverCheckIndex)
Assert-True ($excludedHoverBranchSource.Contains('FermenterEnvironmentSpeedSystem.TryGetRemainingSeconds(') -and
             $excludedHoverBranchSource.Contains('StationText.FormatDuration(') -and
             $excludedHoverBranchSource.Contains('StationText.ColorizeTimer(') -and
             $excludedHoverBranchSource.Contains('InsertAfterFirstLine(')) 'An excluded Fermenter hover must still insert its gold remaining-time clock.'
Assert-True ([regex]::Matches($excludedHoverBranchSource, 'StationText\.ColorizeTimer\(').Count -eq 1) 'Excluded Fermenter hover must color only its remaining-time value.'
foreach ($forbiddenExcludedHoverDetail in @(
    'GetEnvironmentStatus(',
    'StationText.CoverLabel',
    'StationText.DepthLabel',
    'StationText.FermentationSpeedLabel',
    'StationText.FermentationGuidanceLabel',
    'FormatMultiplier('))
{
    Assert-True (-not $excludedHoverBranchSource.Contains($forbiddenExcludedHoverDetail)) "Excluded Fermenter hover must remain time-only: $forbiddenExcludedHoverDetail"
}
$checkpointOwnerStart = $fermenterEnvironmentSource.IndexOf('internal static void CheckpointOwner(', [StringComparison]::Ordinal)
$checkpointAllOwnersStart = $fermenterEnvironmentSource.IndexOf('internal static void CheckpointAllOwners(', $checkpointOwnerStart, [StringComparison]::Ordinal)
$notifyBatchStartedStart = $fermenterEnvironmentSource.IndexOf('internal static void NotifyBatchStartedOrReset(', [StringComparison]::Ordinal)
$notifyBatchClearedStart = $fermenterEnvironmentSource.IndexOf('internal static void NotifyBatchCleared(', $notifyBatchStartedStart, [StringComparison]::Ordinal)
$projectEffectiveElapsedStart = $fermenterEnvironmentSource.IndexOf('internal static double ProjectEffectiveElapsed(', [StringComparison]::Ordinal)
$tryGetRemainingStart = $fermenterEnvironmentSource.IndexOf('internal static bool TryGetRemainingSeconds(', $projectEffectiveElapsedStart, [StringComparison]::Ordinal)
$getEnvironmentStatusStart = $fermenterEnvironmentSource.IndexOf('internal static EnvironmentStatus GetEnvironmentStatus(', $tryGetRemainingStart, [StringComparison]::Ordinal)
$getCurrentBonusRateStart = $fermenterEnvironmentSource.IndexOf('private static float GetCurrentBonusRate(', $getEnvironmentStatusStart, [StringComparison]::Ordinal)
$tryGetDepthStart = $fermenterEnvironmentSource.IndexOf('private static bool TryGetDepthMeters(', $getCurrentBonusRateStart, [StringComparison]::Ordinal)
$projectPendingBonusStart = $fermenterEnvironmentSource.IndexOf('private static long ProjectPendingBonusTicks(', [StringComparison]::Ordinal)
$maximumBonusTicksStart = $fermenterEnvironmentSource.IndexOf('private static long GetMaximumTotalBonusTicks(', $projectPendingBonusStart, [StringComparison]::Ordinal)
Assert-True ($checkpointOwnerStart -ge 0 -and $checkpointAllOwnersStart -gt $checkpointOwnerStart -and
             $notifyBatchStartedStart -ge 0 -and $notifyBatchClearedStart -gt $notifyBatchStartedStart -and
             $projectEffectiveElapsedStart -ge 0 -and $tryGetRemainingStart -gt $projectEffectiveElapsedStart -and
             $getEnvironmentStatusStart -gt $tryGetRemainingStart -and
             $getCurrentBonusRateStart -gt $getEnvironmentStatusStart -and $tryGetDepthStart -gt $getCurrentBonusRateStart -and
             $projectPendingBonusStart -ge 0 -and $maximumBonusTicksStart -gt $projectPendingBonusStart) 'Fermenter environment exclusion source boundaries were not found.'
$checkpointOwnerSource = $fermenterEnvironmentSource.Substring($checkpointOwnerStart, $checkpointAllOwnersStart - $checkpointOwnerStart)
$notifyBatchStartedSource = $fermenterEnvironmentSource.Substring($notifyBatchStartedStart, $notifyBatchClearedStart - $notifyBatchStartedStart)
$projectEffectiveElapsedSource = $fermenterEnvironmentSource.Substring($projectEffectiveElapsedStart, $tryGetRemainingStart - $projectEffectiveElapsedStart)
$tryGetRemainingSource = $fermenterEnvironmentSource.Substring($tryGetRemainingStart, $getEnvironmentStatusStart - $tryGetRemainingStart)
$getCurrentBonusRateSource = $fermenterEnvironmentSource.Substring($getCurrentBonusRateStart, $tryGetDepthStart - $getCurrentBonusRateStart)
$projectPendingBonusSource = $fermenterEnvironmentSource.Substring($projectPendingBonusStart, $maximumBonusTicksStart - $projectPendingBonusStart)
Assert-True ($checkpointOwnerSource.Contains('StationModule.IsFermenterBonusExcluded(fermenter)') -and
             [regex]::IsMatch($checkpointOwnerSource, '(?s)IsFermenterBonusExcluded\(fermenter\).*?currentBonusRate\s*=.*?0f')) 'Owner checkpoints must persist a neutral future bonus rate for excluded Fermenters.'
Assert-True ($notifyBatchStartedSource.Contains('StationModule.IsFermenterBonusExcluded(fermenter)') -and
             $notifyBatchStartedSource.Contains('ClearState(') -and
             $notifyBatchStartedSource.IndexOf('ClearState(', [StringComparison]::Ordinal) -lt $notifyBatchStartedSource.IndexOf('WriteState(', [StringComparison]::Ordinal)) 'A new excluded Fermenter batch must clear FineDining environment state instead of creating it.'
Assert-True ($projectPendingBonusSource.Contains('StationModule.IsFermenterBonusExcluded(fermenter)') -and
             [regex]::IsMatch($projectPendingBonusSource, '(?s)IsFermenterBonusExcluded\(fermenter\).*?return 0L;')) 'Excluded Fermenters must never accrue future bonus ticks from a persisted rate.'
Assert-True ($projectEffectiveElapsedSource.Contains('snapshot.AccumulatedBonusTicks') -and
             $projectEffectiveElapsedSource.Contains('ProjectPendingBonusTicks(')) 'Switching a Fermenter into the exclusion list must preserve already-earned elapsed bonus while stopping future accrual.'
Assert-True ($tryGetRemainingSource.Contains('StationModule.IsFermenterBonusExcluded(fermenter)') -and
             [regex]::IsMatch($tryGetRemainingSource, '(?s)IsFermenterBonusExcluded\(fermenter\).*?speedMultiplier\s*=.*?1f')) 'Excluded Fermenter remaining time must report neutral x1 speed.'
Assert-True ($getCurrentBonusRateSource.Contains('StationModule.IsFermenterBonusExcluded(fermenter)') -and
             $getCurrentBonusRateSource.IndexOf('StationModule.IsFermenterBonusExcluded(fermenter)', [StringComparison]::Ordinal) -lt $getCurrentBonusRateSource.IndexOf('GetEnvironmentStatus(fermenter)', [StringComparison]::Ordinal)) 'Excluded Fermenters must bypass cover and depth bonus calculation.'
$showFermenterStart = $stationInputResolverSource.IndexOf('internal static void ShowFermenter(', [StringComparison]::Ordinal)
$stationInputCanShowStart = $stationInputResolverSource.IndexOf('private static bool CanShow(', $showFermenterStart, [StringComparison]::Ordinal)
Assert-True ($showFermenterStart -ge 0 -and $stationInputCanShowStart -gt $showFermenterStart) 'Fermenter input-icon source boundaries were not found.'
$showFermenterSource = $stationInputResolverSource.Substring($showFermenterStart, $stationInputCanShowStart - $showFermenterStart)
Assert-True ($showFermenterSource.Contains('ShowFor("FermenterInput", fermenter, inputs, max);') -and
             -not $showFermenterSource.Contains('IsFermenterBonusExcluded')) 'Fermenter exclusion must not suppress the normal available-input icon path.'
$stationHintCrosshairPatchType = Get-TypeRequired $assembly 'FineDining.StationHintHudCrosshairPatch'
$resolveSwitchTarget = Get-MethodRequired $stationHintCrosshairPatchType 'ResolveSwitchTarget'
Assert-True ($resolveSwitchTarget.ReturnType.FullName -eq 'UnityEngine.Component') 'Station switch routing must resolve one shared component target.'
Assert-True ($resolveSwitchTarget.GetParameters().Count -eq 1 -and $resolveSwitchTarget.GetParameters()[0].ParameterType.FullName -eq 'Switch') 'Station switch routing must accept the selected vanilla Switch.'
Assert-True ($stationHintUiSource.Contains('_status.color = new Color32(255, 165, 0, 255);')) 'Uncolored station-card status text, including Grimpy recipe amounts, must retain its base orange color.'
Assert-True ($stationHintUiSource.Contains('_secondaryStatus.color = new Color32(159, 232, 112, 255);')) 'CookingStation auto-eject status must use the positive green color.'
Assert-True ($stationHintUiSource.Contains('candidate.SecondaryStatusText')) 'CookingStation progress UI must render the secondary status line.'
foreach ($rowKey in @(
    'Station Icon Rows - Cooking Station',
    'Station Icon Rows - Smelter',
    'Station Icon Rows - Windmill',
    'Station Icon Rows - Fermenter',
    'Station Icon Rows - Grimpy Box'))
{
    Assert-True ($stationModuleSource.Contains('"' + $rowKey + '"')) "Station row config key is missing: $rowKey"
}
Assert-True (-not $stationModuleSource.Contains('"Enable",')) 'The always-enabled station display must not retain its old Enable config binding.'
Assert-True ($stationModuleSource.Contains('new AcceptableValueRange<int>(0, MaxHintRows)')) 'Station row configs must use the shared 0..4 range.'
Assert-True ($stationHintUiSource.Contains('layout.constraintCount = StationModule.HintColumns;') -and $stationHintUiSource.Contains('int maximumRows = StationModule.MaxHintRows;')) 'Station hint layout must share the five-column, four-row contract.'
Assert-True ($stationInputResolverSource.Contains('max - progressRows * StationModule.HintColumns')) 'CookingStation input cards must use only rows left after progress cards.'
Assert-True ($cookingProgressSource.Contains('for (int slot = 0; slot < station.m_slots.Length; slot++)') -and $cookingProgressSource.Contains('candidates.Count >= maxCandidates')) 'Cooking progress must scan past skipped slots while respecting the configured card budget.'
Assert-True (-not $smelterHoverSource.Contains('StationModule.SmelterRows') -and -not $smelterHoverSource.Contains('StationModule.WindmillRows')) 'Smelter and Windmill row settings must hide icons without hiding time text.'
Assert-True (-not $fermenterHoverSource.Contains('StationModule.FermenterRows')) 'Fermenter row settings must hide icons without hiding environment text.'
Assert-True (-not $stationModuleSource.Contains('Station Hint Diagnostics')) 'The removed station diagnostics config binding must not remain.'
Assert-True (-not $stationInputResolverSource.Contains('StationModule.Diagnostics') -and -not $stationInputResolverSource.Contains('LogGate(') -and -not $stationInputResolverSource.Contains('CandidateBuildResult')) 'Diagnostic-only station candidate accounting and logging must not remain.'
Assert-True (-not $stationHoverPatchSource.Contains('LogDiagnostics(') -and -not $stationHoverPatchSource.Contains('[Station Diagnostics]')) 'Diagnostic-only HUD hover inspection must not remain.'
Assert-True ($stationModuleSource.Contains('ValheimCuisineCompatibility.Initialize();') -and $stationModuleSource.Contains('ValheimCuisineCompatibility.Shutdown();')) 'Station lifecycle must initialize and release ValheimCuisine compatibility.'
Assert-True ($stationHoverPatchSource.Contains('ValheimCuisineCompatibility.TryShow(')) 'The HUD crosshair path must dispatch optional ValheimCuisine hover information.'
Assert-True ($valheimCuisineCompatibilitySource.Contains('zdo.GetInt(ZDOVars.s_level, 0)') -and $valheimCuisineCompatibilitySource.Contains('zdo.GetFloat(ZDOVars.s_product, 0f)') -and $valheimCuisineCompatibilitySource.Contains('zdo.GetLong(ZDOVars.s_lastTime, nowTicks)')) 'Freydis hover timing must read the authoritative ZDO state directly.'
foreach ($forbiddenMutation in @('GetTimeSinceLastUpdate(', 'zdo.Set(', 'ClaimOwnership(', 'InvokeRPC('))
{
    Assert-True (-not $valheimCuisineCompatibilitySource.Contains($forbiddenMutation)) "ValheimCuisine hover compatibility must remain read-only: $forbiddenMutation"
}
Assert-True ($valheimCuisineCompatibilitySource.Contains('hoverable is Container container') -and $valheimCuisineCompatibilitySource.Contains('container.GetComponent(_grimpyConverterType)')) 'Grimpy compatibility must require the converter on the selected Container itself.'
Assert-True ($valheimCuisineCompatibilitySource.Contains('countAsContained: true') -and $valheimCuisineCompatibilitySource.Contains('countAsContained: false')) 'Grimpy candidates must combine box and player items while counting only box contents toward the recipe.'
Assert-True ($valheimCuisineCompatibilitySource.Contains('recipe.RequiredAmount.ToString') -and $valheimCuisineCompatibilitySource.Contains('recipe.ProducedAmount.ToString')) 'Grimpy candidate status must show required and produced amounts.'
Assert-True ($valheimCuisineCompatibilitySource.Contains('StationModule.GetHintLimit(') -and $valheimCuisineCompatibilitySource.Contains('StationModule.GrimpyBoxRows.Value')) 'Grimpy Box cards must follow their dedicated row setting.'
Assert-True ($valheimCuisineCompatibilitySource.Contains('_cachedGrimpyMaxHints == maxHints') -and $valheimCuisineCompatibilitySource.Contains('Time.unscaledTime + CandidateCacheSeconds')) 'Grimpy candidate work must include the row-derived limit in its short cache key.'
Assert-True (-not $valheimCuisineCompatibilitySource.Contains('AzuCraftyBoxesCompatibility')) 'Grimpy Box hints must not imply unsupported nearby-container pulling.'
$stationHintCandidateType = Get-TypeRequired $assembly 'FineDining.StationHintCandidate'
Assert-True ($null -ne $stationHintCandidateType.GetProperty('SecondaryStatusText', [Reflection.BindingFlags] 'Instance,Public,NonPublic')) 'Station hint candidates must carry a secondary status line.'
Assert-True ($cookingProgressSource.Contains('CookingStationAutoPopSystem.ShouldShowAutoEject(')) 'CookingStation progress must delegate auto-eject display policy to the auto-pop system.'
$autoEjectDecisionIndex = $cookingProgressSource.IndexOf('bool showAutoEject =', [StringComparison]::Ordinal)
$cookingTimerColorIndex = $cookingProgressSource.IndexOf('timerText = StationText.ColorizeTimer(timerText);', [StringComparison]::Ordinal)
$cookingCandidateIndex = $cookingProgressSource.IndexOf('StationHintCandidate? candidate = CreateCandidate(', [StringComparison]::Ordinal)
Assert-True ($autoEjectDecisionIndex -ge 0 -and $autoEjectDecisionIndex -lt $cookingTimerColorIndex -and $cookingTimerColorIndex -lt $cookingCandidateIndex) 'CookingStation must decide Auto eject from the raw countdown, then color only the countdown before creating its card.'
Assert-True (([regex]::Matches($cookingProgressSource, 'StationText\.ColorizeTimer\(')).Count -eq 1 -and -not $cookingProgressSource.Contains('ColorizeTimer(StationText.AutoEjectLabel)')) 'CookingStation Auto eject text must remain outside gold timer markup.'
Assert-True (-not $cookingProgressSource.Contains('TryReadPlan(') -and -not $cookingProgressSource.Contains('IsAutoPopPlanEligible(')) 'CookingStation progress must not interpret persisted auto-pop plans itself.'
Assert-True ($cookingProgressSource.Contains('status != CookingStatus.Burnt')) 'Burnt CookingStation slots must never display auto-eject.'

$shouldAutoPop = Get-MethodRequired $autoPopCoreType 'ShouldAutoPop'
$hasDistinctOvercookStage = Get-MethodRequired $autoPopCoreType 'HasDistinctOvercookStage'
$clampSkillFactor = Get-MethodRequired $autoPopCoreType 'ClampSkillFactor'
$clampBonusCount = Get-MethodRequired $autoPopCoreType 'ClampBonusCount'
Assert-True (-not [bool]$shouldAutoPop.Invoke($null, [object[]] @([float]0, [float]0, $true))) 'Cooking level zero must never auto-pop.'
Assert-True ([bool]$shouldAutoPop.Invoke($null, [object[]] @([float]0.5, [float]0.499999, $true))) 'Cooking level 50 must auto-pop below the 50% boundary.'
Assert-True (-not [bool]$shouldAutoPop.Invoke($null, [object[]] @([float]0.5, [float]0.5, $true))) 'Cooking level 50 must not auto-pop at the exclusive 50% boundary.'
Assert-True ([bool]$shouldAutoPop.Invoke($null, [object[]] @([float]1, [float]1, $true))) 'Cooking level 100 must guarantee auto-pop even when the random value is one.'
Assert-True (-not [bool]$shouldAutoPop.Invoke($null, [object[]] @([float]::NaN, [float]0, $true))) 'NaN Cooking skill must not auto-pop.'
Assert-True (-not [bool]$shouldAutoPop.Invoke($null, [object[]] @([float]1, [float]0, $false))) 'A no-overcook conversion must never auto-pop, even at Cooking level 100.'
Assert-True ([bool]$hasDistinctOvercookStage.Invoke($null, [object[]] @([float]30, 'CookedMeat', 'Coal'))) 'A finite cooked-to-burnt conversion must allow auto-pop.'
Assert-True (-not [bool]$hasDistinctOvercookStage.Invoke($null, [object[]] @([float]30, 'CookedMeat', ''))) 'A station without an overcooked output must reject auto-pop.'
Assert-True (-not [bool]$hasDistinctOvercookStage.Invoke($null, [object[]] @([float]30, 'CookedMeat', 'CookedMeat'))) 'A conversion without a distinct burnt stage must reject auto-pop.'
Assert-True (-not [bool]$hasDistinctOvercookStage.Invoke($null, [object[]] @([float]30, 'Coal', 'Ash'))) 'A conversion whose cooked output is already Coal must reject auto-pop.'
foreach ($invalidCookTime in @([float]0, [float]-1, [float]::NaN, [float]::PositiveInfinity))
{
    Assert-True (-not [bool]$hasDistinctOvercookStage.Invoke($null, [object[]] @($invalidCookTime, 'CookedMeat', 'Coal'))) 'A non-positive or non-finite cook time must reject auto-pop.'
}
Assert-True ([Math]::Abs([float]$clampSkillFactor.Invoke($null, [object[]] @([float]0.5)) - 0.5) -lt 0.0001) 'Cooking skill factor must preserve an in-range value.'
Assert-True ([float]$clampSkillFactor.Invoke($null, [object[]] @([float]::NaN)) -eq 0) 'NaN Cooking skill must clamp to zero.'
Assert-True ([float]$clampSkillFactor.Invoke($null, [object[]] @([float]2)) -eq 1) 'Cooking skill above level 100 must clamp to one.'
Assert-True ([int]$clampBonusCount.Invoke($null, [object[]] @(-1)) -eq 0) 'Negative persisted CookingStation bonuses must clamp to zero.'
Assert-True ([int]$clampBonusCount.Invoke($null, [object[]] @(2147483647)) -eq 1) 'Corrupt CookingStation bonuses must clamp to one extra item.'

$autoPopSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\CookingStationAutoPopPatches.cs') -Raw
$registerRpcsStart = $autoPopSource.IndexOf('internal static void RegisterRpcs(', [StringComparison]::Ordinal)
$requestAddBoundary = $autoPopSource.IndexOf('internal static void RequestAdd(', $registerRpcsStart, [StringComparison]::Ordinal)
$requestAddStart = $autoPopSource.IndexOf('internal static void RequestAdd(', [StringComparison]::Ordinal)
$raiseExperienceStart = $autoPopSource.IndexOf('internal static void RaiseAddExperienceOrDefer(', $requestAddStart, [StringComparison]::Ordinal)
$receivePlanStart = $autoPopSource.IndexOf('private static void ReceivePlan(', [StringComparison]::Ordinal)
$takePendingStart = $autoPopSource.IndexOf('private static bool TryTakePendingPlan(', $receivePlanStart, [StringComparison]::Ordinal)
$processSlotsStart = $autoPopSource.IndexOf('internal static void ProcessCompletedSlots(', [StringComparison]::Ordinal)
$receivePlanBoundary = $autoPopSource.IndexOf('private static void ReceivePlan(', $processSlotsStart, [StringComparison]::Ordinal)
$tryAutoPopStart = $autoPopSource.IndexOf('private static void TryAutoPopSlot(', [StringComparison]::Ordinal)
$broadcastBonusEffectStart = $autoPopSource.IndexOf('private static void BroadcastBonusEffect(', $tryAutoPopStart, [StringComparison]::Ordinal)
$receiveBonusEffectStart = $autoPopSource.IndexOf('private static void ReceiveBonusEffect(', $broadcastBonusEffectStart, [StringComparison]::Ordinal)
$forceClearStart = $autoPopSource.IndexOf('private static void ForceClearSlot(', $tryAutoPopStart, [StringComparison]::Ordinal)
$logBonusEffectFailureStart = $autoPopSource.IndexOf('private static void LogBonusEffectFailure(', $forceClearStart, [StringComparison]::Ordinal)
$registrationMarkerStart = $autoPopSource.IndexOf('private sealed class RegistrationMarker', $logBonusEffectFailureStart, [StringComparison]::Ordinal)
Assert-True ($registerRpcsStart -ge 0 -and $requestAddBoundary -gt $registerRpcsStart) 'CookingStation RPC-registration source section was not found.'
Assert-True ($requestAddStart -ge 0 -and $raiseExperienceStart -gt $requestAddStart) 'CookingStation request-add source section was not found.'
Assert-True ($receivePlanStart -ge 0 -and $takePendingStart -gt $receivePlanStart) 'CookingStation owner-plan source section was not found.'
Assert-True ($processSlotsStart -ge 0 -and $receivePlanBoundary -gt $processSlotsStart) 'CookingStation completion source section was not found.'
Assert-True ($tryAutoPopStart -ge 0 -and
             $broadcastBonusEffectStart -gt $tryAutoPopStart -and
             $receiveBonusEffectStart -gt $broadcastBonusEffectStart -and
             $forceClearStart -gt $receiveBonusEffectStart) 'CookingStation auto-pop feedback source sections were not found.'
Assert-True ($logBonusEffectFailureStart -gt $forceClearStart -and $registrationMarkerStart -gt $logBonusEffectFailureStart) 'CookingStation bonus-effect failure source section was not found.'
$registerRpcsSource = $autoPopSource.Substring($registerRpcsStart, $requestAddBoundary - $registerRpcsStart)
$requestAddSource = $autoPopSource.Substring($requestAddStart, $raiseExperienceStart - $requestAddStart)
$receivePlanSource = $autoPopSource.Substring($receivePlanStart, $takePendingStart - $receivePlanStart)
$processSlotsSource = $autoPopSource.Substring($processSlotsStart, $receivePlanBoundary - $processSlotsStart)
$tryAutoPopSource = $autoPopSource.Substring($tryAutoPopStart, $broadcastBonusEffectStart - $tryAutoPopStart)
$broadcastBonusEffectSource = $autoPopSource.Substring($broadcastBonusEffectStart, $receiveBonusEffectStart - $broadcastBonusEffectStart)
$receiveBonusEffectSource = $autoPopSource.Substring($receiveBonusEffectStart, $forceClearStart - $receiveBonusEffectStart)
$logBonusEffectFailureSource = $autoPopSource.Substring($logBonusEffectFailureStart, $registrationMarkerStart - $logBonusEffectFailureStart)
Assert-True ($requestAddSource.Contains('CanAutoPopConversion(')) 'The inserting client must gate auto-pop before prepaying collection experience.'
Assert-True ($receivePlanSource.Contains('CanAutoPopConversion(station, conversion)')) 'The station owner must independently gate persisted auto-pop plans.'
Assert-True ($processSlotsSource.Contains('!IsAutoPopPlanEligible(station, plan)') -and $processSlotsSource.Contains('plan = plan.DisableAutoPop();')) 'Persisted auto-pop plans must be disabled if their overcook stage is no longer valid.'
Assert-True ($tryAutoPopSource.Contains('!IsAutoPopPlanEligible(station, plan)') -and $tryAutoPopSource.Contains('plan.DisableAutoPop()')) 'Auto-pop execution must revalidate the overcook stage immediately before spawning output.'
Assert-True ($registerRpcsSource.Contains('nview.Unregister(AutoPopBonusEffectRpc);') -and
             [regex]::IsMatch($registerRpcsSource, '(?s)nview\.Register<int>\(\s*AutoPopBonusEffectRpc,\s*\(sender, bonusCount\) => ReceiveBonusEffect\(')) 'CookingStation Awake registration must replace and register the auto-eject bonus-effect RPC handler.'
Assert-True ([regex]::IsMatch(
    $autoPopSource,
    '(?s)internal static class CookingStationAutoPopRpcPatch.*?private static void Postfix\(CookingStation __instance\).*?CookingStationAutoPopSystem\.RegisterRpcs\(__instance\);')) 'CookingStation Awake must register both auto-pop RPCs.'

$spawnOutputIndex = $tryAutoPopSource.IndexOf('SpawnItemMethod.Invoke(', [StringComparison]::Ordinal)
$spawnFailureCatchIndex = $tryAutoPopSource.IndexOf('catch (Exception exception)', $spawnOutputIndex, [StringComparison]::Ordinal)
$successForceClearIndex = $tryAutoPopSource.LastIndexOf('ForceClearSlot(slot, nview, zdo);', [StringComparison]::Ordinal)
$successClearPlanIndex = $tryAutoPopSource.LastIndexOf('ClearPlan(station, slot);', [StringComparison]::Ordinal)
$broadcastAfterClearIndex = $tryAutoPopSource.IndexOf('BroadcastBonusEffect(station, plan.BonusCount);', [StringComparison]::Ordinal)
Assert-True ($spawnOutputIndex -ge 0 -and
             $spawnFailureCatchIndex -gt $spawnOutputIndex -and
             $successForceClearIndex -gt $spawnOutputIndex -and
             $successClearPlanIndex -gt $successForceClearIndex -and
             $broadcastAfterClearIndex -gt $successClearPlanIndex) 'Auto-eject bonus feedback must broadcast only after output spawning and successful slot/plan clearing.'
Assert-True (([regex]::Matches($tryAutoPopSource, 'BroadcastBonusEffect\(')).Count -eq 1) 'Auto-eject completion must have exactly one success-only bonus-feedback broadcast.'
$spawnFailureSource = $tryAutoPopSource.Substring($spawnFailureCatchIndex, $successForceClearIndex - $spawnFailureCatchIndex)
Assert-True ($spawnFailureSource.Contains('return;') -and
             -not $spawnFailureSource.Contains('BroadcastBonusEffect(')) 'Failed or partial auto-eject spawning must return without broadcasting bonus feedback.'

$broadcastClampIndex = $broadcastBonusEffectSource.IndexOf('bonusCount = CookingStationAutoPopCore.ClampBonusCount(bonusCount);', [StringComparison]::Ordinal)
$broadcastPositiveGateIndex = $broadcastBonusEffectSource.IndexOf('if (bonusCount <= 0)', [StringComparison]::Ordinal)
$broadcastRpcIndex = $broadcastBonusEffectSource.IndexOf('nview.InvokeRPC(', [StringComparison]::Ordinal)
Assert-True ($broadcastClampIndex -ge 0 -and
             $broadcastPositiveGateIndex -gt $broadcastClampIndex -and
             $broadcastRpcIndex -gt $broadcastPositiveGateIndex) 'Auto-eject feedback must clamp and reject non-positive bonuses before broadcasting.'
Assert-True ($broadcastBonusEffectSource.Contains('!nview.IsOwner()') -and
             $broadcastBonusEffectSource.Contains('ZNetView.Everybody') -and
             $broadcastBonusEffectSource.Contains('AutoPopBonusEffectRpc')) 'Only the station owner may broadcast auto-eject bonus feedback, and it must target every peer.'
Assert-True ($broadcastBonusEffectSource.Contains('catch (Exception exception)') -and
             $broadcastBonusEffectSource.Contains('LogBonusEffectFailure(exception);') -and
             -not $broadcastBonusEffectSource.Contains('ForceClearSlot(') -and
             -not $broadcastBonusEffectSource.Contains('ClearPlan(') -and
             -not $broadcastBonusEffectSource.Contains('WritePlan(') -and
             -not $broadcastBonusEffectSource.Contains('throw;')) 'Bonus-effect broadcast failures must be best-effort and must not mutate completed output state.'
Assert-True ($logBonusEffectFailureSource.Contains('item output remains complete')) 'Bonus-effect failure logging must state that completed item output is preserved.'

$receiverClampIndex = $receiveBonusEffectSource.IndexOf('bonusCount = CookingStationAutoPopCore.ClampBonusCount(bonusCount);', [StringComparison]::Ordinal)
$receiverValidationIndex = $receiveBonusEffectSource.IndexOf('if (zdo == null || sender != zdo.GetOwner() || bonusCount <= 0)', [StringComparison]::Ordinal)
$receiverSharedEffectIndex = $receiveBonusEffectSource.IndexOf('CookingProductionBonusSystem.ShowBonusEffect(', [StringComparison]::Ordinal)
Assert-True ($receiverClampIndex -ge 0 -and
             $receiverValidationIndex -gt $receiverClampIndex -and
             $receiverSharedEffectIndex -gt $receiverValidationIndex) 'Auto-eject bonus receivers must validate the owner and positive clamped count before delegating client effects.'
$showSharedBonusEffect = Get-MethodRequired $productionBonusSystemType 'ShowBonusEffect'
Assert-True ($showSharedBonusEffect.IsStatic -and
             $showSharedBonusEffect.GetParameters().Count -eq 2 -and
             $showSharedBonusEffect.GetParameters()[0].ParameterType.FullName -eq 'UnityEngine.Vector3' -and
             $showSharedBonusEffect.GetParameters()[1].ParameterType -eq [int]) 'Shared production bonus feedback must accept one world position and one bonus count.'

$slotPlanType = Get-TypeRequired $assembly 'FineDining.CookingStationSlotPlan'
$slotPlanConstructor = @($slotPlanType.GetConstructors([Reflection.BindingFlags] 'Instance,Public,NonPublic') |
    Where-Object { $_.GetParameters().Count -eq 5 })[0]
Assert-True ($null -ne $slotPlanConstructor) 'CookingStation slot plan constructor was not found.'
$autoEjectPlan = $slotPlanConstructor.Invoke([object[]] @($true, $true, 0, 'RawMeat', 'CookedMeat'))
$manualPlan = $slotPlanConstructor.Invoke([object[]] @($false, $false, 0, 'RawMeat', 'CookedMeat'))
$coalPlan = $slotPlanConstructor.Invoke([object[]] @($true, $true, 0, 'RawMeat', 'Coal'))
$disableAutoPop = Get-MethodRequired $slotPlanType 'DisableAutoPop'
$disabledAutoEjectPlan = $disableAutoPop.Invoke($autoEjectPlan, [object[]] @())
$slotPlanFlags = [Reflection.BindingFlags] 'Instance,Public,NonPublic'
Assert-True (-not [bool]$slotPlanType.GetProperty('AutoPop', $slotPlanFlags).GetValue($disabledAutoEjectPlan)) 'Disabling a stale plan must clear auto-pop.'
Assert-True ([bool]$slotPlanType.GetProperty('CollectionExperiencePrepaid', $slotPlanFlags).GetValue($disabledAutoEjectPlan)) 'Disabling a stale plan must preserve already prepaid collection experience.'
$isMatchingAutoEjectPlan = Get-MethodRequired $autoPopSystemType 'IsMatchingAutoEjectPlan'
Assert-True ([bool]$isMatchingAutoEjectPlan.Invoke($null, [object[]] @($autoEjectPlan, 'RawMeat', 'CookedMeat', $false))) 'A matching active auto-pop slot must display auto-eject.'
Assert-True ([bool]$isMatchingAutoEjectPlan.Invoke($null, [object[]] @($autoEjectPlan, 'CookedMeat', '', $true))) 'A matching completed auto-pop slot may display auto-eject until ejected.'
Assert-True (-not [bool]$isMatchingAutoEjectPlan.Invoke($null, [object[]] @($manualPlan, 'RawMeat', 'CookedMeat', $false))) 'A manual CookingStation slot must not display auto-eject.'
Assert-True (-not [bool]$isMatchingAutoEjectPlan.Invoke($null, [object[]] @($autoEjectPlan, 'OtherMeat', 'CookedMeat', $false))) 'A stale input plan must not display auto-eject.'
Assert-True (-not [bool]$isMatchingAutoEjectPlan.Invoke($null, [object[]] @($autoEjectPlan, 'RawMeat', 'OtherFood', $false))) 'A stale output conversion must not display auto-eject.'
Assert-True (-not [bool]$isMatchingAutoEjectPlan.Invoke($null, [object[]] @($autoEjectPlan, 'OtherFood', '', $true))) 'A mismatched completed output must not display auto-eject.'
Assert-True (-not [bool]$isMatchingAutoEjectPlan.Invoke($null, [object[]] @($coalPlan, 'RawMeat', 'Coal', $false))) 'A Coal output must not display auto-eject.'

$getPlanKey = Get-MethodRequired $autoPopSystemType 'GetPlanKey'
$slotZeroAutoKey = [string]$getPlanKey.Invoke($null, [object[]] @(0, 'auto'))
$slotOneAutoKey = [string]$getPlanKey.Invoke($null, [object[]] @(1, 'auto'))
$slotZeroBonusKey = [string]$getPlanKey.Invoke($null, [object[]] @(0, 'bonus'))
Assert-True ($slotZeroAutoKey.StartsWith($slotStateKeyPrefix, [StringComparison]::Ordinal)) 'Generated slot keys must use the owned prefix.'
Assert-True ($slotZeroAutoKey -ne $slotOneAutoKey) 'Different CookingStation slots must have distinct plan keys.'
Assert-True ($slotZeroAutoKey -ne $slotZeroBonusKey) 'Different CookingStation plan fields must have distinct keys.'

foreach ($patchContract in @(
    @('FineDining.CookingStationAutoPopRpcPatch', 'Awake'),
    @('FineDining.CookingStationPlannedAddPatch', 'CookItem'),
    @('FineDining.CookingStationSlotPlanPatch', 'RPC_AddItem'),
    @('FineDining.CookingStationPlannedExperiencePatch', 'OnInteract'),
    @('FineDining.CookingStationBonusChancePatch', 'OnInteract'),
    @('FineDining.CookingStationAutoPopCompletionPatch', 'UpdateCooking'),
    @('FineDining.CookingStationExcludedOutputGuardPatch', 'RPC_RemoveDoneItem')))
{
    Assert-HarmonyPatchTarget $assembly $patchContract[0] 'CookingStation' $patchContract[1]
}

foreach ($patchMethodContract in @(
    @('FineDining.CookingStationAutoPopRpcPatch', 'HarmonyPostfix'),
    @('FineDining.CookingStationPlannedAddPatch', 'HarmonyTranspiler'),
    @('FineDining.CookingStationSlotPlanPatch', 'HarmonyPrefix'),
    @('FineDining.CookingStationPlannedExperiencePatch', 'HarmonyTranspiler'),
    @('FineDining.CookingStationBonusChancePatch', 'HarmonyTranspiler'),
    @('FineDining.CookingStationAutoPopCompletionPatch', 'HarmonyPostfix'),
    @('FineDining.CookingStationExcludedOutputGuardPatch', 'HarmonyPrefix'),
    @('FineDining.CookingStationExcludedOutputGuardPatch', 'HarmonyPostfix')))
{
    Assert-HarmonyPatchMethodAttribute $assembly $patchMethodContract[0] $patchMethodContract[1]
}

$cookingBonusCoreType = Get-TypeRequired $assembly 'FineDining.CookingProductionBonusCore'
Assert-True ([float](Get-Constant $cookingBonusCoreType 'MaximumChanceAtMaxCookingPercent') -eq 25.0) 'Production bonus chance must share one 25% maximum.'
$calculateBonusChance = Get-MethodRequired $cookingBonusCoreType 'CalculatePerItemChance'
$productionBonusChanceExamples = @(
    [pscustomobject]@{ Label = 'Cooking level 0 with the default production chance'; Skill = 0.0; MaxChance = 25.0; Expected = 0.0 },
    [pscustomobject]@{ Label = 'Cooking level 50 with the default production chance'; Skill = 0.5; MaxChance = 25.0; Expected = 0.125 },
    [pscustomobject]@{ Label = 'Cooking level 100 with the default production chance'; Skill = 1.0; MaxChance = 25.0; Expected = 0.25 },
    [pscustomobject]@{ Label = 'Cooking level 0 with the default Fermenter chance'; Skill = 0.0; MaxChance = 20.0; Expected = 0.0 },
    [pscustomobject]@{ Label = 'Cooking level 50 with the default Fermenter chance'; Skill = 0.5; MaxChance = 20.0; Expected = 0.10 },
    [pscustomobject]@{ Label = 'Cooking level 100 with the default Fermenter chance'; Skill = 1.0; MaxChance = 20.0; Expected = 0.20 })
foreach ($example in $productionBonusChanceExamples)
{
    $actualChance = [float]$calculateBonusChance.Invoke(
        $null,
        [object[]] @([float]$example.Skill, [float]$example.MaxChance))
    Assert-True ([Math]::Abs($actualChance - [float]$example.Expected) -lt 0.0001) "$($example.Label) is incorrect."
}

$productionBonusSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\CookingProductionBonusPatches.cs') -Raw
Assert-True ($productionBonusSource.Contains('DamageText.instance.ShowText(') -and
             $productionBonusSource.Contains('InventoryGui.instance.m_craftBonusEffect.Create(') -and
             $productionBonusSource.Contains('DamageText.TextType.Bonus')) 'Shared production feedback must retain floating bonus text and the client craft-bonus effect.'
$recipeBonusMethodStart = $productionBonusSource.IndexOf(
    'internal static int CalculateCookingSkillBonusOrUseVanilla(',
    [StringComparison]::Ordinal)
$configuredCookingChanceMethodStart = $productionBonusSource.IndexOf(
    'internal static float CalculateConfiguredCookingChance(',
    $recipeBonusMethodStart,
    [StringComparison]::Ordinal)
$configuredRollMethodStart = $productionBonusSource.IndexOf(
    'internal static int RollConfiguredBonusItems(',
    $configuredCookingChanceMethodStart,
    [StringComparison]::Ordinal)
$productionExclusionMethodStart = $productionBonusSource.IndexOf(
    'internal static bool IsExcludedOutputPrefab(',
    $configuredRollMethodStart,
    [StringComparison]::Ordinal)
Assert-True ($recipeBonusMethodStart -ge 0 -and
             $configuredCookingChanceMethodStart -gt $recipeBonusMethodStart -and
             $configuredRollMethodStart -gt $configuredCookingChanceMethodStart -and
             $productionExclusionMethodStart -gt $configuredRollMethodStart) 'Direct production-bonus method boundaries were not found.'
$recipeBonusMethodSource = $productionBonusSource.Substring(
    $recipeBonusMethodStart,
    $configuredCookingChanceMethodStart - $recipeBonusMethodStart)
$configuredCookingChanceMethodSource = $productionBonusSource.Substring(
    $configuredCookingChanceMethodStart,
    $configuredRollMethodStart - $configuredCookingChanceMethodStart)
$configuredRollMethodSource = $productionBonusSource.Substring(
    $configuredRollMethodStart,
    $productionExclusionMethodStart - $configuredRollMethodStart)
Assert-True ([regex]::IsMatch(
    $recipeBonusMethodSource,
    '(?s)CalculatePerItemChance\(\s*skillFactor,\s*DietConfig\.GetCookingBonusChanceAtMaxCookingPercent\(\)\)')) 'Cooking recipes must use the general direct chance config.'
Assert-True ([regex]::IsMatch(
    $configuredCookingChanceMethodSource,
    '(?s)CalculatePerItemChance\(\s*skillFactor,\s*DietConfig\.GetCookingBonusChanceAtMaxCookingPercent\(\)\)')) 'Manual CookingStation collection must resolve the general direct chance config.'
Assert-True ([regex]::IsMatch(
    $configuredRollMethodSource,
    '(?s)CalculatePerItemChance\(\s*skillFactor,\s*chanceAtMaxCookingPercent\)')) 'Shared production rolls must consume the direct chance supplied by their caller.'
Assert-True (-not $productionBonusSource.Contains('VanillaBonusChance') -and
             -not $productionBonusSource.Contains('ApplyConfiguredOutputPercent')) 'The former vanilla-relative production chance path must not remain.'

$cookingStationBonusSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\CookingStationBonusPatches.cs') -Raw
Assert-True ($cookingStationBonusSource.Contains('CookingProductionBonusSystem.CalculateConfiguredCookingChance(')) 'Manual CookingStation collection must route through the general direct chance helper.'
$cookingStationAutoPopSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\CookingStationAutoPopPatches.cs') -Raw
Assert-True ([regex]::IsMatch(
    $cookingStationAutoPopSource,
    '(?s)RollConfiguredBonusItems\(\s*conversion\.m_to\.gameObject\.name,\s*1,\s*skillFactor,\s*DietConfig\.GetCookingBonusChanceAtMaxCookingPercent\(\)\)')) 'CookingStation insertion plans must use the general direct chance config.'
$fermenterOutputBonusSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\FermenterCookingBonusPatches.cs') -Raw
Assert-True ([regex]::IsMatch(
    $fermenterOutputBonusSource,
    '(?s)RollConfiguredBonusItems\(\s*outputPrefabName,\s*baseItemCount,\s*skillPermille\s*/\s*1000f,\s*DietConfig\.GetFermenterOutputBonusChanceAtMaxCookingPercent\(\)\)')) 'Fermenter output rolls must use their separate direct chance config.'
Assert-True (-not $recipeBonusMethodSource.Contains('GetFermenterOutputBonusChanceAtMaxCookingPercent') -and
             -not $configuredCookingChanceMethodSource.Contains('GetFermenterOutputBonusChanceAtMaxCookingPercent') -and
             -not $cookingStationAutoPopSource.Contains('GetFermenterOutputBonusChanceAtMaxCookingPercent')) 'Cooking recipes and CookingStations must not consume the Fermenter chance config.'

$chefMathType = Get-TypeRequired $assembly 'FineDining.ChefChoiceMath'
$getFoodWeight = Get-MethodRequired $chefMathType 'GetFoodSelectionWeight'
$getInterpolatedMultiplierMode = Get-MethodRequired $chefMathType 'GetInterpolatedMultiplierMode'
$getTriangularQuantile = Get-MethodRequired $chefMathType 'GetTriangularQuantile'
$getChefMultiplier = Get-MethodRequired $chefMathType 'GetChefMultiplier'
$clampChefMultiplierMinimum = Get-MethodRequired $chefMathType 'ClampMultiplierMinimum'
$clampChefMultiplierMaximum = Get-MethodRequired $chefMathType 'ClampMultiplierMaximum'
$clampChefMultiplierMode = Get-MethodRequired $chefMathType 'ClampMultiplierMode'
$chooseWeightedIndex = Get-MethodRequired $chefMathType 'ChooseWeightedIndex'
$clampChefPreferencePercentage = Get-MethodRequired $chefMathType 'ClampPercentage'
$blendChefCategoryProbability = Get-MethodRequired $chefMathType 'BlendCategoryProbability'
Assert-True ([Math]::Abs([float](Get-Constant $chefMathType 'MaximumTierSelectionStrength') - 20) -lt 0.0001) 'Chef high-tier selection strength must allow values through 20.'
Assert-True ([Math]::Abs([float]$getFoodWeight.Invoke($null, [object[]] @([float]0, [float]1, $true, [float]5)) - 1) -lt 0.0001) 'Cooking level zero must keep all resolved Chef tiers equally weighted.'
$defaultHighestTierWeight = [float]$getFoodWeight.Invoke(
    $null,
    [object[]] @([float]1, [float]1, $true, [float]5))
Assert-True ([Math]::Abs($defaultHighestTierWeight - [Math]::Exp(5)) -lt 0.01) 'The default tier-selection strength must give the highest tier an exp(5) weight at Cooking level 100.'
$partialTierWeight = [float]$getFoodWeight.Invoke(
    $null,
    [object[]] @([float]0.5, [float]0.5, $true, [float]5))
Assert-True ([Math]::Abs($partialTierWeight - [Math]::Exp(1.25)) -lt 0.0001) 'Chef tier weight must be exp(K * Cooking factor * normalized tier).'
Assert-True ([Math]::Abs([float]$getFoodWeight.Invoke($null, [object[]] @([float]1, [float]1, $true, [float]0)) - 1) -lt 0.0001) 'A zero tier-selection strength must disable Cooking-based tier weighting.'
Assert-True ([Math]::Abs([float]$getFoodWeight.Invoke($null, [object[]] @([float]1, [float]1, $false, [float]20)) - 1) -lt 0.0001) 'AllTier Chef foods must retain neutral selection weight at every configured tier-selection strength.'
Assert-True ([Math]::Abs([float]$getInterpolatedMultiplierMode.Invoke($null, [object[]] @([float]1.2, [float]1.6, [float]1.5, [float]0)) - 1.2) -lt 0.0001) 'Cooking level zero must place the triangular multiplier mode at Minimum Multiplier.'
Assert-True ([Math]::Abs([float]$getInterpolatedMultiplierMode.Invoke($null, [object[]] @([float]1.2, [float]1.6, [float]1.5, [float]0.5)) - 1.35) -lt 0.0001) 'Cooking level 50 must move the multiplier mode halfway toward its configured level-100 target.'
Assert-True ([Math]::Abs([float]$getInterpolatedMultiplierMode.Invoke($null, [object[]] @([float]1.2, [float]1.6, [float]1.5, [float]1)) - 1.5) -lt 0.0001) 'Cooking level 100 must use the configured multiplier mode.'
$lowerTriangleMedian = [float]$getTriangularQuantile.Invoke($null, [object[]] @([float]0.5, [float]0))
$upperTriangleMedian = [float]$getTriangularQuantile.Invoke($null, [object[]] @([float]0.5, [float]1))
Assert-True ([Math]::Abs($lowerTriangleMedian - (1 - [Math]::Sqrt(0.5))) -lt 0.0001) 'A minimum-mode triangle must favor the lower multiplier range.'
Assert-True ([Math]::Abs($upperTriangleMedian - [Math]::Sqrt(0.5)) -lt 0.0001) 'A maximum-mode triangle must favor the upper multiplier range.'
Assert-True ([Math]::Abs(($lowerTriangleMedian + $upperTriangleMedian) - 1) -lt 0.0001) 'Endpoint triangular multiplier distributions must remain symmetric.'
Assert-True ([Math]::Abs([float]$getTriangularQuantile.Invoke($null, [object[]] @([float]0.75, [float]0.75)) - 0.75) -lt 0.0001) 'The triangular inverse CDF must return its mode at the mode CDF boundary.'
Assert-True ([Math]::Abs([float](Get-Constant $chefMathType 'MinimumAllowedMultiplier') - 1) -lt 0.0001) 'Chef Multiplier Minimum must allow x1.00 as its lowest configured value.'
Assert-True ([Math]::Abs([float]$clampChefMultiplierMinimum.Invoke($null, [object[]] @([float]0.5)) - 1) -lt 0.0001) 'Chef Multiplier Minimum values below x1.00 must clamp to the allowed floor.'
Assert-True ([Math]::Abs([float]$clampChefMultiplierMaximum.Invoke($null, [object[]] @([float]1.2, [float]1.6)) - 1.6) -lt 0.0001) 'A Chef maximum below its minimum must collapse the effective range to the minimum.'
Assert-True ([Math]::Abs([float]$clampChefMultiplierMode.Invoke($null, [object[]] @([float]2, [float]1.2, [float]1.6)) - 1.6) -lt 0.0001) 'A multiplier mode above Maximum Multiplier must clamp to the effective maximum.'
Assert-True ([Math]::Abs([float]$clampChefMultiplierMode.Invoke($null, [object[]] @([float]1, [float]1.2, [float]1.6)) - 1.2) -lt 0.0001) 'A multiplier mode below Minimum Multiplier must clamp to the effective minimum.'
Assert-True ([Math]::Abs([float]$getChefMultiplier.Invoke($null, [object[]] @([float]1.2, [float]1.6, [float]0.75, [float]1, [float]1.5)) - 1.5) -lt 0.0001) 'At Cooking level 100, the configured triangular mode must be the inverse-CDF boundary result.'
Assert-True ([Math]::Abs([float]$getChefMultiplier.Invoke($null, [object[]] @([float]1.6, [float]1.2, [float]0.5, [float]1, [float]1.5)) - 1.6) -lt 0.0001) 'A Chef minimum above its maximum must produce the minimum as the single effective multiplier.'
$weightedArguments = New-Object 'System.Object[]' 2
$weightedArguments[0] = [float[]] @(1, 4)
$weightedArguments[1] = [float]0.199999
Assert-True ([int]$chooseWeightedIndex.Invoke($null, $weightedArguments) -eq 0) 'Weighted Chef selection must preserve the lower bucket below its boundary.'
$weightedArguments[1] = [float]0.2
Assert-True ([int]$chooseWeightedIndex.Invoke($null, $weightedArguments) -eq 1) 'Weighted Chef selection must enter the higher bucket at its boundary.'

foreach ($clampCase in @(
    @([float]::NaN, [float]0, 'NaN'),
    @([float]-1, [float]0, 'negative'),
    @([float]0, [float]0, 'zero'),
    @([float]50, [float]50, 'mid-range'),
    @([float]100, [float]100, 'maximum'),
    @([float]101, [float]100, 'above-maximum'),
    @([float]::PositiveInfinity, [float]100, 'positive-infinity')))
{
    $actual = [float]$clampChefPreferencePercentage.Invoke(
        $null,
        [object[]] @([float]$clampCase[0]))
    Assert-True ([Math]::Abs($actual - [float]$clampCase[1]) -lt 0.0001) "Chef preference percentage must clamp $($clampCase[2]) input."
}

$baseCategoryProbability = [float]0.2
$historyCategoryProbability = [float]0.6
foreach ($blendCase in @(
    @([float]0, [float]0.2, '0%'),
    @([float]50, [float]0.4, '50%'),
    @([float]100, [float]0.6, '100%'),
    @([float]::NaN, [float]0.2, 'NaN'),
    @([float]-10, [float]0.2, 'negative'),
    @([float]250, [float]0.6, 'above-maximum')))
{
    $actual = [float]$blendChefCategoryProbability.Invoke(
        $null,
        [object[]] @(
            $baseCategoryProbability,
            $historyCategoryProbability,
            [float]$blendCase[0]))
    Assert-True ([Math]::Abs($actual - [float]$blendCase[1]) -lt 0.0001) "Chef category blending must produce the expected $($blendCase[2]) result."
}

# At 100%, an Eitr 3 / Stamina 3 / Health 1 history must be represented
# exactly as 3:3:1 regardless of the pre-existing category distribution.
$healthHistoryProbability = [float](1.0 / 7.0)
$staminaHistoryProbability = [float](3.0 / 7.0)
$eitrHistoryProbability = [float](3.0 / 7.0)
$healthAtFullPreference = [float]$blendChefCategoryProbability.Invoke(
    $null,
    [object[]] @([float]0.7, $healthHistoryProbability, [float]100))
$staminaAtFullPreference = [float]$blendChefCategoryProbability.Invoke(
    $null,
    [object[]] @([float]0.2, $staminaHistoryProbability, [float]100))
$eitrAtFullPreference = [float]$blendChefCategoryProbability.Invoke(
    $null,
    [object[]] @([float]0.1, $eitrHistoryProbability, [float]100))
Assert-True ([Math]::Abs(($healthAtFullPreference + $staminaAtFullPreference + $eitrAtFullPreference) - 1) -lt 0.0001) 'A fully history-driven Chef category distribution must remain normalized.'
Assert-True ([Math]::Abs(($staminaAtFullPreference / $healthAtFullPreference) - 3) -lt 0.0001) 'At 100%, Stamina food probability must be exactly three times Health for a 3:1 history count.'
Assert-True ([Math]::Abs(($eitrAtFullPreference / $healthAtFullPreference) - 3) -lt 0.0001) 'At 100%, Eitr food probability must be exactly three times Health for a 3:1 history count.'
Assert-True ([Math]::Abs($staminaAtFullPreference - $eitrAtFullPreference) -lt 0.0001) 'Equal Stamina and Eitr history counts must produce equal category probabilities at 100% preference.'

# Mirror the production reconstruction from category probability back to each
# candidate. Candidate counts and tier weights must define the 0% baseline,
# while a 100% preference must produce exactly the 1:3:3 history distribution.
$aggregateBaseMasses = [float[]] @(5, 2, 3) # H [1,4], S [2], E [1,1,1]
$aggregateHistoryProbabilities = [float[]] @(
    $healthHistoryProbability,
    $staminaHistoryProbability,
    $eitrHistoryProbability)
$aggregateCategoryProbabilitiesAtZero = [float[]]::new(3)
$aggregateCategoryProbabilitiesAtFull = [float[]]::new(3)
for ($axisIndex = 0; $axisIndex -lt 3; $axisIndex++)
{
    $baseProbability = [float]($aggregateBaseMasses[$axisIndex] / [float]10)
    $aggregateCategoryProbabilitiesAtZero[$axisIndex] = [float]$blendChefCategoryProbability.Invoke(
        $null,
        [object[]] @($baseProbability, $aggregateHistoryProbabilities[$axisIndex], [float]0))
    $aggregateCategoryProbabilitiesAtFull[$axisIndex] = [float]$blendChefCategoryProbability.Invoke(
        $null,
        [object[]] @($baseProbability, $aggregateHistoryProbabilities[$axisIndex], [float]100))
    Assert-True ([Math]::Abs($aggregateCategoryProbabilitiesAtZero[$axisIndex] - $baseProbability) -lt 0.0001) 'At 0%, reconstructed Chef category mass must equal its candidate base-weight share.'
    Assert-True ([Math]::Abs($aggregateCategoryProbabilitiesAtFull[$axisIndex] - $aggregateHistoryProbabilities[$axisIndex]) -lt 0.0001) 'At 100%, reconstructed Chef category mass must equal its recent-history share.'
}

$healthCandidateWeightOne = $aggregateCategoryProbabilitiesAtFull[0] * [float]1 / $aggregateBaseMasses[0]
$healthCandidateWeightFour = $aggregateCategoryProbabilitiesAtFull[0] * [float]4 / $aggregateBaseMasses[0]
Assert-True ([Math]::Abs((($healthCandidateWeightOne + $healthCandidateWeightFour) / $aggregateCategoryProbabilitiesAtFull[0]) - 1) -lt 0.0001) 'Reconstructed Health candidate weights must sum to their category probability.'
Assert-True ([Math]::Abs(($healthCandidateWeightFour / $healthCandidateWeightOne) - 4) -lt 0.0001) 'Recent-food category preference must preserve the 1:4 tier-weight ratio within Health foods.'
Assert-True ([Math]::Abs(($aggregateCategoryProbabilitiesAtFull[1] / $aggregateCategoryProbabilitiesAtFull[0]) - 3) -lt 0.0001) 'Candidate-count differences must not dilute the 3:1 Stamina/Health history ratio at 100%.'
Assert-True ([Math]::Abs(($aggregateCategoryProbabilitiesAtFull[2] / $aggregateCategoryProbabilitiesAtFull[0]) - 3) -lt 0.0001) 'Candidate-count differences must not dilute the 3:1 Eitr/Health history ratio at 100%.'

$foodIdentityType = Get-TypeRequired $assembly 'FineDining.FoodIdentity'
$foodStatAxisType = Get-TypeRequired $assembly 'FineDining.FoodStatAxis'
$hasChefFoodStats = Get-MethodRequired $foodIdentityType 'HasChefFoodStats'
Assert-True ($hasChefFoodStats.GetParameters().Count -eq 3) 'Chef Choice food identity must only accept health, stamina, and eitr stats.'
Assert-True (-not [bool]$hasChefFoodStats.Invoke($null, [object[]] @([float]0, [float]0, [float]0))) 'A statless consumable must not be eligible for Chef Choice.'
Assert-True ([bool]$hasChefFoodStats.Invoke($null, [object[]] @([float]1, [float]0, [float]0))) 'A positive health stat must remain Chef Choice eligible.'
Assert-True ([bool]$hasChefFoodStats.Invoke($null, [object[]] @([float]0, [float]1, [float]0))) 'A positive stamina stat must remain Chef Choice eligible.'
Assert-True ([bool]$hasChefFoodStats.Invoke($null, [object[]] @([float]0, [float]0, [float]1))) 'A positive eitr stat must remain Chef Choice eligible.'
$foodStatAxisMethods = @($foodIdentityType.GetMethods(
    [Reflection.BindingFlags] 'Static,Public,NonPublic') | Where-Object {
        $_.Name -eq 'TryGetFoodStatAxis' -and
        $_.GetParameters().Count -eq 4 -and
        $_.GetParameters()[0].ParameterType -eq [float] -and
        $_.GetParameters()[1].ParameterType -eq [float] -and
        $_.GetParameters()[2].ParameterType -eq [float]
    })
Assert-True ($foodStatAxisMethods.Count -eq 1) 'The pure Health/Stamina/Eitr food-axis classifier is missing or ambiguous.'
Assert-True (@($foodIdentityType.GetMethods(
    [Reflection.BindingFlags] 'Static,Public,NonPublic') | Where-Object {
        $_.Name -eq 'TryGetDominantFoodStat'
    }).Count -eq 0) 'The obsolete dominant-stat classifier must not remain beside the food-axis policy.'
$tryGetFoodStatAxis = $foodStatAxisMethods[0]
function Assert-FoodStatAxis(
    [float] $Health,
    [float] $Stamina,
    [float] $Eitr,
    [bool] $ExpectedSuccess,
    [string] $ExpectedAxis,
    [string] $Description)
{
    $arguments = [object[]] @($Health, $Stamina, $Eitr, $null)
    $success = [bool]$tryGetFoodStatAxis.Invoke($null, $arguments)
    Assert-True ($success -eq $ExpectedSuccess) "Unexpected food-stat axis for $Description."
    Assert-True ($arguments[3].ToString() -eq $ExpectedAxis) "Expected $ExpectedAxis for $Description, got $($arguments[3])."
}

Assert-FoodStatAxis 10 5 0 $true 'Health' 'Health above Stamina without Eitr'
Assert-FoodStatAxis 5 10 0 $true 'Stamina' 'Stamina above Health without Eitr'
Assert-FoodStatAxis 10 10 0 $true 'Stamina' 'Health/Stamina tie without Eitr'
Assert-FoodStatAxis 100 100 0.1 $true 'Eitr' 'positive Eitr priority over equal Health/Stamina'
Assert-FoodStatAxis 0 0 1 $true 'Eitr' 'Eitr-only food'
Assert-FoodStatAxis 0 0 0 $false 'None' 'statless consumable'
Assert-FoodStatAxis -1 -2 -3 $false 'None' 'all-nonpositive consumable'

$dietPlayerPatchSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\PlayerFoodPatches.cs') -Raw
Assert-True ([regex]::Matches(
    $dietPlayerPatchSource,
    '(?s)if\s*\(!FoodIdentity\.IsDirectlyEdible\(item\)\)\s*\{\s*return true;\s*\}').Count -eq 1) 'Non-H/S/E CanEat checks must fall through to vanilla handling.'
Assert-True ([regex]::IsMatch(
    $dietPlayerPatchSource,
    '(?s)if\s*\(!FoodIdentity\.IsDirectlyEdible\(item\)\)\s*\{\s*__state\s*=\s*new VanillaEatBoundaryState\(__instance, item\);\s*return true;\s*\}')) 'Non-H/S/E EatFood calls must fall through to vanilla while capturing replacement-boundary state.'
$eatFoodPostfixStart = $dietPlayerPatchSource.IndexOf(
    'private static void EatFoodPostfix(',
    [StringComparison]::Ordinal)
$updateFoodPatchStart = $dietPlayerPatchSource.IndexOf(
    '[HarmonyPatch("UpdateFood")]',
    $eatFoodPostfixStart,
    [StringComparison]::Ordinal)
Assert-True ($eatFoodPostfixStart -ge 0 -and $updateFoodPatchStart -gt $eatFoodPostfixStart) 'Player.EatFood postfix source boundaries are missing.'
$eatFoodPostfixSource = $dietPlayerPatchSource.Substring(
    $eatFoodPostfixStart,
    $updateFoodPatchStart - $eatFoodPostfixStart)
$failedEatReturn = [regex]::Match(
    $eatFoodPostfixSource,
    '(?s)if\s*\(\s*!__result\s*\)\s*\{\s*return;\s*\}')
Assert-True $failedEatReturn.Success 'A failed EatFood call must return before awarding Cooking experience.'
$foodExperienceGuard = [regex]::Match(
    $eatFoodPostfixSource,
    '(?s)if\s*\(\s*__instance\s*==\s*Player\.m_localPlayer\s*&&\s*FoodIdentity\.IsDirectlyEdible\(item\)\s*\)\s*\{\s*float\s+experience\s*=\s*DietConfig\.GetCookingExperiencePerFoodEaten\(\);\s*if\s*\(\s*experience\s*>\s*0f\s*\)\s*\{\s*__instance\.RaiseSkill\(\s*Skills\.SkillType\.Cooking,\s*experience\s*\);\s*\}\s*\}')
Assert-True $foodExperienceGuard.Success 'A successful local H/S/E EatFood call must award the configured positive Cooking experience exactly once.'
$foodExperienceAwardIndex = $eatFoodPostfixSource.IndexOf(
    '__instance.RaiseSkill(',
    [StringComparison]::Ordinal)
$vanillaBoundaryReturnIndex = $eatFoodPostfixSource.IndexOf(
    '__state == null',
    [StringComparison]::Ordinal)
Assert-True ($failedEatReturn.Index -ge 0 -and
             $foodExperienceAwardIndex -gt $failedEatReturn.Index -and
             $vanillaBoundaryReturnIndex -gt $foodExperienceAwardIndex) 'Cooking experience must be awarded before the vanilla replacement-boundary return so direct food with null state still grants experience.'
Assert-True ($null -eq $foodIdentityType.GetMethod('IsChefChoiceEligible', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'Redundant Chef food-identity aliases must remain removed.'
Assert-True ($null -eq $foodIdentityType.GetMethod('IsDietConsumable', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'Redundant Diet food-identity aliases must remain removed.'
$foodEffectType = Get-TypeRequired $assembly 'FineDining.FoodEffect'
$foodEffectFlags = [Reflection.BindingFlags] 'Instance,Public,NonPublic'
Assert-True ($null -eq $foodEffectType.GetProperty('Duration', $foodEffectFlags)) 'The unreachable regen-only tooltip duration state must remain removed.'

$spoilagePolicyType = Get-TypeRequired $assembly 'FineDining.SpoilagePolicy'
$spoilageYamlDocumentType = Get-TypeRequired $assembly 'FineDining.SpoilageYamlDocument'
$spoilageYamlLifetimesType = Get-TypeRequired $assembly 'FineDining.SpoilageYamlLifetimes'
$spoilageYamlLifetimeValueType = Get-TypeRequired $assembly 'FineDining.SpoilageYamlLifetimeValue'
$spoilageYamlLifetimeValueConverterType = Get-TypeRequired $assembly 'FineDining.SpoilageYamlLifetimeValueConverter'
$spoilageGroupType = Get-TypeRequired $assembly 'FineDining.SpoilageGroup'
$spoilageExpiryActionType = Get-TypeRequired $assembly 'FineDining.SpoilageExpiryAction'
Assert-True ((Get-Constant $spoilagePolicyType 'PolicyFileName') -eq 'Spoilage.yml') 'Spoilage policy filename is incorrect.'
Assert-True ((Get-Constant $spoilagePolicyType 'KeepOriginalKeyword') -eq 'keep') 'The compact keep-original keyword must remain canonical and stable.'
Assert-True (([Enum]::GetNames($spoilageExpiryActionType) -join ',') -eq 'Replace,KeepOriginal') 'Spoilage expiry actions must distinguish replacement from keeping the original prefab.'
$defaultSpoilageResourceName = [string](Get-Constant $spoilagePolicyType 'DefaultPolicyResourceName')
Assert-True ($defaultSpoilageResourceName -eq 'FineDining.Resources.Defaults.Spoilage.yml') 'Embedded Spoilage policy resource ID is incorrect.'
Assert-True ([double](Get-Constant $spoilagePolicyType 'MaximumLifetimeHours') -eq 720d) 'Spoilage lifetime must be capped at 720 hours.'
Assert-Method $spoilagePolicyType 'IsChefChoiceBlacklisted'
Assert-True ($null -ne $spoilageYamlDocumentType.GetProperty('ChefChoiceBlacklist')) 'Spoilage.yml schema must expose chefChoiceBlacklist.'
Assert-True ($null -ne $spoilageYamlLifetimesType.GetProperty('UnfermentedFood')) 'Spoilage.yml lifetime schema must expose unfermentedFood.'
Assert-True ($spoilageYamlLifetimesType.GetProperty('FeastMaterial').PropertyType -eq $spoilageYamlLifetimeValueType -and
             $spoilageYamlLifetimesType.GetProperty('FeastResult').PropertyType -eq $spoilageYamlLifetimeValueType) 'Group lifetime YAML values must share the inline hours/action scalar contract.'
Assert-True ($null -ne $spoilageYamlLifetimeValueConverterType.GetMethod('ReadYaml') -and
             $null -ne $spoilageYamlLifetimeValueConverterType.GetMethod('WriteYaml')) 'The inline group-lifetime scalar converter must support both parsing and canonical serialization.'
Assert-Method (Get-TypeRequired $assembly 'FineDining.DietModule') 'RequestChefCollectionReconcile'
$tryParsePolicy = Get-MethodRequired $spoilagePolicyType 'TryParseAndNormalize'
$defaultSpoilagePath = Join-Path $projectRoot 'Resources\Defaults\Spoilage.yml'
Assert-True (Test-Path -LiteralPath $defaultSpoilagePath -PathType Leaf) 'The editable Spoilage.yml source default is missing.'
$defaultPolicyYaml = Get-Content -LiteralPath $defaultSpoilagePath -Raw
$defaultSpoilageResource = $assembly.GetManifestResourceStream($defaultSpoilageResourceName)
Assert-True ($null -ne $defaultSpoilageResource) 'The embedded Spoilage.yml default stream is missing.'
$defaultSpoilageReader = [IO.StreamReader]::new($defaultSpoilageResource)
try
{
    $embeddedDefaultPolicyYaml = $defaultSpoilageReader.ReadToEnd()
}
finally
{
    $defaultSpoilageReader.Dispose()
}
$normalizedSourcePolicyYaml = $defaultPolicyYaml.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd([char[]] @("`n")) + "`n"
$normalizedEmbeddedPolicyYaml = $embeddedDefaultPolicyYaml.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd([char[]] @("`n")) + "`n"
Assert-True ($normalizedEmbeddedPolicyYaml -eq $normalizedSourcePolicyYaml) 'Embedded Spoilage.yml must match its editable source default.'
Assert-True ($defaultPolicyYaml.Contains('Hours are valid from 0 through 720; 0 disables that group, including 0, keep.')) 'The default Spoilage.yml guidance must document the 720-hour maximum and permit retained keep on disabled groups.'
Assert-True ($defaultPolicyYaml.Contains('<hours>[, keep]')) 'The default Spoilage.yml guidance must document the inline group keep syntax.'
Assert-True ($defaultPolicyYaml.Contains('<replacement prefab or keep>') -and
             $defaultPolicyYaml.Contains('PreservedIdentityFood, 100, keep')) 'The default Spoilage.yml guidance must document the keep-original expiry result.'
foreach ($defaultLifetime in @(
    'farmingHarvest: 96',
    'cookingStationInput: 48',
    'cookingStationOutput: 72',
    'unfermentedFood: 72',
    'fermentedFood: 96',
    'fish: 48',
    'otherEdible: 48'))
{
    Assert-True ($defaultPolicyYaml.Contains($defaultLifetime)) "Default Spoilage.yml lifetime is missing: $defaultLifetime"
}
Assert-True ($defaultPolicyYaml -match '(?m)^\s*feastMaterial:\s*96,\s*keep\s*$') 'The default feast-material policy must keep its original prefab after 96 hours.'
Assert-True ($defaultPolicyYaml -match '(?m)^\s*feastResult:\s*72,\s*keep\s*$') 'The default feast-result policy must keep its original prefab after 72 hours.'
Assert-True ($defaultPolicyYaml.Contains('chefChoiceBlacklist: []')) 'The embedded default policy must expose an empty Chef Choice blacklist.'
$defaultOverrideBlocks = [regex]::Matches(
    $defaultPolicyYaml,
    '(?m)^overrides:[^\r\n]*(?:\r?\n[ \t]+[^\r\n]*)*')
Assert-True ($defaultOverrideBlocks.Count -eq 1) 'The default policy must contain exactly one top-level overrides block.'
$defaultOverrideBlock = $defaultOverrideBlocks[0]
$defaultOverrideRows = @([regex]::Matches(
    $defaultOverrideBlock.Value,
    '(?m)^[ \t]*-[ \t]*(?<Entry>[^\r\n]+)\r?$') | ForEach-Object { $_.Groups['Entry'].Value.Trim() })
$expectedDefaultOverrideRows = @('Raspberry, 96, keep', 'Mushroom, 96, keep', 'Honey, 0', 'Blueberries, 96, keep')
Assert-True ($defaultOverrideRows.Count -eq 4 -and
             ($defaultOverrideRows -join '|') -ceq ($expectedDefaultOverrideRows -join '|')) 'Default exact overrides must be Raspberry 96 keep, Mushroom 96 keep, Honey disabled, then Blueberries 96 keep, with no extra entries.'
# Keep custom-rule fixtures independent of the shipped nonempty default list.
$emptyOverridePolicyYaml = $defaultPolicyYaml.Remove($defaultOverrideBlock.Index, $defaultOverrideBlock.Length).Insert(
    $defaultOverrideBlock.Index, 'overrides: []')
Assert-True ([regex]::Matches($emptyOverridePolicyYaml, '(?m)^overrides: \[\]\r?$').Count -eq 1) 'Custom override fixtures must start from one replaceable empty overrides block.'
$defaultPolicyParseArguments = [object[]] @([string]$defaultPolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $defaultPolicyParseArguments)) 'The default policy with a 72-hour unfermented-food lifetime must parse.'
Assert-True ([string]$defaultPolicyParseArguments[2] -match '(?m)^\s*unfermentedFood:\s*72\s*$') 'Normalized policy YAML must retain the default unfermented-food lifetime.'
$defaultNormalizedPolicy = $defaultPolicyParseArguments[1]
$defaultNormalizedPolicyType = $defaultNormalizedPolicy.GetType()
$getGroupExpiryAction = Get-MethodRequired $defaultNormalizedPolicyType 'GetExpiryAction'
$getGroupLifetimeHours = Get-MethodRequired $defaultNormalizedPolicyType 'GetLifetimeHours'
$getGroupLifetimeTicks = Get-MethodRequired $defaultNormalizedPolicyType 'GetLifetimeTicks'
$expectedDefaultGroupHours = @{
    FarmingHarvest = 96
    CookingStationInput = 48
    CookingStationOutput = 72
    UnfermentedFood = 72
    FermentedFood = 96
    FeastMaterial = 96
    FeastResult = 72
    Fish = 48
    OtherEdible = 48
}
$expectedDefaultGroupActions = @{
    FarmingHarvest = 'Replace'
    CookingStationInput = 'Replace'
    CookingStationOutput = 'Replace'
    UnfermentedFood = 'Replace'
    FermentedFood = 'Replace'
    FeastMaterial = 'KeepOriginal'
    FeastResult = 'KeepOriginal'
    Fish = 'Replace'
    OtherEdible = 'Replace'
}
foreach ($group in [Enum]::GetValues($spoilageGroupType))
{
    $actualAction = [string]$getGroupExpiryAction.Invoke(
        $defaultNormalizedPolicy,
        [object[]] @($group))
    Assert-True ($actualAction -eq $expectedDefaultGroupActions[$group.ToString()]) "Unexpected default expiry action for '$group': '$actualAction'."
    $expectedHours = [double]$expectedDefaultGroupHours[$group.ToString()]
    Assert-True ([double]$getGroupLifetimeHours.Invoke($defaultNormalizedPolicy, [object[]] @($group)) -eq $expectedHours -and
                 [long]$getGroupLifetimeTicks.Invoke($defaultNormalizedPolicy, [object[]] @($group)) -eq [long]($expectedHours * [TimeSpan]::TicksPerHour)) "Default group '$group' must normalize to $expectedHours real hours."
}
$defaultNormalizedYaml = [string]$defaultPolicyParseArguments[2]
Assert-True ($defaultNormalizedYaml -match '(?m)^\s*feastMaterial:\s*96,\s*keep\s*$' -and
             $defaultNormalizedYaml -match '(?m)^\s*feastResult:\s*72,\s*keep\s*$') 'Normalized policy YAML must retain both default Feast keep actions as plain inline scalars.'
$defaultPolicyRoundTripArguments = [object[]] @($defaultNormalizedYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $defaultPolicyRoundTripArguments)) 'Canonical default group-action YAML must parse again.'
Assert-True ([string]$defaultPolicyRoundTripArguments[2] -ceq $defaultNormalizedYaml) 'Canonical default group-action YAML must remain byte-stable across a parse/serialize round trip.'
$defaultOverrideFlags = [Reflection.BindingFlags] 'Instance,Public,NonPublic'
$defaultOverridesProperty = $defaultNormalizedPolicyType.GetProperty('Overrides', $defaultOverrideFlags)
Assert-True ($null -ne $defaultOverridesProperty) 'Parsed default policy must expose its exact overrides for validation.'
foreach ($defaultOverridePolicy in @($defaultNormalizedPolicy, $defaultPolicyRoundTripArguments[1]))
{
    $defaultOverrides = $defaultOverridesProperty.GetValue($defaultOverridePolicy)
    Assert-True ($defaultOverrides.Count -eq 4) 'Initial and round-tripped default policies must contain exactly four exact-prefab overrides.'
    foreach ($defaultOverrideSpec in @(
        @('Raspberry', [double]96, $true, 'KeepOriginal', ''),
        @('Mushroom', [double]96, $true, 'KeepOriginal', ''),
        @('Honey', [double]0, $false, 'Replace', 'RottenMeat'),
        @('Blueberries', [double]96, $true, 'KeepOriginal', '')))
    {
        $defaultOverrideName = [string]$defaultOverrideSpec[0]
        $defaultOverride = $defaultOverrides[$defaultOverrideName]
        Assert-True ($null -ne $defaultOverride) "Default exact override is missing: $defaultOverrideName"
        $defaultOverrideType = $defaultOverride.GetType()
        $actualDefaultOverrideName = [string]$defaultOverrideType.GetProperty('PrefabName', $defaultOverrideFlags).GetValue($defaultOverride)
        $actualDefaultOverrideHours = [double]$defaultOverrideType.GetProperty('Hours', $defaultOverrideFlags).GetValue($defaultOverride)
        $actualDefaultOverrideTicks = [long]$defaultOverrideType.GetProperty('LifetimeTicks', $defaultOverrideFlags).GetValue($defaultOverride)
        $actualDefaultOverrideHasResult = [bool]$defaultOverrideType.GetProperty('HasResultOverride', $defaultOverrideFlags).GetValue($defaultOverride)
        $actualDefaultOverrideAction = [string]$defaultOverrideType.GetProperty('ExpiryAction', $defaultOverrideFlags).GetValue($defaultOverride)
        $actualDefaultOverrideReplacement = [string]$defaultOverrideType.GetProperty('ReplacementPrefab', $defaultOverrideFlags).GetValue($defaultOverride)
        Assert-True ($actualDefaultOverrideName -ceq $defaultOverrideName -and
                     $actualDefaultOverrideHours -eq [double]$defaultOverrideSpec[1] -and
                     $actualDefaultOverrideTicks -eq [long]([double]$defaultOverrideSpec[1] * [TimeSpan]::TicksPerHour)) "Default '$defaultOverrideName' must preserve its exact prefab and hours/ticks, including truly disabled zero ticks for Honey."
        Assert-True ($actualDefaultOverrideHasResult -eq [bool]$defaultOverrideSpec[2] -and
                     $actualDefaultOverrideAction -ceq [string]$defaultOverrideSpec[3] -and
                     $actualDefaultOverrideReplacement -ceq [string]$defaultOverrideSpec[4]) "Default '$defaultOverrideName' must retain its explicit keep or two-field disabled result semantics through normalization and round-trip."
    }
}
$emptyOverrideParseArguments = [object[]] @($emptyOverridePolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $emptyOverrideParseArguments)) 'The isolated empty-override fixture baseline must parse.'
Assert-True ($defaultOverridesProperty.GetValue($emptyOverrideParseArguments[1]).Count -eq 0) 'Custom-rule fixtures must not inherit the four shipped exact overrides.'
$customUnfermentedPolicyYaml = $defaultPolicyYaml.Replace(
    'unfermentedFood: 72',
    'unfermentedFood: 12.5')
$customUnfermentedParseArguments = [object[]] @([string]$customUnfermentedPolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $customUnfermentedParseArguments)) 'A custom unfermented-food lifetime must parse.'
Assert-True ([string]$customUnfermentedParseArguments[2] -match '(?m)^\s*unfermentedFood:\s*12\.5\s*$') 'Normalized policy YAML must retain a custom unfermented-food lifetime.'
$maximumUnfermentedPolicyYaml = $defaultPolicyYaml.Replace(
    'unfermentedFood: 72',
    'unfermentedFood: 720')
$maximumUnfermentedParseArguments = [object[]] @([string]$maximumUnfermentedPolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $maximumUnfermentedParseArguments)) 'An unfermented-food lifetime of exactly 720 hours must be accepted.'
$overMaximumUnfermentedPolicyYaml = $defaultPolicyYaml.Replace(
    'unfermentedFood: 72',
    'unfermentedFood: 721')
$overMaximumUnfermentedParseArguments = [object[]] @([string]$overMaximumUnfermentedPolicyYaml, $null, '', '')
Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $overMaximumUnfermentedParseArguments)) 'An unfermented-food lifetime above 720 hours must be rejected.'
Assert-True ([string]$overMaximumUnfermentedParseArguments[3] -match 'lifetimes\.unfermentedFood.*0 through 720') 'An over-limit unfermented-food lifetime must report its field and valid range.'
$missingUnfermentedPolicyYaml = [regex]::Replace(
    $defaultPolicyYaml,
    '(?m)^[ \t]*unfermentedFood:[^\r\n]*(?:\r?\n|$)',
    '')
$missingUnfermentedParseArguments = [object[]] @([string]$missingUnfermentedPolicyYaml, $null, '', '')
Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $missingUnfermentedParseArguments)) 'A policy missing lifetimes.unfermentedFood must be rejected.'
Assert-True ([string]$missingUnfermentedParseArguments[3] -match 'lifetimes\.unfermentedFood.*required') 'A missing unfermented-food lifetime must report its required field.'
$nullUnfermentedPolicyYaml = $defaultPolicyYaml.Replace(
    'unfermentedFood: 72',
    'unfermentedFood:')
$nullUnfermentedParseArguments = [object[]] @([string]$nullUnfermentedPolicyYaml, $null, '', '')
Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $nullUnfermentedParseArguments)) 'An explicitly null lifetimes.unfermentedFood value must be rejected.'
Assert-True ([string]$nullUnfermentedParseArguments[3] -match 'lifetimes\.unfermentedFood.*required') 'A null unfermented-food lifetime must report its required field.'
$maximumLifetimePolicyYaml = $defaultPolicyYaml.Replace(
    'farmingHarvest: 96',
    'farmingHarvest: 720')
$maximumLifetimeParseArguments = [object[]] @([string]$maximumLifetimePolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $maximumLifetimeParseArguments)) 'A group lifetime of exactly 720 hours must be accepted.'
$overMaximumLifetimePolicyYaml = $defaultPolicyYaml.Replace(
    'farmingHarvest: 96',
    'farmingHarvest: 721')
$overMaximumLifetimeParseArguments = [object[]] @([string]$overMaximumLifetimePolicyYaml, $null, '', '')
Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $overMaximumLifetimeParseArguments)) 'A group lifetime above 720 hours must be rejected.'
Assert-True ([string]$overMaximumLifetimeParseArguments[3] -match '0 through 720') 'An over-limit group lifetime must report the 720-hour range.'
$uppercaseGroupKeepPolicyYaml = $defaultPolicyYaml.Replace(
    'farmingHarvest: 96',
    'farmingHarvest: 12.5, KEEP')
$uppercaseGroupKeepParseArguments = [object[]] @([string]$uppercaseGroupKeepPolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $uppercaseGroupKeepParseArguments)) 'A positive group lifetime must accept the case-insensitive keep action.'
$uppercaseGroupKeepNormalizedPolicy = $uppercaseGroupKeepParseArguments[1]
$uppercaseGroupKeepNormalizedYaml = [string]$uppercaseGroupKeepParseArguments[2]
$farmingHarvestGroup = [Enum]::Parse($spoilageGroupType, 'FarmingHarvest')
Assert-True ([string]$getGroupExpiryAction.Invoke(
                 $uppercaseGroupKeepNormalizedPolicy,
                 [object[]] @($farmingHarvestGroup)) -eq 'KeepOriginal') 'A group keep value must normalize to the KeepOriginal action.'
Assert-True ($uppercaseGroupKeepNormalizedYaml -match '(?m)^\s*farmingHarvest:\s*12\.5,\s*keep\s*$') 'Group policy normalization must canonicalize KEEP to the lowercase keep keyword.'
Assert-True ($uppercaseGroupKeepNormalizedYaml -cnotmatch '(?m)^\s*farmingHarvest:\s*12\.5,\s*KEEP\s*$') 'Canonical group policy YAML must not preserve non-canonical keep casing.'
$uppercaseGroupKeepRoundTripArguments = [object[]] @($uppercaseGroupKeepNormalizedYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $uppercaseGroupKeepRoundTripArguments)) 'Canonical group keep YAML must parse again.'
Assert-True ([string]$uppercaseGroupKeepRoundTripArguments[2] -ceq $uppercaseGroupKeepNormalizedYaml) 'Canonical group keep YAML must remain stable across a parse/serialize round trip.'
$maximumGroupKeepPolicyYaml = $defaultPolicyYaml.Replace(
    'farmingHarvest: 96',
    'farmingHarvest: 720, keep')
$maximumGroupKeepParseArguments = [object[]] @([string]$maximumGroupKeepPolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $maximumGroupKeepParseArguments)) 'A keep group lifetime of exactly 720 hours must be accepted.'
$overMaximumGroupKeepPolicyYaml = $defaultPolicyYaml.Replace(
    'farmingHarvest: 96',
    'farmingHarvest: 721, keep')
$overMaximumGroupKeepParseArguments = [object[]] @([string]$overMaximumGroupKeepPolicyYaml, $null, '', '')
Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $overMaximumGroupKeepParseArguments)) 'A keep group lifetime above 720 hours must be rejected.'
Assert-True ([string]$overMaximumGroupKeepParseArguments[3] -match 'lifetimes\.farmingHarvest.*0 through 720') 'An over-limit keep group lifetime must report its field and valid range.'
$minimumGroupKeepPolicyYaml = $defaultPolicyYaml.Replace(
    'farmingHarvest: 96',
    'farmingHarvest: 0.000000001, keep')
$minimumGroupKeepParseArguments = [object[]] @([string]$minimumGroupKeepPolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $minimumGroupKeepParseArguments)) 'A positive fractional keep group lifetime must parse.'
$minimumGroupKeepPolicy = $minimumGroupKeepParseArguments[1]
$minimumLifetimeProperty = $minimumGroupKeepPolicy.GetType().GetProperty(
    'Lifetimes',
    [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $minimumLifetimeProperty) 'Normalized group lifetimes required for minimum-duration validation are missing.'
$minimumGroupLifetimes = $minimumLifetimeProperty.GetValue($minimumGroupKeepPolicy)
$minimumGroupLifetime = $minimumGroupLifetimes[$farmingHarvestGroup]
$minimumGroupTicksProperty = $minimumGroupLifetime.GetType().GetProperty(
    'Ticks',
    [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $minimumGroupTicksProperty) 'Normalized group lifetime ticks required for minimum-duration validation are missing.'
$minimumGroupTicks = [long]$minimumGroupTicksProperty.GetValue($minimumGroupLifetime)
Assert-True ($minimumGroupTicks -eq [TimeSpan]::TicksPerSecond) 'A positive sub-second keep group lifetime must retain the one-second internal minimum.'
foreach ($disabledGroupSpec in @(
    @('farmingHarvest', 'FarmingHarvest', '96'),
    @('feastMaterial', 'FeastMaterial', '96, keep'),
    @('feastResult', 'FeastResult', '72, keep')))
{
    $groupName = [string]$disabledGroupSpec[0]
    $group = [Enum]::Parse($spoilageGroupType, [string]$disabledGroupSpec[1])
    $disabledGroupYaml = $defaultPolicyYaml.Replace(
        "${groupName}: $($disabledGroupSpec[2])",
        "${groupName}: 0, KEEP")
    $disabledGroupParseArguments = [object[]] @($disabledGroupYaml, $null, '', '')
    Assert-True ([bool]$tryParsePolicy.Invoke($null, $disabledGroupParseArguments)) "Disabled group '$groupName' must retain a keep action."
    $disabledGroupPolicy = $disabledGroupParseArguments[1]
    Assert-True ([double]$getGroupLifetimeHours.Invoke($disabledGroupPolicy, [object[]] @($group)) -eq 0 -and
                 [long]$getGroupLifetimeTicks.Invoke($disabledGroupPolicy, [object[]] @($group)) -eq 0) "Disabled keep group '$groupName' must remain zero hours/ticks, not receive the one-second minimum."
    Assert-True ([string]$getGroupExpiryAction.Invoke($disabledGroupPolicy, [object[]] @($group)) -eq 'KeepOriginal') "Disabled keep group '$groupName' must preserve its configured action."
    $disabledGroupNormalizedYaml = [string]$disabledGroupParseArguments[2]
    Assert-True ($disabledGroupNormalizedYaml -cmatch "(?m)^\s*${groupName}:\s*0,\s*keep\s*$") "Disabled keep group '$groupName' must serialize as canonical 0, keep."
    $disabledGroupRoundTripArguments = [object[]] @($disabledGroupNormalizedYaml, $null, '', '')
    Assert-True ([bool]$tryParsePolicy.Invoke($null, $disabledGroupRoundTripArguments) -and
                 [string]$disabledGroupRoundTripArguments[2] -ceq $disabledGroupNormalizedYaml) "Disabled keep group '$groupName' must survive a parse/serialize round trip without losing keep."
}
foreach ($invalidGroupLifetime in @(
    [pscustomobject]@{ Value = '0, RottenMeat'; Error = "expiry action must be 'keep'"; Label = 'disabled group replacement prefab' },
    [pscustomobject]@{ Value = '24, RottenMeat'; Error = "expiry action must be 'keep'"; Label = 'group replacement prefab' },
    [pscustomobject]@{ Value = '24,'; Error = "expiry action must be 'keep'"; Label = 'empty group action' },
    [pscustomobject]@{ Value = '24, keep, extra'; Error = "must be '<hours>' or '<hours>, keep'"; Label = 'extra group field' },
    [pscustomobject]@{ Value = 'banana, keep'; Error = 'must be a number'; Label = 'non-numeric group lifetime' }))
{
    $invalidGroupPolicyYaml = $defaultPolicyYaml.Replace(
        'farmingHarvest: 96',
        "farmingHarvest: $($invalidGroupLifetime.Value)")
    $invalidGroupParseArguments = [object[]] @([string]$invalidGroupPolicyYaml, $null, '', '')
    Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $invalidGroupParseArguments)) "An invalid $($invalidGroupLifetime.Label) must be rejected."
    $invalidGroupError = [string]$invalidGroupParseArguments[3]
    Assert-True ($invalidGroupError -match 'lifetimes\.farmingHarvest' -and
                 $invalidGroupError.Contains([string]$invalidGroupLifetime.Error)) "An invalid $($invalidGroupLifetime.Label) must report the field and reason."
}
$maximumOverridePolicyYaml = $emptyOverridePolicyYaml.Replace(
    'overrides: []',
    "overrides:`n  - BoundaryFood, 720")
$maximumOverrideParseArguments = [object[]] @([string]$maximumOverridePolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $maximumOverrideParseArguments)) 'An exact-prefab override of exactly 720 hours must be accepted.'
$overMaximumOverridePolicyYaml = $emptyOverridePolicyYaml.Replace(
    'overrides: []',
    "overrides:`n  - BoundaryFood, 721")
$overMaximumOverrideParseArguments = [object[]] @([string]$overMaximumOverridePolicyYaml, $null, '', '')
Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $overMaximumOverrideParseArguments)) 'An exact-prefab override above 720 hours must be rejected.'
Assert-True ([string]$overMaximumOverrideParseArguments[3] -match '0 through 720') 'An over-limit override must report the 720-hour range.'
$keepOverridePolicyYaml = $emptyOverridePolicyYaml.Replace(
    'overrides: []',
    "overrides:`n  - AutomaticFood, 12.5`n  - KeepFood, 24, KEEP`n  - ReplaceFood, 36, RottenMeat")
$keepOverrideParseArguments = [object[]] @([string]$keepOverridePolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $keepOverrideParseArguments)) 'A positive exact override must accept the case-insensitive keep result.'
$keepNormalizedPolicy = $keepOverrideParseArguments[1]
$keepNormalizedYaml = [string]$keepOverrideParseArguments[2]
Assert-True ($keepNormalizedYaml -match '(?m)^\s*-\s+KeepFood,\s*24,\s*keep\s*$') 'Policy normalization must canonicalize KEEP to the lowercase keep keyword.'
Assert-True ($keepNormalizedYaml -cnotmatch '(?m)^\s*-\s+KeepFood,\s*24,\s*KEEP\s*$') 'Canonical policy YAML must not preserve non-canonical keep casing.'
$normalizedPolicyFlags = [Reflection.BindingFlags] 'Instance,Public,NonPublic'
$normalizedOverridesProperty = $keepNormalizedPolicy.GetType().GetProperty('Overrides', $normalizedPolicyFlags)
$replacementPrefabsProperty = $keepNormalizedPolicy.GetType().GetProperty('ReplacementPrefabs', $normalizedPolicyFlags)
Assert-True ($null -ne $normalizedOverridesProperty -and $null -ne $replacementPrefabsProperty) 'Normalized spoilage policy override internals required for canonical validation are missing.'
$normalizedOverrides = $normalizedOverridesProperty.GetValue($keepNormalizedPolicy)
$automaticOverride = $normalizedOverrides['AutomaticFood']
$keepOverride = $normalizedOverrides['KeepFood']
$replaceOverride = $normalizedOverrides['ReplaceFood']
$overrideFlags = [Reflection.BindingFlags] 'Instance,Public,NonPublic'
$hasResultOverrideProperty = $keepOverride.GetType().GetProperty('HasResultOverride', $overrideFlags)
$expiryActionProperty = $keepOverride.GetType().GetProperty('ExpiryAction', $overrideFlags)
$replacementPrefabProperty = $keepOverride.GetType().GetProperty('ReplacementPrefab', $overrideFlags)
Assert-True ($null -ne $hasResultOverrideProperty -and $null -ne $expiryActionProperty -and $null -ne $replacementPrefabProperty) 'Normalized item overrides must retain their explicit expiry-result contract.'
Assert-True (-not [bool]$hasResultOverrideProperty.GetValue($automaticOverride)) 'A two-field override must continue to use automatic replacement routing.'
Assert-True ([string]$expiryActionProperty.GetValue($automaticOverride) -eq 'Replace' -and
             [string]$replacementPrefabProperty.GetValue($automaticOverride) -eq 'RottenMeat') 'A two-field force-include must retain the hidden RottenMeat fallback.'
Assert-True ([bool]$hasResultOverrideProperty.GetValue($keepOverride) -and
             [string]$expiryActionProperty.GetValue($keepOverride) -eq 'KeepOriginal' -and
             [string]::IsNullOrEmpty([string]$replacementPrefabProperty.GetValue($keepOverride))) 'A keep override must be explicit, select KeepOriginal, and not masquerade as a replacement prefab.'
Assert-True ([bool]$hasResultOverrideProperty.GetValue($replaceOverride) -and
             [string]$expiryActionProperty.GetValue($replaceOverride) -eq 'Replace' -and
             [string]$replacementPrefabProperty.GetValue($replaceOverride) -eq 'RottenMeat') 'An explicit replacement override must retain replacement semantics.'
$spoilagePolicySource = Get-Content -LiteralPath (Join-Path $projectRoot 'SpoilagePolicy.cs') -Raw
$groupActionInheritanceContract = [regex]::Match(
    $spoilagePolicySource,
    '(?s)if\s*\(!itemOverride\.HasResultOverride\).*?FoodClassifier\.TryClassify\(item,\s*out overrideGroup\).*?expiryAction\s*=\s*policy\.GetExpiryAction\(overrideGroup\).*?replacementPrefab\s*=\s*expiryAction\s*==\s*SpoilageExpiryAction\.KeepOriginal')
Assert-True $groupActionInheritanceContract.Success 'A result-less exact override must inherit the classified group action, including Feast keep.'
$unclassifiedOverrideFallbackContract = [regex]::Match(
    $spoilagePolicySource,
    '(?s)else\s*\{\s*expiryAction\s*=\s*SpoilageExpiryAction\.Replace;\s*replacementPrefab\s*=\s*SpoilageDefaults\.RottenMeatPrefabName;')
Assert-True $unclassifiedOverrideFallbackContract.Success 'An unclassified result-less force-include must retain the RottenMeat replacement fallback.'
Assert-True ($spoilagePolicySource.Contains('SpoilageExpiryAction expiryAction = itemOverride.ExpiryAction;') -and
             $spoilagePolicySource.Contains('if (!itemOverride.HasResultOverride)')) 'An explicit exact-override result must remain authoritative over the group action.'
$replacementPrefabs = $replacementPrefabsProperty.GetValue($keepNormalizedPolicy)
Assert-True (-not [bool]$replacementPrefabs.Contains('keep')) 'The reserved keep keyword must never enter the replacement-terminal prefab set.'
$keepRoundTripArguments = [object[]] @($keepNormalizedYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $keepRoundTripArguments)) 'Canonical keep policy YAML must parse again.'
Assert-True ([string]$keepRoundTripArguments[2] -ceq $keepNormalizedYaml) 'Canonical keep policy YAML must be stable across a parse/serialize round trip.'
$disabledKeepPolicyYaml = $emptyOverridePolicyYaml.Replace(
    'overrides: []',
    "overrides:`n  - DisabledKeepFood, 0, keep")
$disabledKeepParseArguments = [object[]] @([string]$disabledKeepPolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $disabledKeepParseArguments)) 'A disabled exact override must permit retaining keep for later re-enabling.'
$disabledKeepPolicy = $disabledKeepParseArguments[1]
$disabledKeepOverride = $normalizedOverridesProperty.GetValue($disabledKeepPolicy)['DisabledKeepFood']
$overrideLifetimeTicksProperty = $disabledKeepOverride.GetType().GetProperty('LifetimeTicks', $overrideFlags)
$overrideHoursProperty = $disabledKeepOverride.GetType().GetProperty('Hours', $overrideFlags)
Assert-True ([long]$overrideLifetimeTicksProperty.GetValue($disabledKeepOverride) -eq 0 -and
             [double]$overrideHoursProperty.GetValue($disabledKeepOverride) -eq 0) 'Zero-hour keep overrides must be disabled, not assigned the positive one-second lifetime minimum.'
Assert-True ([bool]$hasResultOverrideProperty.GetValue($disabledKeepOverride) -and
             [string]$expiryActionProperty.GetValue($disabledKeepOverride) -eq 'KeepOriginal' -and
             [string]::IsNullOrEmpty([string]$replacementPrefabProperty.GetValue($disabledKeepOverride))) 'Disabled keep overrides must retain the explicit KeepOriginal action without a replacement prefab.'
$disabledKeepNormalizedYaml = [string]$disabledKeepParseArguments[2]
Assert-True ($disabledKeepNormalizedYaml -cmatch '(?m)^\s*-\s+DisabledKeepFood,\s*0,\s*keep\s*$') 'Disabled keep overrides must serialize with their canonical keep suffix intact.'
$disabledKeepRoundTripArguments = [object[]] @($disabledKeepNormalizedYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $disabledKeepRoundTripArguments) -and
             [string]$disabledKeepRoundTripArguments[2] -ceq $disabledKeepNormalizedYaml) 'Disabled exact keep overrides must remain stable across a parse/serialize round trip.'
$disabledReplacementPolicyYaml = $emptyOverridePolicyYaml.Replace(
    'overrides: []',
    "overrides:`n  - DisabledReplacementFood, 0, RottenMeat")
$disabledReplacementParseArguments = [object[]] @($disabledReplacementPolicyYaml, $null, '', '')
Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $disabledReplacementParseArguments)) 'A disabled exact override must still reject a replacement prefab.'
Assert-True ([string]$disabledReplacementParseArguments[3] -match "can only retain 'keep' as an expiry result") 'Rejected zero-hour replacement syntax must explain that only keep may be retained.'
$blacklistPolicyYaml = $defaultPolicyYaml.Replace(
    'chefChoiceBlacklist: []',
    "chefChoiceBlacklist:`n  - ZedFood`n  - applefood")
$parsePolicyArguments = [object[]] @([string]$blacklistPolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $parsePolicyArguments)) 'A valid Chef Choice blacklist must parse.'
$normalizedPolicy = $parsePolicyArguments[1]
$normalizedPolicyYaml = [string]$parsePolicyArguments[2]
Assert-True ($normalizedPolicyYaml.IndexOf('applefood', [StringComparison]::Ordinal) -lt $normalizedPolicyYaml.IndexOf('ZedFood', [StringComparison]::Ordinal)) 'Chef Choice blacklist entries must serialize in deterministic order.'
$blacklistProperty = $normalizedPolicy.GetType().GetProperty(
    'ChefChoiceBlacklist',
    [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $blacklistProperty) 'The normalized policy must retain the Chef Choice blacklist.'
$normalizedBlacklist = $blacklistProperty.GetValue($normalizedPolicy)
$blacklistContains = $normalizedBlacklist.GetType().GetMethod('Contains')
Assert-True ([bool]$blacklistContains.Invoke($normalizedBlacklist, [object[]] @('APPLEFOOD'))) 'Chef Choice blacklist matching must be case-insensitive.'
$duplicateBlacklistYaml = $defaultPolicyYaml.Replace(
    'chefChoiceBlacklist: []',
    "chefChoiceBlacklist:`n  - DuplicateFood`n  - duplicatefood")
$duplicateParseArguments = [object[]] @([string]$duplicateBlacklistYaml, $null, '', '')
Assert-True (-not [bool]$tryParsePolicy.Invoke($null, $duplicateParseArguments)) 'Case-insensitive duplicate Chef Choice blacklist entries must be rejected atomically.'

$pukeFoodRemovalOrderType = Get-TypeRequired $assembly 'FineDining.PukeFoodRemovalOrder'
Assert-True $pukeFoodRemovalOrderType.IsEnum 'Puke food removal order must be represented by an enum.'
$pukeFoodRemovalOrderNames = @([Enum]::GetNames($pukeFoodRemovalOrderType))
Assert-True ($pukeFoodRemovalOrderNames.Count -eq 3 -and
             $pukeFoodRemovalOrderNames[0] -eq 'OldestFirst' -and
             $pukeFoodRemovalOrderNames[1] -eq 'Random' -and
             $pukeFoodRemovalOrderNames[2] -eq 'NewestFirst') 'Puke food removal order must expose exactly OldestFirst, Random, and NewestFirst.'
$oldestPukeRemovalOrder = [Enum]::Parse($pukeFoodRemovalOrderType, 'OldestFirst')
$randomPukeRemovalOrder = [Enum]::Parse($pukeFoodRemovalOrderType, 'Random')
$newestPukeRemovalOrder = [Enum]::Parse($pukeFoodRemovalOrderType, 'NewestFirst')

$dietConfigType = Get-TypeRequired $assembly 'FineDining.DietConfig'
Assert-Method $dietConfigType 'GetFullCourseMultiplier'
Assert-Method $dietConfigType 'GetBaseSlotScale'
Assert-Method $dietConfigType 'CalculateBaseSlotScale'
Assert-Method $dietConfigType 'GetPukeFoodRemovalOrder'
Assert-Method $dietConfigType 'GetChefMultiplierMin'
Assert-Method $dietConfigType 'GetChefMultiplierMax'
Assert-Method $dietConfigType 'GetChefHighTierSelectionStrength'
Assert-Method $dietConfigType 'GetChefMultiplierModeAtMaxCooking'
Assert-Method $dietConfigType 'GetChefRecentFoodPreferencePercent'
Assert-Method $dietConfigType 'GetCookingExperiencePerFoodEaten'
Assert-True ($null -eq $dietConfigType.GetMethod('GetChefMultiplierHighRollBias', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'The removed high-roll bias getter must not return.'
$fullCourseMultiplierConfigField = $dietConfigType.GetField(
    'FullCourseMultiplier',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
$maxFoodSlotsConfigField = $dietConfigType.GetField(
    'MaxFoodSlots',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
$foodStatScaleConfigField = $dietConfigType.GetField(
    'FoodStatScale',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
$chefTierSelectionStrengthConfigField = $dietConfigType.GetField(
    'ChefHighTierSelectionStrength',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
$chefMultiplierMinConfigField = $dietConfigType.GetField(
    'ChefMultiplierMin',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
$chefMultiplierMaxConfigField = $dietConfigType.GetField(
    'ChefMultiplierMax',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
$chefMultiplierModeConfigField = $dietConfigType.GetField(
    'ChefMultiplierModeAtMaxCooking',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
$cookingExperienceConfigField = $dietConfigType.GetField(
    'CookingExperiencePerFoodEaten',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
$pukeRemovalOrderConfigField = $dietConfigType.GetField(
    'PukeRemovalOrder',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
Assert-True ($null -ne $chefMultiplierMinConfigField) 'Chef Multiplier Minimum config storage is missing.'
Assert-True ($null -ne $chefMultiplierMaxConfigField) 'Chef Multiplier Maximum config storage is missing.'
Assert-True ($null -ne $chefMultiplierModeConfigField) 'Chef multiplier mode config storage is missing.'
Assert-True ($null -ne $chefTierSelectionStrengthConfigField) 'Chef high-tier selection strength config storage is missing.'
Assert-True ($null -ne $cookingExperienceConfigField) 'Cooking experience-per-food config storage is missing.'
Assert-True ($null -ne $pukeRemovalOrderConfigField) 'Puke food removal-order config storage is missing.'
Assert-True ($null -ne $fullCourseMultiplierConfigField) 'Full Course multiplier config storage is missing.'
Assert-True ($null -ne $maxFoodSlotsConfigField) 'Maximum food-slot config storage is missing.'
Assert-True ($null -ne $foodStatScaleConfigField) 'Shared food stat scale config storage is missing.'
Assert-True ($null -eq $dietConfigType.GetField('SixSlotFoodStatScale', [Reflection.BindingFlags] 'Static,Public,NonPublic') -and
             $null -eq $dietConfigType.GetField('NineSlotFoodStatScale', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'Separate six- and nine-slot scale settings must be removed without legacy aliases.'
Assert-True ($chefMultiplierMinConfigField.FieldType.IsGenericType -and $chefMultiplierMinConfigField.FieldType.GetGenericArguments()[0] -eq [float]) 'Chef Multiplier Minimum must remain a floating-point ConfigEntry.'
Assert-True ($chefMultiplierMaxConfigField.FieldType.IsGenericType -and $chefMultiplierMaxConfigField.FieldType.GetGenericArguments()[0] -eq [float]) 'Chef Multiplier Maximum must remain a floating-point ConfigEntry.'
Assert-True ($chefMultiplierModeConfigField.FieldType.IsGenericType -and $chefMultiplierModeConfigField.FieldType.GetGenericArguments()[0] -eq [float]) 'Chef multiplier mode must remain a floating-point ConfigEntry.'
Assert-True ($chefTierSelectionStrengthConfigField.FieldType.IsGenericType -and $chefTierSelectionStrengthConfigField.FieldType.GetGenericArguments()[0] -eq [float]) 'Chef high-tier selection strength must remain a floating-point ConfigEntry.'
Assert-True ($cookingExperienceConfigField.FieldType.IsGenericType -and $cookingExperienceConfigField.FieldType.GetGenericArguments()[0] -eq [float]) 'Cooking experience per food eaten must use a floating-point ConfigEntry.'
Assert-True ($pukeRemovalOrderConfigField.FieldType.IsGenericType -and
             $pukeRemovalOrderConfigField.FieldType.GetGenericTypeDefinition().FullName -eq 'BepInEx.Configuration.ConfigEntry`1' -and
             $pukeRemovalOrderConfigField.FieldType.GetGenericArguments()[0] -eq $pukeFoodRemovalOrderType) 'Puke food removal order must use a ConfigEntry of the dedicated enum.'
Assert-True ($fullCourseMultiplierConfigField.FieldType.IsGenericType -and $fullCourseMultiplierConfigField.FieldType.GetGenericArguments()[0] -eq [float]) 'Full Course multiplier must remain a floating-point ConfigEntry.'
Assert-True ($maxFoodSlotsConfigField.FieldType.IsGenericType -and $maxFoodSlotsConfigField.FieldType.GetGenericArguments()[0] -eq [int]) 'Maximum food slots must use an integer ConfigEntry.'
Assert-True ($foodStatScaleConfigField.FieldType.IsGenericType -and $foodStatScaleConfigField.FieldType.GetGenericArguments()[0] -eq [float]) 'The shared food stat scale must use a floating-point ConfigEntry.'
Assert-True ($null -eq $dietConfigType.GetMethod('GetKnownFoodsForFullSlots', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'Removed configurable slot-unlock target must not return.'
Assert-True ($null -eq $dietConfigType.GetField('KnownFoodsForFullSlots', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'Removed configurable slot-unlock target storage must not return.'
Assert-True ($null -eq $dietConfigType.GetField('SlotScaleConstant', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'Removed single slot-scale config storage must not return.'
Assert-True ($null -eq $dietConfigType.GetField('ChefMultiplierHighRollBias', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'The removed high-roll bias config storage must not return.'
Assert-True ($null -eq $dietConfigType.GetMethod('GetChefHighTierBias', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'The replaced high-tier bias getter must not return.'
Assert-True ($null -eq $dietConfigType.GetField('ChefHighTierBias', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'The replaced high-tier bias config storage must not return.'
Assert-True ($null -eq $dietConfigType.GetMethod('GetFullStraightMultiplier', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'A legacy Full Straight multiplier getter must not be introduced.'
Assert-True ($null -eq $dietConfigType.GetField('FullStraightMultiplier', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'A legacy Full Straight multiplier config field must not be introduced.'
$chefPreferenceConfigField = $dietConfigType.GetField(
    'ChefRecentFoodPreferencePercent',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
Assert-True ($null -ne $chefPreferenceConfigField) 'Chef recent-food preference config storage is missing.'
Assert-True ($chefPreferenceConfigField.FieldType.IsGenericType -and $chefPreferenceConfigField.FieldType.GetGenericArguments()[0] -eq [float]) 'Chef recent-food preference config must remain a floating-point ConfigEntry.'
$dietConfigSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\DietConfig.cs') -Raw
$getPukeFoodRemovalOrder = Get-MethodRequired $dietConfigType 'GetPukeFoodRemovalOrder'
Assert-True ($getPukeFoodRemovalOrder.ReturnType -eq $pukeFoodRemovalOrderType -and
             $getPukeFoodRemovalOrder.GetParameters().Count -eq 0) 'The Puke removal-order getter must return the synchronized enum value without arguments.'
Assert-True ([string]$getPukeFoodRemovalOrder.Invoke($null, [object[]] @()) -eq 'NewestFirst') 'Puke food removal must default to NewestFirst before configuration is initialized.'
$pukeRemovalConfigBinding = [regex]::Match(
    $dietConfigSource,
    '(?s)PukeRemovalOrder\s*=\s*BindSynced\(\s*config,\s*configSync,\s*ConfigPresentation\.Diet,\s*"Puke Food Removal Order",\s*PukeFoodRemovalOrder\.NewestFirst,\s*ConfigPresentation\.Synced\(\s*"(?<Description>[^"]*)"')
Assert-True $pukeRemovalConfigBinding.Success 'Puke food removal order must bind in the synchronized Diet section with NewestFirst as its default.'
$pukeRemovalConfigDescription = $pukeRemovalConfigBinding.Groups['Description'].Value
Assert-True ([regex]::IsMatch($pukeRemovalConfigDescription, '(?i)\bvanilla\b.*\brandom\b')) 'Puke removal-order config help must explain that vanilla removal is random.'
$maxSlotsConfigStart = $dietConfigSource.IndexOf(
    'MaxFoodSlots = BindSynced(',
    [StringComparison]::Ordinal)
$foodStatScaleConfigStart = $dietConfigSource.IndexOf(
    'FoodStatScale = BindSynced(',
    $maxSlotsConfigStart,
    [StringComparison]::Ordinal)
$fullCourseConfigStart = $dietConfigSource.IndexOf(
    'FullCourseMultiplier = BindSynced(',
    $foodStatScaleConfigStart,
    [StringComparison]::Ordinal)
Assert-True ($maxSlotsConfigStart -ge 0 -and $foodStatScaleConfigStart -gt $maxSlotsConfigStart -and $fullCourseConfigStart -gt $foodStatScaleConfigStart) 'Maximum slots, shared food stat scale, and Full Course must be bound together in display order.'
$maxSlotsConfigSource = $dietConfigSource.Substring($maxSlotsConfigStart, $foodStatScaleConfigStart - $maxSlotsConfigStart)
$foodStatScaleConfigSource = $dietConfigSource.Substring($foodStatScaleConfigStart, $fullCourseConfigStart - $foodStatScaleConfigStart)
Assert-True ($maxSlotsConfigSource.Contains('"Maximum Food Slots"') -and [regex]::IsMatch($maxSlotsConfigSource, '(?m)^\s*9,\s*$')) 'Maximum food slots must retain its config key and default to nine.'
Assert-True ([regex]::IsMatch($maxSlotsConfigSource, 'new\s+AcceptableValueRange<int>\(\s*3,\s*9\)')) 'Maximum food slots must permit every integer from three through nine.'
Assert-True ($foodStatScaleConfigSource.Contains('"Food Stat Scale"') -and [regex]::IsMatch($foodStatScaleConfigSource, '(?m)^\s*0\.9f,\s*$')) 'The shared food stat scale must default to 90% of a filled vanilla three-food total.'
Assert-True ([regex]::IsMatch($foodStatScaleConfigSource, 'new\s+AcceptableValueRange<float>\(\s*0\.1f,\s*3f\)')) 'The shared food stat scale must be constrained to x0.10-x3.00.'
Assert-True ($foodStatScaleConfigSource.Contains('3 * this value / currently unlocked slots')) 'Food stat scale config help must explain the dynamic per-food formula.'
$recentHistoryConfigStart = $dietConfigSource.IndexOf(
    'RecentHistorySize = BindSynced(',
    $fullCourseConfigStart,
    [StringComparison]::Ordinal)
Assert-True ($fullCourseConfigStart -ge 0 -and $recentHistoryConfigStart -gt $fullCourseConfigStart) 'Full Course multiplier must be bound through the synchronized Diet config path in display order.'
$fullCourseConfigSource = $dietConfigSource.Substring(
    $fullCourseConfigStart,
    $recentHistoryConfigStart - $fullCourseConfigStart)
Assert-True ($fullCourseConfigSource.Contains('"Full Course Multiplier"')) 'Full Course multiplier config key is incorrect.'
Assert-True ([regex]::IsMatch($fullCourseConfigSource, '(?m)^\s*1\.2f,\s*$')) 'Full Course multiplier must default to x1.20.'
Assert-True ([regex]::IsMatch($fullCourseConfigSource, 'new\s+AcceptableValueRange<float>\(\s*1f,\s*5f\)')) 'Full Course multiplier must be constrained to x1.00-x5.00.'
$diminishingThresholdConfigStart = $dietConfigSource.IndexOf(
    'DiminishingThreshold = BindSynced(',
    $recentHistoryConfigStart,
    [StringComparison]::Ordinal)
$diminishingFactorConfigStart = $dietConfigSource.IndexOf(
    'DiminishingFactor = BindSynced(',
    $diminishingThresholdConfigStart,
    [StringComparison]::Ordinal)
$chefMultiplierMinConfigStart = $dietConfigSource.IndexOf(
    'ChefMultiplierMin = BindSynced(',
    [StringComparison]::Ordinal)
Assert-True ($diminishingThresholdConfigStart -gt $recentHistoryConfigStart -and
             $diminishingFactorConfigStart -gt $diminishingThresholdConfigStart -and
             $chefMultiplierMinConfigStart -gt $diminishingFactorConfigStart) 'Recent-history size, diminishing threshold/factor, and Chef settings must retain their display order.'
$diminishingThresholdConfigSource = $dietConfigSource.Substring(
    $diminishingThresholdConfigStart,
    $diminishingFactorConfigStart - $diminishingThresholdConfigStart)
$diminishingFactorConfigSource = $dietConfigSource.Substring(
    $diminishingFactorConfigStart,
    $chefMultiplierMinConfigStart - $diminishingFactorConfigStart)
Assert-True ($diminishingThresholdConfigSource.Contains('"Diminishing Returns Start Count"') -and
             [regex]::IsMatch($diminishingThresholdConfigSource, '(?m)^\s*4,\s*$')) 'Diminishing returns must begin with the fourth total consumption by default.'
Assert-True ($diminishingFactorConfigSource.Contains('"Diminishing Returns Multiplier"') -and
             [regex]::IsMatch($diminishingFactorConfigSource, '(?m)^\s*0\.75f,\s*$')) 'The default next-consumption diminishing preview must use x0.75.'
$chefMultiplierModeConfigStart = $dietConfigSource.IndexOf(
    'ChefMultiplierModeAtMaxCooking = BindSynced(',
    $chefMultiplierMinConfigStart,
    [StringComparison]::Ordinal)
$chefMultiplierMaxConfigStart = $dietConfigSource.IndexOf(
    'ChefMultiplierMax = BindSynced(',
    $chefMultiplierModeConfigStart,
    [StringComparison]::Ordinal)
$chefTierSelectionStrengthConfigStart = $dietConfigSource.IndexOf(
    'ChefHighTierSelectionStrength = BindSynced(',
    $chefMultiplierMaxConfigStart,
    [StringComparison]::Ordinal)
$chefPreferenceConfigStart = $dietConfigSource.IndexOf(
    'ChefRecentFoodPreferencePercent = BindSynced(',
    $chefTierSelectionStrengthConfigStart,
    [StringComparison]::Ordinal)
Assert-True ($chefMultiplierMinConfigStart -ge 0 -and $chefMultiplierModeConfigStart -gt $chefMultiplierMinConfigStart -and $chefMultiplierMaxConfigStart -gt $chefMultiplierModeConfigStart -and $chefTierSelectionStrengthConfigStart -gt $chefMultiplierMaxConfigStart -and $chefPreferenceConfigStart -gt $chefTierSelectionStrengthConfigStart) 'Chef multiplier minimum, most-likely value, maximum, and tier/history preferences must be bound through the synchronized config path in display order.'
$chefMultiplierMinConfigSource = $dietConfigSource.Substring(
    $chefMultiplierMinConfigStart,
    $chefMultiplierModeConfigStart - $chefMultiplierMinConfigStart)
$chefMultiplierModeConfigSource = $dietConfigSource.Substring(
    $chefMultiplierModeConfigStart,
    $chefMultiplierMaxConfigStart - $chefMultiplierModeConfigStart)
$chefMultiplierMaxConfigSource = $dietConfigSource.Substring(
    $chefMultiplierMaxConfigStart,
    $chefTierSelectionStrengthConfigStart - $chefMultiplierMaxConfigStart)
Assert-True ($chefMultiplierMinConfigSource.Contains('"Minimum Multiplier"')) 'Chef multiplier minimum config key is incorrect.'
Assert-True ([regex]::IsMatch($chefMultiplierMinConfigSource, '(?m)^\s*1\.1f,\s*$')) 'Chef Multiplier Minimum must default to x1.10.'
Assert-True ([regex]::IsMatch($chefMultiplierMinConfigSource, 'new\s+AcceptableValueRange<float>\(ChefChoiceMath\.MinimumAllowedMultiplier,\s*5f\)')) 'Chef Multiplier Minimum must be constrained to x1.00-x5.00.'
Assert-True ($chefMultiplierMaxConfigSource.Contains('"Maximum Multiplier"')) 'Chef multiplier maximum config key is incorrect.'
Assert-True ([regex]::IsMatch($chefMultiplierMaxConfigSource, '(?m)^\s*1\.5f,\s*$')) 'Chef Multiplier Maximum must default to x1.50.'
Assert-True ([regex]::IsMatch($chefMultiplierMaxConfigSource, 'new\s+AcceptableValueRange<float>\(ChefChoiceMath\.MinimumAllowedMultiplier,\s*5f\)')) 'Chef Multiplier Maximum must be constrained to x1.00-x5.00 before effective bound normalization.'
$chefTierSelectionStrengthConfigSource = $dietConfigSource.Substring(
    $chefTierSelectionStrengthConfigStart,
    $chefPreferenceConfigStart - $chefTierSelectionStrengthConfigStart)
Assert-True ($chefTierSelectionStrengthConfigSource.Contains('"High-Tier Selection Strength"')) 'Chef high-tier selection strength config key is incorrect.'
Assert-True ([regex]::IsMatch($chefTierSelectionStrengthConfigSource, '(?m)^\s*5f,\s*$')) 'Chef high-tier selection strength must default to 5.'
Assert-True ([regex]::IsMatch($chefTierSelectionStrengthConfigSource, 'new\s+AcceptableValueRange<float>\(\s*0f,\s*ChefChoiceMath\.MaximumTierSelectionStrength\)')) 'Chef high-tier selection strength must be constrained to 0-20.'
Assert-True ($chefTierSelectionStrengthConfigSource.Contains('Higher values make high-tier foods more likely at high Cooking levels.')) 'Chef high-tier selection strength must explain the effect of larger values.'
Assert-True ($chefTierSelectionStrengthConfigSource.Contains('0 disables tier weighting.')) 'Chef high-tier selection strength must explain that zero disables tier weighting.'
Assert-True ($chefMultiplierModeConfigSource.Contains('"Most Likely Multiplier at Max Cooking Level"')) 'Chef multiplier mode config key is incorrect.'
Assert-True ([regex]::IsMatch($chefMultiplierModeConfigSource, '(?m)^\s*1\.5f,\s*$')) 'Cooking level 100 multiplier mode must default to x1.50.'
Assert-True ([regex]::IsMatch($chefMultiplierModeConfigSource, 'new\s+AcceptableValueRange<float>\(ChefChoiceMath\.MinimumAllowedMultiplier,\s*5f\)')) 'Chef multiplier mode must accept the same x1.00-x5.00 raw range before effective Min-Max clamping.'
$cookingExperienceConfigStart = $dietConfigSource.IndexOf(
    'CookingExperiencePerFoodEaten = BindSynced(',
    $chefPreferenceConfigStart,
    [StringComparison]::Ordinal)
Assert-True ($chefPreferenceConfigStart -ge 0 -and $cookingExperienceConfigStart -gt $chefPreferenceConfigStart) 'Chef recent-food preference must be bound through the synchronized config path.'
$chefPreferenceConfigSource = $dietConfigSource.Substring(
    $chefPreferenceConfigStart,
    $cookingExperienceConfigStart - $chefPreferenceConfigStart)
Assert-True ($chefPreferenceConfigSource.Contains('"Recent Food-Type Preference (%)"')) 'Chef recent-food preference config key is incorrect.'
Assert-True ([regex]::IsMatch($chefPreferenceConfigSource, '(?m)^\s*70f,\s*$')) 'Chef recent-food preference must default to 70%.'
Assert-True ([regex]::IsMatch($chefPreferenceConfigSource, 'new\s+AcceptableValueRange<float>\(0f,\s*100f\)')) 'Chef recent-food preference must be constrained to 0-100%.'
$cookingBonusConfigStart = $dietConfigSource.IndexOf(
    'CookingBonusChanceAtMaxCookingPercent = BindSynced(',
    $cookingExperienceConfigStart,
    [StringComparison]::Ordinal)
$fermenterBonusConfigStart = $dietConfigSource.IndexOf(
    'FermenterOutputBonusChanceAtMaxCookingPercent = BindSynced(',
    $cookingBonusConfigStart,
    [StringComparison]::Ordinal)
$bonusExclusionConfigStart = $dietConfigSource.IndexOf(
    'CookingBonusExcludedOutputPrefabs = BindSynced(',
    $fermenterBonusConfigStart,
    [StringComparison]::Ordinal)
Assert-True ($cookingExperienceConfigStart -ge 0 -and
             $cookingBonusConfigStart -gt $cookingExperienceConfigStart -and
             $fermenterBonusConfigStart -gt $cookingBonusConfigStart -and
             $bonusExclusionConfigStart -gt $fermenterBonusConfigStart) 'Food experience, production, Fermenter, and blacklist config bindings must retain their 450/400/350/300 order.'
$cookingExperienceConfigSource = $dietConfigSource.Substring(
    $cookingExperienceConfigStart,
    $cookingBonusConfigStart - $cookingExperienceConfigStart)
$cookingBonusConfigSource = $dietConfigSource.Substring(
    $cookingBonusConfigStart,
    $fermenterBonusConfigStart - $cookingBonusConfigStart)
$fermenterBonusConfigSource = $dietConfigSource.Substring(
    $fermenterBonusConfigStart,
    $bonusExclusionConfigStart - $fermenterBonusConfigStart)
Assert-True ($cookingExperienceConfigSource.Contains('"Cooking Experience per Food Eaten"')) 'The food-eaten Cooking experience config key is incorrect.'
Assert-True ([regex]::IsMatch($cookingExperienceConfigSource, '(?m)^\s*0\.15f,\s*$')) 'Food-eaten Cooking experience must default to 0.15.'
Assert-True ([regex]::IsMatch($cookingExperienceConfigSource, 'new\s+AcceptableValueRange<float>\(0f,\s*1f\)')) 'Food-eaten Cooking experience must be constrained to 0-1.'
Assert-True ($cookingBonusConfigSource.Contains('"Production Bonus Chance at Max Cooking (%)"')) 'The production-bonus chance config key is incorrect.'
Assert-True ([regex]::IsMatch($cookingBonusConfigSource, '(?m)^\s*25f,\s*$')) 'Production bonus chance must default to 25% at Cooking level 100.'
Assert-True ($cookingBonusConfigSource.Contains('CookingProductionBonusCore.MaximumChanceAtMaxCookingPercent')) 'Production bonus chance must use the shared 25% maximum.'
Assert-True ($fermenterBonusConfigSource.Contains('"Fermenter Output Bonus Chance at Max Cooking (%)"')) 'The Fermenter output-bonus chance config key is incorrect.'
Assert-True ([regex]::IsMatch($fermenterBonusConfigSource, '(?m)^\s*20f,\s*$')) 'Fermenter output bonus chance must default to 20% at Cooking level 100.'
Assert-True ($fermenterBonusConfigSource.Contains('CookingProductionBonusCore.MaximumChanceAtMaxCookingPercent')) 'Fermenter output bonus chance must use the shared 25% maximum.'
Assert-True (-not $dietConfigSource.Contains('Production Bonus Chance Scale (%)') -and
             -not $dietConfigSource.Contains('CookingBonusOutputPercent') -and
             -not $dietConfigSource.Contains('GetCookingBonusOutputPercent')) 'The former percentage-of-vanilla production bonus setting must not remain.'
$bindSyncedStart = $dietConfigSource.IndexOf(
    'private static ConfigEntry<T> BindSynced<T>(',
    [StringComparison]::Ordinal)
Assert-True ($bindSyncedStart -ge 0) 'The synchronized Diet config helper is missing.'
$bindSyncedSource = $dietConfigSource.Substring($bindSyncedStart)
Assert-True ($bindSyncedSource.Contains('configSync.AddConfigEntry(entry)')) 'Diet synchronized config entries must be registered with ServerSync.'
Assert-True ($bindSyncedSource.Contains('synced.SynchronizedConfig = true;')) 'Diet synchronized config entries must remain server-authoritative.'

$chefTierCatalogType = Get-TypeRequired $assembly 'FineDining.ChefFoodTierCatalog'
$resourceMapPolicyType = Get-TypeRequired $assembly 'FineDining.ChefResourceMapPolicy'
$resourceMapSnapshotType = Get-TypeRequired $assembly 'FineDining.ChefResourceMapSnapshot'
Assert-True ((Get-Constant $resourceMapPolicyType 'ResourceMapFileName') -eq 'ResourceMap.yml') 'ResourceMap filename must preserve its cross-platform casing.'
Assert-True ((Get-Constant $resourceMapPolicyType 'DefaultResourceMapResourceName') -eq 'FineDining.Resources.Defaults.ResourceMap.yml') 'Embedded ResourceMap resource ID is incorrect.'
Assert-True ((Get-Constant $resourceMapPolicyType 'SyncedYamlIdentifier') -eq 'finedining_resource_map_yaml') 'ResourceMap sync identifier is incorrect.'
$resourceMapPolicyFields = @($resourceMapPolicyType.GetFields([Reflection.BindingFlags] 'Static,Public,NonPublic'))
Assert-True (@($resourceMapPolicyFields | Where-Object { $_.FieldType -eq [IO.FileSystemWatcher] }).Count -eq 1) 'ResourceMap policy must own exactly one hot-reload watcher.'
Assert-True (@($resourceMapPolicyFields | Where-Object { $_.FieldType -eq [Timers.Timer] }).Count -eq 1) 'ResourceMap policy must own exactly one debounce timer.'
$resourceMapSyncedFields = @($resourceMapPolicyFields | Where-Object {
    $_.FieldType.IsGenericType -and
    $_.FieldType.GetGenericTypeDefinition().FullName -eq 'ServerSync.CustomSyncedValue`1' -and
    $_.FieldType.GetGenericArguments()[0] -eq [string]
})
Assert-True ($resourceMapSyncedFields.Count -eq 1) 'ResourceMap policy must own exactly one synchronized YAML string.'
$defaultResourceMapProperty = $resourceMapPolicyType.GetProperty(
    'DefaultResourceMapYaml',
    [Reflection.BindingFlags] 'Static,Public,NonPublic')
Assert-True ($null -ne $defaultResourceMapProperty) 'The embedded ResourceMap YAML accessor is missing.'
$defaultResourceMapYaml = [string]$defaultResourceMapProperty.GetValue($null)
$defaultResourceMapPath = Join-Path $projectRoot 'Resources\Defaults\ResourceMap.yml'
$sourceDefaultResourceMapYaml = (Get-Content -LiteralPath $defaultResourceMapPath -Raw).
    Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd([char[]] @("`n")) + "`n"
Assert-True ($defaultResourceMapYaml -eq $sourceDefaultResourceMapYaml) 'Embedded ResourceMap.yml must match its editable source default.'
$tryParseResourceMap = Get-MethodRequired $resourceMapPolicyType 'TryParseResourceMapYaml'
function Parse-ResourceMap([string] $Yaml)
{
    $arguments = [object[]] @($Yaml, $null, '')
    $parsed = [bool]$tryParseResourceMap.Invoke($null, $arguments)
    return @($parsed, $arguments[1], [string]$arguments[2])
}

$defaultMapParse = Parse-ResourceMap $defaultResourceMapYaml
Assert-True ([bool]$defaultMapParse[0]) "Embedded ResourceMap.yml must parse: $($defaultMapParse[2])"
$defaultMap = $defaultMapParse[1]
$tierCountProperty = $resourceMapSnapshotType.GetProperty(
    'TierCount',
    [Reflection.BindingFlags] 'Instance,Public,NonPublic')
$resourceCountProperty = $resourceMapSnapshotType.GetProperty(
    'ResourceCount',
    [Reflection.BindingFlags] 'Instance,Public,NonPublic')
$tierNamesProperty = $resourceMapSnapshotType.GetProperty(
    'TierNames',
    [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $tierCountProperty -and [int]$tierCountProperty.GetValue($defaultMap) -eq 8) 'The default ResourceMap must retain eight ordered tiers.'
Assert-True ($null -ne $resourceCountProperty -and [int]$resourceCountProperty.GetValue($defaultMap) -gt 0) 'The default ResourceMap must contain resource tokens.'
Assert-True ($null -ne $tierNamesProperty) 'The ResourceMap snapshot must expose its ordered tier names.'
$defaultTierNames = @($tierNamesProperty.GetValue($defaultMap))
Assert-True (($defaultTierNames -join ',') -eq 'Meadows,BlackForest,Swamp,Ocean,Mountain,Plains,Mistlands,AshLands') 'The default ResourceMap tier order must remain stable from low to high.'
$getTierName = Get-MethodRequired $resourceMapSnapshotType 'GetTierName'
Assert-True ([string]$getTierName.Invoke($defaultMap, [object[]] @(0)) -eq 'Meadows') 'The first default ResourceMap tier must be Meadows.'
Assert-True ([string]$getTierName.Invoke($defaultMap, [object[]] @(7)) -eq 'AshLands') 'The last default ResourceMap tier must be AshLands.'
$tryGetSnapshotResourceTier = Get-MethodRequired $resourceMapSnapshotType 'TryGetResourceTier'
$resourceTierArguments = [object[]] @('Resin', 0)
Assert-True ([bool]$tryGetSnapshotResourceTier.Invoke($defaultMap, $resourceTierArguments)) 'The default Chef ResourceMap must contain Resin.'
Assert-True ([int]$resourceTierArguments[1] -eq 0) 'Resin must retain its Meadows resource tier.'
$resourceTierArguments = [object[]] @('$item_AsksvinMeat', 0)
Assert-True ([bool]$tryGetSnapshotResourceTier.Invoke($defaultMap, $resourceTierArguments)) 'ResourceMap token normalization must strip item localization prefixes.'
Assert-True ([int]$resourceTierArguments[1] -eq 7) 'Ashlands resources must resolve to the highest default Chef tier.'
foreach ($resourceTierCase in @(
    @('TrophyFrostTroll', 1),
    @('CryptKey', 2),
    @('TrophyAbomination', 2),
    @('SerpentMeat', 3),
    @('TrophySerpent', 3),
    @('Wishbone', 4),
    @('FreezeGland', 4),
    @('Onion', 4),
    @('TrophyHatchling', 4),
    @('TrophyFenring', 4),
    @('Cloudberry', 5),
    @('TrophyBjornUndead', 5),
    @('TrophyGoblin', 5),
    @('TrophyLox', 5),
    @('DvergrKey', 6),
    @('MushroomMagecap', 6),
    @('MushroomJotunPuffs', 6),
    @('RoyalJelly', 6),
    @('MushroomSmokePuff', 7),
    @('Vineberry', 7),
    @('Fiddleheadfern', 7),
    @('TrophySeekerQueen', 7),
    @('TrophyCharredMelee', 7)))
{
    $resourceTierArguments = [object[]] @([string]$resourceTierCase[0], 0)
    Assert-True ([bool]$tryGetSnapshotResourceTier.Invoke($defaultMap, $resourceTierArguments)) "Expanded default ResourceMap is missing $($resourceTierCase[0])."
    Assert-True ([int]$resourceTierArguments[1] -eq [int]$resourceTierCase[1]) "Unexpected default tier for $($resourceTierCase[0])."
}
Assert-True ([regex]::Matches($defaultResourceMapYaml, '(?m)^\s*-\s+Resin\s*$').Count -eq 1) 'The inert Ocean Resin duplicate must not return.'
Assert-True ([regex]::Matches($defaultResourceMapYaml, '(?m)^\s*-\s+BoneFragments\s*$').Count -eq 1) 'The inert Plains BoneFragments duplicate must not return.'
Assert-True (-not [regex]::IsMatch($defaultResourceMapYaml, '(?m)^SerpentItems\s*:')) 'SerpentMeat must stay in Ocean without adding a probability-shifting tier.'

$customResourceMapYaml = @'
Low:
  - $item_Test-Food
High:
  - test_food
  - OtherFood
'@
$customMapParse = Parse-ResourceMap $customResourceMapYaml
Assert-True ([bool]$customMapParse[0]) 'A valid editable ResourceMap must parse.'
$customMap = $customMapParse[1]
$resourceTierArguments = [object[]] @('TEST FOOD', 0)
Assert-True ([bool]$tryGetSnapshotResourceTier.Invoke($customMap, $resourceTierArguments)) 'Editable ResourceMap tokens must use locale-independent normalization.'
Assert-True ([int]$resourceTierArguments[1] -eq 0) 'A normalized duplicate resource must keep its first tier.'
$renamedMapParse = Parse-ResourceMap "RenamedLow:`n  - TestFood`nHigh:`n  - OtherFood`n"
$extraTierMapParse = Parse-ResourceMap "Low:`n  - TestFood`nHigh:`n  - OtherFood`nUnused: []`n"
$contentEquals = Get-MethodRequired $resourceMapSnapshotType 'ContentEquals'
Assert-True (-not [bool]$contentEquals.Invoke($customMap, [object[]] @($renamedMapParse[1]))) 'ResourceMap equality must include editable tier names.'
Assert-True (-not [bool]$contentEquals.Invoke($customMap, [object[]] @($extraTierMapParse[1]))) 'ResourceMap equality must include empty trailing tiers.'

foreach ($invalidResourceMap in @(
    '',
    '- Meadows',
    "Meadows: Wood",
    "Meadows:`n  - prefab: Wood",
    "Meadows:`n  - `"`"",
    "Meadows:`n  - Wood`n---`nBlackForest:`n  - FineWood",
    "Meadows:`n  - Wood`nmeadows:`n  - Stone"))
{
    $invalidParse = Parse-ResourceMap $invalidResourceMap
    Assert-True (-not [bool]$invalidParse[0]) 'Malformed ResourceMap.yml must be rejected atomically.'
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$invalidParse[2])) 'Malformed ResourceMap.yml must report an error.'
}

$ensureDefaultResourceMap = Get-MethodRequired $resourceMapPolicyType 'EnsureDefaultFileExists'
$resourceMapSmokeRoot = [string](Join-Path ([IO.Path]::GetTempPath()) ('FineDining.ResourceMap.' + [Guid]::NewGuid().ToString('N')))
$resourceMapSmokePath = [string](Join-Path $resourceMapSmokeRoot 'ResourceMap.yml')
try
{
    $ensureDefaultResourceMap.Invoke($null, [object[]] @($resourceMapSmokePath)) | Out-Null
    Assert-True ([IO.File]::ReadAllText($resourceMapSmokePath) -eq $defaultResourceMapYaml) 'A missing ResourceMap.yml must be generated from the embedded default.'
    [IO.File]::WriteAllText($resourceMapSmokePath, "UserTier:`n  - UserFood`n")
    $ensureDefaultResourceMap.Invoke($null, [object[]] @($resourceMapSmokePath)) | Out-Null
    Assert-True ([IO.File]::ReadAllText($resourceMapSmokePath) -eq "UserTier:`n  - UserFood`n") 'An existing edited ResourceMap.yml must never be overwritten.'
}
finally
{
    if ([IO.Directory]::Exists($resourceMapSmokeRoot))
    {
        [IO.Directory]::Delete($resourceMapSmokeRoot, $true)
    }
}

$resourceMapPolicySource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\ChefResourceMapPolicy.cs') -Raw
$chefTierCatalogSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\ChefFoodTierCatalog.cs') -Raw
$chefCollectionSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\ChefCollectionService.cs') -Raw
$foodStateStoreSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\FoodStateStore.cs') -Raw
$dietModuleSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\DietModule.cs') -Raw
$pluginSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Plugin.cs') -Raw
$foodRulesSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\FoodRules.cs') -Raw
$foodRulesType = Get-TypeRequired $assembly 'FineDining.FoodRules'
foreach ($fullCourseMethod in @(
    'IsFullCourseActive',
    'GetFullCourseScale',
    'WillHaveFullCourseAfterEating'))
{
    Assert-Method $foodRulesType $fullCourseMethod
}
$foodRulesMethodNames = @($foodRulesType.GetMethods([Reflection.BindingFlags] 'Static,Public,NonPublic') | ForEach-Object { $_.Name })
foreach ($legacyFullStraightMethod in @(
    'IsFullStraightActive',
    'GetFullStraightScale',
    'WillHaveFullStraightAfterEating'))
{
    Assert-True ($foodRulesMethodNames -notcontains $legacyFullStraightMethod) "Legacy FoodRules API remains: $legacyFullStraightMethod"
}
Assert-True ($foodRulesSource.Contains('DietConfig.GetFullCourseMultiplier()')) 'Full Course scaling must use the synchronized configured multiplier.'
Assert-True ((Get-Constant $foodRulesType 'MinimumFullCourseSlots') -eq 3) 'Full Course must become eligible at the initial three unlocked slots.'
$isFullCourseEligible = Get-MethodRequired $foodRulesType 'IsFullCourseEligible'
foreach ($fullCourseCase in @(
    @(0, 0, $false),
    @(2, 2, $false)))
{
    $eligible = [bool]$isFullCourseEligible.Invoke(
        $null,
        [object[]] @([int]$fullCourseCase[0], [int]$fullCourseCase[1]))
    Assert-True ($eligible -eq [bool]$fullCourseCase[2]) "Unexpected Full Course eligibility for $($fullCourseCase[0]) slots and $($fullCourseCase[1]) foods."
}
foreach ($unlockedSlots in 3..9)
{
    foreach ($activeDietFoods in 0..$unlockedSlots)
    {
        $eligible = [bool]$isFullCourseEligible.Invoke(
            $null,
            [object[]] @([int]$unlockedSlots, [int]$activeDietFoods))
        Assert-True ($eligible -eq ($activeDietFoods -eq $unlockedSlots)) "Full Course must require every currently unlocked slot to contain a valid diet food ($activeDietFoods/$unlockedSlots)."
    }
}
$intOnlyFullCourseMethods = @($foodRulesType.GetMethods([Reflection.BindingFlags] 'Static,Public,NonPublic') | Where-Object {
    ($_.Name -eq 'IsFullCourseActive' -or $_.Name -eq 'GetFullCourseScale') -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType -eq [int]
})
Assert-True ($intOnlyFullCourseMethods.Count -eq 0) 'Player-unaware Full Course count overloads must not bypass the current unlocked-slot requirement.'
$hudFoodSlotsSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\HudFoodSlots.cs') -Raw
$playerFoodLogicSourceForSlots = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\PlayerFoodLogic.cs') -Raw
$playerFoodPatchesSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\PlayerFoodPatches.cs') -Raw
Assert-True ($hudFoodSlotsSource.Contains('FoodSlotProgression.GetCurrentSlots(player, state)')) 'HUD visibility must follow the player-specific unlocked slot tier.'
Assert-True ([regex]::Matches($playerFoodLogicSourceForSlots, 'FoodSlotProgression\.GetCurrentSlots\(player, state\)').Count -ge 2) 'CanEat and consume scaling must share the player-specific unlocked slot tier.'
Assert-True ($playerFoodLogicSourceForSlots.Contains('if (foods.Count >= unlockedFoodSlots)')) 'Every active consumable must occupy capacity even when it has no positive Health/Stamina/Eitr stat.'
$findTargetFood = Get-MethodRequired $foodRulesType 'FindTargetFood'
Assert-True ($findTargetFood.ReturnType.FullName -eq 'Player+Food' -and
             $findTargetFood.GetParameters().Count -eq 3 -and
             $findTargetFood.GetParameters()[0].ParameterType.FullName -eq 'Player' -and
             $findTargetFood.GetParameters()[1].ParameterType.FullName -eq 'FineDining.PlayerFoodStateData' -and
             $findTargetFood.GetParameters()[2].ParameterType.FullName -eq 'ItemDrop+ItemData') 'The shared target-food policy must return the actual Player.Food selected for one player, state, and item.'
Assert-True ($playerFoodLogicSourceForSlots.Contains('FoodRules.FindTargetFood(player, state, item)')) 'Direct consumption must use the shared target-food policy.'
Assert-True ($foodRulesSource.Contains('foods.Count < FoodSlotProgression.GetCurrentSlots(player, state)')) 'Target-slot allocation must use total active-food occupancy and the unlocked slot tier.'
Assert-True ($foodRulesSource.Contains('while (foods.Count > maximum)')) 'Slot-tier decreases must trim total active-food occupancy to the new capacity.'
Assert-True ($foodRulesSource.Contains('(Math.Max(0, knownFoodCount - 6) + 2) / 3')) 'Slot progression must use the fixed six-food baseline and three-food unlock intervals.'
Assert-True ($foodRulesSource.Contains('active.AppliedScale *= scaleRatio')) 'Existing active-food snapshots must be proportionally rebased when a pending slot tier is applied.'
Assert-True ($foodRulesSource.Contains('state.AppliedBaseSlotScale = nextBaseScale')) 'The applied slot profile scale must be persisted after every rebase.'
Assert-True ($foodRulesSource.Contains('nextBaseScale != previousBaseScale')) 'Every representable endpoint change must rebase active snapshots before the persisted base is replaced.'
Assert-True (-not $foodRulesSource.Contains('Math.Min(25f, active.AppliedScale')) 'Valid high endpoint and Chef multiplier combinations must not be truncated during slot rebasing.'
Assert-True (-not $foodStateStoreSource.Contains('Math.Min(25f, scale)')) 'Valid high endpoint and Chef multiplier combinations must not be truncated while diet state is normalized.'
Assert-True ($foodRulesSource.Contains('current > MaximumFoodSlots')) 'Persisted slot tiers outside the supported 3-9 range must be normalized before rebase.'
Assert-True ($playerFoodLogicSourceForSlots.Contains('FoodSlotProgression.ApplyPendingAfterFoodRemoval(') -and $playerFoodLogicSourceForSlots.Contains('trimExcess: false')) 'Replacing or refreshing an active food must apply a pending slot tier before replacing its target.'
Assert-True ($foodRulesSource.Contains('FoodSlotProgression.GetSlotsAfterFoodRemoval(player, state)')) 'Food tooltip previews must use the post-replacement slot tier.'
Assert-True ($foodRulesSource.Contains('!replacesExistingFood || !replacesDietFood')) 'Full Course prediction must count an H/S/E food that replaces a non-H/S/E active consumable.'
Assert-True ($playerFoodPatchesSource.Contains('VanillaEatBoundaryState? __state') -and $playerFoodPatchesSource.Contains('__state.TryGetReplacedOrRemovedFood(')) 'Vanilla-path consumables must apply pending tiers only after an actual replacement or removal.'
Assert-True ($playerFoodPatchesSource.Contains('FoodSlotProgression.TrimExcessFoods(__instance, state, protectedFood);')) 'Vanilla-path replacement trimming must preserve the newly consumed food.'
Assert-True (-not $playerFoodPatchesSource.Contains('_remainingTime')) 'Vanilla replacement detection must ignore the forced one-second update applied to every active food.'
Assert-True ($playerFoodLogicSourceForSlots.Contains('FoodRules.SetActiveFoodEffect(state, key, effect);')) 'Direct food consumption must persist its exact consumed effect breakdown and oldest-to-newest Active order.'
$recordVanillaConsumptionStart = $playerFoodPatchesSource.IndexOf(
    'private static void RecordVanillaFoodConsumption(',
    [StringComparison]::Ordinal)
$updateFoodPatchStart = $playerFoodPatchesSource.IndexOf(
    '[HarmonyPatch("UpdateFood")]',
    $recordVanillaConsumptionStart,
    [StringComparison]::Ordinal)
Assert-True ($recordVanillaConsumptionStart -ge 0 -and $updateFoodPatchStart -gt $recordVanillaConsumptionStart) 'Vanilla food-consumption ordering helper source boundaries are missing.'
$recordVanillaConsumptionSource = $playerFoodPatchesSource.Substring(
    $recordVanillaConsumptionStart,
    $updateFoodPatchStart - $recordVanillaConsumptionStart)
$eatFoodPostfixStart = $playerFoodPatchesSource.IndexOf(
    'private static void EatFoodPostfix(',
    [StringComparison]::Ordinal)
Assert-True ($eatFoodPostfixStart -ge 0 -and $recordVanillaConsumptionStart -gt $eatFoodPostfixStart) 'Vanilla EatFood postfix source boundaries are missing.'
$eatFoodPostfixSource = $playerFoodPatchesSource.Substring(
    $eatFoodPostfixStart,
    $recordVanillaConsumptionStart - $eatFoodPostfixStart)
Assert-True ([regex]::IsMatch(
    $eatFoodPostfixSource,
    '(?s)if\s*\(\s*!__result\s*\)\s*\{\s*return;\s*\}.*?if\s*\(\s*__state\s*==\s*null\s*\)\s*\{\s*return;\s*\}.*?RecordVanillaFoodConsumption\(\s*__instance\s*,\s*item\s*,\s*state\s*\);.*?FoodStateStore\.SaveState\(\s*__instance\s*,\s*state\s*\);')) 'Every successful vanilla/non-direct consumption must record its Active order before the state is saved.'
Assert-True ([regex]::IsMatch(
    $recordVanillaConsumptionSource,
    '(?s)FoodIdentity\.GetCanonicalPrefabName\(item\).*?foreach\s*\(\s*Player\.Food\s+food\s+in\s+player\.GetFoods\(\)\s*\).*?FoodIdentity\.GetCanonicalPrefabName\(food\)\s*==\s*key.*?FoodRules\.SetActiveFoodEffect\(\s*state\s*,\s*key\s*,\s*null\s*\);')) 'Vanilla/non-direct consumption must move only the matching active Player.Food identity to the newest persisted position without fabricating a Diet breakdown.'
Assert-True ([regex]::IsMatch(
    $foodStateStoreSource,
    '(?s)private static void NormalizeActive\(.*?foreach\s*\(\s*ActiveFoodData\s+entry\s+in\s+state\.Active\s*\).*?state\.Active\s*=\s*normalized;')) 'Active-food normalization must preserve the persisted oldest-to-newest list traversal order.'
Assert-True ($playerFoodLogicSourceForSlots.Contains('FoodSlotProgression.TrimExcessFoods(player, state, targetFood);')) 'Post-replacement trimming must preserve the newly consumed food.'
Assert-True ($playerFoodPatchesSource.Contains('DietModule.RequestDietReconcile();')) 'Player load and known-item resets must request slot reconciliation.'
$dietModuleType = Get-TypeRequired $assembly 'FineDining.DietModule'
$dietModuleStaticFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
Assert-True ($null -ne $dietModuleType.GetField('_dietReconcileRequested', $dietModuleStaticFlags)) 'General Diet reconciliation must have its own pending state.'
Assert-True ($null -ne $dietModuleType.GetField('_chefReconcileRequested', $dietModuleStaticFlags)) 'Chef collection reconciliation must have its own pending state.'
Assert-True ($null -eq $dietModuleType.GetField('_reconcileRequested', $dietModuleStaticFlags)) 'The old shared Diet/Chef reconciliation gate must not return.'
foreach ($operator in @('\+=', '-='))
{
    foreach ($fieldName in @('MaxFoodSlots', 'FoodStatScale'))
    {
        Assert-True ([regex]::IsMatch(
            $dietModuleSource,
            "DietConfig\.$fieldName\.SettingChanged\s*$operator\s*FoodStateShapeChanged;")) "$fieldName changes must register and release Diet reconciliation."
    }
}
Assert-True (-not $dietModuleSource.Contains('SixSlotFoodStatScale') -and
             -not $dietModuleSource.Contains('NineSlotFoodStatScale')) 'Diet config change subscriptions must not retain removed per-maximum scale settings.'
Assert-True ($resourceMapPolicySource.Contains('new CustomSyncedValue<string>(')) 'ResourceMap.yml must be synchronized by the server.'
Assert-True ($resourceMapPolicySource.Contains('ThreadingHelper.SynchronizingObject')) 'ResourceMap hot reload must return to the Unity main thread.'
Assert-True ($resourceMapPolicySource.Contains('keeping the last-known-good resource map')) 'Invalid ResourceMap edits must preserve the last valid snapshot.'
Assert-True ($resourceMapPolicySource.Contains('_syncedYaml.AssignLocalValue(normalizedYaml)')) 'The source of truth must publish valid ResourceMap edits.'
Assert-True ($pluginSource.Contains('ChefResourceMapPolicy.RefreshAuthority();')) 'ResourceMap authority must be reconciled every frame.'
Assert-True ($pluginSource.Contains('ChefResourceMapPolicy.Initialize(ConfigSync);')) 'The plugin must initialize ResourceMap synchronization.'
Assert-True ($pluginSource.Contains('ChefResourceMapPolicy.Shutdown();')) 'The plugin must release ResourceMap watchers and events.'
Assert-True ($chefCollectionSource.Contains('if (!ChefFoodTierCatalog.IsReady)')) 'Chef selection must wait for an authoritative ResourceMap catalog.'
Assert-True ($chefCollectionSource.Contains('ChefFoodTierCatalog.GetSnapshot()')) 'Chef candidates must reuse the tier catalog snapshot.'
Assert-True (-not $chefCollectionSource.Contains('ObjectDB.instance.m_items')) 'Chef collection refresh must not rescan and reclassify the full ObjectDB.'
Assert-True ([regex]::IsMatch(
    $chefCollectionSource,
    '(?s)ChefChoiceMath\.GetChefMultiplier\(\s*DietConfig\.GetChefMultiplierMin\(\),\s*DietConfig\.GetChefMultiplierMax\(\),')) 'Chef multiplier rolls must use both synchronized effective bounds.'
Assert-True ([regex]::IsMatch(
    $foodStateStoreSource,
    '(?s)private static void NormalizeChef\(PlayerFoodStateData state\).*?float minimum = DietConfig\.GetChefMultiplierMin\(\);\s*float maximum = DietConfig\.GetChefMultiplierMax\(\);.*?Math\.Max\(minimum, Math\.Min\(maximum, multiplier\)\)')) 'Persisted Chef multipliers must normalize to both synchronized effective bounds.'
foreach ($operator in @('\+=', '-='))
{
    Assert-True ([regex]::IsMatch(
        $dietModuleSource,
        "DietConfig\.ChefMultiplierMin\.SettingChanged\s*$operator\s*FoodStateShapeChanged;")) 'Chef Multiplier Minimum changes must register and release state reconciliation.'
    Assert-True ([regex]::IsMatch(
        $dietModuleSource,
        "DietConfig\.ChefMultiplierMax\.SettingChanged\s*$operator\s*FoodStateShapeChanged;")) 'Chef Multiplier Maximum changes must register and release state reconciliation.'
}
Assert-True ($chefTierCatalogSource.Contains('BuildCatalog(') -and $chefTierCatalogSource.Contains('ChefResourceMapSnapshot resourceMap')) 'A catalog rebuild must capture one ResourceMap snapshot.'
Assert-True ($chefTierCatalogSource.Contains('ChefResourceMapPolicy.NormalizeResourceToken(value)')) 'Chef tier lookup must use ResourceMap-owned token normalization.'
$normalizeResourceToken = Get-MethodRequired $resourceMapPolicyType 'NormalizeResourceToken'
Assert-True ([string]$normalizeResourceToken.Invoke($null, [object[]] @('  $item_WolfMeat(Clone)  ')) -eq 'wolfmeat') 'ResourceMap token normalization must trim localization and clone decorations.'
Assert-True ($chefTierCatalogSource.Contains('GetDirectTier(') -and $chefTierCatalogSource.Contains('resourceMap.TryGetResourceTier(')) 'Direct tiers must resolve through the captured ResourceMap snapshot.'
Assert-True (-not $chefTierCatalogSource.Contains('OrderedTiers')) 'The removed hard-coded ResourceMap must not return.'
Assert-True (-not $chefTierCatalogSource.Contains('ResourceTierByToken')) 'The removed static ResourceMap lookup must not return.'
Assert-True ($chefTierCatalogSource.Contains('pair.Value.TierCount != other.TierCount')) 'Catalog equality must include dynamic tier counts.'
Assert-True ($chefTierCatalogSource.Contains('!pair.Value.TierName.Equals(other.TierName, StringComparison.Ordinal)')) 'Catalog equality must include editable tier names.'
$catalogStaticFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
Assert-True ($null -eq $chefTierCatalogType.GetField('OrderedTiers', $catalogStaticFlags)) 'The hard-coded ResourceMap tier field must not return.'
Assert-True ($null -eq $chefTierCatalogType.GetField('ResourceTierByToken', $catalogStaticFlags)) 'The hard-coded ResourceMap lookup field must not return.'
$buildChefCatalog = Get-MethodRequired $chefTierCatalogType 'BuildCatalog'
$buildChefCatalogParameters = @($buildChefCatalog.GetParameters())
Assert-True ($buildChefCatalogParameters.Count -eq 3 -and $buildChefCatalogParameters[2].ParameterType -eq $resourceMapSnapshotType) 'Chef catalog rebuilds must consume one explicit ResourceMap snapshot.'
$getDirectChefTier = Get-MethodRequired $chefTierCatalogType 'GetDirectTier'
$getDirectChefTierParameters = @($getDirectChefTier.GetParameters())
Assert-True ($getDirectChefTierParameters.Count -eq 2 -and $getDirectChefTierParameters[1].ParameterType -eq $resourceMapSnapshotType) 'Direct Chef tiers must use the captured ResourceMap snapshot.'
$initializeMapIndex = $pluginSource.IndexOf('ChefResourceMapPolicy.Initialize(ConfigSync);', [StringComparison]::Ordinal)
$initializeDietIndex = $pluginSource.IndexOf('DietModule.Initialize(Config, ConfigSync);', [StringComparison]::Ordinal)
$refreshMapIndex = $pluginSource.IndexOf('ChefResourceMapPolicy.RefreshAuthority();', [StringComparison]::Ordinal)
$catalogTickIndex = $pluginSource.IndexOf('ChefFoodTierCatalog.Tick();', [StringComparison]::Ordinal)
$shutdownMapIndex = $pluginSource.IndexOf('ChefResourceMapPolicy.Shutdown();', [StringComparison]::Ordinal)
$resetCatalogIndex = $pluginSource.IndexOf('ChefFoodTierCatalog.Reset();', [StringComparison]::Ordinal)
Assert-True ($initializeMapIndex -ge 0 -and $initializeMapIndex -lt $initializeDietIndex) 'ResourceMap synchronization must initialize before the Diet module.'
Assert-True ($refreshMapIndex -ge 0 -and $refreshMapIndex -lt $catalogTickIndex) 'ResourceMap authority must refresh before the Chef catalog tick.'
Assert-True ($shutdownMapIndex -ge 0 -and $shutdownMapIndex -lt $resetCatalogIndex) 'ResourceMap synchronization must stop before the Chef catalog resets.'
$projectSource = Get-Content -LiteralPath (Join-Path $projectRoot 'FineDining.csproj') -Raw
Assert-True ($projectSource.Contains('<EmbeddedResource Include="Resources\Defaults\Spoilage.yml"/>')) 'Spoilage.yml must be embedded in FineDining.dll.'
Assert-True (-not [regex]::IsMatch($projectSource, '<Content Include="[^"]*Spoilage\.yml"')) 'Spoilage.yml must be runtime-generated instead of shipped as a loose package file.'
Assert-True ($projectSource.Contains('<EmbeddedResource Include="Resources\Defaults\ResourceMap.yml"/>')) 'ResourceMap.yml must be embedded in FineDining.dll.'
Assert-True (-not [regex]::IsMatch($projectSource, '<Content Include="[^"]*ResourceMap\.yml"')) 'ResourceMap.yml must be runtime-generated instead of shipped as a loose package file.'
Assert-True ($projectSource.Contains('<EmbeddedResource Include="Resources\UI\FullCourseIcon.png"/>')) 'The transparent Full Course tableware icon must be embedded in FineDining.dll.'

$classifierType = Get-TypeRequired $assembly 'FineDining.FoodClassifier'
$selectGroup = Get-MethodRequired $classifierType 'TrySelectGroup'
function Assert-Classification(
    [bool] $Farming,
    [bool] $CookingInput,
    [bool] $CookingOutput,
    [bool] $Fermented,
    [bool] $Unfermented,
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
        $Unfermented,
        $FeastMaterial,
        $FeastResult,
        $Fish,
        $Edible,
        $null)
    $tracked = [bool] $selectGroup.Invoke($null, $arguments)
    Assert-True ($tracked -eq $ExpectedTracked) "Unexpected tracked state for classification '$ExpectedGroup'."
    if ($ExpectedTracked)
    {
        Assert-True ($arguments[9].ToString() -eq $ExpectedGroup) "Expected classification '$ExpectedGroup', got '$($arguments[9])'."
    }
}

Assert-Classification $true $false $true $false $true $false $false $false $true $true 'FarmingHarvest'
Assert-Classification $false $true $false $false $true $false $false $false $true $true 'CookingStationOutput'
Assert-Classification $false $true $false $false $true $false $false $false $false $true 'CookingStationInput'
Assert-Classification $false $false $true $false $true $false $false $false $true $true 'CookingStationOutput'
Assert-Classification $false $false $false $true $true $false $false $false $true $true 'FermentedFood'
Assert-Classification $false $false $false $false $true $true $false $false $true $true 'FeastMaterial'
Assert-Classification $false $false $false $false $true $false $false $true $true $true 'Fish'
Assert-Classification $false $false $false $false $true $false $false $false $false $true 'UnfermentedFood'
Assert-Classification $false $false $false $false $true $false $false $false $true $true 'UnfermentedFood'
Assert-Classification $false $false $false $false $false $false $false $false $true $true 'OtherEdible'
Assert-Classification $false $false $false $false $false $false $false $false $false $false 'OtherEdible'

$findFoodReachableFermenterInputs = Get-MethodRequired $classifierType 'FindFoodReachableFermenterInputs'
$prefabPairType = [Collections.Generic.KeyValuePair[string,string]]
$fermenterConversionListType = [Collections.Generic.List``1].MakeGenericType($prefabPairType)
$fermenterConversions = [Activator]::CreateInstance($fermenterConversionListType)
foreach ($conversion in @(
    @('DirectBase', 'Food'),
    @('ChainBase', 'Intermediate'),
    @('UnrelatedBase', 'UnrelatedOutput'),
    @('EdibleDeadEnd', 'UnrelatedOutput'),
    @('CycleA', 'CycleB'),
    @('CycleB', 'CycleA')))
{
    $fermenterConversions.Add(
        [Collections.Generic.KeyValuePair[string,string]]::new(
            [string]$conversion[0],
            [string]$conversion[1]))
}

$prefabSetType = [Collections.Generic.HashSet[string]]
$reverseConversionDictionaryType =
    [Collections.Generic.Dictionary``2].MakeGenericType([string], $prefabSetType)
$reverseConversionEdges = [Activator]::CreateInstance(
    $reverseConversionDictionaryType,
    [object[]] @([StringComparer]::OrdinalIgnoreCase))
function Add-ReverseConversionEdge(
    [object] $Graph,
    [string] $InputPrefab,
    [string] $OutputPrefab)
{
    if (-not $Graph.ContainsKey($OutputPrefab))
    {
        $Graph.Add(
            $OutputPrefab,
            [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase))
    }

    $Graph[$OutputPrefab].Add($InputPrefab) | Out-Null
}

Add-ReverseConversionEdge $reverseConversionEdges 'DirectBase' 'Food'
Add-ReverseConversionEdge $reverseConversionEdges 'ChainBase' 'Intermediate'
# This edge represents a CookingStation continuation. It intentionally is not
# a Fermenter candidate, but it must still make ChainBase food-reachable.
Add-ReverseConversionEdge $reverseConversionEdges 'Intermediate' 'Food'
Add-ReverseConversionEdge $reverseConversionEdges 'UnrelatedBase' 'UnrelatedOutput'
Add-ReverseConversionEdge $reverseConversionEdges 'EdibleDeadEnd' 'UnrelatedOutput'
Add-ReverseConversionEdge $reverseConversionEdges 'CycleA' 'CycleB'
Add-ReverseConversionEdge $reverseConversionEdges 'CycleB' 'CycleA'
$directlyEdiblePrefabs = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$directlyEdiblePrefabs.Add('Food') | Out-Null
# A directly edible source must not qualify when its own Fermenter target has
# no route to edible food. This guards target reachability rather than a wrong
# intersection between candidate sources and the reachable-node set.
$directlyEdiblePrefabs.Add('EdibleDeadEnd') | Out-Null
$reachableFermenterInputs = $findFoodReachableFermenterInputs.Invoke(
    $null,
    [object[]] @($fermenterConversions, $reverseConversionEdges, $directlyEdiblePrefabs))
Assert-True ($reachableFermenterInputs.Contains('DirectBase')) 'A Fermenter input whose direct output is edible must be food-reachable.'
Assert-True ($reachableFermenterInputs.Contains('ChainBase')) 'A Fermenter input followed by a CookingStation chain to edible food must be food-reachable.'
Assert-True (-not $reachableFermenterInputs.Contains('UnrelatedBase')) 'A Fermenter input on an unrelated dead branch must not be food-reachable.'
Assert-True (-not $reachableFermenterInputs.Contains('EdibleDeadEnd')) 'A directly edible Fermenter input must not qualify when its own output is a dead end.'
Assert-True (-not $reachableFermenterInputs.Contains('CycleA') -and
             -not $reachableFermenterInputs.Contains('CycleB')) 'An unreachable conversion cycle must terminate without qualifying its Fermenter inputs.'

$spoilageGroupType = Get-TypeRequired $assembly 'FineDining.SpoilageGroup'
$spoilageDefaultsType = Get-TypeRequired $assembly 'FineDining.SpoilageDefaults'
$getReplacementPrefab = Get-MethodRequired $spoilageDefaultsType 'GetReplacementPrefab'
$expectedReplacements = @{
    FarmingHarvest = 'FineDining_RottenProduce'
    CookingStationInput = 'RottenMeat'
    CookingStationOutput = 'RottenMeat'
    FermentedFood = 'FineDining_RottenFood'
    UnfermentedFood = 'FineDining_RottenFood'
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

$spoilageReferenceEntryType = Get-TypeRequired $assembly 'FineDining.SpoilageReferenceEntry'
$spoilageReferenceGeneratorType = Get-TypeRequired $assembly 'FineDining.SpoilageReferenceGenerator'
Assert-True ((Get-Constant $spoilageReferenceGeneratorType 'ReferenceFileName') -eq 'Spoilage.reference.yml') 'Spoilage reference filename is incorrect.'
$spoilageReferenceEntries = [Array]::CreateInstance($spoilageReferenceEntryType, 0)
$buildSpoilageReference = Get-MethodRequired $spoilageReferenceGeneratorType 'BuildReferenceContent'
$spoilageReference = [string]$buildSpoilageReference.Invoke($null, [object[]] @(,$spoilageReferenceEntries))
Assert-True ($spoilageReference.Contains('# ===== automatic: farmingHarvest =====')) 'Spoilage reference must retain the farming-harvest section label.'
Assert-True ($spoilageReference.Contains('# ===== automatic: unfermentedFood =====')) 'Spoilage reference must expose the unfermented-food section label.'
Assert-True ($spoilageReference.Contains('# ===== exact overrides: enabled =====')) 'Spoilage reference must retain the enabled-override section label.'
Assert-True ($spoilageReference.Contains('# ===== exact overrides: disabled =====')) 'Spoilage reference must retain the disabled-override section label.'
Assert-True ($spoilageReference.IndexOf('automatic: farmingHarvest', [StringComparison]::Ordinal) -lt $spoilageReference.IndexOf('exact overrides: enabled', [StringComparison]::Ordinal)) 'Automatic spoilage groups must precede exact overrides.'
$fishReferenceIndex = $spoilageReference.IndexOf('automatic: fish', [StringComparison]::Ordinal)
$unfermentedReferenceIndex = $spoilageReference.IndexOf('automatic: unfermentedFood', [StringComparison]::Ordinal)
$otherEdibleReferenceIndex = $spoilageReference.IndexOf('automatic: otherEdible', [StringComparison]::Ordinal)
Assert-True ($fishReferenceIndex -ge 0 -and $unfermentedReferenceIndex -gt $fishReferenceIndex -and $otherEdibleReferenceIndex -gt $unfermentedReferenceIndex) 'The unfermented-food reference section must follow Fish and precede OtherEdible.'
Assert-True ($spoilageReference.Contains('# (none)')) 'Empty spoilage reference sections must remain explicit.'
Assert-True ($spoilageReference.Contains("under 'overrides:' in Spoilage.yml.")) 'Spoilage reference guidance must name the active Spoilage.yml policy file.'
Assert-True ($spoilageReference.Contains("hours[, replacement prefab or keep]")) 'Spoilage reference guidance must document that copied rows can use the keep result.'
$spoilageReferenceEntryConstructor = @(
    $spoilageReferenceEntryType.GetConstructors([Reflection.BindingFlags] 'Instance,Public,NonPublic') |
        Where-Object { $_.GetParameters().Count -eq 6 }
) | Select-Object -First 1
Assert-True ($null -ne $spoilageReferenceEntryConstructor) 'The compact spoilage-reference entry constructor is missing.'
$feastMaterialReferenceEntry = $spoilageReferenceEntryConstructor.Invoke([object[]] @(
    'FeastMaterialReferenceFood',
    'Example Mod',
    [Enum]::Parse($spoilageGroupType, 'FeastMaterial'),
    $null,
    [double]72,
    'keep'))
$feastResultReferenceEntry = $spoilageReferenceEntryConstructor.Invoke([object[]] @(
    'FeastResultReferenceFood',
    'Example Mod',
    [Enum]::Parse($spoilageGroupType, 'FeastResult'),
    $null,
    [double]48,
    'keep'))
$feastKeepReferenceEntries = [Array]::CreateInstance($spoilageReferenceEntryType, 2)
$feastKeepReferenceEntries.SetValue($feastMaterialReferenceEntry, 0)
$feastKeepReferenceEntries.SetValue($feastResultReferenceEntry, 1)
$feastKeepSpoilageReference = [string]$buildSpoilageReference.Invoke($null, [object[]] @(,$feastKeepReferenceEntries))
Assert-True ($feastKeepSpoilageReference.Contains('- FeastMaterialReferenceFood, 72, keep')) 'An automatic FeastMaterial reference row must expose its inherited group keep action.'
Assert-True ($feastKeepSpoilageReference.Contains('- FeastResultReferenceFood, 48, keep')) 'An automatic FeastResult reference row must expose its inherited group keep action.'
$captureReferenceActionContract = [regex]::Match(
    (Get-Content -LiteralPath (Join-Path $projectRoot 'SpoilageReferenceGenerator.cs') -Raw),
    '(?s)rule\.ExpiryAction\s*==\s*SpoilageExpiryAction\.KeepOriginal\s*\?\s*SpoilagePolicy\.KeepOriginalKeyword\s*:\s*rule\.ReplacementPrefab')
Assert-True $captureReferenceActionContract.Success 'Runtime reference capture must emit keep from the resolved automatic group action.'
$keepReferenceEntry = $spoilageReferenceEntryConstructor.Invoke([object[]] @(
    'KeepReferenceFood',
    'Example Mod',
    $null,
    $true,
    [double]12.5,
    'keep'))
$keepReferenceEntries = [Array]::CreateInstance($spoilageReferenceEntryType, 1)
$keepReferenceEntries.SetValue($keepReferenceEntry, 0)
$keepSpoilageReference = [string]$buildSpoilageReference.Invoke($null, [object[]] @(,$keepReferenceEntries))
Assert-True ($keepSpoilageReference.Contains('- KeepReferenceFood, 12.5, keep')) 'An enabled keep override must remain directly copyable from Spoilage.reference.yml.'
$copiedKeepReferencePolicyYaml = $emptyOverridePolicyYaml.Replace(
    'overrides: []',
    "overrides:`n  - KeepReferenceFood, 12.5, keep")
$copiedKeepReferenceParseArguments = [object[]] @([string]$copiedKeepReferencePolicyYaml, $null, '', '')
Assert-True ([bool]$tryParsePolicy.Invoke($null, $copiedKeepReferenceParseArguments)) 'A keep row copied from Spoilage.reference.yml must parse as active configuration.'
Assert-True ([string]$copiedKeepReferenceParseArguments[2] -match '(?m)^\s*-\s+KeepReferenceFood,\s*12\.5,\s*keep\s*$') 'A copied keep reference row must retain its canonical result.'
$spoilageReferenceGeneratorSource = Get-Content -LiteralPath (Join-Path $projectRoot 'SpoilageReferenceGenerator.cs') -Raw
Assert-True ($null -eq $spoilageReferenceGeneratorType.GetMethod('TryWriteCurrentReference', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'Spoilage reference generation must not expose a removed manual-command path.'
$pluginStaticFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
Assert-True ($null -ne $pluginType.GetProperty('ConfigDirectoryPath', $pluginStaticFlags)) 'FineDiningPlugin must own the shared config-directory path.'
Assert-True ($null -ne $pluginType.GetProperty('IsRuntimeReferenceAuthority', $pluginStaticFlags)) 'FineDiningPlugin must own the shared runtime-reference authority decision.'
Assert-True ($spoilageReferenceGeneratorSource.Contains('FineDiningPlugin.IsRuntimeReferenceAuthority')) 'Automatic spoilage-reference generation must retain its shared server-authority gate.'

$includePickable = Get-MethodRequired $classifierType 'ShouldIncludePickableOutput'
Assert-True (-not [bool]$includePickable.Invoke($null, [object[]] @($true, $false, $true))) 'Cultivated seed outputs must be excluded.'
Assert-True ([bool]$includePickable.Invoke($null, [object[]] @($true, $false, $false))) 'Cultivated non-seed outputs must be included.'
Assert-True ([bool]$includePickable.Invoke($null, [object[]] @($false, $true, $false))) 'Wild edible outputs must be included.'
Assert-True (-not [bool]$includePickable.Invoke($null, [object[]] @($false, $false, $false))) 'Wild non-edible outputs must be excluded.'

$stateType = Get-TypeRequired $assembly 'FineDining.PlayerFoodStateData'
$slotProgressionType = Get-TypeRequired $assembly 'FineDining.FoodSlotProgression'
$calculateUnlockedSlots = Get-MethodRequired $slotProgressionType 'CalculateUnlockedSlots'
$calculateScaleRebase = Get-MethodRequired $slotProgressionType 'CalculateScaleRebase'
Assert-True ((Get-Constant $slotProgressionType 'MinimumFoodSlots') -eq 3) 'Progressive food slots must start at three.'
Assert-True ((Get-Constant $slotProgressionType 'MaximumFoodSlots') -eq 9) 'Persisted progressive slot tiers must be bounded by the supported nine-slot maximum.'
foreach ($slotCase in @(
    @(-1, 3),
    @(0,  3),
    @(6,  3),
    @(7,  4),
    @(9,  4),
    @(10, 5),
    @(12, 5),
    @(13, 6),
    @(15, 6),
    @(16, 7),
    @(18, 7),
    @(19, 8),
    @(21, 8),
    @(22, 9),
    @(99, 9)))
{
    foreach ($maximumSlots in 3..9)
    {
        $actualSlots = [int]$calculateUnlockedSlots.Invoke(
            $null,
            [object[]] @([int]$slotCase[0], [int]$maximumSlots))
        $expectedSlots = [Math]::Min($maximumSlots, [int]$slotCase[1])
        Assert-True ($actualSlots -eq $expectedSlots) "Unexpected unlocked slot count for $($slotCase[0]) known foods and maximum ${maximumSlots}: $actualSlots"
    }
}
Assert-True ([int]$calculateUnlockedSlots.Invoke($null, [object[]] @(99, 2)) -eq 3 -and
             [int]$calculateUnlockedSlots.Invoke($null, [object[]] @(99, 10)) -eq 9) 'Slot progression must defensively clamp unsupported maximums to the inclusive 3-9 range.'
$calculateBaseSlotScale = Get-MethodRequired $dietConfigType 'CalculateBaseSlotScale'
Assert-True ($calculateBaseSlotScale.GetParameters().Count -eq 3) 'Base slot scaling must use one shared configured ratio, not separate six- and nine-slot endpoints.'
foreach ($maximumSlots in 3..9)
{
    foreach ($unlockedSlots in 3..$maximumSlots)
    {
        foreach ($configuredScale in @([float]0.1, [float]0.9, [float]1.2, [float]3))
        {
            $baseScale = [float]$calculateBaseSlotScale.Invoke(
                $null,
                [object[]] @([int]$maximumSlots, [int]$unlockedSlots, $configuredScale))
            $expectedScale = 3.0 * $configuredScale / $unlockedSlots
            Assert-True ([Math]::Abs($baseScale - $expectedScale) -lt 0.0001) "Unexpected per-food scale for maximum $maximumSlots, unlocked $unlockedSlots, configured ${configuredScale}: $baseScale"
            Assert-True ([Math]::Abs(($baseScale * $unlockedSlots / 3.0) - $configuredScale) -lt 0.0001) 'Filling every unlocked slot must produce the configured ratio of a comparable vanilla three-food total.'
            if ($configuredScale -eq [float]0.9)
            {
                Assert-True ([Math]::Abs(($baseScale * $unlockedSlots / 3.0 * 1.2) - 1.08) -lt 0.0001) 'Default Full Course must give a comparable 108% vanilla food total at every supported slot count.'
            }
        }
    }
}
foreach ($scaleClampCase in @(
    @(2, 9, 0.9),
    @(10, 10, 0.3),
    @(5, -1, 0.9),
    @(5, 9, 0.54)))
{
    $actualScale = [float]$calculateBaseSlotScale.Invoke(
        $null,
        [object[]] @([int]$scaleClampCase[0], [int]$scaleClampCase[1], [float]0.9))
    Assert-True ([Math]::Abs($actualScale - [double]$scaleClampCase[2]) -lt 0.0001) 'Base slot scaling must clamp both the configured maximum and persisted unlocked-slot count before dividing.'
}
Assert-True ([Math]::Abs([float]$calculateScaleRebase.Invoke($null, [object[]] @([float]0.9, [float]0.675)) - 0.75) -lt 0.0001) 'Advancing from three to four default slots must rebase active snapshots by x0.75.'
Assert-True ([Math]::Abs([float]$calculateScaleRebase.Invoke($null, [object[]] @([float]0.45, [float]0.3)) - (2.0 / 3.0)) -lt 0.0001) 'Changing from a full six-slot profile to a full nine-slot profile must preserve the endpoint ratio.'
Assert-True ([Math]::Abs([float]$calculateScaleRebase.Invoke($null, [object[]] @([float]0.3, [float]0.45)) - 1.5) -lt 0.0001) 'Changing from a full nine-slot profile to a full six-slot profile must restore the reciprocal endpoint ratio.'

$historyType = Get-TypeRequired $assembly 'FineDining.HistoryEntryData'
$historyServiceType = Get-TypeRequired $assembly 'FineDining.RecentHistoryService'
$chefCollectionType = Get-TypeRequired $assembly 'FineDining.ChefCollectionService'
$state = [Activator]::CreateInstance($stateType, $true)
$unlockedFoodSlotsField = $stateType.GetField(
    'UnlockedFoodSlots',
    [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $unlockedFoodSlotsField -and $unlockedFoodSlotsField.FieldType -eq [int]) 'Persisted Diet state must retain the applied unlocked-slot tier.'
$appliedBaseSlotScaleField = $stateType.GetField(
    'AppliedBaseSlotScale',
    [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $appliedBaseSlotScaleField -and $appliedBaseSlotScaleField.FieldType -eq [float]) 'Persisted Diet state must retain the exact slot-profile base scale used by active-food snapshots.'
$recentField = $stateType.GetField('Recent', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $recentField) 'Diet recent-history field is missing.'
$activeFoodDataType = Get-TypeRequired $assembly 'FineDining.ActiveFoodData'
$activeField = $stateType.GetField('Active', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $activeField -and
             $activeField.FieldType.IsGenericType -and
             $activeField.FieldType.GetGenericArguments()[0] -eq $activeFoodDataType) 'Diet Active state must persist an ordered list of ActiveFoodData entries.'
$setActiveFoodEffect = Get-MethodRequired $foodRulesType 'SetActiveFoodEffect'
$setActiveFoodEffectParameters = @($setActiveFoodEffect.GetParameters())
Assert-True ($setActiveFoodEffect.ReturnType -eq [void] -and
             $setActiveFoodEffectParameters.Count -eq 3 -and
             $setActiveFoodEffectParameters[0].ParameterType -eq $stateType -and
             $setActiveFoodEffectParameters[1].ParameterType -eq [string] -and
             [Nullable]::GetUnderlyingType($setActiveFoodEffectParameters[2].ParameterType) -eq $foodEffectType) 'Active-food snapshots must be recorded through one state/key/nullable-effect ordering method.'
Assert-True ($null -eq $foodRulesType.GetMethod('SetActiveFoodScale', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'The scale-only consumption recorder must not bypass effect-breakdown snapshots.'
$getActiveFood = Get-MethodRequired $foodRulesType 'GetActiveFood'
Assert-True ($getActiveFood.IsAssembly -and $getActiveFood.ReturnType -eq $activeFoodDataType) 'HUD hover must be able to retrieve the consumed snapshot without recalculating a prospective effect.'
$foodEffectConstructors = @($foodEffectType.GetConstructors($foodEffectFlags))
Assert-True ($foodEffectConstructors.Count -eq 1 -and $foodEffectConstructors[0].GetParameters().Count -eq 11) 'FoodEffect must retain one complete effect constructor for managed snapshot tests.'
$foodEffectConstructor = $foodEffectConstructors[0]
function New-SmokeFoodEffect(
    [float] $AppliedScale,
    [float] $FreshnessScale = 1,
    [float] $DiminishingScale = 1,
    [bool] $IsChef = $false,
    [float] $ChefMultiplier = 1,
    [bool] $FullCourseActive = $false)
{
    $effectiveScale = $AppliedScale
    if ($FullCourseActive)
    {
        $effectiveScale *= [float]1.2
    }
    return $foodEffectConstructor.Invoke([object[]] @(
        $FreshnessScale, $AppliedScale, [float]$effectiveScale, $DiminishingScale,
        [float]0, [float]0, [float]0, [float]0,
        $IsChef, $ChefMultiplier, $FullCourseActive))
}
$activeKeyField = $activeFoodDataType.GetField('Key', $foodEffectFlags)
$activeScaleField = $activeFoodDataType.GetField('AppliedScale', $foodEffectFlags)
$activeHasBreakdownField = $activeFoodDataType.GetField('HasEffectBreakdown', $foodEffectFlags)
$activeChefMultiplierField = $activeFoodDataType.GetField('ChefMultiplier', $foodEffectFlags)
$activeFreshnessScaleField = $activeFoodDataType.GetField('FreshnessScale', $foodEffectFlags)
$activeDiminishingScaleField = $activeFoodDataType.GetField('DiminishingScale', $foodEffectFlags)
Assert-True ($null -ne $activeKeyField -and $activeKeyField.FieldType -eq [string] -and
             $null -ne $activeScaleField -and $activeScaleField.FieldType -eq [float]) 'Persisted ActiveFoodData identity and applied scale fields are missing.'
Assert-True ($null -ne $activeHasBreakdownField -and $activeHasBreakdownField.FieldType -eq [bool]) 'Persisted ActiveFoodData must distinguish consumed breakdowns from legacy scale-only entries.'
foreach ($activeFactorField in @($activeChefMultiplierField, $activeFreshnessScaleField, $activeDiminishingScaleField))
{
    Assert-True ($null -ne $activeFactorField -and $activeFactorField.FieldType -eq [float]) 'Persisted ActiveFoodData must retain every exact consumed Chef, freshness, and diminishing factor.'
}
Assert-True ($null -eq $activeFoodDataType.GetField('FullCourseActive', $foodEffectFlags) -and
             $null -eq $activeFoodDataType.GetField('FullCourseScale', $foodEffectFlags)) 'Full Course must remain a live effect, not a persisted consumption snapshot.'
$activeOrderState = [Activator]::CreateInstance($stateType, $true)
$activeOrder = $activeField.GetValue($activeOrderState)
foreach ($activeSpec in @(
    @('A', [float]0.5, [float]0.8, [float]1, $true, [float]1.25),
    @('B', [float]0.6, [float]0.8, [float]0.75, $false, [float]1),
    @('C', [float]0.7, [float]1, [float]1, $false, [float]1)))
{
    $snapshotEffect = New-SmokeFoodEffect $activeSpec[1] $activeSpec[2] $activeSpec[3] $activeSpec[4] $activeSpec[5]
    $setActiveFoodEffect.Invoke(
        $null,
        [object[]] @($activeOrderState, [string]$activeSpec[0], $snapshotEffect)) | Out-Null
    $savedActiveFood = $getActiveFood.Invoke($null, [object[]] @($activeOrderState, [string]$activeSpec[0]))
    Assert-True ([bool]$activeHasBreakdownField.GetValue($savedActiveFood) -and
                 [float]$activeScaleField.GetValue($savedActiveFood) -eq [float]$activeSpec[1] -and
                 [float]$activeFreshnessScaleField.GetValue($savedActiveFood) -eq [float]$activeSpec[2] -and
                 [float]$activeDiminishingScaleField.GetValue($savedActiveFood) -eq [float]$activeSpec[3] -and
                 [float]$activeChefMultiplierField.GetValue($savedActiveFood) -eq [float]$activeSpec[5]) 'Consumption must snapshot the exact applied scale and all component multipliers.'
}
$originalActiveA = $getActiveFood.Invoke($null, [object[]] @($activeOrderState, 'A'))
$replacementEffect = New-SmokeFoodEffect ([float]0.9) ([float]0.75) ([float]1) $true ([float]1.5) $true
$setActiveFoodEffect.Invoke(
    $null,
    [object[]] @($activeOrderState, 'A', $replacementEffect)) | Out-Null
$activeOrderKeys = @($activeOrder | ForEach-Object { [string]$activeKeyField.GetValue($_) })
Assert-True ($activeOrder.Count -eq 3 -and ($activeOrderKeys -join ',') -eq 'B,C,A') 'Re-eating an active food must move its existing ActiveFoodData entry to the newest tail without duplication.'
Assert-True ([object]::ReferenceEquals($originalActiveA, $activeOrder[2])) 'Re-eating must update and move the existing snapshot entry rather than duplicate its identity.'
Assert-True ([float]$activeScaleField.GetValue($activeOrder[2]) -eq [float]0.9 -and
             [bool]$activeHasBreakdownField.GetValue($activeOrder[2]) -and
             [float]$activeFreshnessScaleField.GetValue($activeOrder[2]) -eq [float]0.75 -and
             [float]$activeDiminishingScaleField.GetValue($activeOrder[2]) -eq [float]1 -and
             [float]$activeChefMultiplierField.GetValue($activeOrder[2]) -eq [float]1.5) 'Re-eating must replace all old snapshot factors and store AppliedScale without live Full Course.'
Assert-True ($null -eq $getActiveFood.Invoke($null, [object[]] @($activeOrderState, 'MissingFood'))) 'Missing active food must not fabricate a consumed effect snapshot.'
$setActiveFoodEffect.Invoke($null, [object[]] @($activeOrderState, 'B', $null)) | Out-Null
$vanillaActive = $getActiveFood.Invoke($null, [object[]] @($activeOrderState, 'B'))
Assert-True ($activeOrder.Count -eq 3 -and
             [object]::ReferenceEquals($activeOrder[2], $vanillaActive) -and
             [float]$activeScaleField.GetValue($vanillaActive) -eq [float]1 -and
             -not [bool]$activeHasBreakdownField.GetValue($vanillaActive) -and
             [float]$activeFreshnessScaleField.GetValue($vanillaActive) -eq [float]1 -and
             [float]$activeDiminishingScaleField.GetValue($vanillaActive) -eq [float]1 -and
             [float]$activeChefMultiplierField.GetValue($vanillaActive) -eq [float]1) 'Vanilla re-consumption must move the same entry to the tail and clear stale Diet snapshot factors.'
$hasValidEffectBreakdown = Get-MethodRequired $stateStoreType 'HasValidEffectBreakdown'
$legacyActive = [Activator]::CreateInstance($activeFoodDataType, $true)
$activeScaleField.SetValue($legacyActive, [float]0.5625)
Assert-True (-not [bool]$activeHasBreakdownField.GetValue($legacyActive) -and
             -not [bool]$hasValidEffectBreakdown.Invoke($null, [object[]] @($legacyActive))) 'Legacy v3 scale-only snapshots must remain explicitly unknown instead of inventing consumption factors.'
Assert-True ([bool]$hasValidEffectBreakdown.Invoke($null, [object[]] @($originalActiveA))) 'A complete consumed snapshot must retain its breakdown during normalization.'
$activeChefMultiplierField.SetValue($legacyActive, [float]99)
$activeFreshnessScaleField.SetValue($legacyActive, [float]0)
$activeDiminishingScaleField.SetValue($legacyActive, [float]0.01)
$activeHasBreakdownField.SetValue($legacyActive, $true)
Assert-True ([bool]$hasValidEffectBreakdown.Invoke($null, [object[]] @($legacyActive))) 'Snapshot validation must preserve historical finite factors without clamping them to current Chef or freshness settings.'
foreach ($invalidSnapshotFactor in @(
    @($activeChefMultiplierField, [float]::NaN),
    @($activeChefMultiplierField, [float]::PositiveInfinity),
    @($activeChefMultiplierField, [float]0.9),
    @($activeFreshnessScaleField, [float]::NaN),
    @($activeFreshnessScaleField, [float]::PositiveInfinity),
    @($activeFreshnessScaleField, [float]-0.1),
    @($activeFreshnessScaleField, [float]1.1),
    @($activeDiminishingScaleField, [float]::NaN),
    @($activeDiminishingScaleField, [float]::PositiveInfinity),
    @($activeDiminishingScaleField, [float]0),
    @($activeDiminishingScaleField, [float]1.1)))
{
    $factorField = $invalidSnapshotFactor[0]
    $originalFactorValue = $factorField.GetValue($legacyActive)
    $factorField.SetValue($legacyActive, [float]$invalidSnapshotFactor[1])
    Assert-True (-not [bool]$hasValidEffectBreakdown.Invoke($null, [object[]] @($legacyActive))) "Invalid $($factorField.Name) metadata must not be presented as an exact consumed breakdown."
    Assert-True ([float]$activeScaleField.GetValue($legacyActive) -eq [float]0.5625) 'Invalid optional hover metadata must never alter an otherwise valid legacy AppliedScale.'
    $factorField.SetValue($legacyActive, $originalFactorValue)
}
$normalizeActiveStart = $foodStateStoreSource.IndexOf('private static void NormalizeActive(', [StringComparison]::Ordinal)
$validateActiveBreakdownStart = $foodStateStoreSource.IndexOf('private static bool HasValidEffectBreakdown(', $normalizeActiveStart, [StringComparison]::Ordinal)
Assert-True ($normalizeActiveStart -ge 0 -and $validateActiveBreakdownStart -gt $normalizeActiveStart) 'Active snapshot normalization boundaries are missing.'
$normalizeActiveSource = $foodStateStoreSource.Substring($normalizeActiveStart, $validateActiveBreakdownStart - $normalizeActiveStart)
Assert-True ($normalizeActiveSource.Contains('AppliedScale = scale') -and
             $normalizeActiveSource.Contains('HasValidEffectBreakdown(entry)') -and
             $normalizeActiveSource.Contains('HasEffectBreakdown = hasEffectBreakdown') -and
             $normalizeActiveSource.Contains('ChefMultiplier = hasEffectBreakdown ? entry.ChefMultiplier : 1f') -and
             $normalizeActiveSource.Contains('FreshnessScale = hasEffectBreakdown ? entry.FreshnessScale : 1f') -and
             $normalizeActiveSource.Contains('DiminishingScale = hasEffectBreakdown ? entry.DiminishingScale : 1f')) 'Loading state must preserve valid exact factors and drop malformed or legacy metadata without recomputing AppliedScale.'
Assert-True (-not $normalizeActiveSource.Contains('DietConfig.GetChef') -and
             -not $normalizeActiveSource.Contains('FreshnessRuntime.') -and
             -not $normalizeActiveSource.Contains('CalculateDiminishingScale(')) 'Loaded snapshots must not be recalculated from current settings or item freshness.'

$calculateExtraEffectScale = Get-MethodRequired $hudFoodPanelsType 'CalculateExtraEffectScale'
$formatEatenFoodHover = Get-MethodRequired $hudFoodPanelsType 'FormatEatenFoodHover'
Assert-True ($calculateExtraEffectScale.ReturnType -eq [float] -and
             $calculateExtraEffectScale.GetParameters().Count -eq 3 -and
             $formatEatenFoodHover.ReturnType -eq [string] -and
             $formatEatenFoodHover.GetParameters().Count -eq 6) 'Eaten-food hover must expose pure extra-scale and localized text formatters for managed verification.'
foreach ($hoverSlots in 3..9)
{
    $hoverBaseScale = [float](2.7 / $hoverSlots)
    foreach ($hoverExtraFactor in @([float]0, [float]0.5625, [float]0.75, [float]1, [float]1.35, [float]5))
    {
        foreach ($hoverFullCourseScale in @([float]1, [float]1.2))
        {
            $hoverExtraScale = [float]$calculateExtraEffectScale.Invoke($null, [object[]] @(
                [float]($hoverBaseScale * $hoverExtraFactor), $hoverBaseScale, $hoverFullCourseScale))
            Assert-True ([Math]::Abs($hoverExtraScale - $hoverExtraFactor * $hoverFullCourseScale) -lt 0.0001) "The net-effect title must exclude the $hoverSlots-slot baseline while preserving consumed modifiers and live Full Course."
        }
    }
}
foreach ($invalidHoverScales in @(
    @([float]::NaN, [float]0.9, [float]1),
    @([float]::PositiveInfinity, [float]0.9, [float]1),
    @([float]-0.1, [float]0.9, [float]1),
    @([float]0.9, [float]0, [float]1),
    @([float]0.9, [float]::NaN, [float]1),
    @([float]0.9, [float]::PositiveInfinity, [float]1),
    @([float]0.9, [float]0.9, [float]0),
    @([float]0.9, [float]0.9, [float]::NaN),
    @([float]0.9, [float]0.9, [float]::PositiveInfinity),
    @([float]::MaxValue, [float]1, [float]2)))
{
    Assert-True ([float]$calculateExtraEffectScale.Invoke($null, [object[]]$invalidHoverScales) -eq [float]1) 'Invalid or overflowing hover-only scales must fail softly to a neutral multiplier.'
}
$hoverFactorLabels = [string[]] @('Full Course', 'Chef', 'Freshness', 'Diminish')
$formatSnapshot = [Activator]::CreateInstance($activeFoodDataType, $true)
$activeHasBreakdownField.SetValue($formatSnapshot, $true)
$neutralHover = [string]$formatEatenFoodHover.Invoke($null, [object[]] @(
    'Carrot', 'Net effect', $hoverFactorLabels, [float]1, [float]1, $formatSnapshot))
Assert-True ($neutralHover -ceq '<color=orange>Carrot</color> — Net effect <color=#B8B8B8>×1.00</color>') 'An ordinary eaten food must display its localized name and neutral net effect, with no filler second line.'
$snapshotHover = [string]$formatEatenFoodHover.Invoke($null, [object[]] @(
    'Carrot', 'Net effect', $hoverFactorLabels, [float]1.35, [float]1.2, $originalActiveA))
$expectedSnapshotHover = '<color=orange>Carrot</color> — Net effect <color=#9FE870>×1.35</color>' + "`n" +
    '<color=#9FE870>Full Course ×1.20</color> · <color=#9FE870>Chef ×1.50</color> · <color=#FFB454>Freshness ×0.75</color>'
Assert-True ($snapshotHover -ceq $expectedSnapshotHover) 'Eaten hover must compose exactly two lines with live Full Course, consumed Chef and freshness in order, omitting neutral diminishing.'
$activeFreshnessScaleField.SetValue($formatSnapshot, [float]0.8)
$activeDiminishingScaleField.SetValue($formatSnapshot, [float]0.75)
$penaltyHover = [string]$formatEatenFoodHover.Invoke($null, [object[]] @(
    'Carrot', 'Net effect', $hoverFactorLabels, [float]0.6, [float]1, $formatSnapshot))
Assert-True ($penaltyHover.Contains('Net effect <color=#FFB454>×0.60</color>') -and
             $penaltyHover.EndsWith('<color=#FFB454>Freshness ×0.80</color> · <color=#FFB454>Diminish ×0.75</color>') -and
             -not $penaltyHover.Contains('Full Course') -and -not $penaltyHover.Contains('Chef')) 'A diminished stale food must show only its two active penalties, with no neutral factors.'
$activeChefMultiplierField.SetValue($formatSnapshot, [float]1.25)
$activeDiminishingScaleField.SetValue($formatSnapshot, [float]1)
$cancelledHover = [string]$formatEatenFoodHover.Invoke($null, [object[]] @(
    'Carrot', 'Net effect', $hoverFactorLabels, [float]1, [float]1, $formatSnapshot))
Assert-True ($cancelledHover.Contains('Net effect <color=#B8B8B8>×1.00</color>') -and
             $cancelledHover.Contains('Chef ×1.25') -and $cancelledHover.Contains('Freshness ×0.80')) 'Non-neutral factors must remain visible even when they cancel to a neutral total.'
$activeChefMultiplierField.SetValue($formatSnapshot, [float]1.0001)
$activeFreshnessScaleField.SetValue($formatSnapshot, [float]0.9999)
$activeDiminishingScaleField.SetValue($formatSnapshot, [float]0.9999)
$roundedNeutralHover = [string]$formatEatenFoodHover.Invoke($null, [object[]] @(
    'Carrot', 'Net effect', $hoverFactorLabels, [float]1.0001, [float]1.0001, $formatSnapshot))
Assert-True ($roundedNeutralHover -ceq $neutralHover) 'Factors displayed as x1.00 must be omitted instead of occupying the second line.'
foreach ($unknownHoverSnapshot in @($null, $vanillaActive))
{
    $unknownHover = [string]$formatEatenFoodHover.Invoke($null, [object[]] @(
        'Carrot', 'Net effect', $hoverFactorLabels, [float]1.2, [float]1.2, $unknownHoverSnapshot))
    Assert-True ($unknownHover.Contains('Net effect <color=#9FE870>×1.20</color>') -and
                 -not $unknownHover.Contains("`n")) 'Unknown or legacy effect breakdowns must show the valid combined title without fabricated factor details.'
}
$originalHoverCulture = [Threading.Thread]::CurrentThread.CurrentCulture
try
{
    [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('de-DE')
    $localizedHover = [string]$formatEatenFoodHover.Invoke($null, [object[]] @(
        '당근', '종합 효과', [string[]] @('풀 코스', '셰프', '신선도', '반복'),
        [float]1.35, [float]1.2, $originalActiveA))
    Assert-True ($localizedHover.StartsWith('<color=orange>당근</color> — 종합 효과 <color=#9FE870>×1.35</color>') -and
                 $localizedHover.Contains('풀 코스 ×1.20') -and $localizedHover.Contains('셰프 ×1.50') -and
                 $localizedHover.Contains('신선도 ×0.75') -and -not $localizedHover.Contains('1,35')) 'Eaten-hover labels must be supplied by localization while multiplier formatting remains culture-invariant.'
}
finally
{
    [Threading.Thread]::CurrentThread.CurrentCulture = $originalHoverCulture
}
# Exercise the real persisted-snapshot rebase without initializing Unity or writing a config file.
$applySlotCount = Get-MethodRequired $slotProgressionType 'ApplySlotCount'
$originalMaxSlotsConfig = $maxFoodSlotsConfigField.GetValue($null)
$originalFoodScaleConfig = $foodStatScaleConfigField.GetValue($null)
$rebaseMaxSlotsConfig = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($maxFoodSlotsConfigField.FieldType)
$rebaseFoodScaleConfig = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($foodStatScaleConfigField.FieldType)
$rebaseMaxSlotsValue = $maxFoodSlotsConfigField.FieldType.GetField('_typedValue', [Reflection.BindingFlags] 'Instance,NonPublic')
$rebaseFoodScaleValue = $foodStatScaleConfigField.FieldType.GetField('_typedValue', [Reflection.BindingFlags] 'Instance,NonPublic')
Assert-True ($null -ne $rebaseMaxSlotsValue -and $null -ne $rebaseFoodScaleValue) 'Managed config-entry value storage required for the no-Unity rebase smoke is missing.'
try
{
    $maxFoodSlotsConfigField.SetValue($null, $rebaseMaxSlotsConfig)
    $foodStatScaleConfigField.SetValue($null, $rebaseFoodScaleConfig)
    foreach ($previousSlots in 3..9)
    {
        foreach ($nextSlots in 3..9)
        {
            foreach ($nextConfiguredScale in @([float]0.9, [float]1.2))
            {
                $rebaseMaxSlotsValue.SetValue($rebaseMaxSlotsConfig, [int]$nextSlots)
                $rebaseFoodScaleValue.SetValue($rebaseFoodScaleConfig, $nextConfiguredScale)
                $rebaseState = [Activator]::CreateInstance($stateType, $true)
                $previousBaseScale = [float]$calculateBaseSlotScale.Invoke($null, [object[]] @([int]$previousSlots, [int]$previousSlots, [float]0.9))
                $nextBaseScale = [float]$calculateBaseSlotScale.Invoke($null, [object[]] @([int]$nextSlots, [int]$nextSlots, $nextConfiguredScale))
                $unlockedFoodSlotsField.SetValue($rebaseState, [int]$previousSlots)
                $appliedBaseSlotScaleField.SetValue($rebaseState, $previousBaseScale)
                $snapshotComponents = @(
                    @([float]0.75, [float]1, $false, [float]1),
                    @([float]1, [float]1, $false, [float]1),
                    @([float]0.9, [float]1, $true, [float]1.5),
                    @([float]0.75, [float]0.75, $false, [float]1),
                    @([float]0, [float]1, $true, [float]1.5))
                $snapshotFactors = @([float]0.75, [float]1, [float]1.35, [float]0.5625, [float]0)
                for ($snapshotIndex = 0; $snapshotIndex -lt $snapshotFactors.Count; $snapshotIndex++)
                {
                    $snapshotComponent = $snapshotComponents[$snapshotIndex]
                    $snapshotEffect = New-SmokeFoodEffect ([float]($previousBaseScale * $snapshotFactors[$snapshotIndex])) $snapshotComponent[0] $snapshotComponent[1] $snapshotComponent[2] $snapshotComponent[3]
                    $setActiveFoodEffect.Invoke($null, [object[]] @(
                        $rebaseState,
                        "RebaseFood$snapshotIndex",
                        $snapshotEffect)) | Out-Null
                }
                $applySlotCount.Invoke($null, [object[]] @($rebaseState, [int]$nextSlots)) | Out-Null
                Assert-True ([int]$unlockedFoodSlotsField.GetValue($rebaseState) -eq $nextSlots -and
                             [Math]::Abs([float]$appliedBaseSlotScaleField.GetValue($rebaseState) - $nextBaseScale) -lt 0.0001) 'Applying a supported slot count must persist its matching dynamic base scale.'
                $rebasedActiveFoods = $activeField.GetValue($rebaseState)
                Assert-True ($rebasedActiveFoods.Count -eq $snapshotFactors.Count) 'Slot rebasing itself must not discard or duplicate persisted active-food entries.'
                for ($snapshotIndex = 0; $snapshotIndex -lt $snapshotFactors.Count; $snapshotIndex++)
                {
                    $rebasedActiveFood = $rebasedActiveFoods[$snapshotIndex]
                    $expectedAppliedScale = $nextBaseScale * $snapshotFactors[$snapshotIndex]
                    Assert-True ([string]$activeKeyField.GetValue($rebasedActiveFood) -eq "RebaseFood$snapshotIndex" -and
                                 [Math]::Abs([float]$activeScaleField.GetValue($rebasedActiveFood) - $expectedAppliedScale) -lt 0.0001) "Changing slots $previousSlots -> $nextSlots must preserve active order and per-food Chef/Diminish factors."
                    $snapshotComponent = $snapshotComponents[$snapshotIndex]
                    Assert-True ([bool]$activeHasBreakdownField.GetValue($rebasedActiveFood) -and
                                 [float]$activeFreshnessScaleField.GetValue($rebasedActiveFood) -eq [float]$snapshotComponent[0] -and
                                 [float]$activeDiminishingScaleField.GetValue($rebasedActiveFood) -eq [float]$snapshotComponent[1] -and
                                 [float]$activeChefMultiplierField.GetValue($rebasedActiveFood) -eq [float]$snapshotComponent[3]) "Changing slots $previousSlots -> $nextSlots must not rebase, round, or recalculate the consumed hover breakdown."
                }
                Assert-True (-not [bool]$applySlotCount.Invoke($null, [object[]] @($rebaseState, [int]$nextSlots))) 'Reapplying the same slot count and scale must be idempotent.'
            }
        }
    }
}
finally
{
    $maxFoodSlotsConfigField.SetValue($null, $originalMaxSlotsConfig)
    $foodStatScaleConfigField.SetValue($null, $originalFoodScaleConfig)
}
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
$getNextStack = Get-MethodRequired $historyServiceType 'GetNextStack'
$previewHistoryEntry = @($recent | Where-Object {
    [string]$historyType.GetField('Key').GetValue($_) -eq 'A'
})[0]
$historyStackField = $historyType.GetField('Stack')
$historyStackField.SetValue($previewHistoryEntry, 2)
Assert-True ([int]$getNextStack.Invoke($null, [object[]] @($state, 'A', $false)) -eq 3) 'A regular food eaten twice must preview a neutral third consumption below the default threshold.'
$historyStackField.SetValue($previewHistoryEntry, 3)
Assert-True ([int]$getNextStack.Invoke($null, [object[]] @($state, 'A', $false)) -eq 4) 'A regular food eaten three times must preview the diminishing fourth consumption.'
Assert-True ([int]$getNextStack.Invoke($null, [object[]] @($state, 'A', $true)) -eq 1) 'Chef Choice must reset the preview stack so the same food is exempt from x0.75 diminishing returns.'
$historyStackField.SetValue($previewHistoryEntry, 4)
Assert-True ([int]$getNextStack.Invoke($null, [object[]] @($state, 'A', $false)) -eq 5) 'A regular food already eaten four times must continue previewing a diminished later consumption.'

$axisDictionaryDefinition = [Collections.Generic.Dictionary[string, int]].GetGenericTypeDefinition()
$axisDictionaryType = $axisDictionaryDefinition.MakeGenericType(
    [Type[]] @([string], $foodStatAxisType))
$foodAxesByKey = [Activator]::CreateInstance($axisDictionaryType)
$addFoodAxis = $axisDictionaryType.GetMethod('Add')
$historyCompositionState = [Activator]::CreateInstance($stateType, $true)
$historyCompositionRecent = $recentField.GetValue($historyCompositionState)
$historyCompositionSpecs = @(
    @('HealthRecent', 500, 'Health'),
    @('StaminaRecent1', 2, 'Stamina'),
    @('StaminaRecent2', 20, 'Stamina'),
    @('StaminaRecent3', 200, 'Stamina'),
    @('EitrRecent1', 3, 'Eitr'),
    @('EitrRecent2', 30, 'Eitr'),
    @('EitrRecent3', 300, 'Eitr'))
foreach ($spec in $historyCompositionSpecs)
{
    $key = [string]$spec[0]
    $axis = [Enum]::Parse($foodStatAxisType, [string]$spec[2])
    $null = $addFoodAxis.Invoke($foodAxesByKey, [object[]] @($key, $axis))
    $entry = [Activator]::CreateInstance($historyType, $true)
    $historyType.GetField('Key').SetValue($entry, $key)
    $historyType.GetField('Stack').SetValue($entry, [int]$spec[1])
    $historyCompositionRecent.Add($entry)
}

# An unresolved old/modded history entry must not enter the category denominator.
$unresolvedHistoryEntry = [Activator]::CreateInstance($historyType, $true)
$historyType.GetField('Key').SetValue($unresolvedHistoryEntry, 'UnavailableOldFood')
$historyType.GetField('Stack').SetValue($unresolvedHistoryEntry, 9999)
$historyCompositionRecent.Add($unresolvedHistoryEntry)
$getRecentFoodComposition = Get-MethodRequired $chefCollectionType 'GetRecentFoodComposition'
$recentFoodComposition = $getRecentFoodComposition.Invoke(
    $null,
    [object[]] @($historyCompositionState, $foodAxesByKey))
$getRecentFoodShare = Get-MethodRequired $recentFoodComposition.GetType() 'GetShare'
$recentHealthShare = [float]$getRecentFoodShare.Invoke($recentFoodComposition, [object[]] @(0))
$recentStaminaShare = [float]$getRecentFoodShare.Invoke($recentFoodComposition, [object[]] @(1))
$recentEitrShare = [float]$getRecentFoodShare.Invoke($recentFoodComposition, [object[]] @(2))
Assert-True ([Math]::Abs($recentHealthShare - (1.0 / 7.0)) -lt 0.0001) 'Each recent Health entry must count once regardless of its diminishing stack.'
Assert-True ([Math]::Abs($recentStaminaShare - (3.0 / 7.0)) -lt 0.0001) 'Each recent Stamina entry must count once regardless of its diminishing stack.'
Assert-True ([Math]::Abs($recentEitrShare - (3.0 / 7.0)) -lt 0.0001) 'Each recent Eitr entry must count once regardless of its diminishing stack.'

$reorderedCompositionState = [Activator]::CreateInstance($stateType, $true)
$reorderedCompositionRecent = $recentField.GetValue($reorderedCompositionState)
foreach ($spec in @($historyCompositionSpecs)[($historyCompositionSpecs.Count - 1)..0])
{
    $entry = [Activator]::CreateInstance($historyType, $true)
    $historyType.GetField('Key').SetValue($entry, [string]$spec[0])
    $historyType.GetField('Stack').SetValue($entry, 1)
    $reorderedCompositionRecent.Add($entry)
}
$reorderedFoodComposition = $getRecentFoodComposition.Invoke(
    $null,
    [object[]] @($reorderedCompositionState, $foodAxesByKey))
for ($axisIndex = 0; $axisIndex -lt 3; $axisIndex++)
{
    $originalShare = [float]$getRecentFoodShare.Invoke(
        $recentFoodComposition,
        [object[]] @($axisIndex))
    $reorderedShare = [float]$getRecentFoodShare.Invoke(
        $reorderedFoodComposition,
        [object[]] @($axisIndex))
    Assert-True ([Math]::Abs($originalShare - $reorderedShare) -lt 0.0001) 'Recent food-type composition must not depend on consumption order.'
}

$trySelectCandidateStart = $chefCollectionSource.IndexOf(
    'private static bool TrySelectWeightedCandidate(',
    [StringComparison]::Ordinal)
$getKnownCandidatesStart = $chefCollectionSource.IndexOf(
    'private static List<ChefCandidate> GetKnownFoodCandidates(',
    $trySelectCandidateStart,
    [StringComparison]::Ordinal)
Assert-True ($trySelectCandidateStart -ge 0 -and $getKnownCandidatesStart -gt $trySelectCandidateStart) 'Chef weighted-category selection source section was not found.'
$trySelectCandidateSource = $chefCollectionSource.Substring(
    $trySelectCandidateStart,
    $getKnownCandidatesStart - $trySelectCandidateStart)
Assert-True ($trySelectCandidateSource.Contains('availableHistoryShare += recentFoodComposition.GetShare(axisIndex);')) 'Chef history shares must be summed only for categories with available candidates.'
Assert-True ([regex]::IsMatch($trySelectCandidateSource, 'recentFoodComposition\.GetShare\(candidate\.AxisIndex\)\s*/\s*availableHistoryShare')) 'Chef history probabilities must be renormalized over available food categories.'
Assert-True ([regex]::IsMatch($trySelectCandidateSource, 'categoryProbability\s*\*\s*baseWeights\[index\]\s*/\s*categoryBaseWeight')) 'Chef selection must preserve tier weighting within the selected food category.'
$recentCompositionMethodStart = $chefCollectionSource.IndexOf(
    'private static RecentFoodComposition GetRecentFoodComposition(',
    [StringComparison]::Ordinal)
$rollChefMultiplierStart = $chefCollectionSource.IndexOf(
    'private static float RollChefMultiplier(',
    $recentCompositionMethodStart,
    [StringComparison]::Ordinal)
Assert-True ($recentCompositionMethodStart -ge 0 -and $rollChefMultiplierStart -gt $recentCompositionMethodStart) 'Recent Chef food composition source section was not found.'
$recentCompositionSource = $chefCollectionSource.Substring(
    $recentCompositionMethodStart,
    $rollChefMultiplierStart - $recentCompositionMethodStart)
Assert-True ($recentCompositionSource.Contains('foreach (HistoryEntryData entry in state.Recent)')) 'Recent Chef category preference must count the unique history entries.'
Assert-True (-not $recentCompositionSource.Contains('entry.Stack')) 'Recent Chef category preference must not weight entries by their diminishing stack.'

# If Eitr has history but no currently available candidate, the available
# Health/Stamina shares must renormalize from 1:3:3 to 1:3 rather than losing
# the unavailable Eitr probability mass.
$availableHistoryShare = [float]($recentHealthShare + $recentStaminaShare)
$availableHealthProbability = [float]$blendChefCategoryProbability.Invoke(
    $null,
    [object[]] @([float]0.5, [float]($recentHealthShare / $availableHistoryShare), [float]100))
$availableStaminaProbability = [float]$blendChefCategoryProbability.Invoke(
    $null,
    [object[]] @([float]0.5, [float]($recentStaminaShare / $availableHistoryShare), [float]100))
Assert-True ([Math]::Abs(($availableHealthProbability + $availableStaminaProbability) - 1) -lt 0.0001) 'Available Chef categories must retain a normalized probability total.'
Assert-True ([Math]::Abs(($availableStaminaProbability / $availableHealthProbability) - 3) -lt 0.0001) 'Available-history renormalization must preserve the requested 3:1 Stamina/Health ratio.'

$tryConsumeChefStart = $chefCollectionSource.IndexOf(
    'internal static bool TryConsumeChefEntry(',
    [StringComparison]::Ordinal)
$refillAfterConsumptionStart = $chefCollectionSource.IndexOf(
    'internal static bool RefillAfterConsumption(',
    $tryConsumeChefStart,
    [StringComparison]::Ordinal)
Assert-True ($tryConsumeChefStart -ge 0 -and $refillAfterConsumptionStart -gt $tryConsumeChefStart) 'Chef consume/refill source boundaries were not found.'
$tryConsumeChefSource = $chefCollectionSource.Substring(
    $tryConsumeChefStart,
    $refillAfterConsumptionStart - $tryConsumeChefStart)
$chefRemovalIndex = $tryConsumeChefSource.IndexOf(
    'state.Chef.RemoveAt(index);',
    [StringComparison]::Ordinal)
Assert-True ($chefRemovalIndex -ge 0) 'Consuming a Chef entry must remove the matched collection entry.'
$afterChefRemovalSource = $tryConsumeChefSource.Substring($chefRemovalIndex)
Assert-True (-not $afterChefRemovalSource.Contains('EnsureChefCollection(')) 'TryConsumeChefEntry must not refill after removing the consumed Chef entry.'
Assert-True (-not $tryConsumeChefSource.Contains('RefillAfterConsumption(')) 'TryConsumeChefEntry must leave replacement selection to the post-history refill path.'

$playerFoodLogicSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\PlayerFoodLogic.cs') -Raw
Assert-True ($playerFoodLogicSource.Contains('PlayerPrivateAccess.GetTotalFoodValue(')) 'Food totals must pass through Player.GetTotalFoodValue so external player-stat postfixes remain intact.'
Assert-True (-not $playerFoodLogicSource.Contains('private static void GetTotalFoodValue(')) 'FineDining must not bypass external player-stat postfixes with a private food-total clone.'
Assert-True ([regex]::IsMatch(
    $playerFoodLogicSource,
    '(?s)float effectiveScale = isDietFood\s*\? FoodRules\.GetAppliedScale\(player, state, food\) \* fullCourseScale\s*: 1f;\s*food\.m_health = food\.m_item\.m_shared\.m_food \* effectiveScale \* normalizedTime;\s*food\.m_stamina = food\.m_item\.m_shared\.m_foodStamina \* effectiveScale \* normalizedTime;\s*food\.m_eitr = food\.m_item\.m_shared\.m_foodEitr \* effectiveScale \* normalizedTime;.*?PlayerPrivateAccess\.GetTotalFoodValue\(.*?player\.SetMaxHealth\(health, flashBar: true\);\s*player\.SetMaxStamina\(stamina, flashBar: true\);\s*PlayerPrivateAccess\.SetMaxEitr\(player, eitr, flashBar: true\);')) 'Dynamic slot and Full Course scales must affect only food contributions before untouched external Health/Stamina/Eitr totals are applied.'
Assert-True ([regex]::IsMatch(
    $playerFoodLogicSource,
    '(?s)regen \+= food\.m_item\.m_shared\.m_foodRegen \* scale;.*?float regenMultiplier = 1f;\s*player\.GetSEMan\(\)\.ModifyHealthRegen\(ref regenMultiplier\);\s*player\.Heal\(regen \* regenMultiplier\);')) 'Slot scaling must affect food health-regeneration contributions without scaling external status-effect regeneration multipliers.'
$consumeChefCallIndex = $playerFoodLogicSource.IndexOf(
    'ChefCollectionService.TryConsumeChefEntry(',
    [StringComparison]::Ordinal)
$registerRecentIndex = $playerFoodLogicSource.IndexOf(
    'RecentHistoryService.RegisterConsumption(',
    $consumeChefCallIndex,
    [StringComparison]::Ordinal)
$refillChefIndex = $playerFoodLogicSource.IndexOf(
    'ChefCollectionService.RefillAfterConsumption(',
    $registerRecentIndex,
    [StringComparison]::Ordinal)
Assert-True ($consumeChefCallIndex -ge 0 -and $registerRecentIndex -gt $consumeChefCallIndex -and $refillChefIndex -gt $registerRecentIndex) 'Chef consumption must register recent history before selecting its replacement.'

Assert-True ((Get-Constant $stateStoreType 'StatePrefix') -eq 'v3:') 'Diet state must use the v3 JSON prefix after adding persisted slot-profile rebasing.'
Assert-True ($stateType.IsSerializable -and $historyType.IsSerializable -and $activeFoodDataType.IsSerializable) 'Diet state models and ordered Active entries must remain compatible with Unity JsonUtility.'
Assert-True ($null -eq $stateType.GetField('ChefQueue', [Reflection.BindingFlags] 'Instance,Public,NonPublic')) 'The uniform Chef shuffle queue must not remain in persisted state.'
Assert-True ($null -eq $stateStoreType.GetMethod('NormalizeQueue', [Reflection.BindingFlags] 'Static,NonPublic')) 'Removed Chef queue normalization must not return.'
Assert-True ($null -eq $chefCollectionType.GetMethod('Shuffle', [Reflection.BindingFlags] 'Static,NonPublic')) 'Uniform Chef shuffle must not return.'
Assert-True ($null -eq $chefCollectionType.GetMethod('RebuildQueue', [Reflection.BindingFlags] 'Static,NonPublic')) 'Uniform Chef queue rebuilding must not return.'
Assert-Method $chefCollectionType 'RotateOldest'
$updateFoodLogicStart = $playerFoodLogicSourceForSlots.IndexOf(
    'internal static void UpdateFood(',
    [StringComparison]::Ordinal)
$refreshFoodStatsStart = $playerFoodLogicSourceForSlots.IndexOf(
    'internal static void RefreshFoodStats(',
    $updateFoodLogicStart,
    [StringComparison]::Ordinal)
Assert-True ($updateFoodLogicStart -ge 0 -and $refreshFoodStatsStart -gt $updateFoodLogicStart) 'PlayerFoodLogic.UpdateFood source boundaries are missing.'
$updateFoodLogicSource = $playerFoodLogicSourceForSlots.Substring(
    $updateFoodLogicStart,
    $refreshFoodStatsStart - $updateFoodLogicStart)
$naturalExpiryCountDeclaration = [regex]::Match(
    $updateFoodLogicSource,
    '(?m)^\s*int\s+(?<Name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*0\s*;\s*$')
Assert-True $naturalExpiryCountDeclaration.Success 'UpdateFood must batch naturally expired foods in an integer counter.'
$naturalExpiryCountName = [regex]::Escape($naturalExpiryCountDeclaration.Groups['Name'].Value)
$naturalExpiryIncrementPattern =
    '(?s)foods\.RemoveAt\(index\);[^}]*?if\s*\((?<Condition>.*?)\)\s*\{\s*' +
    $naturalExpiryCountName + '\+\+;\s*\}'
$naturalExpiryIncrement = [regex]::Match(
    $updateFoodLogicSource,
    $naturalExpiryIncrementPattern)
Assert-True $naturalExpiryIncrement.Success 'Each naturally expired food must increment the batched Chef Choice counter inside the expired-food loop.'
Assert-True ([regex]::IsMatch(
    $naturalExpiryIncrement.Groups['Condition'].Value,
    '(?:^|&&)\s*!forceUpdate\s*(?:&&|$)')) 'Forced UpdateFood calls must not count as natural food expiration.'
Assert-True ([regex]::Matches(
    $updateFoodLogicSource,
    $naturalExpiryCountName + '\+\+;').Count -eq 1) 'Natural expiration must have one counter increment site inside the expired-food loop.'
$rotateNaturalExpiryPattern =
    '(?s)if\s*\(\s*(?:' + $naturalExpiryCountName + '\s*>\s*0\s*&&\s*player\s*==\s*Player\.m_localPlayer|' +
    'player\s*==\s*Player\.m_localPlayer\s*&&\s*' + $naturalExpiryCountName + '\s*>\s*0)\s*\)\s*\{\s*' +
    'ChefCollectionService\.RotateOldest\(\s*player,\s*' + $naturalExpiryCountName + '\s*\);'
Assert-True ([regex]::IsMatch($updateFoodLogicSource, $rotateNaturalExpiryPattern)) 'Natural expiration must rotate Chef Choice only for the local player and pass the full expired-food count.'
Assert-True ([regex]::Matches(
    $updateFoodLogicSource,
    'ChefCollectionService\.RotateOldest\(').Count -eq 1) 'UpdateFood must have exactly one batched Chef Choice rotation site.'
Assert-True ($null -eq $assembly.GetType('FineDining.DietConsumePatch', $false)) 'The removed consume-time full Chef reroll patch must not return.'
$pukeChefRotationType = Get-TypeRequired $assembly 'FineDining.DietPukeChefRotationPatch'
Assert-HarmonyPatchTarget $assembly 'FineDining.DietPukeChefRotationPatch' 'SE_Puke' 'UpdateStatusEffect'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.DietPukeChefRotationPatch' 'HarmonyPrefix'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.DietPukeChefRotationPatch' 'HarmonyPostfix'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.DietPukeChefRotationPatch' 'HarmonyFinalizer'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.DietPukeChefRotationPatch' 'HarmonyPriority'
$pukeFoodRemovalRuntimeType = Get-TypeRequired $assembly 'FineDining.PukeFoodRemovalRuntime'
$pukeRuntimeFlags = [Reflection.BindingFlags] 'Static,Public,NonPublic'
$pukeThreadContextFields = @($pukeFoodRemovalRuntimeType.GetFields($pukeRuntimeFlags) | Where-Object {
    @($_.GetCustomAttributesData() | Where-Object {
        $_.AttributeType.FullName -eq 'System.ThreadStaticAttribute'
    }).Count -gt 0
})
Assert-True ($pukeThreadContextFields.Count -eq 1 -and
             $pukeThreadContextFields[0].FieldType -eq [int]) 'SE_Puke removal context must use one thread-static nesting-depth field.'
$enterPukeUpdate = Get-MethodRequired $pukeFoodRemovalRuntimeType 'EnterPukeUpdate'
$exitPukeUpdate = Get-MethodRequired $pukeFoodRemovalRuntimeType 'ExitPukeUpdate'
$tryRemoveOrderedFood = Get-MethodRequired $pukeFoodRemovalRuntimeType 'TryRemoveOrderedFood'
$selectPukeRemovalIndex = Get-MethodRequired $pukeFoodRemovalRuntimeType 'SelectRemovalIndex'
$tryGetElapsedSinceEaten = Get-MethodRequired $pukeFoodRemovalRuntimeType 'TryGetElapsedSinceEaten'
Assert-True ($enterPukeUpdate.ReturnType -eq [void] -and $enterPukeUpdate.GetParameters().Count -eq 0 -and
             $exitPukeUpdate.ReturnType -eq [void] -and $exitPukeUpdate.GetParameters().Count -eq 0) 'SE_Puke context entry and exit methods must be parameterless.'
$tryRemoveOrderedFoodParameters = @($tryRemoveOrderedFood.GetParameters())
Assert-True ($tryRemoveOrderedFood.ReturnType -eq [bool] -and
             $tryRemoveOrderedFoodParameters.Count -eq 2 -and
             $tryRemoveOrderedFoodParameters[0].ParameterType.FullName -eq 'Player' -and
             $tryRemoveOrderedFoodParameters[1].ParameterType.IsByRef -and
             $tryRemoveOrderedFoodParameters[1].ParameterType.GetElementType() -eq [bool]) 'Ordered Puke removal must report whether the Player.RemoveOneFood prefix handled the call and whether a food was removed.'
$selectPukeRemovalIndexParameters = @($selectPukeRemovalIndex.GetParameters())
Assert-True ($selectPukeRemovalIndex.ReturnType -eq [int] -and
             $selectPukeRemovalIndexParameters.Count -eq 3 -and
             $selectPukeRemovalIndexParameters[0].ParameterType.IsGenericType -and
             $selectPukeRemovalIndexParameters[0].ParameterType.GetGenericTypeDefinition().FullName -eq 'System.Collections.Generic.List`1' -and
             $selectPukeRemovalIndexParameters[1].ParameterType -eq $pukeFoodRemovalOrderType -and
             $selectPukeRemovalIndexParameters[2].ParameterType.IsGenericType -and
             $selectPukeRemovalIndexParameters[2].ParameterType.GetGenericTypeDefinition().FullName -eq 'System.Collections.Generic.IReadOnlyList`1' -and
             $selectPukeRemovalIndexParameters[2].ParameterType.GetGenericArguments()[0] -eq $activeFoodDataType -and
             $selectPukeRemovalIndexParameters[2].IsOptional) 'Puke removal selection must consume the active Player.Food list, configured enum, and persisted oldest-to-newest Active order.'
$playerFoodType = $selectPukeRemovalIndexParameters[0].ParameterType.GetGenericArguments()[0]
$tryGetElapsedSinceEatenParameters = @($tryGetElapsedSinceEaten.GetParameters())
Assert-True ($tryGetElapsedSinceEaten.ReturnType -eq [bool] -and
             $tryGetElapsedSinceEatenParameters.Count -eq 2 -and
             $tryGetElapsedSinceEatenParameters[0].ParameterType -eq $playerFoodType -and
             $tryGetElapsedSinceEatenParameters[1].ParameterType.IsByRef -and
             $tryGetElapsedSinceEatenParameters[1].ParameterType.GetElementType() -eq [float]) 'Puke timing extraction must expose a safe elapsed-time result for one Player.Food.'

$pukeContextDepthField = $pukeThreadContextFields[0]
$exitPukeUpdate.Invoke($null, [object[]] @()) | Out-Null
Assert-True ([int]$pukeContextDepthField.GetValue($null) -eq 0) 'SE_Puke context depth must be clear outside UpdateStatusEffect.'
try
{
    $enterPukeUpdate.Invoke($null, [object[]] @()) | Out-Null
    $enterPukeUpdate.Invoke($null, [object[]] @()) | Out-Null
    Assert-True ([int]$pukeContextDepthField.GetValue($null) -eq 2) 'Nested SE_Puke updates must retain a balanced thread-local depth.'
    $exitPukeUpdate.Invoke($null, [object[]] @()) | Out-Null
    Assert-True ([int]$pukeContextDepthField.GetValue($null) -eq 1) 'Exiting one nested SE_Puke update must keep the outer context active.'
}
finally
{
    $exitPukeUpdate.Invoke($null, [object[]] @()) | Out-Null
}
Assert-True ([int]$pukeContextDepthField.GetValue($null) -eq 0) 'SE_Puke context must be restored after balanced exit.'

$foodItemField = $playerFoodType.GetField('m_item', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
$foodRemainingTimeField = $playerFoodType.GetField('m_time', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
$foodNameField = $playerFoodType.GetField('m_name', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $foodItemField -and $null -ne $foodRemainingTimeField -and
             $null -ne $foodNameField) 'Player.Food identity and timing fields required by Puke ordering are unavailable.'
$itemDataType = $foodItemField.FieldType
$itemSharedField = $itemDataType.GetField('m_shared', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $itemSharedField) 'Player.Food item shared data required by Puke ordering is unavailable.'
$sharedDataType = $itemSharedField.FieldType
$foodBurnTimeField = $sharedDataType.GetField('m_foodBurnTime', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $foodBurnTimeField) 'Player.Food burn time required by Puke ordering is unavailable.'
$pukeSelectionFoods = [Activator]::CreateInstance($selectPukeRemovalIndexParameters[0].ParameterType)
foreach ($pukeFoodTiming in @(
    [pscustomobject]@{ BurnTime = [float]500; RemainingTime = [float]300 },
    [pscustomobject]@{ BurnTime = [float]100; RemainingTime = [float]10 },
    [pscustomobject]@{ BurnTime = [float]50; RemainingTime = [float]49 }))
{
    $food = [Activator]::CreateInstance($playerFoodType)
    $item = [Activator]::CreateInstance($itemDataType)
    $shared = [Activator]::CreateInstance($sharedDataType)
    $foodBurnTimeField.SetValue($shared, [float]$pukeFoodTiming.BurnTime)
    $itemSharedField.SetValue($item, $shared)
    $foodItemField.SetValue($food, $item)
    $foodRemainingTimeField.SetValue($food, [float]$pukeFoodTiming.RemainingTime)
    $pukeSelectionFoods.Add($food)
}
$elapsedSinceFirstFoodArguments = [object[]] @($pukeSelectionFoods[0], [float]0)
Assert-True ([bool]$tryGetElapsedSinceEaten.Invoke($null, $elapsedSinceFirstFoodArguments)) 'A valid active food must expose its elapsed time since consumption.'
Assert-True ([Math]::Abs([float]$elapsedSinceFirstFoodArguments[1] - [float]200) -lt [float]0.0001) 'Elapsed time since consumption must equal burn time minus remaining food time.'
$selectOldestArguments = [object[]] @($pukeSelectionFoods, $oldestPukeRemovalOrder, $null)
$selectNewestArguments = [object[]] @($pukeSelectionFoods, $newestPukeRemovalOrder, $null)
$selectRandomArguments = [object[]] @($pukeSelectionFoods, $randomPukeRemovalOrder, $null)
Assert-True ([int]$selectPukeRemovalIndex.Invoke($null, $selectOldestArguments) -eq 0) 'OldestFirst must select the food with the greatest elapsed time since it was last eaten.'
Assert-True ([int]$selectPukeRemovalIndex.Invoke($null, $selectNewestArguments) -eq 2) 'NewestFirst must select the food with the least elapsed time since it was last eaten.'
Assert-True ([int]$selectPukeRemovalIndex.Invoke($null, $selectRandomArguments) -eq -1) 'Random must not select an ordered-removal index.'
$sameTickFoods = [Activator]::CreateInstance($selectPukeRemovalIndexParameters[0].ParameterType)
foreach ($sameTickFoodKey in @('NewestTickFood', 'MiddleTickFood', 'OldestTickFood'))
{
    $food = [Activator]::CreateInstance($playerFoodType)
    $item = [Activator]::CreateInstance($itemDataType)
    $shared = [Activator]::CreateInstance($sharedDataType)
    $foodNameField.SetValue($food, $sameTickFoodKey)
    $foodBurnTimeField.SetValue($shared, [float]100)
    $itemSharedField.SetValue($item, $shared)
    $foodItemField.SetValue($food, $item)
    $foodRemainingTimeField.SetValue($food, [float]75)
    $sameTickFoods.Add($food)
}
$activeFoodListType = [Collections.Generic.List``1].MakeGenericType([Type[]] @($activeFoodDataType))
$sameTickConsumptionOrder = [Activator]::CreateInstance($activeFoodListType)
foreach ($sameTickOrderKey in @('OldestTickFood', 'MiddleTickFood', 'NewestTickFood'))
{
    $activeEntry = [Activator]::CreateInstance($activeFoodDataType, $true)
    $activeKeyField.SetValue($activeEntry, $sameTickOrderKey)
    $activeScaleField.SetValue($activeEntry, [float]1)
    $sameTickConsumptionOrder.Add($activeEntry)
}
$selectSameTickOldestArguments = [object[]] @(
    $sameTickFoods,
    $oldestPukeRemovalOrder,
    $sameTickConsumptionOrder)
$selectSameTickNewestArguments = [object[]] @(
    $sameTickFoods,
    $newestPukeRemovalOrder,
    $sameTickConsumptionOrder)
Assert-True ([int]$selectPukeRemovalIndex.Invoke($null, $selectSameTickOldestArguments) -eq 2) 'OldestFirst must use persisted consumption order, not slot order, when elapsed timers are equal.'
Assert-True ([int]$selectPukeRemovalIndex.Invoke($null, $selectSameTickNewestArguments) -eq 0) 'NewestFirst must use persisted consumption order, not slot order, when elapsed timers are equal.'
$nullElapsedArguments = [object[]] @($null, [float]0)
Assert-True (-not [bool]$tryGetElapsedSinceEaten.Invoke($null, $nullElapsedArguments)) 'Malformed active foods must not crash elapsed-time selection.'

$removeOneFoodPatchStart = $playerFoodPatchesSource.IndexOf(
    '[HarmonyPatch(nameof(Player.RemoveOneFood))]',
    [StringComparison]::Ordinal)
$clearFoodPatchStart = $playerFoodPatchesSource.IndexOf(
    '[HarmonyPatch(nameof(Player.ClearFood))]',
    $removeOneFoodPatchStart,
    [StringComparison]::Ordinal)
Assert-True ($removeOneFoodPatchStart -ge 0 -and $clearFoodPatchStart -gt $removeOneFoodPatchStart) 'Player.RemoveOneFood patch source boundaries are missing.'
$removeOneFoodPatchSource = $playerFoodPatchesSource.Substring(
    $removeOneFoodPatchStart,
    $clearFoodPatchStart - $removeOneFoodPatchStart)
Assert-True ([regex]::IsMatch(
    $removeOneFoodPatchSource,
    '(?s)\[HarmonyPrefix\].*?RemoveOneFoodPrefix\(.*?if\s*\(\s*!PukeFoodRemovalRuntime\.TryRemoveOrderedFood\(\s*__instance\s*,\s*out\s+bool\s+\w+\s*\)\s*\)\s*\{\s*return true;\s*\}.*?__result\s*=\s*\w+;\s*return false;')) 'Player.RemoveOneFood must run vanilla when ordered Puke handling declines and skip vanilla only after an ordered-mode result.'
Assert-True ([regex]::IsMatch(
    $removeOneFoodPatchSource,
    '(?s)\[HarmonyPostfix\].*?RemoveOneFoodPostfix\(.*?if\s*\(\s*__result\s*\).*?ApplyPendingAfterFoodRemoval\(')) 'The existing successful-removal postfix must remain after adding the Puke prefix.'

$pukeRemovalRuntimeStart = $playerFoodPatchesSource.IndexOf(
    'internal static class PukeFoodRemovalRuntime',
    [StringComparison]::Ordinal)
Assert-True ($pukeRemovalRuntimeStart -ge 0) 'Puke removal runtime source is missing.'
$pukeRemovalRuntimeSource = $playerFoodPatchesSource.Substring($pukeRemovalRuntimeStart)
$tryRemoveOrderedFoodStart = $pukeRemovalRuntimeSource.IndexOf(
    'internal static bool TryRemoveOrderedFood(',
    [StringComparison]::Ordinal)
$selectRemovalIndexStart = $pukeRemovalRuntimeSource.IndexOf(
    'internal static int SelectRemovalIndex(',
    $tryRemoveOrderedFoodStart,
    [StringComparison]::Ordinal)
$tryGetElapsedStart = $pukeRemovalRuntimeSource.IndexOf(
    'internal static bool TryGetElapsedSinceEaten(',
    $selectRemovalIndexStart,
    [StringComparison]::Ordinal)
Assert-True ($tryRemoveOrderedFoodStart -ge 0 -and
             $selectRemovalIndexStart -gt $tryRemoveOrderedFoodStart -and
             $tryGetElapsedStart -gt $selectRemovalIndexStart) 'Puke ordered-removal source boundaries are missing.'
$tryRemoveOrderedFoodSource = $pukeRemovalRuntimeSource.Substring(
    $tryRemoveOrderedFoodStart,
    $selectRemovalIndexStart - $tryRemoveOrderedFoodStart)
$selectRemovalIndexSource = $pukeRemovalRuntimeSource.Substring(
    $selectRemovalIndexStart,
    $tryGetElapsedStart - $selectRemovalIndexStart)
$tryGetElapsedSource = $pukeRemovalRuntimeSource.Substring($tryGetElapsedStart)
Assert-True ([regex]::IsMatch(
    $tryRemoveOrderedFoodSource,
    '(?s)_pukeUpdateDepth\s*<=\s*0.*?return false;')) 'Ordered food removal must decline calls outside the thread-local SE_Puke context.'
Assert-True ([regex]::IsMatch(
    $tryRemoveOrderedFoodSource,
    '(?s)order\s*==\s*PukeFoodRemovalOrder\.Random\s*\)\s*\{.*?return false;.*?SelectRemovalIndex\(')) 'Random Puke removal must delegate to the original vanilla Player.RemoveOneFood implementation.'
Assert-True ([regex]::IsMatch(
    $tryRemoveOrderedFoodSource,
    '(?s)SelectRemovalIndex\(.*?foods\.RemoveAt\(\s*removalIndex\s*\);.*?removed\s*=\s*true;.*?return true;')) 'Only an ordered Puke mode may remove the selected active-food entry and report the prefix as handled.'
Assert-True ([regex]::IsMatch(
    $tryRemoveOrderedFoodSource,
    '(?s)FoodStateStore\.GetState\(\s*player\s*\).*?SelectRemovalIndex\(\s*foods\s*,\s*order\s*,\s*state\.Active\s*\)')) 'Ordered Puke removal must pass the persisted oldest-to-newest Active order into selection.'
Assert-True ($selectRemovalIndexSource.Contains('TryGetElapsedSinceEaten(foods[index], out float elapsed)')) 'Puke ordering must compare normalized elapsed-time values for each active food.'
Assert-True ($selectRemovalIndexSource.Contains('GetConsumptionRank(foods[index], consumptionOrder)') -and
             $selectRemovalIndexSource.Contains('IsPreferredTie(')) 'Equal Puke elapsed timers must be resolved by persisted consumption rank.'
Assert-True ([regex]::IsMatch(
    $selectRemovalIndexSource,
    '(?s)SelectByConsumptionOrder\(\s*foods\s*,\s*consumptionOrder\s*,\s*newestFirst\s*\).*?selectedIndex\s*>=\s*0.*?orderedFallback\s*>=\s*0')) 'Malformed-timing fallback must prefer persisted consumption order before slot order.'
Assert-True ([regex]::IsMatch(
    $tryGetElapsedSource,
    '(?s)burnTime\s*=\s*shared\.m_foodBurnTime;.*?remainingTime\s*=\s*food!?\.m_time;.*?burnTime\s*-\s*remainingTime')) 'Puke ordering must derive last-consumption age from food burn time minus remaining time.'

$pukePatchStart = $dietTooltipSource.IndexOf(
    '[HarmonyPatch(typeof(SE_Puke), nameof(SE_Puke.UpdateStatusEffect))]',
    [StringComparison]::Ordinal)
$pukePatchEnd = $dietTooltipSource.IndexOf(
    'internal static class CookingSkillTooltipText',
    $pukePatchStart,
    [StringComparison]::Ordinal)
Assert-True ($pukePatchStart -ge 0 -and $pukePatchEnd -gt $pukePatchStart) 'SE_Puke patch source boundaries are missing.'
$pukePatchSource = $dietTooltipSource.Substring($pukePatchStart, $pukePatchEnd - $pukePatchStart)
Assert-True ([regex]::IsMatch(
    $pukePatchSource,
    '(?s)\[HarmonyPrefix\].*?Prefix\(.*?PukeFoodRemovalRuntime\.EnterPukeUpdate\(\);')) 'SE_Puke prefix must enter the ordered-removal context before vanilla UpdateStatusEffect runs.'
Assert-True ([regex]::IsMatch(
    $pukePatchSource,
    '(?s)\[HarmonyPostfix\].*?Postfix\(.*?CountRemovedFoods\(.*?ChefCollectionService\.RotateOldest\(')) 'SE_Puke postfix must retain actual-removal counting and Chef Choice rotation.'
Assert-True ([regex]::IsMatch(
    $pukePatchSource,
    '(?s)\[HarmonyFinalizer\].*?Finalizer\(.*?PukeFoodRemovalRuntime\.ExitPukeUpdate\(\);.*?return __exception;')) 'SE_Puke finalizer must always leave the ordered-removal context while preserving the original exception.'
$allPukeContextCallSource = (@(
    Get-ChildItem -LiteralPath $projectRoot -Recurse -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj)[\\/]' } |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n")
Assert-True ([regex]::Matches($allPukeContextCallSource, 'PukeFoodRemovalRuntime\.EnterPukeUpdate\(').Count -eq 1 -and
             [regex]::Matches($allPukeContextCallSource, 'PukeFoodRemovalRuntime\.ExitPukeUpdate\(').Count -eq 1) 'Ordered-removal context entry and exit must remain scoped exclusively to SE_Puke.UpdateStatusEffect.'
$countRemovedFoods = Get-MethodRequired $pukeChefRotationType 'CountRemovedFoods'
Assert-True ([int]$countRemovedFoods.Invoke($null, [object[]] @(9, 8)) -eq 1) 'One Puke-removed food must advance Chef Choice once.'
Assert-True ([int]$countRemovedFoods.Invoke($null, [object[]] @(5, 2)) -eq 3) 'Multiple actual removals must advance Chef Choice once per food.'
Assert-True ([int]$countRemovedFoods.Invoke($null, [object[]] @(2, 2)) -eq 0) 'A Puke tick without a removed food must not advance Chef Choice.'
Assert-True ([int]$countRemovedFoods.Invoke($null, [object[]] @(1, 2)) -eq 0) 'An increased food count must not advance Chef Choice.'
Assert-HarmonyPatchTarget $assembly 'FineDining.DietPukeTooltipPatch' 'ItemDrop+ItemData' 'GetTooltip'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.DietPukeTooltipPatch' 'HarmonyPostfix'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.DietPukeTooltipPatch' 'HarmonyPriority'

$cookingSkillTooltipType = Get-TypeRequired $assembly 'FineDining.CookingSkillTooltipText'
$appendCookingSkillTooltip = Get-MethodRequired $cookingSkillTooltipType 'Append'
$matchesSkillDescription = Get-MethodRequired $cookingSkillTooltipType 'MatchesSkillDescription'
$hasFineDiningHeading = Get-MethodRequired $cookingSkillTooltipType 'HasFineDiningHeading'
$cookingHeadingToken = [string](Get-Constant $cookingSkillTooltipType 'HeadingToken')
$cookingAutoEjectToken = [string](Get-Constant $cookingSkillTooltipType 'AutoEjectToken')
$cookingBonusOutputToken = [string](Get-Constant $cookingSkillTooltipType 'BonusOutputToken')
$cookingChefTierToken = [string](Get-Constant $cookingSkillTooltipType 'ChefTierToken')
$cookingChefMultiplierToken = [string](Get-Constant $cookingSkillTooltipType 'ChefMultiplierToken')
$cookingChefBothToken = [string](Get-Constant $cookingSkillTooltipType 'ChefBothToken')
$vanillaCookingDescription = '$skill_cooking_description' + "`n" + '$third_party_skill_suffix'
foreach ($tooltipCase in @(
    @($false, $false, $false, @()),
    @($true,  $false, $false, @($cookingBonusOutputToken)),
    @($false, $true,  $false, @($cookingChefTierToken)),
    @($false, $false, $true,  @($cookingChefMultiplierToken)),
    @($true,  $true,  $false, @($cookingBonusOutputToken, $cookingChefTierToken)),
    @($true,  $false, $true,  @($cookingBonusOutputToken, $cookingChefMultiplierToken)),
    @($false, $true,  $true,  @($cookingChefBothToken)),
    @($true,  $true,  $true,  @($cookingBonusOutputToken, $cookingChefBothToken))))
{
    $bonusEnabled = [bool]$tooltipCase[0]
    $tierEnabled = [bool]$tooltipCase[1]
    $multiplierEnabled = [bool]$tooltipCase[2]
    $expectedOptionalTokens = @($tooltipCase[3])
    $result = [string]$appendCookingSkillTooltip.Invoke(
        $null,
        [object[]] @(
            $vanillaCookingDescription,
            $bonusEnabled,
            $tierEnabled,
            $multiplierEnabled))
    $expectedLines = @($cookingHeadingToken, $cookingAutoEjectToken) + $expectedOptionalTokens
    $expectedResult = $vanillaCookingDescription + "`n`n" + ($expectedLines -join "`n")
    Assert-True ($result -eq $expectedResult) 'Cooking skill tooltip must preserve prior text and emit enabled FineDining lines in their exact display order.'
    foreach ($optionalToken in @($cookingBonusOutputToken, $cookingChefTierToken, $cookingChefMultiplierToken, $cookingChefBothToken))
    {
        $expected = $expectedOptionalTokens -contains $optionalToken
        Assert-True ($result.Contains($optionalToken) -eq $expected) "Unexpected conditional Cooking skill token: $optionalToken"
    }

    $secondPass = [string]$appendCookingSkillTooltip.Invoke(
        $null,
        [object[]] @($result, -not $bonusEnabled, -not $tierEnabled, -not $multiplierEnabled))
    Assert-True ($secondPass -eq $result) 'Cooking skill tooltip append must remain idempotent even if settings change before a repeated postfix.'
    Assert-True ([regex]::Matches($result, [regex]::Escape($cookingHeadingToken)).Count -eq 1) 'Cooking skill tooltip heading must be appended exactly once.'
}

$emptyCookingTooltip = [string]$appendCookingSkillTooltip.Invoke(
    $null,
    [object[]] @($null, $false, $false, $false))
Assert-True ($emptyCookingTooltip -eq ($cookingHeadingToken + "`n" + $cookingAutoEjectToken)) 'An empty Cooking description must not receive leading blank lines.'
Assert-True ([bool]$matchesSkillDescription.Invoke($null, [object[]] @('$skill_cooking_description', '$skill_cooking_description'))) 'Cooking tooltip matching must accept the exact vanilla token.'
Assert-True ([bool]$matchesSkillDescription.Invoke($null, [object[]] @($vanillaCookingDescription, '$skill_cooking_description'))) 'Cooking tooltip matching must retain compatibility with third-party suffixes.'
Assert-True (-not [bool]$matchesSkillDescription.Invoke($null, [object[]] @('$skill_run_description', '$skill_cooking_description'))) 'Cooking tooltip matching must reject another skill.'
Assert-True (-not [bool]$matchesSkillDescription.Invoke($null, [object[]] @($null, '$skill_cooking_description'))) 'Cooking tooltip matching must reject a null tooltip.'
Assert-True (-not [bool]$matchesSkillDescription.Invoke($null, [object[]] @('$skill_cooking_description', ''))) 'Cooking tooltip matching must reject an empty skill description.'
Assert-True ([bool]$hasFineDiningHeading.Invoke($null, [object[]] @($cookingHeadingToken))) 'Cooking alignment must recognize the exact raw FineDining heading token.'
Assert-True ([bool]$hasFineDiningHeading.Invoke($null, [object[]] @($vanillaCookingDescription + "`n`n" + $cookingHeadingToken + "`n" + $cookingAutoEjectToken))) 'Cooking alignment must recognize the raw FineDining heading inside an appended tooltip.'
Assert-True (-not [bool]$hasFineDiningHeading.Invoke($null, [object[]] @('$skill_cooking_description'))) 'Cooking alignment must reject a tooltip without the FineDining heading.'
Assert-True (-not [bool]$hasFineDiningHeading.Invoke($null, [object[]] @(''))) 'Cooking alignment must reject empty tooltip text.'
$nullCookingHeadingArguments = New-Object 'System.Object[]' 1
Assert-True (-not [bool]$hasFineDiningHeading.Invoke($null, $nullCookingHeadingArguments)) 'Cooking alignment must reject null tooltip text.'

Assert-HarmonyPatchTarget $assembly 'FineDining.CookingSkillTooltipPatch' 'SkillsDialog' 'Setup'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.CookingSkillTooltipPatch' 'HarmonyPostfix'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.CookingSkillTooltipPatch' 'HarmonyPriority'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.CookingSkillTooltipPatch' 'HarmonyAfter'
$dietTooltipSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\TooltipAndConsumePatches.cs') -Raw
$cookingPatchStart = $dietTooltipSource.IndexOf('internal static class CookingSkillTooltipPatch', [StringComparison]::Ordinal)
$cookingAlignmentPatchStart = $dietTooltipSource.IndexOf('internal static class CookingSkillTooltipAlignmentPatch', $cookingPatchStart, [StringComparison]::Ordinal)
Assert-True ($cookingPatchStart -ge 0 -and $cookingAlignmentPatchStart -gt $cookingPatchStart) 'Cooking skill tooltip patch source could not be isolated.'
$cookingPatchSource = $dietTooltipSource.Substring($cookingPatchStart, $cookingAlignmentPatchStart - $cookingPatchStart)
Assert-True ([regex]::IsMatch(
    $cookingPatchSource,
    '(?s)\[HarmonyPostfix\]\s*\[HarmonyPriority\(Priority\.Last\)\]\s*\[HarmonyAfter\("randyknapp\.mods\.epicloot"\)\]\s*private static void Postfix')) 'Cooking skill tooltip must run at last priority after EpicLoot.'
Assert-True ($cookingPatchSource.Contains('CookingSkillTooltipText.Append(') -and
             $cookingPatchSource.Contains('DietConfig.GetCookingBonusChanceAtMaxCookingPercent() > 0f') -and
             $cookingPatchSource.Contains('DietConfig.GetFermenterOutputBonusChanceAtMaxCookingPercent() > 0f')) 'Cooking skill tooltip must advertise bonus output while either direct production-bonus chance is enabled.'
Assert-True ([regex]::IsMatch(
    $cookingPatchSource,
    '(?s)DietConfig\.GetChefHighTierSelectionStrength\(\) > 0f,\s*DietConfig\.GetChefMultiplierModeAtMaxCooking\(\) >\s*DietConfig\.GetChefMultiplierMin\(\)\)')) 'Cooking tooltip settings must expose multiplier-mode progression only when Cooking can move the mode above the minimum.'
Assert-True ($cookingPatchSource.Contains('GetComponentsInChildren<UITooltip>(true)')) 'Cooking skill tooltip must retain a replacement-UI row fallback.'
Assert-True ($cookingPatchSource.Contains('tooltip.m_topic,') -and $cookingPatchSource.Contains('tooltip.m_anchor,') -and $cookingPatchSource.Contains('tooltip.m_fixedPosition')) 'Cooking skill tooltip update must preserve the existing tooltip layout and topic.'
Assert-True (-not $dietTooltipSource.Contains('m_info.m_description =')) 'FineDining must not mutate shared skill descriptions.'

Assert-HarmonyPatchTarget $assembly 'FineDining.CookingSkillTooltipAlignmentPatch' 'UITooltip' 'UpdateTextElements'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.CookingSkillTooltipAlignmentPatch' 'HarmonyPostfix'
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.CookingSkillTooltipAlignmentPatch' 'HarmonyPriority'
$cookingAlignmentPatchSource = $dietTooltipSource.Substring($cookingAlignmentPatchStart)
Assert-True ([regex]::IsMatch(
    $cookingAlignmentPatchSource,
    '(?s)\[HarmonyPostfix\]\s*\[HarmonyPriority\(Priority\.Last\)\]\s*private static void Postfix\(UITooltip __instance\)')) 'Cooking tooltip body alignment must run after the instantiated tooltip text has been populated.'
Assert-True ($cookingAlignmentPatchSource.Contains('CookingSkillTooltipText.HasFineDiningHeading(__instance.m_text)')) 'Cooking tooltip body alignment must be restricted by the raw FineDining heading.'
Assert-True ($cookingAlignmentPatchSource.Contains('UITooltip.m_current != null && UITooltip.m_current != __instance')) 'Cooking tooltip body alignment must ignore a global tooltip owned by another active UITooltip while allowing initial render before ownership is assigned.'
Assert-True ($cookingAlignmentPatchSource.Contains('UITooltip.m_tooltip.GetComponentsInChildren<TMP_Text>(true)')) 'Cooking tooltip body alignment must inspect only the instantiated tooltip hierarchy.'
Assert-True ($cookingAlignmentPatchSource.Contains('string.Equals(textElement.name, "Text", StringComparison.Ordinal)')) 'Cooking tooltip body alignment must select only the body element named Text.'
Assert-True ($cookingAlignmentPatchSource.Contains('textElement.horizontalAlignment = HorizontalAlignmentOptions.Left;')) 'Cooking tooltip body must use horizontal-only left alignment.'
Assert-True (-not $cookingAlignmentPatchSource.Contains('m_tooltipPrefab')) 'Cooking tooltip body alignment must never mutate the shared tooltip prefab.'
Assert-True (-not $cookingAlignmentPatchSource.Contains('"Topic"')) 'Cooking tooltip body alignment must leave the topic/title element untouched.'
Assert-True (-not $cookingAlignmentPatchSource.Contains('textElement.verticalAlignment =') -and
             -not $cookingAlignmentPatchSource.Contains('textElement.alignment =')) 'Cooking tooltip body alignment must not change vertical or combined alignment.'
Assert-Method $stateStoreType 'SerializeState'
Assert-Method $stateStoreType 'DeserializeState'

$chefTierInfoType = Get-TypeRequired $assembly 'FineDining.ChefFoodTierInfo'
$chefTierReferenceType = Get-TypeRequired $assembly 'FineDining.ChefTierReferenceGenerator'
$chefTierInfoFlags = [Reflection.BindingFlags] 'Instance,Public,NonPublic'
Assert-True ($null -ne $chefTierInfoType.GetProperty('ItemNameToken', $chefTierInfoFlags)) 'Chef tier catalog entries must carry the known-food token used by candidate selection.'
Assert-True ($null -ne $chefTierInfoType.GetProperty('Axis', $chefTierInfoFlags)) 'Chef tier catalog entries must carry their H/S/E classification axis.'
$referenceEntryType = Get-TypeRequired $assembly 'FineDining.ChefTierReferenceGenerator+ReferenceEntry'
$tierInfoConstructor = @($chefTierInfoType.GetConstructors([Reflection.BindingFlags] 'Instance,NonPublic'))[0]
$referenceEntryConstructor = @($referenceEntryType.GetConstructors([Reflection.BindingFlags] 'Instance,NonPublic'))[0]
$withFoodIdentity = Get-MethodRequired $chefTierInfoType 'WithFoodIdentity'
$foodStatAxisType = Get-TypeRequired $assembly 'FineDining.FoodStatAxis'
$healthAxis = [Enum]::Parse($foodStatAxisType, 'Health')
$staminaAxis = [Enum]::Parse($foodStatAxisType, 'Stamina')
$eitrAxis = [Enum]::Parse($foodStatAxisType, 'Eitr')
$vanillaTierInfo = $tierInfoConstructor.Invoke([object[]] @('VanillaFallback', -1, 8, 'AllTier', 'no_recipe_or_conversion', [string[]] @()))
$modTierInfo = $tierInfoConstructor.Invoke([object[]] @('ModFallback', -1, 8, 'AllTier', 'unmapped_sources', [string[]] @('ModIngredient')))
$meadowsStaminaInfo = $tierInfoConstructor.Invoke([object[]] @('MeadowsStamina', 0, 8, 'Meadows', '', [string[]] @()))
$meadowsModHealthInfo = $tierInfoConstructor.Invoke([object[]] @('MeadowsModHealth', 0, 8, 'Meadows', '', [string[]] @()))
$meadowsVanillaHealthInfo = $tierInfoConstructor.Invoke([object[]] @('MeadowsVanillaHealth', 0, 8, 'Meadows', '', [string[]] @()))
$meadowsEitrInfo = $tierInfoConstructor.Invoke([object[]] @('MeadowsEitr', 0, 8, 'Meadows', '', [string[]] @()))
$blackForestHealthInfo = $tierInfoConstructor.Invoke([object[]] @('BlackForestHealth', 1, 8, 'BlackForest', '', [string[]] @()))
$reservedTierNameInfo = $tierInfoConstructor.Invoke([object[]] @('ReservedTierFood', 2, 8, 'unassigned', '', [string[]] @()))
$withFoodIdentity.Invoke($vanillaTierInfo, [object[]] @('$item_vanilla_fallback', $staminaAxis)) | Out-Null
$withFoodIdentity.Invoke($modTierInfo, [object[]] @('$item_mod_fallback', $eitrAxis)) | Out-Null
$withFoodIdentity.Invoke($meadowsStaminaInfo, [object[]] @('$item_meadows_stamina', $staminaAxis)) | Out-Null
$withFoodIdentity.Invoke($meadowsModHealthInfo, [object[]] @('$item_meadows_mod_health', $healthAxis)) | Out-Null
$withFoodIdentity.Invoke($meadowsVanillaHealthInfo, [object[]] @('$item_meadows_vanilla_health', $healthAxis)) | Out-Null
$withFoodIdentity.Invoke($meadowsEitrInfo, [object[]] @('$item_meadows_eitr', $eitrAxis)) | Out-Null
$withFoodIdentity.Invoke($blackForestHealthInfo, [object[]] @('$item_black_forest_health', $healthAxis)) | Out-Null
$withFoodIdentity.Invoke($reservedTierNameInfo, [object[]] @('$item_reserved_tier_food', $healthAxis)) | Out-Null
$referenceEntries = [Array]::CreateInstance($referenceEntryType, 8)
$referenceEntries.SetValue($referenceEntryConstructor.Invoke([object[]] @($modTierInfo, 'ExampleMod')), 0)
$referenceEntries.SetValue($referenceEntryConstructor.Invoke([object[]] @($vanillaTierInfo, 'Valheim')), 1)
$referenceEntries.SetValue($referenceEntryConstructor.Invoke([object[]] @($meadowsStaminaInfo, 'Valheim')), 2)
$referenceEntries.SetValue($referenceEntryConstructor.Invoke([object[]] @($meadowsModHealthInfo, 'ExampleMod')), 3)
$referenceEntries.SetValue($referenceEntryConstructor.Invoke([object[]] @($meadowsVanillaHealthInfo, 'Valheim')), 4)
$referenceEntries.SetValue($referenceEntryConstructor.Invoke([object[]] @($meadowsEitrInfo, 'Valheim')), 5)
$referenceEntries.SetValue($referenceEntryConstructor.Invoke([object[]] @($blackForestHealthInfo, 'Valheim')), 6)
$referenceEntries.SetValue($referenceEntryConstructor.Invoke([object[]] @($reservedTierNameInfo, 'ExampleMod')), 7)
$buildChefTierReference = Get-MethodRequired $chefTierReferenceType 'BuildReferenceContent'
$chefTierReference = [string]$buildChefTierReference.Invoke($null, [object[]] @(,$referenceEntries))
Assert-True ($chefTierReference.Contains('# Positive Eitr is eitrFood; otherwise Health <= Stamina is staminaFood, else healthFood.')) 'Chef tier reference must document the active food-axis classification policy.'
Assert-True ($chefTierReference.IndexOf('unassigned:', [StringComparison]::Ordinal) -lt $chefTierReference.IndexOf('Meadows:', [StringComparison]::Ordinal)) 'Unassigned Chef foods must precede resolved tiers.'
Assert-True ($chefTierReference.IndexOf('Meadows:', [StringComparison]::Ordinal) -lt $chefTierReference.IndexOf('BlackForest:', [StringComparison]::Ordinal)) 'Resolved Chef tiers must follow ResourceMap index order.'
Assert-True ($chefTierReference.Contains('"unassigned (tier 2)":')) 'A ResourceMap tier named unassigned must receive a unique diagnostic output key.'
Assert-True ($chefTierReference.Contains('- prefab: VanillaFallback')) 'Chef tier reference must list prefab fields.'
Assert-True ($chefTierReference.Contains('foodType: staminaFood')) 'Unassigned Chef foods must expose their H/S/E classification.'
Assert-True ($chefTierReference.Contains('reason: unmapped_sources')) 'Chef tier reference must list fallback reasons.'
Assert-True ($chefTierReference.Contains('sources: []')) 'Chef tier reference must preserve empty source lists.'
Assert-True ($chefTierReference.Contains('# counts: healthFood=2, staminaFood=1, eitrFood=1')) 'Resolved tier summaries must count H/S/E foods.'
$meadowsStart = $chefTierReference.IndexOf('Meadows:', [StringComparison]::Ordinal)
$blackForestStart = $chefTierReference.IndexOf('BlackForest:', [StringComparison]::Ordinal)
$meadowsReference = $chefTierReference.Substring($meadowsStart, $blackForestStart - $meadowsStart)
$healthSection = $meadowsReference.IndexOf('# --- healthFood ---', [StringComparison]::Ordinal)
$staminaSection = $meadowsReference.IndexOf('# --- staminaFood ---', [StringComparison]::Ordinal)
$eitrSection = $meadowsReference.IndexOf('# --- eitrFood ---', [StringComparison]::Ordinal)
Assert-True ($healthSection -ge 0 -and $healthSection -lt $staminaSection -and $staminaSection -lt $eitrSection) 'Foods inside each tier must be grouped Health, Stamina, then Eitr.'
Assert-True ($meadowsReference.Contains('- MeadowsVanillaHealth, healthFood')) 'Resolved Chef foods must use the compact prefab/category schema.'
Assert-True ($meadowsReference.IndexOf('# ----- Valheim -----', $healthSection, [StringComparison]::Ordinal) -lt $meadowsReference.IndexOf('# ----- ExampleMod -----', $healthSection, [StringComparison]::Ordinal)) 'Each food category must group Valheim before mod-owned prefabs.'
Assert-True (-not $chefTierReference.Contains('allTierFallback:')) 'Chef tier reference must not add an allTierFallback wrapper.'
Assert-True (-not $chefTierReference.Contains('owner:')) 'Chef tier reference ownership must remain comment-header-only.'
Assert-True ($null -eq $chefTierCatalogType.GetMethod('GetFallbackSnapshot', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'The full Chef catalog reference must not retain a fallback-only snapshot API.'
$chefTierReferenceSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\ChefTierReferenceGenerator.cs') -Raw
Assert-True ($chefTierReferenceSource.Contains('ChefFoodTierCatalog.GetSnapshot()')) 'Chef tier reference generation must reuse the complete catalog snapshot.'
Assert-True ((Get-Constant $chefTierReferenceType 'ReferenceFileName') -eq 'FoodTier.reference.yml') 'Chef tier reference filename is incorrect.'
Assert-True ($null -eq $chefTierReferenceType.GetMethod('TryWriteCurrentReference', [Reflection.BindingFlags] 'Static,Public,NonPublic')) 'Chef tier reference generation must not expose a removed manual-command path.'
Assert-True ($chefTierReferenceSource.Contains('FineDiningPlugin.IsRuntimeReferenceAuthority')) 'Automatic Chef-reference generation must retain its shared server-authority gate.'

$terminalCommandsType = Get-TypeRequired $assembly 'FineDining.DietTerminalCommands'
$terminalCommandsSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\TerminalCommands.cs') -Raw
Assert-True (([regex]::Matches($terminalCommandsSource, 'new Terminal\.ConsoleCommand\(')).Count -eq 3) 'FineDining must register exactly three console commands.'
foreach ($commandName in @('fd:rerollchef', 'fd:clearrecent', 'fd:printstate')) {
    Assert-True ([regex]::IsMatch($terminalCommandsSource, '(?s)"' + [regex]::Escape($commandName) + '".*?onlyAdmin:\s*true')) ("Command must remain admin-only: " + $commandName)
}
foreach ($removedCommand in @('"fd"', 'fd_rerollchef', 'fd_clearrecent', 'fd_printstate', 'fd_refer')) {
    Assert-True (-not $terminalCommandsSource.Contains($removedCommand)) ("Removed command must not remain: " + $removedCommand)
}
Assert-True ($terminalCommandsSource.Contains('ChefCollectionService.RerollAll(player);')) 'Chef Choice reroll implementation must remain available.'
Assert-True ($pluginSource.Contains('SpoilageReferenceGenerator.Tick();') -and $pluginSource.Contains('ChefTierReferenceGenerator.Tick();')) 'Reference generation must remain automatic on server authority.'

$encodeClock = Get-MethodRequired $spoilageClockType 'EncodeClockValue'
$decodeClock = Get-MethodRequired $spoilageClockType 'TryDecodeClockValue'
$composeClock = Get-MethodRequired $spoilageClockType 'ComposeClockValues'
$parseClock = Get-MethodRequired $spoilageClockType 'TryParseClockValue'
$addClockTicks = Get-MethodRequired $spoilageClockType 'AddTicksSaturating'
$shouldProcessInventoryClock = Get-MethodRequired $decayType 'ShouldProcessInventorySpoilageClock'
$composeStackClockValues = Get-MethodRequired $decayType 'ComposeStackClockValues'
$canMergeStackClockValues = Get-MethodRequired $decayType 'CanMergeStackClockValues'
$composeAssignedLifetimeValues = Get-MethodRequired $freshnessType 'ComposeAssignedLifetimeValues'
$canMergeAssignedLifetimeValues = Get-MethodRequired $freshnessType 'CanMergeAssignedLifetimeValues'
$isSpoiled = Get-MethodRequired $spoilageClockType 'IsSpoiled'
$markSpoiled = Get-MethodRequired $spoilageClockType 'MarkSpoiled'
$clearSpoiled = Get-MethodRequired $spoilageClockType 'ClearSpoiled'
$composeSpoiledValues = Get-MethodRequired $spoilageClockType 'ComposeSpoiledValues'
$canMergeSpoiledValues = Get-MethodRequired $spoilageClockType 'CanMergeSpoiledValues'
$completeKeepOriginalExpiry = Get-MethodRequired $decayType 'CompleteKeepOriginalExpiry'
$tryGetFreshnessRatio = Get-MethodRequired $freshnessType 'TryGetFreshnessRatio'
Assert-True ([bool]$canMergeSpoiledValues.Invoke($null, [object[]] @($null, $null))) 'Two unmarked stacks must be compatible for spoiled-state merging.'
Assert-True ([bool]$canMergeSpoiledValues.Invoke($null, [object[]] @($null, '1'))) 'A missing marker and canonical spoiled marker must be compatible.'
Assert-True ([bool]$canMergeSpoiledValues.Invoke($null, [object[]] @('1', '1'))) 'Two canonical spoiled markers must be compatible.'
Assert-True (-not [bool]$canMergeSpoiledValues.Invoke($null, [object[]] @('future:v2', '1'))) 'An unknown spoiled-marker format must fail closed.'
Assert-True ($null -eq $composeSpoiledValues.Invoke($null, [object[]] @($null, $null))) 'Two unmarked stacks must remain unmarked.'
Assert-True ([string]$composeSpoiledValues.Invoke($null, [object[]] @($null, '1')) -eq '1') 'A spoiled source must mark an unmarked destination.'
Assert-True ([string]$composeSpoiledValues.Invoke($null, [object[]] @('1', $null)) -eq '1') 'A spoiled destination must remain spoiled when receiving an unmarked source.'
Assert-True ([string]$composeSpoiledValues.Invoke($null, [object[]] @('1', '1')) -eq '1') 'Spoiled-state composition must be idempotent.'
Assert-True ([string]$composeSpoiledValues.Invoke($null, [object[]] @('future:v2', '1')) -eq 'future:v2') 'Fail-closed spoiled-state composition must preserve an unknown destination value.'
$itemCustomDataField = $itemDataType.GetField('m_customData', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
$itemStackField = $itemDataType.GetField('m_stack', [Reflection.BindingFlags] 'Instance,Public,NonPublic')
Assert-True ($null -ne $itemCustomDataField -and $null -ne $itemStackField) 'ItemData fields required for keep-original marker validation are missing.'
$keepExpiredItem = [Activator]::CreateInstance($itemDataType)
$keepExpiredCustomData = $itemCustomDataField.GetValue($keepExpiredItem)
if ($null -eq $keepExpiredCustomData)
{
    $keepExpiredCustomData = [Activator]::CreateInstance($itemCustomDataField.FieldType)
    $itemCustomDataField.SetValue($keepExpiredItem, $keepExpiredCustomData)
}
$expiryDataKey = [string](Get-Constant $spoilageClockType 'ExpiryDataKey')
$spoiledDataKey = [string](Get-Constant $spoilageClockType 'SpoiledDataKey')
$assignedLifetimeDataKey = [string](Get-Constant $freshnessType 'AssignedLifetimeDataKey')
$placedAnchorDataKey = [string](Get-Constant $decayType 'PlacedAnchorDataKey')
$keepExpiredCustomData[$expiryDataKey] = '123'
$keepExpiredCustomData[$assignedLifetimeDataKey] = '456'
$keepExpiredCustomData[$placedAnchorDataKey] = '789'
$keepExpiredCustomData['third.party.keep'] = 'preserve'
$itemStackField.SetValue($keepExpiredItem, 7)
Assert-True (-not [bool]$isSpoiled.Invoke($null, [object[]] @($keepExpiredItem))) 'An item without the canonical marker must begin unspoiled.'
Assert-True ([bool]$completeKeepOriginalExpiry.Invoke($null, [object[]] @($keepExpiredItem))) 'Keep-original expiry must persist a completed spoiled state.'
Assert-True ([bool]$isSpoiled.Invoke($null, [object[]] @($keepExpiredItem)) -and
             [string]$keepExpiredCustomData[$spoiledDataKey] -eq '1') 'Keep-original expiry must write the canonical permanent marker.'
Assert-True (-not $keepExpiredCustomData.ContainsKey($expiryDataKey) -and
             -not $keepExpiredCustomData.ContainsKey($assignedLifetimeDataKey) -and
             -not $keepExpiredCustomData.ContainsKey($placedAnchorDataKey)) 'Completed keep-original expiry must remove the active clock, assigned lifetime, and placement anchor.'
Assert-True ([string]$keepExpiredCustomData['third.party.keep'] -eq 'preserve') 'Keep-original expiry must preserve unrelated item custom data.'
Assert-True ([int]$itemStackField.GetValue($keepExpiredItem) -eq 7) 'Keep-original expiry must preserve the original stack amount.'
Assert-True (-not [bool]$completeKeepOriginalExpiry.Invoke($null, [object[]] @($keepExpiredItem))) 'Completing an already spoiled keep-original item must be idempotent.'
$spoiledFreshnessArguments = [object[]] @($keepExpiredItem, [single]1)
Assert-True ([bool]$tryGetFreshnessRatio.Invoke($null, $spoiledFreshnessArguments) -and
             [single]$spoiledFreshnessArguments[1] -eq [single]0) 'A permanently marked edible item must expose zero freshness without an active clock.'
Assert-True ([bool]$clearSpoiled.Invoke($null, [object[]] @($keepExpiredItem))) 'The internal replacement cleanup seam must clear an existing spoiled marker.'
Assert-True (-not [bool]$isSpoiled.Invoke($null, [object[]] @($keepExpiredItem))) 'Clearing the marker must return the item to an unmarked metadata state.'
Assert-True ([bool]$markSpoiled.Invoke($null, [object[]] @($keepExpiredItem)) -and
             -not [bool]$markSpoiled.Invoke($null, [object[]] @($keepExpiredItem))) 'Writing the canonical spoiled marker must be idempotent.'
foreach ($removedClockMethod in @('EncodeClockValue', 'TryDecodeClockValue', 'ComposeClockValues'))
{
    Assert-True ($null -eq $decayType.GetMethod($removedClockMethod, [Reflection.BindingFlags] 'Static,Public,NonPublic')) "DecayRuntime must not retain a forwarding clock method: $removedClockMethod"
}
$prepareItemForAddStart = $decayRuntimeSource.IndexOf('internal static bool PrepareItemForAdd(', [StringComparison]::Ordinal)
$prepareInheritedItemStart = $decayRuntimeSource.IndexOf('internal static bool PrepareInheritedItemForAdd(', $prepareItemForAddStart, [StringComparison]::Ordinal)
Assert-True ($prepareItemForAddStart -ge 0 -and $prepareInheritedItemStart -gt $prepareItemForAddStart) 'PrepareItemForAdd source boundaries were not found.'
$prepareItemForAddSource = $decayRuntimeSource.Substring($prepareItemForAddStart, $prepareInheritedItemStart - $prepareItemForAddStart)
$prepareAddClockGate = $prepareItemForAddSource.IndexOf('ShouldProcessInventorySpoilageClock(', [StringComparison]::Ordinal)
$prepareAddEnsureState = $prepareItemForAddSource.IndexOf('EnsureItemState(', [StringComparison]::Ordinal)
Assert-True ($prepareAddClockGate -ge 0 -and $prepareAddClockGate -lt $prepareAddEnsureState) 'Inventory.AddItem must apply the player-or-existing-clock gate before creating spoilage or freshness metadata.'
$reconcileStart = $decayRuntimeSource.IndexOf('private static void Reconcile(', [StringComparison]::Ordinal)
$ensureItemStateStart = $decayRuntimeSource.IndexOf('private static bool EnsureItemState(', $reconcileStart, [StringComparison]::Ordinal)
Assert-True ($reconcileStart -ge 0 -and $ensureItemStateStart -gt $reconcileStart) 'Inventory reconciliation source boundaries were not found.'
$reconcileSource = $decayRuntimeSource.Substring($reconcileStart, $ensureItemStateStart - $reconcileStart)
$reconcileClockGate = $reconcileSource.IndexOf('ShouldProcessInventorySpoilageClock(', [StringComparison]::Ordinal)
$reconcileEnsureState = $reconcileSource.IndexOf('EnsureItemState(', [StringComparison]::Ordinal)
Assert-True ($reconcileClockGate -ge 0 -and $reconcileClockGate -lt $reconcileEnsureState) 'Inventory reconciliation must skip timerless containers before creating spoilage or freshness metadata.'
$decayPatchesSource = Get-Content -LiteralPath (Join-Path $projectRoot 'DecayPatches.cs') -Raw
$mergeTrackerStart = $decayPatchesSource.IndexOf('internal static class InventoryAddMergeTracker', [StringComparison]::Ordinal)
$mergeTrackerEnd = $decayPatchesSource.IndexOf('[HarmonyPatch(typeof(Inventory)', $mergeTrackerStart, [StringComparison]::Ordinal)
Assert-True ($mergeTrackerStart -ge 0 -and $mergeTrackerEnd -gt $mergeTrackerStart) 'InventoryAddMergeTracker source boundaries were not found.'
$mergeTrackerSource = $decayPatchesSource.Substring($mergeTrackerStart, $mergeTrackerEnd - $mergeTrackerStart)
$mergePrefixStart = $mergeTrackerSource.IndexOf('internal static InventoryAddMergeState? Prefix(', [StringComparison]::Ordinal)
$mergePostfixStart = $mergeTrackerSource.IndexOf('internal static void Postfix(', $mergePrefixStart, [StringComparison]::Ordinal)
$captureMetadataStart = $mergeTrackerSource.IndexOf('private static void CaptureOriginalSourceMetadata(', $mergePostfixStart, [StringComparison]::Ordinal)
$restoreMetadataStart = $mergeTrackerSource.IndexOf('private static void RestoreTemporarySourceMetadata(', $captureMetadataStart, [StringComparison]::Ordinal)
$restoreKeyStart = $mergeTrackerSource.IndexOf('private static void RestoreKey(', $restoreMetadataStart, [StringComparison]::Ordinal)
Assert-True ($mergePrefixStart -ge 0 -and $mergePostfixStart -gt $mergePrefixStart -and $captureMetadataStart -gt $mergePostfixStart -and $restoreMetadataStart -gt $captureMetadataStart -and $restoreKeyStart -gt $restoreMetadataStart) 'InventoryAddMergeTracker method boundaries were not found.'
$mergePrefixSource = $mergeTrackerSource.Substring($mergePrefixStart, $mergePostfixStart - $mergePrefixStart)
$captureBeforeActivation = $mergePrefixSource.IndexOf('CaptureOriginalSourceMetadata(source, state);', [StringComparison]::Ordinal)
$temporaryActivation = $mergePrefixSource.IndexOf('DecayRuntime.PrepareItemForAdd(inventory, source);', [StringComparison]::Ordinal)
Assert-True ($captureBeforeActivation -ge 0 -and $captureBeforeActivation -lt $temporaryActivation) 'Inventory.AddItem must snapshot timerless source metadata before temporary player-inventory activation.'
$mergePostfixSource = $mergeTrackerSource.Substring($mergePostfixStart, $captureMetadataStart - $mergePostfixStart)
Assert-True ($mergePostfixSource.Contains('RestoreTemporarySourceMetadataSafe(inventory, source, state);')) 'Inventory.AddItem postfix must safely restore temporary activation metadata after a failed or partial move.'
$markerOnlySourceGate = [regex]::Match(
    $mergePrefixSource,
    '(?s)!DecayRuntime\.TryGetExpiryTicks\(source,\s*out state\.SourceClockValue\)\s*&&\s*!state\.SourceSpoiled')
Assert-True $markerOnlySourceGate.Success 'Inventory.AddItem merge tracking must retain a marker-only source even after its active clock was removed.'
$restoreMetadataSource = $mergeTrackerSource.Substring($restoreMetadataStart, $restoreKeyStart - $restoreMetadataStart)
Assert-True ($restoreMetadataSource.Contains('state.SourceHadValidClock || inventory.m_inventory.Contains(source)')) 'Temporary source metadata restoration must preserve pre-existing clocks and a source reference actually owned by the destination inventory.'
$addItemFinalizerCalls = [regex]::Matches(
    $decayPatchesSource,
    'InventoryAddMergeTracker\.RestoreTemporarySourceMetadataSafe\(__instance, item, __state\);').Count
Assert-True ($addItemFinalizerCalls -eq 3) 'Every patched Inventory.AddItem overload must restore temporary source metadata from a Harmony finalizer.'
$composeInventoryStart = $decayRuntimeSource.IndexOf('internal static bool ComposeInventoryStackMetadata(', [StringComparison]::Ordinal)
$composeDirectStart = $decayRuntimeSource.IndexOf('internal static bool ComposeDirectRecoveryMergeExpiry(', $composeInventoryStart, [StringComparison]::Ordinal)
$composeGroundStart = $decayRuntimeSource.IndexOf('internal static void ComposeGroundStackExpiry(', $composeDirectStart, [StringComparison]::Ordinal)
$registerGroundStart = $decayRuntimeSource.IndexOf('internal static void RegisterGroundDrop(', $composeGroundStart, [StringComparison]::Ordinal)
Assert-True ($composeInventoryStart -ge 0 -and $composeDirectStart -gt $composeInventoryStart -and $composeGroundStart -gt $composeDirectStart -and $registerGroundStart -gt $composeGroundStart) 'Spoiled-stack composition method boundaries were not found.'
$composeInventorySource = $decayRuntimeSource.Substring($composeInventoryStart, $composeDirectStart - $composeInventoryStart)
$composeInventoryMarkerGate = $composeInventorySource.IndexOf('sourceSpoiled || SpoilageClock.IsSpoiled(target)', [StringComparison]::Ordinal)
$composeInventoryClockGate = $composeInventorySource.IndexOf('!SpoilageClock.IsValidClockValue(sourceClockValue)', [StringComparison]::Ordinal)
Assert-True ($composeInventoryMarkerGate -ge 0 -and $composeInventoryMarkerGate -lt $composeInventoryClockGate) 'Inventory composition must promote a marker-only stack before rejecting its intentionally absent clock.'
$composeDirectSource = $decayRuntimeSource.Substring($composeDirectStart, $composeGroundStart - $composeDirectStart)
Assert-True ($composeDirectSource.IndexOf('sourceSpoiled || targetSpoiled', [StringComparison]::Ordinal) -lt
             $composeDirectSource.IndexOf('!TryGetExpiryTicks(source, out long sourceClockValue)', [StringComparison]::Ordinal)) 'AzuEPI direct recovery composition must promote spoiled state before requiring a source clock.'
$composeGroundSource = $decayRuntimeSource.Substring($composeGroundStart, $registerGroundStart - $composeGroundStart)
Assert-True ($composeGroundSource.IndexOf('SpoilageClock.IsSpoiled(destination.m_itemData)', [StringComparison]::Ordinal) -lt
             $composeGroundSource.IndexOf('!TryGetExpiryTicks(source.m_itemData, out long sourceClockValue)', [StringComparison]::Ordinal)) 'Ground AutoStack composition must promote spoiled state before requiring a source clock.'
$placedSpoilageSource = Get-Content -LiteralPath (Join-Path $projectRoot 'PlacedItemSpoilage.cs') -Raw
$captureConsumedStart = $placedSpoilageSource.IndexOf('private static long CaptureConsumedRemaining(', [StringComparison]::Ordinal)
$noCostPlacementStart = $placedSpoilageSource.IndexOf('private static bool IsNoCostPlacement(', $captureConsumedStart, [StringComparison]::Ordinal)
$pieceRecoveryBeginStart = $placedSpoilageSource.IndexOf('internal static PieceRecoverySpoilageState Begin(', $noCostPlacementStart, [StringComparison]::Ordinal)
$pieceRecoveryEndStart = $placedSpoilageSource.IndexOf('internal static void End(', $pieceRecoveryBeginStart, [StringComparison]::Ordinal)
Assert-True ($captureConsumedStart -ge 0 -and $noCostPlacementStart -gt $captureConsumedStart -and $pieceRecoveryBeginStart -gt $noCostPlacementStart -and $pieceRecoveryEndStart -gt $pieceRecoveryBeginStart) 'Placed-food inheritance source boundaries were not found.'
$captureConsumedSource = $placedSpoilageSource.Substring($captureConsumedStart, $noCostPlacementStart - $captureConsumedStart)
Assert-True ($captureConsumedSource.Contains('if (SpoilageClock.IsSpoiled(item))') -and
             $captureConsumedSource.Contains('earliestRemainingTicks = 0L;')) 'Placing a permanently spoiled ingredient must inherit an already-expired deadline.'
$pieceRecoveryBeginSource = $placedSpoilageSource.Substring($pieceRecoveryBeginStart, $pieceRecoveryEndStart - $pieceRecoveryBeginStart)
Assert-True ($pieceRecoveryBeginSource.Contains('if (SpoilageClock.IsSpoiled(placedDrop.m_itemData))') -and
             $pieceRecoveryBeginSource.Contains('state.RemainingTicks = 0L;')) 'Recovering a permanently spoiled placed item must retain its already-expired state.'
Assert-True ([bool]$shouldProcessInventoryClock.Invoke($null, [object[]] @($true, $false))) 'Timerless food must activate when it first enters a player inventory.'
Assert-True ([bool]$shouldProcessInventoryClock.Invoke($null, [object[]] @($true, $true))) 'An existing player-inventory clock must continue to be processed.'
Assert-True (-not [bool]$shouldProcessInventoryClock.Invoke($null, [object[]] @($false, $false))) 'Timerless food in a location or ordinary container must remain unactivated.'
Assert-True ([bool]$shouldProcessInventoryClock.Invoke($null, [object[]] @($false, $true))) 'An already activated clock must continue to be processed in an ordinary container.'
$nowTicks = [DateTime]::UtcNow.Ticks
$fiveSeconds = [TimeSpan]::TicksPerSecond * 5L
$tenSeconds = [TimeSpan]::TicksPerSecond * 10L
$twentySeconds = [TimeSpan]::TicksPerSecond * 20L
$pausedClock = [long] $encodeClock.Invoke($null, [object[]] @($nowTicks, $tenSeconds, $true))
$runningClock = [long] $encodeClock.Invoke($null, [object[]] @($nowTicks, $twentySeconds, $false))
$decodeArguments = [object[]] @($pausedClock, $nowTicks, [long]0, $false)
Assert-True ([bool]$decodeClock.Invoke($null, $decodeArguments)) 'Paused spoilage clock must decode.'
Assert-True ([bool]$decodeArguments[3]) 'Paused spoilage clock must retain its paused state.'
Assert-True ([long]$decodeArguments[2] -eq $tenSeconds) 'Paused spoilage clock must retain its remaining duration.'
$continuedContainerClockArguments = [object[]] @($runningClock, ($nowTicks + $fiveSeconds), [long]0, $false)
Assert-True ([bool]$decodeClock.Invoke($null, $continuedContainerClockArguments)) 'An activated running clock must remain decodable after moving into a container.'
Assert-True (-not [bool]$continuedContainerClockArguments[3] -and [long]$continuedContainerClockArguments[2] -eq ($twentySeconds - $fiveSeconds)) 'An ordinary container must not pause or reset an activated running clock.'
$composedClock = [long] $composeClock.Invoke($null, [object[]] @($runningClock, $pausedClock, $nowTicks, $false))
$composedArguments = [object[]] @($composedClock, $nowTicks, [long]0, $false)
Assert-True ([bool]$decodeClock.Invoke($null, $composedArguments)) 'Composed spoilage clock must decode.'
Assert-True ([long]$composedArguments[2] -eq $tenSeconds) 'Stack composition must keep the lower remaining spoilage time.'
$pausedComposedClock = [long]$composeClock.Invoke($null, [object[]] @($runningClock, $pausedClock, $nowTicks, $true))
Assert-True ($pausedComposedClock -eq -$tenSeconds) 'A paused destination stack must keep the lower duration in paused form.'
$pausedTenText = (-$tenSeconds).ToString([Globalization.CultureInfo]::InvariantCulture)
$pausedTwentyText = (-$twentySeconds).ToString([Globalization.CultureInfo]::InvariantCulture)
$runningTwentyText = $runningClock.ToString([Globalization.CultureInfo]::InvariantCulture)
Assert-True ($null -eq $composeStackClockValues.Invoke($null, [object[]] @($null, $null))) 'Merging two timerless stacks must keep the result unactivated.'
Assert-True ([string]$composeStackClockValues.Invoke($null, [object[]] @($pausedTwentyText, $null)) -eq $pausedTwentyText) 'A timerless source must not erase an activated destination clock.'
Assert-True ([string]$composeStackClockValues.Invoke($null, [object[]] @($null, $pausedTenText)) -eq $pausedTenText) 'A timed source must activate a timerless merge destination with its existing clock.'
Assert-True ([string]$composeStackClockValues.Invoke($null, [object[]] @($pausedTwentyText, $pausedTenText)) -eq $pausedTenText) 'A timed merge must retain the lower remaining lifetime.'
Assert-True ([bool]$canMergeStackClockValues.Invoke($null, [object[]] @($null, $null))) 'Missing spoilage clocks must remain stack-compatible.'
Assert-True ([bool]$canMergeStackClockValues.Invoke($null, [object[]] @($null, $pausedTenText))) 'A missing and a canonical spoilage clock must remain stack-compatible.'
Assert-True ([bool]$canMergeStackClockValues.Invoke($null, [object[]] @($pausedTwentyText, $pausedTenText))) 'Two canonical paused clocks must remain stack-compatible.'
Assert-True ([bool]$canMergeStackClockValues.Invoke($null, [object[]] @($runningTwentyText, $runningTwentyText))) 'Two canonical running clocks must remain stack-compatible without reading world time.'
Assert-True (-not [bool]$canMergeStackClockValues.Invoke($null, [object[]] @('future:v2', $pausedTenText))) 'A future spoilage-clock format must fail closed.'
Assert-True (-not [bool]$canMergeStackClockValues.Invoke($null, [object[]] @('00123', $pausedTenText))) 'A non-canonical spoilage clock must fail closed.'
Assert-True (-not [bool]$canMergeStackClockValues.Invoke($null, [object[]] @($runningTwentyText, $pausedTenText))) 'Running and paused clocks must fail closed when no authoritative world clock exists.'
$lifetimeTenText = $tenSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
$lifetimeTwentyText = $twentySeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
Assert-True ([bool]$canMergeAssignedLifetimeValues.Invoke($null, [object[]] @($null, $null))) 'Missing assigned lifetimes must remain stack-compatible.'
Assert-True ([bool]$canMergeAssignedLifetimeValues.Invoke($null, [object[]] @($null, $lifetimeTenText))) 'A missing and a canonical assigned lifetime must remain stack-compatible.'
Assert-True ([bool]$canMergeAssignedLifetimeValues.Invoke($null, [object[]] @($lifetimeTenText, $lifetimeTwentyText))) 'Canonical assigned lifetimes must remain stack-compatible.'
Assert-True (-not [bool]$canMergeAssignedLifetimeValues.Invoke($null, [object[]] @('future:v2', $lifetimeTenText))) 'A future assigned-lifetime format must fail closed.'
Assert-True (-not [bool]$canMergeAssignedLifetimeValues.Invoke($null, [object[]] @('00123', $lifetimeTenText))) 'A non-canonical assigned lifetime must fail closed.'
Assert-True ([string]$composeAssignedLifetimeValues.Invoke($null, [object[]] @($lifetimeTenText, $lifetimeTwentyText)) -eq $lifetimeTwentyText) 'Assigned-lifetime composition must retain the larger freshness basis.'
$canonicalClockArguments = [object[]] @('123', [long]0)
Assert-True ([bool]$parseClock.Invoke($null, $canonicalClockArguments) -and [long]$canonicalClockArguments[1] -eq 123) 'A canonical persisted clock must parse.'
foreach ($invalidClockText in @('', '0', '00123', [long]::MinValue.ToString([Globalization.CultureInfo]::InvariantCulture)))
{
    $invalidClockArguments = [object[]] @($invalidClockText, [long]0)
    Assert-True (-not [bool]$parseClock.Invoke($null, $invalidClockArguments)) "Malformed persisted clock must be rejected: '$invalidClockText'"
}
$nearMaximumClock = [long]::MaxValue - [long]5
Assert-True ([long]$addClockTicks.Invoke($null, [object[]] @($nearMaximumClock, [long]10)) -eq [long]::MaxValue) 'Spoilage clock addition must saturate instead of overflowing.'

foreach ($fieldName in @('BatchTokenKey', 'AccumulatedBonusTicksKey', 'LastCheckpointTicksKey', 'BonusRateKey'))
{
    $value = [string](Get-Constant $environmentType $fieldName)
    Assert-True ($value.StartsWith('FineDining_FermenterEnv_', [StringComparison]::Ordinal)) "Fermenter state key is not FineDining-owned: $fieldName"
}
$fermenterBonusSystemType = Get-TypeRequired $assembly 'FineDining.FermenterCookingBonusSystem'
Assert-True ([Math]::Abs([float](Get-Constant $fermenterBonusSystemType 'CookingExperienceOnAdd') - 0.4) -lt 0.0001) 'Fermenter insertion experience must remain 0.4 when the prefab is not excluded.'
Assert-True ([Math]::Abs([float](Get-Constant $fermenterBonusSystemType 'CookingExperienceOnCollect') - 0.6) -lt 0.0001) 'Fermenter collection experience must remain 0.6 when the prefab is not excluded.'
Assert-True ([Math]::Abs(
    [float](Get-Constant $fermenterBonusSystemType 'CookingExperienceOnAdd') +
    [float](Get-Constant $fermenterBonusSystemType 'CookingExperienceOnCollect') - 1.0) -lt 0.0001) 'A normal Fermenter batch must retain one total Cooking experience across insertion and collection.'
$fermenterCookingBonusSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Diet\Patches\FermenterCookingBonusPatches.cs') -Raw
$fermenterRequestTapStart = $fermenterCookingBonusSource.IndexOf('internal static void RequestTap(', [StringComparison]::Ordinal)
$fermenterTakeOwnerStart = $fermenterCookingBonusSource.IndexOf('internal static OwnerTapContext? TakeOwnerTapContext(', $fermenterRequestTapStart, [StringComparison]::Ordinal)
$fermenterCompleteTapStart = $fermenterCookingBonusSource.IndexOf('internal static void CompleteDelayedTap(', $fermenterTakeOwnerStart, [StringComparison]::Ordinal)
$fermenterSendResponseStart = $fermenterCookingBonusSource.IndexOf('private static void SendTapResponse(', $fermenterCompleteTapStart, [StringComparison]::Ordinal)
$fermenterHandleRequestStart = $fermenterCookingBonusSource.IndexOf('private static void HandleTapRequest(', $fermenterSendResponseStart, [StringComparison]::Ordinal)
$fermenterHandleExcludedStart = $fermenterCookingBonusSource.IndexOf('private static void HandleExcludedTapRequest(', $fermenterHandleRequestStart, [StringComparison]::Ordinal)
$fermenterRejectOwnerStart = $fermenterCookingBonusSource.IndexOf('private static void RejectOwnerTap(', $fermenterHandleExcludedStart, [StringComparison]::Ordinal)
$fermenterHandleCompletedStart = $fermenterCookingBonusSource.IndexOf('private static void HandleTapCompleted(', $fermenterRejectOwnerStart, [StringComparison]::Ordinal)
$fermenterFindRequesterStart = $fermenterCookingBonusSource.IndexOf('private static Player? FindRequestingPlayer(', $fermenterHandleCompletedStart, [StringComparison]::Ordinal)
$fermenterAddExperiencePatchStart = $fermenterCookingBonusSource.IndexOf('internal static class FermenterCookingExperienceAddItemPatch', [StringComparison]::Ordinal)
$fermenterRpcPatchStart = $fermenterCookingBonusSource.IndexOf('internal static class FermenterCookingBonusRpcPatch', $fermenterAddExperiencePatchStart, [StringComparison]::Ordinal)
Assert-True ($fermenterRequestTapStart -ge 0 -and $fermenterTakeOwnerStart -gt $fermenterRequestTapStart -and
             $fermenterCompleteTapStart -gt $fermenterTakeOwnerStart -and $fermenterSendResponseStart -gt $fermenterCompleteTapStart -and
             $fermenterHandleRequestStart -gt $fermenterSendResponseStart -and $fermenterHandleExcludedStart -gt $fermenterHandleRequestStart -and
             $fermenterRejectOwnerStart -gt $fermenterHandleExcludedStart -and
             $fermenterHandleCompletedStart -gt $fermenterRejectOwnerStart -and $fermenterFindRequesterStart -gt $fermenterHandleCompletedStart -and
             $fermenterAddExperiencePatchStart -ge 0 -and $fermenterRpcPatchStart -gt $fermenterAddExperiencePatchStart) 'Fermenter Cooking exclusion source boundaries were not found.'
$fermenterRequestTapSource = $fermenterCookingBonusSource.Substring($fermenterRequestTapStart, $fermenterTakeOwnerStart - $fermenterRequestTapStart)
$fermenterTakeOwnerSource = $fermenterCookingBonusSource.Substring($fermenterTakeOwnerStart, $fermenterCompleteTapStart - $fermenterTakeOwnerStart)
$fermenterCompleteTapSource = $fermenterCookingBonusSource.Substring($fermenterCompleteTapStart, $fermenterSendResponseStart - $fermenterCompleteTapStart)
$fermenterHandleRequestSource = $fermenterCookingBonusSource.Substring($fermenterHandleRequestStart, $fermenterHandleExcludedStart - $fermenterHandleRequestStart)
$fermenterHandleExcludedSource = $fermenterCookingBonusSource.Substring($fermenterHandleExcludedStart, $fermenterRejectOwnerStart - $fermenterHandleExcludedStart)
$fermenterHandleCompletedSource = $fermenterCookingBonusSource.Substring($fermenterHandleCompletedStart, $fermenterFindRequesterStart - $fermenterHandleCompletedStart)
$fermenterAddExperiencePatchSource = $fermenterCookingBonusSource.Substring($fermenterAddExperiencePatchStart, $fermenterRpcPatchStart - $fermenterAddExperiencePatchStart)
$clientExcludedGuardStart = $fermenterRequestTapSource.IndexOf('if (StationModule.IsFermenterBonusExcluded(fermenter))', [StringComparison]::Ordinal)
$clientExcludedGuardEnd = $fermenterRequestTapSource.IndexOf('ZDO zdo =', $clientExcludedGuardStart, [StringComparison]::Ordinal)
Assert-True ($clientExcludedGuardStart -ge 0 -and $clientExcludedGuardEnd -gt $clientExcludedGuardStart) 'The client Fermenter exclusion fallback branch was not found.'
$clientExcludedGuardSource = $fermenterRequestTapSource.Substring($clientExcludedGuardStart, $clientExcludedGuardEnd - $clientExcludedGuardStart)
Assert-True ($clientExcludedGuardSource.Contains('ClientRequests.Remove(fermenter);') -and
             $clientExcludedGuardSource.Contains('nview.InvokeRPC(vanillaRpcName, vanillaParameters);') -and
             $clientExcludedGuardSource.Contains('return;')) 'An excluded Fermenter client must discard custom request state and invoke the original vanilla tap RPC.'
Assert-True (-not $clientExcludedGuardSource.Contains('GetSkillFactor(') -and
             -not $clientExcludedGuardSource.Contains('RequestTapRpc')) 'An excluded Fermenter client must not read Cooking level or enter the bonus request RPC.'
$ownerValidationIndex = $fermenterHandleRequestSource.IndexOf('Player? requester = FindRequestingPlayer(', [StringComparison]::Ordinal)
$ownerExcludedGuardIndex = $fermenterHandleRequestSource.IndexOf('StationModule.IsFermenterBonusExcluded(fermenter)', [StringComparison]::Ordinal)
$ownerBonusRollIndex = $fermenterHandleRequestSource.IndexOf('CookingProductionBonusSystem.RollConfiguredBonusItems(', [StringComparison]::Ordinal)
$ownerContextAddIndex = $fermenterHandleRequestSource.IndexOf('OwnerTapContexts.Add(', [StringComparison]::Ordinal)
Assert-True ($ownerValidationIndex -ge 0 -and
             $ownerExcludedGuardIndex -gt $ownerValidationIndex -and
             $ownerBonusRollIndex -gt $ownerExcludedGuardIndex -and
             $ownerContextAddIndex -gt $ownerExcludedGuardIndex -and
             $fermenterHandleRequestSource.Contains('HandleExcludedTapRequest(')) 'The authoritative owner must validate an excluded tap, then bypass Cooking rolls and owner bonus context.'
Assert-True ($fermenterHandleExcludedSource.Contains('RpcTapMethod!.Invoke(fermenter, new object[] { sender });') -and
             $fermenterHandleExcludedSource.Contains('RejectTapRequest(fermenter, sender, requestId, batchTicks);') -and
             -not $fermenterHandleExcludedSource.Contains('RollConfiguredBonusItems(') -and
             -not $fermenterHandleExcludedSource.Contains('OwnerTapContexts.Add(')) 'A validated excluded request must run vanilla RPC_Tap and terminate the custom request without bonus or collection XP.'
Assert-True ($fermenterTakeOwnerSource.Contains('StationModule.IsFermenterBonusExcluded(fermenter)') -and
             $fermenterTakeOwnerSource.Contains('RejectTapRequest(') -and
             $fermenterTakeOwnerSource.Contains('return null;')) 'A config change before DelayedTap must discard an excluded owner bonus context.'
Assert-True ($fermenterCompleteTapSource.Contains('StationModule.IsFermenterBonusExcluded(fermenter)') -and
             $fermenterCompleteTapSource.IndexOf('StationModule.IsFermenterBonusExcluded(fermenter)', [StringComparison]::Ordinal) -lt $fermenterCompleteTapSource.IndexOf('ItemDrop.OnCreateNew(', [StringComparison]::Ordinal) -and
             $fermenterCompleteTapSource.Contains('RejectTapRequest(')) 'A config change during DelayedTap must prevent excluded bonus output and collection completion.'
Assert-True ($fermenterHandleCompletedSource.Contains('StationModule.IsFermenterBonusExcluded(fermenter)') -and
             $fermenterHandleCompletedSource.IndexOf('StationModule.IsFermenterBonusExcluded(fermenter)', [StringComparison]::Ordinal) -lt $fermenterHandleCompletedSource.IndexOf('player.RaiseSkill(', [StringComparison]::Ordinal)) 'Excluded Fermenters must block the 0.6 collection Cooking experience before RaiseSkill.'
Assert-True ($fermenterAddExperiencePatchSource.Contains('Fermenter __instance') -and
             $fermenterAddExperiencePatchSource.Contains('StationModule.IsFermenterBonusExcluded(__instance)') -and
             $fermenterAddExperiencePatchSource.IndexOf('StationModule.IsFermenterBonusExcluded(__instance)', [StringComparison]::Ordinal) -lt $fermenterAddExperiencePatchSource.IndexOf('player.RaiseSkill(', [StringComparison]::Ordinal)) 'Excluded Fermenters must block the 0.4 insertion Cooking experience before RaiseSkill.'
foreach ($fermenterPatchContract in @(
    @('FineDining.FermenterCookingExperienceAddItemPatch', 'AddItem', 'HarmonyPostfix'),
    @('FineDining.FermenterCookingBonusRpcPatch', 'Awake', 'HarmonyPostfix'),
    @('FineDining.FermenterCookingBonusInteractPatch', 'Interact', 'HarmonyTranspiler'),
    @('FineDining.FermenterCookingBonusOutputPatch', 'DelayedTap', 'HarmonyPrefix'),
    @('FineDining.FermenterCookingBonusOutputPatch', 'DelayedTap', 'HarmonyPostfix')))
{
    Assert-HarmonyPatchTarget $assembly $fermenterPatchContract[0] 'Fermenter' $fermenterPatchContract[1]
    Assert-HarmonyPatchMethodAttribute $assembly $fermenterPatchContract[0] $fermenterPatchContract[2]
}
Assert-HarmonyPatchMethodAttribute $assembly 'FineDining.FermenterCookingBonusOutputPatch' 'HarmonyFinalizer'
$resetFermenterBonusRuntime = Get-MethodRequired $fermenterBonusSystemType 'ResetRuntime'
try
{
    $resetFermenterBonusRuntime.Invoke($null, [object[]] @()) | Out-Null
}
catch
{
    throw "Fermenter bonus runtime must initialize fail-soft against the current game API. $($_.Exception)"
}
Assert-True ([regex]::Matches(
    $dietModuleSource,
    'FermenterCookingBonusSystem\.ResetRuntime\(\);').Count -eq 2) 'Fermenter bonus runtime must reset on both Diet initialization and shutdown.'

$resources = @($assembly.GetManifestResourceNames())
foreach ($resource in @(
    'FineDining.Resources.Defaults.Spoilage.yml',
    'FineDining.Resources.Defaults.ResourceMap.yml',
    'FineDining.Resources.UI.FullCourseIcon.png',
    'FineDining.translations.English.yml',
    'FineDining.translations.Korean.yml'))
{
    Assert-True ($resources -contains $resource) "Embedded resource is missing: $resource"
}

$fullCourseIconResource = $assembly.GetManifestResourceStream('FineDining.Resources.UI.FullCourseIcon.png')
Assert-True ($null -ne $fullCourseIconResource) 'The embedded Full Course tableware icon stream is missing.'
try
{
    $fullCourseIconBuffer = New-Object IO.MemoryStream
    $fullCourseIconResource.CopyTo($fullCourseIconBuffer)
    [byte[]] $fullCourseIconBytes = $fullCourseIconBuffer.ToArray()
}
finally
{
    if ($null -ne $fullCourseIconBuffer)
    {
        $fullCourseIconBuffer.Dispose()
    }

    $fullCourseIconResource.Dispose()
}
Assert-True ($fullCourseIconBytes.Length -gt 33) 'The embedded Full Course icon must contain a complete PNG header.'
Assert-True ($fullCourseIconBytes[0] -eq 0x89 -and
             $fullCourseIconBytes[1] -eq 0x50 -and
             $fullCourseIconBytes[2] -eq 0x4E -and
             $fullCourseIconBytes[3] -eq 0x47) 'The embedded Full Course icon must remain a PNG.'
$fullCourseIconWidth = ([int]$fullCourseIconBytes[16] -shl 24) -bor
                       ([int]$fullCourseIconBytes[17] -shl 16) -bor
                       ([int]$fullCourseIconBytes[18] -shl 8) -bor
                       [int]$fullCourseIconBytes[19]
$fullCourseIconHeight = ([int]$fullCourseIconBytes[20] -shl 24) -bor
                        ([int]$fullCourseIconBytes[21] -shl 16) -bor
                        ([int]$fullCourseIconBytes[22] -shl 8) -bor
                        [int]$fullCourseIconBytes[23]
Assert-True ($fullCourseIconWidth -eq 256 -and $fullCourseIconHeight -eq 256) 'The embedded Full Course icon must retain its square 256px source canvas.'
Assert-True ($fullCourseIconBytes[25] -eq 6) 'The embedded Full Course icon must retain an RGBA alpha channel instead of an opaque white background.'

$english = Get-Content -LiteralPath (Join-Path $projectRoot 'translations\English.yml') -Raw
$korean = Get-Content -LiteralPath (Join-Path $projectRoot 'translations\Korean.yml') -Raw
foreach ($key in @(
    'finedining_tooltip_spoils_in',
    'finedining_tooltip_minimum_freshness_in',
    'finedining_tooltip_minimum_freshness_paused',
    'finedining_tooltip_minimum_freshness_reached',
    'finedining_slot_minimum_freshness',
    'finedining_tooltip_staleness',
    'finedining_diet_full_course_title',
    'finedining_diet_full_course_description',
    'finedining_diet_full_course_message',
    'finedining_diet_extra_effect',
    'finedining_diet_effect_chef',
    'finedining_diet_effect_freshness',
    'finedining_diet_effect_diminishing',
    'finedining_diet_tooltip_chef_choice',
    'finedining_diet_tooltip_diminishing_returns',
    'finedining_diet_tooltip_puke_chef_refresh',
    'finedining_diet_recent_diminished',
    'finedining_diet_recent_chef_exempt',
    'finedining_diet_recent_chef_guidance',
    'finedining_diet_recent_chef_guidance_disabled',
    'finedining_diet_chef_cooking_guidance_both',
    'finedining_diet_chef_cooking_guidance_tier',
    'finedining_diet_chef_cooking_guidance_multiplier',
    'finedining_diet_chef_cooking_guidance_disabled',
    'finedining_skill_cooking_heading',
    'finedining_skill_cooking_auto_eject',
    'finedining_skill_cooking_bonus_output',
    'finedining_skill_cooking_chef_tier',
    'finedining_skill_cooking_chef_multiplier',
    'finedining_skill_cooking_chef_both',
    'finedining_station_cover',
    'finedining_station_fermentation_speed',
    'finedining_station_fermentation_guidance',
    'finedining_station_seconds',
    'finedining_station_auto_eject',
    'finedining_station_grimpy_output',
    'finedining_station_freydis_progress',
    'finedining_station_freydis_full'))
{
    Assert-True ($english.Contains($key + ':')) "English localization is missing $key."
    Assert-True ($korean.Contains($key + ':')) "Korean localization is missing $key."
}
Assert-True ($english.Contains('finedining_station_seconds: "{0}s"')) 'English station seconds format must remain compact.'
Assert-True ($korean.Contains('finedining_station_seconds: "{0}초"')) 'Korean station seconds format must remain localized.'
Assert-True ($english.Contains('finedining_station_auto_eject: "Auto eject"')) 'English auto-eject label is incorrect.'
Assert-True ($korean.Contains('finedining_station_auto_eject: "자동 배출"')) 'Korean auto-eject label is incorrect.'
Assert-True ($english.Contains('finedining_tooltip_minimum_freshness_in: "Minimum freshness in {0}"')) 'English Keep countdown wording is incorrect.'
Assert-True ($korean.Contains('finedining_tooltip_minimum_freshness_in: "최저 신선도까지 {0}"')) 'Korean Keep countdown wording is incorrect.'
Assert-True ($english.Contains('finedining_tooltip_minimum_freshness_paused: "Cold preservation paused freshness loss · minimum freshness in {0} ❄"')) 'English paused Keep wording is incorrect.'
Assert-True ($korean.Contains('finedining_tooltip_minimum_freshness_paused: "저온 보존으로 신선도 저하가 멈춤 · 최저 신선도까지 {0} ❄"')) 'Korean paused Keep wording is incorrect.'
Assert-True ($english.Contains('finedining_tooltip_minimum_freshness_reached: "Minimum freshness reached"')) 'English completed Keep wording is incorrect.'
Assert-True ($korean.Contains('finedining_tooltip_minimum_freshness_reached: "최저 신선도"')) 'Korean completed Keep wording is incorrect.'
Assert-True ($english.Contains('finedining_slot_minimum_freshness: "Min"') -and
             $korean.Contains('finedining_slot_minimum_freshness: "최저"')) 'Minimum-freshness slot labels must remain compact.'
Assert-True ($english.Contains('finedining_station_fermentation_speed: "Fermentation speed"')) 'English Fermenter speed label is incorrect.'
Assert-True ($korean.Contains('finedining_station_fermentation_speed: "발효 속도"')) 'Korean Fermenter speed label is incorrect.'
Assert-True ($english.Contains('finedining_station_fermentation_guidance: "More cover and greater depth make fermentation faster."')) 'English Fermenter guidance is incorrect.'
Assert-True ($korean.Contains('finedining_station_fermentation_guidance: "엄폐율이 높고 지표 아래로 깊을수록 빨라짐"')) 'Korean Fermenter guidance is incorrect.'
Assert-True ($english.Contains('finedining_station_freydis_progress: "Stored $1/$2 · next collection $3"')) 'English Freydis progress format must accept a separately colorized duration.'
Assert-True ($korean.Contains('finedining_station_freydis_progress: "수집 $1/$2 · 다음 수집 $3"')) 'Korean Freydis progress format must accept a separately colorized duration.'
Assert-True ($english.Contains('finedining_station_freydis_full: "<color=#9FE870>Collection storage full $1/$2</color>"')) 'English Freydis full format is incorrect.'
Assert-True ($korean.Contains('finedining_station_freydis_full: "<color=#9FE870>수집 완료 $1/$2</color>"')) 'Korean Freydis full format is incorrect.'
Assert-True ($english.Contains('finedining_diet_full_course_title: "Full Course"')) 'English Full Course title is incorrect.'
Assert-True ($english.Contains('finedining_diet_extra_effect: "Net effect"')) 'English eaten-food hover must label its combined modifier Net effect.'
Assert-True ($korean.Contains('finedining_diet_extra_effect: "종합 효과"')) 'Korean eaten-food hover must label its combined modifier 종합 효과.'
Assert-True ($english.Contains('finedining_diet_recent_diminished: "You recently ate ''$1'' $2 times. Eating it now applies food effect <color=orange>x$3</color>."')) 'English recent-history diminishing hover must describe the next consumption prospectively.'
Assert-True ($korean.Contains('finedining_diet_recent_diminished: "최근에 ''$1'' 음식을 $2번 먹었습니다. 지금 먹으면 음식 효과가 <color=orange>x$3</color>으로 적용됩니다."')) 'Korean recent-history diminishing hover must describe the next consumption prospectively.'
Assert-True ($english.Contains('finedining_diet_recent_chef_exempt: "You recently ate ''$1'' $2 times. Chef''s Choice prevents diminishing returns on this consumption."')) 'English recent-history hover must explain Chef Choice diminishing exemption.'
Assert-True ($korean.Contains('finedining_diet_recent_chef_exempt: "최근에 ''$1'' 음식을 $2번 먹었습니다. 셰프의 선택으로 이번 섭취에는 반복 섭취 감쇄가 적용되지 않습니다."')) 'Korean recent-history hover must explain Chef Choice diminishing exemption.'
Assert-True ($english.Contains('finedining_diet_recent_chef_guidance: "Recent food types affect appearance odds when Chef''s Choice refreshes."')) 'English enabled recent-history guidance is incorrect.'
Assert-True ($english.Contains('finedining_diet_recent_chef_guidance_disabled: "Recent food-type influence on Chef''s Choice is disabled."')) 'English disabled recent-history guidance is incorrect.'
Assert-True ($english.Contains('finedining_diet_chef_cooking_guidance_both: "Higher Cooking makes stronger multipliers and higher-tier foods more likely on refresh."')) 'English combined Cooking guidance is incorrect.'
Assert-True ($english.Contains('finedining_diet_chef_cooking_guidance_tier: "Higher Cooking makes higher-tier foods more likely on refresh."')) 'English Cooking tier guidance is incorrect.'
Assert-True ($english.Contains('finedining_diet_chef_cooking_guidance_multiplier: "Higher Cooking makes stronger multipliers more likely on refresh."')) 'English Cooking multiplier guidance is incorrect.'
Assert-True ($english.Contains('finedining_diet_chef_cooking_guidance_disabled: "Cooking-based Chef''s Choice bonuses are disabled."')) 'English disabled Cooking guidance is incorrect.'
Assert-True ($korean.Contains('finedining_diet_recent_chef_guidance: "최근 음식 유형은 셰프의 선택이 갱신될 때 등장 확률에 영향을 줍니다."')) 'Korean enabled recent-history guidance is incorrect.'
Assert-True ($korean.Contains('finedining_diet_recent_chef_guidance_disabled: "최근 음식 유형에 따른 셰프의 선택 보정이 비활성화되어 있습니다."')) 'Korean disabled recent-history guidance is incorrect.'
Assert-True ($korean.Contains('finedining_diet_chef_cooking_guidance_both: "요리 레벨이 높을수록 갱신 시 높은 배율과 상위 티어 음식이 나올 가능성이 커집니다."')) 'Korean combined Cooking guidance is incorrect.'
Assert-True ($korean.Contains('finedining_diet_chef_cooking_guidance_tier: "요리 레벨이 높을수록 갱신 시 상위 티어 음식이 나올 가능성이 커집니다."')) 'Korean Cooking tier guidance is incorrect.'
Assert-True ($korean.Contains('finedining_diet_chef_cooking_guidance_multiplier: "요리 레벨이 높을수록 갱신 시 높은 배율이 나올 가능성이 커집니다."')) 'Korean Cooking multiplier guidance is incorrect.'
Assert-True ($korean.Contains('finedining_diet_chef_cooking_guidance_disabled: "요리 레벨에 따른 셰프의 선택 보정이 비활성화되어 있습니다."')) 'Korean disabled Cooking guidance is incorrect.'
Assert-True (-not [regex]::IsMatch($english, '(?m)^\s*skill_cooking_description\s*:')) 'FineDining must not replace the vanilla English Cooking description globally.'
Assert-True (-not [regex]::IsMatch($korean, '(?m)^\s*skill_cooking_description\s*:')) 'FineDining must not replace the vanilla Korean Cooking description globally.'
foreach ($removedKey in @(
    'finedining_tooltip_spoiled',
    'finedining_tooltip_freshness_effect',
    'finedining_diet_tooltip_diminish_factor',
    'finedining_diet_tooltip_chef_multiplier',
    'finedining_diet_full_straight_title',
    'finedining_diet_full_straight_description',
    'finedining_diet_full_straight_message',
    'finedining_station_timer'))
{
    Assert-True (-not $english.Contains($removedKey + ':')) "Removed English localization remains: $removedKey"
    Assert-True (-not $korean.Contains($removedKey + ':')) "Removed Korean localization remains: $removedKey"
}

$sourceFiles = Get-ChildItem -LiteralPath $projectRoot -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
$fullStraightSourceMatches = @($sourceFiles | Select-String -Pattern 'FullStraight|full_straight')
Assert-True ($fullStraightSourceMatches.Count -eq 0) 'Legacy FullStraight symbols or localization tokens remain in C# source.'
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
    'manifest.json' = (Join-Path $projectRoot 'Thunderstore\manifest.json')
    'CHANGELOG.md' = (Join-Path $projectRoot 'Thunderstore\CHANGELOG.md')
    'icon.png' = (Join-Path $projectRoot 'Thunderstore\icon.png')
    'FineDining.English.yml' = (Join-Path $projectRoot 'translations\English.yml')
} $assembly.GetName().Version.ToString(3)
Assert-ZipPackage $NexusZipPath @{
    'FineDining.dll' = $assemblyPath
}

Write-Host 'FineDining integration smoke checks passed.'
