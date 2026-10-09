#requires -Version 7.4
param(
    [string]$Version='1.0.17',
    [string]$DotNet='dotnet',
    [string]$PowerShell7=(Get-Command pwsh -ErrorAction Stop).Source
)
$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$report=Join-Path $root ('artifacts/full-audit-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $report | Out-Null
$checks=[Collections.Generic.List[object]]::new()
function Native([string]$file,[string[]]$arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new($file); $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.WorkingDirectory=$root; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
    $worker=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$worker.StandardOutput.ReadToEndAsync(); $stderr=$worker.StandardError.ReadToEndAsync()
        if(!$worker.WaitForExit(300000)){$worker.Kill($true);$worker.WaitForExit();throw 'Owned audit process exceeded five minutes.'}
        $output=$stdout.GetAwaiter().GetResult(); $errors=$stderr.GetAwaiter().GetResult()
        if($output){Write-Output $output}; if($errors){Write-Output $errors}
        if($worker.ExitCode -ne 0){throw ('Audit process failed with exit code '+$worker.ExitCode)}
    }finally{$worker.Dispose()}
}
function Step([string]$name,[scriptblock]$operation){
    Write-Output ('Starting: '+$name); $timer=[Diagnostics.Stopwatch]::StartNew();$passed=$false
    try{& $operation | Tee-Object -FilePath (Join-Path $report (($name -replace '[^a-zA-Z0-9]+','-')+'.log'));$passed=$true}
    finally{$checks.Add([pscustomobject]@{Name=$name;Passed=$passed;Seconds=[Math]::Round($timer.Elapsed.TotalSeconds,2)});$checks|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $report 'checks.json')}
}
function Script([string]$file,[string[]]$arguments=@()){
    Native $PowerShell7 (@('-NoProfile','-NonInteractive','-File',(Join-Path $root $file))+$arguments)
}
$regression='Tests/DepthStrap.RegressionTests/bin/Release/net10.0-windows/DepthStrap.RegressionTests.dll'
$recovery='Tests/DepthStrap.RecoveryTests/bin/Release/net10.0-windows/DepthStrap.RecoveryTests.dll'
Push-Location $root
try{
    Step 'Source whitespace and PowerShell syntax' {
        Native 'git' @('-c','core.safecrlf=false','diff','--check','HEAD')
        $files=@(& rg --files Bloxstrap Scripts Tests -g '*.ps1' -g '!**/bin/**' -g '!**/obj/**')
        if($LASTEXITCODE -ne 0){throw 'Script enumeration failed.'}
        foreach($file in $files){$tokens=$null;$errors=$null;[Management.Automation.Language.Parser]::ParseFile((Join-Path $root $file),[ref]$tokens,[ref]$errors)|Out-Null;if($errors.Count){throw ('Script syntax failed: '+$file)}}
        Write-Output ('PASS: '+$files.Count+' public PowerShell scripts parsed.')
    }
    Step 'Full solution build' {Native $DotNet @('build','DepthStrap.slnx','-c','Release','--no-restore','-m:1','-p:UseSharedCompilation=false','-v','minimal')}
    Step 'App-wide regressions and theme layouts' {Native $DotNet @($regression,(Join-Path $report 'public-app.png'))}
    Step 'Toolkit reset and simulated MAC transactions' {Native $DotNet @($recovery,'--self-test')}
    Step 'Toolkit navigation and background synchronization' {Native $DotNet @($recovery,'--ui',(Join-Path $report 'recovery-ui'))}
    Step 'Adapter provider fixtures' {
        Native (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe') @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File','Tests/DepthStrap.RecoveryTests/AdapterDriverFixture.ps1','-Source','Bloxstrap/Recovery/AdapterDriver.ps1')
    }
    Step 'Socket binding fixtures' {Native $DotNet @($recovery,'--socket')}
    Step 'Read-only actual adapter inventory' {Native $DotNet @($recovery,'--inspect')}
    Step 'Live public network loading' {Native $DotNet @($regression,(Join-Path $report 'network.png'),'--public-network-only')}
    Step 'Actual current and downgrade package verification' {Native $DotNet @($regression,'--live-version-audit',(Join-Path $report 'player-packages'))}
    Step 'Public build boundaries' {Script 'Scripts/verify-public-toolkit-boundary.ps1' @('-DotNet',$DotNet)}
    Step 'Public package boundaries' {Script 'Scripts/verify-toolkit-package-boundary.ps1'}
    Step 'Disposable Git privacy fixtures' {Script 'Scripts/verify-git-privacy-guard.ps1'}
    Step 'Dependency vulnerability check' {
        $json=& $DotNet list Bloxstrap/Bloxstrap.csproj package --vulnerable --include-transitive --format json
        if($LASTEXITCODE -ne 0){throw 'Dependency vulnerability query failed.'}
        $data=($json -join "`n")|ConvertFrom-Json
        if(@($data.projects.frameworks.topLevelPackages | Where-Object {$_}).Count -or @($data.projects.frameworks.transitivePackages | Where-Object {$_}).Count){throw 'Known vulnerable packages reported.'}
        Write-Output 'PASS: no known vulnerable direct or transitive packages reported.'
    }
    Step 'Packaged app and elevated helper handoff' {Native $DotNet @($recovery,'--package',(Join-Path $root 'artifacts/release/DepthStrap.exe'))}
    Step 'Release privacy and SHA256 verification' {Script 'Scripts/verify-release-privacy.ps1' @('-Version',$Version)}
    Step 'Approved release source presence' {
        $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $root "artifacts/release/DepthStrap-$Version-source.zip"))
        try{
            foreach($file in @('Bloxstrap/Recovery/MacControls.cs','Bloxstrap/Recovery/AdapterDriver.ps1','Bloxstrap/Recovery/Core/MacChange.cs','Bloxstrap/Roblox/RobloxClientVersion.cs','Bloxstrap/UI/Elements/Settings/Pages/AntiApiPage.cs','Tests/DepthStrap.RecoveryTests/Program.cs')){
                $entry=$zip.GetEntry($file);if(!$entry){throw ('Approved source missing: '+$file)}
                $reader=[IO.StreamReader]::new($entry.Open());try{$archived=$reader.ReadToEnd()}finally{$reader.Dispose()}
                if($archived -ne [IO.File]::ReadAllText((Join-Path $root $file))){throw ('Packaged source differs: '+$file)}
            }
            Write-Output 'PASS: approved runtime, resource, version parsing and tests are present in the source archive.'
        }finally{$zip.Dispose()}
    }
    Step 'Clean extracted source solution build' {
        $source=Join-Path $report 'clean-source'
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $root "artifacts/release/DepthStrap-$Version-source.zip"),$source)
        Native $DotNet @('build',(Join-Path $source 'DepthStrap.slnx'),'-c','Release','-m:1','-p:UseSharedCompilation=false','-v','minimal')
    }
}finally{Pop-Location}
Write-Output ('PASS: '+$checks.Count+' full audit groups. Report: '+$report)
