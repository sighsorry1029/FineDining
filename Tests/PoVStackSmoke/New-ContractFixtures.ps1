param(
    [Parameter(Mandatory = $true)][string]$PathOfValheimanDll,
    [Parameter(Mandatory = $true)][string]$ManagedPath,
    [Parameter(Mandatory = $true)][string]$BepInExCore,
    [Parameter(Mandatory = $true)][string]$JotunnPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

# Isolated copies only. Never install these DLLs into a game or mod profile.
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $BepInExCore 'Mono.Cecil.dll')
$sourcePath = (Resolve-Path -LiteralPath $PathOfValheimanDll).Path
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
foreach ($directory in @($ManagedPath, $BepInExCore, $JotunnPath, (Split-Path $sourcePath))) {
    $resolver.AddSearchDirectory($directory)
}
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
try {
    foreach ($kind in @('extended-policy', 'identity-only', 'value-argument', 'predicate-shape', 'sort-quantity')) {
        $outputPath = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "$kind/PathOfValheiman.dll"))
        if ($outputPath -eq $sourcePath) { throw 'Fixture output must not replace the original DLL.' }
        $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($sourcePath, $reader)
        try {
            $module = $assembly.MainModule
            $rules = $module.Types | Where-Object FullName -eq 'PassiveTree.StackRules'
            $same = $rules.Methods | Where-Object Name -eq 'SameData'
            $bookkeeping = $rules.Methods | Where-Object Name -eq 'Bookkeeping'
            $calls = @($same.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.FullName -eq $bookkeeping.FullName })
            if ($calls.Count -ne 2) { throw 'Fixture input must have two native Bookkeeping calls.' }
            switch ($kind) {
                'extended-policy' {
                    # A new helper checks an additional item field and counts real calls.
                    $counter = [Mono.Cecil.FieldDefinition]::new('FineDiningFixtureChecks',
                        [Mono.Cecil.FieldAttributes]'Public, Static', $module.TypeSystem.Int32)
                    $rules.Fields.Add($counter)
                    $helper = [Mono.Cecil.MethodDefinition]::new('FineDiningFixtureCompare',
                        [Mono.Cecil.MethodAttributes]'Private, Static', $module.TypeSystem.Boolean)
                    $helper.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($module.TypeSystem.Single))
                    $helper.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($module.TypeSystem.Single))
                    $rules.Methods.Add($helper)
                    $il = $helper.Body.GetILProcessor()
                    $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldsfld, $counter)
                    $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1)
                    $il.Emit([Mono.Cecil.Cil.OpCodes]::Add)
                    $il.Emit([Mono.Cecil.Cil.OpCodes]::Stsfld, $counter)
                    $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldarg_0)
                    $il.Emit([Mono.Cecil.Cil.OpCodes]::Ldarg_1)
                    $il.Emit([Mono.Cecil.Cil.OpCodes]::Ceq)
                    $il.Emit([Mono.Cecil.Cil.OpCodes]::Ret)
                    $dataRead = @($same.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.FieldReference] -and $_.Operand.Name -eq 'm_customData' })[0]
                    $resume = $dataRead.Previous
                    $itemType = $same.Parameters[0].ParameterType.Resolve()
                    $durability = $module.ImportReference(($itemType.Fields | Where-Object Name -eq 'm_durability'))
                    $il = $same.Body.GetILProcessor()
                    foreach ($instruction in @(
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldfld, $durability),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_1),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldfld, $durability),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $helper),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brtrue, $resume),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_0),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret)
                    )) { $il.InsertBefore($resume, $instruction) }

                    # PoV alone owns the additional non-identity key policy.
                    $equal = @($bookkeeping.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'op_Equality' })[0].Operand
                    $resume = $bookkeeping.Body.Instructions[0]
                    $il = $bookkeeping.Body.GetILProcessor()
                    foreach ($instruction in @(
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldstr, 'pt.fixture.bookkeeping'),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call, $equal),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Brfalse, $resume),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_1),
                        [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ret)
                    )) { $il.InsertBefore($resume, $instruction) }
                }
                'identity-only' {
                    $assembly.Name.Version = [Version]'99.0.0.0'
                    $module.Mvid = [Guid]::NewGuid()
                }
                'value-argument' {
                    $value = @($same.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'get_Value' })[0].Operand
                    $calls[0].Previous.Operand = $value
                }
                'predicate-shape' {
                    # Semantically unchanged, but outside the supported direct branch boundary.
                    $local = [Mono.Cecil.Cil.VariableDefinition]::new($module.TypeSystem.Boolean)
                    $same.Body.Variables.Add($local)
                    $branch = $calls[0].Next
                    $il = $same.Body.GetILProcessor()
                    $il.InsertBefore($branch, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Stloc, $local))
                    $il.InsertBefore($branch, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldloc, $local))
                }
                'sort-quantity' {
                    $quick = $module.Types | Where-Object FullName -eq 'PassiveTree.QuickStack'
                    $sort = $quick.Methods | Where-Object Name -eq 'Sort'
                    $store = @($sort.Body.Instructions | Where-Object { $_.OpCode.Code -eq 'Stfld' -and $_.Operand.Name -eq 'm_stack' })[0]
                    $itemType = $store.Operand.DeclaringType.Resolve()
                    $store.Operand = $module.ImportReference(($itemType.Fields | Where-Object Name -eq 'm_variant'))
                }
            }
            [IO.Directory]::CreateDirectory((Split-Path $outputPath)) | Out-Null
            $assembly.Write($outputPath)
            Get-FileHash -LiteralPath $outputPath -Algorithm SHA256
        } finally { $assembly.Dispose() }
    }
} finally { $resolver.Dispose() }
