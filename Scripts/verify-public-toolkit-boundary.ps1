param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $root 'Bloxstrap'
$fixture = Join-Path $project ('.boundary-fixture-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $fixture) { throw 'Boundary fixture location already exists.' }
if (!$fixture.StartsWith($project + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid boundary fixture location.' }
try {
    foreach ($name in @('Toolkit', 'DepthStrapToolkit')) {
        $folder = Join-Path $fixture $name
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $folder 'NeverCompile.cs'), 'Deliberately invalid unpublished source fixture')
        [IO.File]::WriteAllText((Join-Path $folder 'NeverShip.txt'), 'Unpublished fixture')
    }
    $raw = & $DotNet msbuild (Join-Path $project 'Bloxstrap.csproj') '-getItem:Compile,None,Content,EmbeddedResource,Resource,ProjectReference' '-p:Configuration=Release'
    if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate public project items.' }
    $evaluated = ($raw -join "`n") | ConvertFrom-Json
    if (!$evaluated.Items.Compile -or !(@($evaluated.Items.Compile | Where-Object { $_.Identity -match 'Utility[\\/]DiagnosticPrivacy\.cs$' }).Count)) {
        throw 'Project evaluation did not include the expected public source.'
    }
    foreach ($kind in @('Compile','None','Content','EmbeddedResource','Resource')) {
        $included = @($evaluated.Items.$kind | Where-Object { $_.FullPath -and $_.FullPath.StartsWith($fixture + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) })
        if ($included.Count) { throw 'An unpublished relocated toolkit fixture reached a public build item.' }
        Write-Output ('PASS: relocated toolkit excluded from '+$kind)
    }
    if (@($evaluated.Items.ProjectReference | Where-Object { $_.Identity -match 'DepthStrapToolkit|Toolkit[\\/]' }).Count) { throw 'Public project references an unpublished toolkit.' }
    Write-Output 'PASS: public project has no toolkit project reference'
    foreach ($source in @('Recovery/MacControls.cs','Recovery/RobloxFullReset.cs','Recovery/Core/MacChange.cs','UI/Elements/Settings/Pages/AntiApiPage.cs')) {
        if (!@($evaluated.Items.Compile | Where-Object { $_.Identity.Replace('\','/') -eq $source }).Count) { throw ('Approved recovery source is missing: '+$source) }
    }
    if (!@($evaluated.Items.EmbeddedResource | Where-Object { $_.Identity.Replace('\','/') -eq 'Recovery/AdapterDriver.ps1' -and $_.LogicalName -eq 'DepthStrap.Recovery.AdapterDriver.ps1' }).Count) { throw 'Approved adapter resource is missing.' }
    Write-Output 'PASS: approved public recovery runtime, page and adapter script are included'
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
exit 0
