$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $root ('artifacts/toolkit-isolated/package-boundary-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $fixture) { throw 'Fixture already exists.' }
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (!$fixture.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture path.' }
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'package-release.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Package script has syntax errors.' }
$function = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Copy-SourceFile' }, $true))
if ($function.Count -ne 1) { throw 'Expected one production source-copy function.' }
try {
    $projectRoot = Join-Path $fixture 'input'
    $sourceRoot = Join-Path $fixture 'output'
    New-Item -ItemType Directory -Path $projectRoot, $sourceRoot -Force | Out-Null
    . ([scriptblock]::Create($function[0].Extent.Text))
    $blocked = @('Toolkit/private.cs', 'Bloxstrap/Toolkit/private.cs', 'Bloxstrap/DepthStrapToolkit/private.cs', 'Scripts/Toolkit/private.ps1', 'Bloxstrap/Integrations/Tools/private.cs')
    $allowed = @('Bloxstrap/Utility/Public.cs', 'Bloxstrap/CommunityToolkit.Mvvm/public.txt', 'Bloxstrap/Recovery/MacControls.cs', 'Bloxstrap/Recovery/AdapterDriver.ps1', 'Tests/DepthStrap.RecoveryTests/Program.cs')
    foreach ($relative in @($blocked) + @($allowed)) {
        $file = Join-Path $projectRoot $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($file)) -Force | Out-Null
        [IO.File]::WriteAllText($file, 'Boundary test fixture')
        Copy-SourceFile $relative
        $present = Test-Path -LiteralPath (Join-Path $sourceRoot $relative)
        if ($relative -in $blocked -and $present) { throw 'Unpublished toolkit entered source staging.' }
        if ($relative -in $allowed -and !$present) { throw 'Normal public source was incorrectly excluded.' }
        Write-Output ('PASS: package source boundary for ' + $relative)
    }
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
exit 0
