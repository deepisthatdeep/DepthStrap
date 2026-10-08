param([ValidatePattern('\A\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?\z')][string]$Version = '1.0.11')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$packageRoot = Join-Path $artifactRoot 'release'
$stageRoot = Join-Path $artifactRoot ('package-' + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $stageRoot 'source'
$binaryRoot = Join-Path $stageRoot 'windows'
$noticeRoot = Join-Path $binaryRoot 'ThirdPartyNotices'
foreach ($directory in @($packageRoot, $sourceRoot, $binaryRoot, $noticeRoot)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

function Copy-SourceFile([string]$RelativePath) {
    $RelativePath = $RelativePath.Replace('\', '/')
    if ($RelativePath -match '(^|[\\/])(\.git|bin|obj|artifacts|Logs|Cache|Accounts)([\\/]|$)' -or
        $RelativePath -match '\.(pdb|log|jsonl|user|suo)$' -or
        $RelativePath -like 'Bloxstrap/Integrations/Tools/*') { return }
    $from = [IO.Path]::GetFullPath((Join-Path $projectRoot $RelativePath))
    $to = [IO.Path]::GetFullPath((Join-Path $sourceRoot $RelativePath))
    if (!$from.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        !$to.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source path leaves the project: $RelativePath"
    }
    if (!(Test-Path -LiteralPath $from -PathType Leaf)) { return }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($to)) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $to
}

$sourceSelections = @('.githooks', '.gitattributes', 'Bloxstrap', 'Scripts', 'Tests', 'DepthStrap.slnx', 'README.md', 'NOTICE.md', 'LICENSE', 'LICENSE-MIT', 'LICENSE-UNLICENSE', 'LICENSE-INITIAL-REPOSITORY', 'flake.nix', 'flake.lock', 'justfile')
if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
    $sourceFiles = & git -C $projectRoot ls-files --cached --others --exclude-standard -- @sourceSelections
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate application source.' }
} else {
    $sourceFiles = foreach ($selection in $sourceSelections) {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $selection) -Recurse -File |
            ForEach-Object { [IO.Path]::GetRelativePath($projectRoot, $_.FullName) }
    }
}
foreach ($sourceFile in $sourceFiles) { Copy-SourceFile $sourceFile }
foreach ($dependency in @('wpfui', 'ColorPicker')) {
    $dependencyPath = Join-Path $projectRoot $dependency
    if (Test-Path -LiteralPath (Join-Path $dependencyPath '.git')) {
        $dependencyFiles = & git -C $dependencyPath ls-files --cached
        if ($LASTEXITCODE -ne 0) { throw "Could not enumerate $dependency source." }
    } else {
        $dependencyFiles = Get-ChildItem -LiteralPath $dependencyPath -Recurse -File |
            ForEach-Object { [IO.Path]::GetRelativePath($dependencyPath, $_.FullName) }
    }
    foreach ($sourceFile in $dependencyFiles) { Copy-SourceFile "$dependency/$sourceFile" }
}

$publishedExe = Join-Path $projectRoot 'artifacts/publish/DepthStrap.exe'
if (!(Test-Path -LiteralPath $publishedExe)) { throw 'Publish the Windows executable before packaging.' }
Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $binaryRoot 'DepthStrap.exe')
foreach ($document in @('README.md', 'NOTICE.md', 'LICENSE', 'LICENSE-MIT', 'LICENSE-UNLICENSE', 'LICENSE-INITIAL-REPOSITORY')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination (Join-Path $binaryRoot $document)
}
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'artifacts/publish') -Filter 'License*.txt' -File |
    Copy-Item -Destination $noticeRoot
foreach ($dependency in @('wpfui', 'ColorPicker')) {
    Get-ChildItem -LiteralPath (Join-Path $sourceRoot $dependency) -Recurse -File |
        Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)([.-]|$)' } |
        ForEach-Object {
            $relativeNotice = [IO.Path]::GetRelativePath($sourceRoot, $_.FullName).Replace('\', '_').Replace('/', '_')
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeRoot $relativeNotice)
        }
}

# Collect resolved package license declarations and locally shipped license texts.
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'Bloxstrap/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$packageNotices = [Collections.Generic.List[string]]::new()
foreach ($package in $assets.libraries.GetEnumerator() | Sort-Object Key) {
    if ($package.Value.type -ne 'package') { continue }
    $packageNotices.Add($package.Key)
    foreach ($packageFolder in $assets.packageFolders.Keys) {
        $localPackage = Join-Path $packageFolder $package.Value.path
        if (!(Test-Path -LiteralPath $localPackage)) { continue }
        Get-ChildItem -LiteralPath $localPackage -Filter '*.nuspec' -File | ForEach-Object {
            [xml]$metadata = Get-Content -LiteralPath $_.FullName -Raw
            $packageNotices.Add('  License: ' + $metadata.package.metadata.license.InnerText)
            $packageNotices.Add('  License URL: ' + $metadata.package.metadata.licenseUrl)
            $packageNotices.Add('  Project: ' + $metadata.package.metadata.projectUrl)
            $packageNotices.Add('  Authors: ' + $metadata.package.metadata.authors)
        }
        Get-ChildItem -LiteralPath $localPackage -Recurse -File |
            Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES|NOTICE|COPYING)([.-]|$)' } |
            ForEach-Object {
                $noticeName = $package.Key.Replace('/', '_') + '_' + [IO.Path]::GetRelativePath($localPackage, $_.FullName).Replace('\', '_').Replace('/', '_')
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeRoot $noticeName)
            }
        break
    }
}
$packageNotices | Set-Content -LiteralPath (Join-Path $noticeRoot 'Dependencies.txt') -Encoding utf8
foreach ($packageFolder in $assets.packageFolders.Keys) {
    foreach ($framework in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
        $frameworkRoot = Join-Path $packageFolder $framework
        if (!(Test-Path -LiteralPath $frameworkRoot)) { continue }
        $frameworkVersion = Get-ChildItem -LiteralPath $frameworkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
        Get-ChildItem -LiteralPath $frameworkVersion.FullName -File |
            Where-Object { $_.Name -match '^(LICENSE|THIRD.PARTY.NOTICES)([.-]|$)' } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeRoot ($framework + '_' + $_.Name)) }
    }
}

$sourceArchive = Join-Path $packageRoot "DepthStrap-$Version-source.zip"
$binaryArchive = Join-Path $packageRoot "DepthStrap-$Version-win-x64.zip"
function Write-Archive([string]$From, [string]$To) {
    $stream = [IO.File]::Open($To, [IO.FileMode]::Create)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $From -Recurse -File) {
            $name = [IO.Path]::GetRelativePath($From, $file.FullName).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
}
Write-Archive $sourceRoot $sourceArchive
Write-Archive $binaryRoot $binaryArchive
Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $packageRoot 'DepthStrap.exe') -Force
$checksums = foreach ($file in @($binaryArchive, $sourceArchive, (Join-Path $packageRoot 'DepthStrap.exe'))) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))"
}
$checksums | Set-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Release package: $packageRoot"
Write-Output "Source staging: $sourceRoot"
