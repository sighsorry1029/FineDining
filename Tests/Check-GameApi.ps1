param(
    [Parameter(Mandatory = $true)][string]$CecilPath,
    [Parameter(Mandatory = $true)][string]$PluginDll,
    [Parameter(Mandatory = $true)][string]$ManagedPath,
    [Parameter(Mandatory = $true)][string]$BepInExCore
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilPath
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($ManagedPath)
$resolver.AddSearchDirectory($BepInExCore)
$resolver.AddSearchDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($PluginDll))))
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($PluginDll), $reader)
$gameScopes = @('assembly_valheim', 'assembly_utils', 'assembly_guiutils')
function Get-AllTypes($Types) {
    foreach ($type in $Types) { $type; Get-AllTypes $type.NestedTypes }
}
function Get-Methods($Type, $Name) {
    $found = @($Type.Methods | Where-Object Name -eq $Name)
    if ($found.Count -gt 0) { return $found }
    if ($Type.BaseType) { Get-Methods $Type.BaseType.Resolve() $Name }
}
function Get-Field($Type, $Name) {
    $found = $Type.Fields | Where-Object Name -eq $Name
    if ($found) { return $found }
    if ($Type.BaseType) { Get-Field $Type.BaseType.Resolve() $Name }
}
function Get-Signature($Parameters) {
    (@($Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',')
}
try {
    $references = 0
    $targets = 0
    $bindings = 0
    foreach ($type in (Get-AllTypes $plugin.MainModule.Types)) {
        foreach ($method in $type.Methods) {
            if (-not $method.HasBody) { continue }
            $lastString = ''
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand
                if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr) { $lastString = $operand }
                if ($operand -is [Mono.Cecil.MemberReference] -and $operand.DeclaringType.Scope.Name -in $gameScopes) {
                    $resolved = $operand.Resolve()
                    if (-not $resolved) { throw "Unresolved game member in $method : $operand" }
                    if ($resolved -is [Mono.Cecil.FieldDefinition] -and $resolved.IsLiteral -and $instruction.OpCode.Code -in @('Ldsfld', 'Ldsflda', 'Stsfld')) {
                        throw "Runtime instruction references constant field in $method : $operand"
                    }
                    if (($resolved -is [Mono.Cecil.FieldDefinition] -or $resolved -is [Mono.Cecil.MethodDefinition]) -and -not $resolved.IsPublic) {
                        throw "Direct non-public game access in $method : $operand"
                    }
                    $references++
                }
                if ($method.Name -ne '.cctor' -or $operand -isnot [Mono.Cecil.GenericInstanceMethod]) { continue }
                if ($operand.Name -eq 'FieldRefAccess') {
                    if ($operand.GenericArguments[0].Scope.Name -notin $gameScopes) { continue }
                    $owner = $operand.GenericArguments[0].Resolve()
                    $field = Get-Field $owner $lastString
                    if (-not $field -or $field.IsStatic) { throw "Missing instance field $owner :: $lastString" }
                    $requested = $operand.GenericArguments[1].FullName
                    $actual = $field.FieldType.FullName
                    $compatible = $requested -eq $actual
                    if ($requested -in @('System.Collections.IDictionary', 'System.Collections.IList')) {
                        $compatible = $field.FieldType.Resolve().Interfaces.InterfaceType.FullName -contains $requested
                    }
                    if ($requested -eq 'System.Int32' -and $field.FieldType.Resolve().IsEnum) {
                        $underlying = $field.FieldType.Resolve().Fields | Where-Object Name -eq 'value__'
                        $compatible = $underlying.FieldType.FullName -eq 'System.Int32'
                    }
                    if (-not $compatible) { throw "Field binding type mismatch: $owner :: $lastString ($actual / $requested)" }
                    $bindings++
                } elseif ($operand.Name -eq 'MethodDelegate' -and $type.FullName -eq 'FineDining.GameAccess' -and $lastString -ne 'LoadImage') {
                    $delegate = $operand.GenericArguments[0]
                    $arguments = @($delegate.GenericArguments)
                    $owner = $arguments[0].Resolve()
                    $returnType = 'System.Void'
                    $end = $arguments.Count
                    if ($delegate.Name.StartsWith('Func')) { $returnType = $arguments[-1].FullName; $end-- }
                    $signature = (@(for ($i = 1; $i -lt $end; $i++) { $arguments[$i].FullName }) -join ',')
                    $matches = @(Get-Methods $owner $lastString | Where-Object {
                        -not $_.IsStatic -and $_.ReturnType.FullName -eq $returnType -and (Get-Signature $_.Parameters) -eq $signature
                    })
                    if ($matches.Count -ne 1) { throw "Method binding mismatch: $owner :: $lastString ($signature)" }
                    $bindings++
                }
            }
        }
        $entries = @()
        foreach ($attribute in $type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' }) {
            $entries += [pscustomobject]@{ Attribute = $attribute; Hooks = @($type.Methods | Where-Object Name -in @('Prefix', 'Postfix', 'Finalizer')) }
        }
        foreach ($hook in $type.Methods) {
            foreach ($attribute in $hook.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' }) {
                $entries += [pscustomobject]@{ Attribute = $attribute; Hooks = @($hook) }
            }
        }
        foreach ($entry in $entries) {
            $attribute = $entry.Attribute
            $args = $attribute.ConstructorArguments
            if ($args.Count -lt 2) { continue } # Optional mod/dynamic targets have separate runtime checks.
            $owner = $args[0].Value.Resolve()
            $name = $args[1].Value
            $methods = @(Get-Methods $owner $name)
            if ($args.Count -ge 3) {
                $signature = if ($args[2].Value -is [Mono.Cecil.TypeReference]) { (@($args | Select-Object -Skip 2 | ForEach-Object { $_.Value.FullName }) -join ',') } else { (@($args[2].Value | ForEach-Object { $_.Value.FullName }) -join ',') }
                $methods = @($methods | Where-Object { (Get-Signature $_.Parameters) -eq $signature })
            }
            if ($methods.Count -ne 1) { throw "Harmony target is missing or ambiguous: $type -> $owner :: $name" }
            $original = $methods[0]
            foreach ($hook in $entry.Hooks) {
                foreach ($parameter in $hook.Parameters) {
                    if ($parameter.Name -eq '__result' -and $parameter.ParameterType.FullName.TrimEnd('&') -ne $original.ReturnType.FullName) {
                        throw "Harmony result type mismatch: $type :: $hook"
                    }
                    if ($parameter.Name.StartsWith('__')) { continue }
                    $argument = $original.Parameters | Where-Object Name -eq $parameter.Name
                    if (-not $argument -or $parameter.ParameterType.FullName.TrimEnd('&') -ne $argument.ParameterType.FullName.TrimEnd('&')) {
                        throw "Harmony argument mismatch: $type :: $hook -> $parameter"
                    }
                }
            }
            $targets++
        }
    }
    if ($plugin.MainModule.AssemblyReferences.Name -contains 'Jotunn') { throw 'Jotunn must not be a required assembly.' }
    $buffer = @(Get-AllTypes $plugin.MainModule.Types | Where-Object FullName -eq 'ServerSync.ConfigSync/SendConfigsAfterLogin/BufferingSocket')
    if ($buffer.Count -ne 1) { throw 'Merged ServerSync BufferingSocket is missing.' }
    $send = @($buffer[0].Methods | Where-Object Name -eq 'Send')
    $bufferedMessages = @($send.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Ldstr' } | ForEach-Object Operand)
    foreach ($message in @('PeerInfo', 'RoutedRPC', 'ZDOData', 'PlayerList', 'HistoricalPlayerList', 'AdminList', 'NetTime')) {
        if ($bufferedMessages -notcontains $message) { throw "Merged ServerSync lacks the pinned handshake update: $message" }
    }
    $game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ManagedPath 'assembly_valheim.dll'), $reader)
    try {
        # A valid patch target is not enough: the quota hook must enclose both
        # native save loaders and the no-save-file return used by a new world.
        $quotaLoadHook = $plugin.MainModule.GetType('FineDining.ZNetServerLoadWorldIceboxSubsystemPatch')
        $quotaLoadTargets = @($quotaLoadHook.CustomAttributes | Where-Object {
            $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' -and
            $_.ConstructorArguments.Count -ge 2 -and
            $_.ConstructorArguments[0].Value.FullName -eq 'ZNet' -and
            $_.ConstructorArguments[1].Value -eq 'ServerLoadWorld'
        })
        if ($quotaLoadTargets.Count -ne 1) {
            throw 'Icebox quota initialization must cover the common server world-load completion, including chunked and new worlds.'
        }
        $znet = $game.MainModule.GetType('ZNet')
        foreach ($edge in @(
            @('Start', 'ZNet', 'ServerLoadWorld'),
            @('ServerLoadWorld', 'ZNet', 'LoadWorld'),
            @('ServerLoadWorld', 'ZNet', 'LoadOldWorld'),
            @('LoadWorld', 'ZDOMan', 'LoadChunks'),
            @('LoadOldWorld', 'ZDOMan', 'Load'),
            @('LoadWorld', 'ZNet', 'WorldSetup'),
            @('LoadOldWorld', 'ZNet', 'WorldSetup')
        )) {
            $caller = @($znet.Methods | Where-Object Name -eq $edge[0])
            $calls = @($caller.Body.Instructions | Where-Object {
                $_.OpCode.Code -in @('Call', 'Callvirt') -and
                $_.Operand.DeclaringType.FullName -eq $edge[1] -and
                $_.Operand.Name -eq $edge[2]
            })
            if ($caller.Count -ne 1 -or $calls.Count -eq 0) {
                throw "World loading changed; recheck Icebox initialization coverage: $($edge -join ' -> ')"
            }
        }
        $loadError = Get-Field $znet 'm_loadError'
        if (-not $loadError.IsPublic -or -not $loadError.IsStatic -or $loadError.FieldType.FullName -ne 'System.Boolean') {
            throw 'Icebox world-load failure guard no longer matches the original game field.'
        }
        foreach ($spec in @(
            'StatusEffect|GetTooltipString||System.String', 'SE_Stats|GetTooltipString||System.String',
            'Fermenter|RPC_Tap|System.Int64|System.Void',
            'CookingStation|SpawnItem|System.String,System.Int32,UnityEngine.Vector3,System.Boolean|System.Void',
            'Player|GetTotalFoodValue|System.Single&,System.Single&,System.Single&|System.Void',
            'Player|SetMaxEitr|System.Single,System.Boolean|System.Void',
            'FejdStartup|SetupGui||System.Void', 'FejdStartup|Start||System.Void',
            'ZNet|GetPeer|ZRpc|ZNetPeer'
        )) {
            $parts = $spec.Split('|')
            $matches = @(Get-Methods ($game.MainModule.GetType($parts[0])) $parts[1] | Where-Object {
                (Get-Signature $_.Parameters) -eq $parts[2] -and $_.ReturnType.FullName -eq $parts[3]
            })
            if ($matches.Count -ne 1) { throw "Dynamic method mismatch: $spec" }
        }
        foreach ($spec in @(
            'ItemDrop|s_instances|System.Collections.Generic.List`1<ItemDrop>',
            'Fermenter|m_delayedTapItem|System.Int32', 'Fermenter|m_delayedTapItemCheated|System.Boolean',
            'StatusEffect|m_nameHash|System.Int32', 'Player|m_foodUpdateTimer|System.Single',
            'Player|m_foodRegenTimer|System.Single', 'Player|m_knownRecipes|System.Collections.Generic.HashSet`1<System.String>',
            'Player|m_knownMaterial|System.Collections.Generic.HashSet`1<System.String>',
            'ZNet|m_adminList|SyncedList', 'ZNet|m_connectionStatus|ZNet/ConnectionStatus',
            'ZNetPeer|m_socket|ISocket', 'ZRpc|m_socket|ISocket',
            'ZPlayFabSocket|m_remotePlayerId|System.String',
            'ZRoutedRpc|m_peers|System.Collections.Generic.List`1<ZNetPeer>',
            'ZRpc|m_functions|System.Collections.Generic.Dictionary`2<System.Int32,ZRpc/RpcMethodBase>'
        )) {
            $parts = $spec.Split('|')
            if ((Get-Field ($game.MainModule.GetType($parts[0])) $parts[1]).FieldType.FullName -ne $parts[2]) {
                throw "ServerSync reflection field mismatch: $spec"
            }
        }
    } finally { $game.Dispose() }
    $gui = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ManagedPath 'assembly_guiutils.dll'), $reader)
    $image = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ManagedPath 'UnityEngine.ImageConversionModule.dll'), $reader)
    try {
        $tooltip = $gui.MainModule.GetType('UITooltip')
        foreach ($spec in @('m_current|UITooltip', 'm_tooltip|UnityEngine.GameObject')) {
            $parts = $spec.Split('|')
            $field = Get-Field $tooltip $parts[0]
            if (-not $field.IsStatic -or $field.FieldType.FullName -ne $parts[1]) { throw "Static tooltip binding mismatch: $spec" }
        }
        $loadImage = @($image.MainModule.GetType('UnityEngine.ImageConversion').Methods | Where-Object {
            $_.Name -eq 'LoadImage' -and $_.IsPublic -and $_.IsStatic -and $_.ReturnType.FullName -eq 'System.Boolean' -and
            (Get-Signature $_.Parameters) -eq 'UnityEngine.Texture2D,System.Byte[]'
        })
        if ($loadImage.Count -ne 1) { throw 'Image decoder delegate mismatch.' }
    } finally { $gui.Dispose(); $image.Dispose() }
    Write-Output "PASS: $references direct game references, $targets explicit Harmony targets/arguments, $bindings private bindings, Icebox server loading paths, selected dynamic/reflection contracts."
    Write-Output 'Metadata/IL validation only; this does not execute Harmony detours, Unity, Mono, RPC transport, or optional mods.'
} finally { $plugin.Dispose(); $resolver.Dispose() }
