param(
    [string]$PublicName = 'deepisthatdeep',
    [string]$PublicEmail = 'deepisthatdeep@users.noreply.github.com'
)
$ErrorActionPreference = 'Stop'
if ($PublicEmail -notmatch '^[^\s<>@]+@users\.noreply\.github\.com$') { throw 'Use a GitHub noreply email.' }
$root = (& git rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Run this inside the repository.' }
$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([IO.Path]::GetFullPath($root) -ne $expected) { throw 'Run the copy of this script belonging to this repository.' }
$oldHooks = & git config --local --get core.hooksPath
if ($oldHooks -and $oldHooks -ne '.githooks') { throw 'Existing custom hooks must be integrated before installing the privacy guard.' }
foreach ($pair in @(@('user.name',$PublicName), @('user.email',$PublicEmail), @('privacy.publicName',$PublicName), @('privacy.publicEmail',$PublicEmail), @('core.hooksPath','.githooks'))) {
    & git config --local $pair[0] $pair[1]
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure the local privacy guard.' }
}
Write-Output 'Installed local commit, message and outgoing-push privacy checks with a public noreply identity.'
