#requires -Version 7.0
#requires -PSEdition Core
param(
    [Parameter(Mandatory)][string] $MonoHost,
    [string] $GamePath = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim',
    [string] $FineDiningDll = (Join-Path $PSScriptRoot '../../bin/Debug/FineDining.dll')
)
$ErrorActionPreference = 'Stop'
$MonoHost = (Resolve-Path -LiteralPath $MonoHost).Path
$FineDiningDll = (Resolve-Path -LiteralPath $FineDiningDll).Path
dotnet build (Join-Path $PSScriptRoot 'FoodStateStoreSmoke.csproj') -c Debug "-p:GameRoot=$GamePath" --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$managed = Join-Path $GamePath 'valheim_Data/Managed'
$core = Join-Path $GamePath 'BepInEx/core'
$runtime = Join-Path $GamePath 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'
$etc = Join-Path $GamePath 'MonoBleedingEdge/etc'
$output = Join-Path $PSScriptRoot 'bin/Debug'
Get-FileHash -LiteralPath $FineDiningDll,"$managed/assembly_valheim.dll",$runtime,$MonoHost |
    Format-List | Out-File (Join-Path $output 'inputs.txt')
& $MonoHost $runtime $managed $etc (Join-Path $output 'net48/FoodStateStoreSmoke.exe') $FineDiningDll $managed $core 2>&1 |
    Tee-Object -FilePath (Join-Path $output 'mono.log')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Independent JSON reader: a YAML reader alone could accept non-JSON output.
$json = Get-Content -LiteralPath (Join-Path $output 'net48/diet-state.json') -Raw | ConvertFrom-Json
if ($json.Recent.Count -ne 2 -or $json.Chef.Count -ne 2 -or $json.Active.Count -ne 2 -or
    $json.Recent[1].Key -cne "식재료_日本`"\`n") {
    throw 'The saved JSON lists or escaped prefab name did not survive the independent JSON reader.'
}
Write-Output 'PASS independent JSON parsing of all lists and escaped Unicode names.'
exit 0
