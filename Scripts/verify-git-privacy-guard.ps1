$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $root ('artifacts/privacy-guard-fixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Scripts') -Destination $fixture -Recurse
Copy-Item -LiteralPath (Join-Path $root '.githooks') -Destination $fixture -Recurse
Copy-Item -LiteralPath (Join-Path $root '.gitattributes') -Destination $fixture
$remote = Join-Path $fixture 'remote.git'
function Invoke-FixtureGit([string[]]$Arguments, [bool]$Success = $true) {
    $output = @(& git @Arguments 2>&1)
    if (($LASTEXITCODE -eq 0) -ne $Success) { throw ("Unexpected Git privacy-fixture result for '" + $Arguments[0] + "' (exit " + $LASTEXITCODE + ").") }
    return $output
}
function Check([bool]$Condition, [string]$Label) { if (!$Condition) { throw $Label }; Write-Output ('PASS: '+$Label) }
Push-Location $fixture
try {
    Invoke-FixtureGit @('init','-b','main') | Out-Null
    & ./Scripts/install-git-privacy-guard.ps1
    Invoke-FixtureGit @('add','.githooks','.gitattributes','Scripts') | Out-Null
    Invoke-FixtureGit @('commit','-m','Public identity fixture') | Out-Null
    Invoke-FixtureGit @('init','--bare',$remote) | Out-Null
    Invoke-FixtureGit @('remote','add','fixture',$remote) | Out-Null
    Invoke-FixtureGit @('push','fixture','main') | Out-Null
    Check $true 'A genuine commit and local push pass with public identities and source files'
    [IO.File]::WriteAllText((Join-Path $fixture 'safe file_é.txt'),'safe')
    Invoke-FixtureGit @('add','safe file_é.txt') | Out-Null
    Invoke-FixtureGit @('-c','user.email=private@example.test','commit','-m','Blocked author fixture') $false | Out-Null
    Check $true 'The installed pre-commit hook rejects a private email override'
    Invoke-FixtureGit @('-c','user.name=Private Person','commit','-m','Blocked name fixture') $false | Out-Null
    Check $true 'The installed pre-commit hook rejects an unexpected personal author name'
    [IO.File]::WriteAllText((Join-Path $fixture 'private.txt'), $env:USERPROFILE)
    Invoke-FixtureGit @('add','private.txt') | Out-Null
    Invoke-FixtureGit @('commit','-m','Blocked content fixture') $false | Out-Null
    Invoke-FixtureGit @('rm','--cached','private.txt') | Out-Null
    Check $true 'Indexed personal profile data is blocked without printing it'
    $secret = 'gh'+'p_'+('x'*36)
    [IO.File]::WriteAllText((Join-Path $fixture 'credential.txt'), $secret)
    Invoke-FixtureGit @('add','credential.txt') | Out-Null
    Invoke-FixtureGit @('commit','-m','Blocked secret fixture') $false | Out-Null
    Invoke-FixtureGit @('rm','--cached','credential.txt') | Out-Null
    Check $true 'A staged credential signature is blocked'
    [IO.File]::WriteAllText((Join-Path $fixture 'Settings.json'),'{}')
    Invoke-FixtureGit @('add','Settings.json') | Out-Null
    Invoke-FixtureGit @('commit','-m','Blocked runtime fixture') $false | Out-Null
    Invoke-FixtureGit @('rm','--cached','Settings.json') | Out-Null
    Check $true 'Runtime settings cannot be included in a commit'
    foreach ($privatePath in @('Toolkit/recovery.txt', 'Bloxstrap/DepthStrapToolkit/recovery.txt')) {
        New-Item -ItemType Directory -Path (Split-Path $privatePath) -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixture $privatePath), 'unpublished synthetic toolkit fixture')
        Invoke-FixtureGit @('add',$privatePath) | Out-Null
        Invoke-FixtureGit @('commit','-m','Blocked unpublished toolkit fixture') $false | Out-Null
        Invoke-FixtureGit @('rm','--cached',$privatePath) | Out-Null
        Check $true 'A relocated unpublished toolkit path is blocked by pre-commit'
    }
    Invoke-FixtureGit @('commit','-m',('Path '+$env:USERPROFILE)) $false | Out-Null
    Check $true 'The installed commit-message hook rejects a private profile path'
    Invoke-FixtureGit @('commit','-m','Unicode source filename fixture') | Out-Null
    Invoke-FixtureGit @('push','fixture','main') | Out-Null
    Check $true 'Spaces and Unicode filenames are scanned without false rejection'
    $before = (Invoke-FixtureGit @('--git-dir',$remote,'rev-parse','main')) -join ''
    [IO.File]::WriteAllText((Join-Path $fixture 'later.txt'),'safe')
    Invoke-FixtureGit @('add','later.txt') | Out-Null
    Invoke-FixtureGit @('-c','user.email=private@example.test','commit','--no-verify','-m','Bypassed commit fixture') | Out-Null
    Invoke-FixtureGit @('push','fixture','main') $false | Out-Null
    $after = (Invoke-FixtureGit @('--git-dir',$remote,'rev-parse','main')) -join ''
    Check ($before -eq $after) 'The installed pre-push hook blocks a private commit even after bypassing pre-commit'
    Invoke-FixtureGit @('checkout','-b','name-fixture',$before) | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'name-fixture.txt'),'safe')
    Invoke-FixtureGit @('add','name-fixture.txt') | Out-Null
    Invoke-FixtureGit @('-c','user.name=Private Person','commit','--no-verify','-m','Bypassed name fixture') | Out-Null
    Invoke-FixtureGit @('push','fixture','name-fixture') $false | Out-Null
    Invoke-FixtureGit @('--git-dir',$remote,'show-ref','--verify','refs/heads/name-fixture') $false | Out-Null
    Check $true 'The pre-push hook also blocks a private name paired with a public email'
    Invoke-FixtureGit @('checkout','-b','toolkit-fixture',$before) | Out-Null
    New-Item -ItemType Directory -Path 'Bloxstrap/Toolkit' -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'Bloxstrap/Toolkit/private-feature.txt'), 'unpublished fixture')
    Invoke-FixtureGit @('add','Bloxstrap/Toolkit/private-feature.txt') | Out-Null
    Invoke-FixtureGit @('commit','--no-verify','-m','Bypassed toolkit fixture') | Out-Null
    Invoke-FixtureGit @('push','fixture','toolkit-fixture') $false | Out-Null
    Invoke-FixtureGit @('--git-dir',$remote,'show-ref','--verify','refs/heads/toolkit-fixture') $false | Out-Null
    Check $true 'Pre-push blocks a relocated toolkit even after pre-commit was bypassed'
} finally { Pop-Location }

# Expected rejected Git commands must not become the verifier's final exit status.
exit 0
