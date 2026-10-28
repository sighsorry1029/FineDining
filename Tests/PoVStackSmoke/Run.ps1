param(
    [Parameter(Mandatory)][string] $MonoHost,
    [Parameter(Mandatory)][string] $PathOfValheimanDll,
    [Parameter(Mandatory)][string] $OptionalDependencyDirectory,
    [string] $GamePath = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim',
    [string] $FineDiningDll = (Join-Path $PSScriptRoot '../../bin/Debug/FineDining.dll')
)
$ErrorActionPreference = 'Stop'
$MonoHost = (Resolve-Path -LiteralPath $MonoHost).Path
$FineDiningDll = (Resolve-Path -LiteralPath $FineDiningDll).Path
$PathOfValheimanDll = (Resolve-Path -LiteralPath $PathOfValheimanDll).Path
$OptionalDependencyDirectory = (Resolve-Path -LiteralPath $OptionalDependencyDirectory).Path
dotnet build (Join-Path $PSScriptRoot 'PoVStackSmoke.csproj') -c Debug "-p:GameRoot=$GamePath" --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$managed = Join-Path $GamePath 'valheim_Data/Managed'
$core = Join-Path $GamePath 'BepInEx/core'
$runtime = Join-Path $GamePath 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'
$etc = Join-Path $GamePath 'MonoBleedingEdge/etc'
$output = Join-Path $PSScriptRoot 'bin/Debug'
Get-FileHash -LiteralPath $FineDiningDll,$PathOfValheimanDll,"$managed/assembly_valheim.dll",$runtime |
    Format-List | Out-File (Join-Path $output 'inputs.txt')
& $MonoHost $runtime $managed $etc (Join-Path $output 'net48/PoVStackSmoke.exe') $FineDiningDll $PathOfValheimanDll $managed $core $OptionalDependencyDirectory 2>&1 |
    Tee-Object -FilePath (Join-Path $output 'mono.log')
exit $LASTEXITCODE
