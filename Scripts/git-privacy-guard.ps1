param([ValidateSet('Commit','Message','Push')][string]$Mode, [string]$MessagePath)
$ErrorActionPreference = 'Stop'
function Read-Git([string[]]$Arguments) {
    $result = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect Git data; privacy check stopped.' }
    return $result
}
function Check-Email([string]$Email, [bool]$Historical = $false) {
    # Older published roots use the generic Codex identity. It is not the owner's personal email.
    if ($Historical -and ($Email -ceq 'codex@openai.com' -or $Email -match '^[^\s<>@]+@users\.noreply\.github\.com$')) { return }
    if ($Email -cne $script:publicEmail) { throw 'Blocked a non-public or unexpected commit identity. Reinstall the privacy guard or use the configured GitHub noreply identity.' }
}
function Check-Name([string]$Name, [bool]$Historical = $false) {
    if ($Name -ceq $script:publicName) { return }
    if ($Historical -and $Name -match '^(Codex|DepthStrap(?: (?:maintainers|contributors))?)$') { return }
    throw 'Blocked an unexpected author or committer name. Use the configured public handle.'
}
function Check-Text([string]$Text) {
    foreach ($needle in $script:needles) {
        if ($Text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'Blocked personal information in Git data. Remove it before committing or pushing.' }
    }
    if ($Text -match 'gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{70,}|AKIA[A-Z0-9]{16}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----') {
        throw 'Blocked a credential signature in Git data. Remove it before committing or pushing.'
    }
}
function Check-File([string]$Revision, [string]$Path) {
    Check-Text $Path
    if ($Path -match '(^|/)(artifacts|Logs|Cache|Accounts|\.git|\.codex|\.agents|bin|obj)(/|$)|^Fonts/|^Bloxstrap/Integrations/Tools/|(^|/)(Settings|State|PlayerState|StudioState)\.json$|(^|/)\.env(?:\.|$)|\.(pdb|log|jsonl|user|suo|pfx|pem)$') {
        throw 'Blocked runtime data, local tools or a private file in Git. Keep application data and credentials outside source control.'
    }
    # Inspect the indexed/committed blob, including binary metadata, rather than an unstaged working file.
    $blob = if ($Revision -eq ':') { ':' + $Path } else { $Revision + ':' + $Path }
    $info = [Diagnostics.ProcessStartInfo]::new('git')
    $info.ArgumentList.Add('show'); $info.ArgumentList.Add($blob)
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info)
    try {
        $buffer = [byte[]]::new(65536); $carry = ''
        while (($read = $process.StandardOutput.BaseStream.Read($buffer,0,$buffer.Length)) -gt 0) {
            $text = $carry + [Text.Encoding]::Latin1.GetString($buffer,0,$read)
            Check-Text $text
            $carry = $text.Substring([Math]::Max(0,$text.Length-4096))
        }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'A staged or outgoing blob could not be inspected.' }
    } finally { if (!$process.HasExited) { $process.Kill() }; $process.Dispose() }
}
try {
    $publicEmail = (Read-Git @('config','--local','--get','privacy.publicEmail')) -join ''
    $publicName = (Read-Git @('config','--local','--get','privacy.publicName')) -join ''
    if ($publicEmail -notmatch '^[^\s<>@]+@users\.noreply\.github\.com$') { throw 'Install the local Git privacy guard before committing.' }
    $privateEmail = & git config --global --get user.email
    $rawNeedles = @($env:USERPROFILE, ($env:USERPROFILE -replace '\\','/'), $env:USERNAME, $privateEmail, $env:GIT_AUTHOR_EMAIL, $env:GIT_COMMITTER_EMAIL)
    $denylist = Join-Path ((Read-Git @('rev-parse','--absolute-git-dir')) -join '') 'privacy-denylist.txt'
    if (Test-Path -LiteralPath $denylist) { $rawNeedles += @(Get-Content -LiteralPath $denylist) }
    $needles = @($rawNeedles | Where-Object { $_ -and $_.Length -ge 5 -and $_ -cne $publicEmail } | Sort-Object -Unique)
    $needles += @($needles | ForEach-Object { [Text.Encoding]::Latin1.GetString([Text.Encoding]::Unicode.GetBytes($_)) })
    if ($Mode -eq 'Commit') {
        foreach ($kind in @('GIT_AUTHOR_IDENT','GIT_COMMITTER_IDENT')) {
            $ident = (Read-Git @('var',$kind)) -join ''
            if ($ident -notmatch '^(.*?) <([^<>]+)>') { throw 'Commit identity is unavailable.' }
            Check-Name $Matches[1]
            Check-Email $Matches[2]
            Check-Text $ident
        }
        # NUL-delimited paths keep spaces and Unicode filenames intact.
        $paths = ((Read-Git @('-c','core.quotepath=false','diff','--cached','--name-only','-z','--diff-filter=ACMR')) -join "`n") -split "`0"
        foreach ($path in $paths) { if ($path) { Check-File ':' $path } }
    } elseif ($Mode -eq 'Message') {
        Check-Text ([IO.File]::ReadAllText($MessagePath))
    } else {
        $seen = [Collections.Generic.HashSet[string]]::new()
        while ($null -ne ($line = [Console]::ReadLine())) {
            $parts = $line -split ' '
            if ($parts.Count -ne 4) { throw 'Invalid outgoing ref information.' }
            if ($parts[1] -match '^0+$') { continue }
            $arguments = @('rev-list',$parts[1])
            if ($parts[3] -notmatch '^0+$') {
                & git cat-file -e ($parts[3]+'^{commit}') 2>$null
                if ($LASTEXITCODE -eq 0) { $arguments += '^'+$parts[3] }
            }
            foreach ($commit in (Read-Git $arguments)) {
                if (!$seen.Add($commit)) { continue }
                foreach ($email in (Read-Git @('show','-s','--format=%ae%n%ce',$commit))) { Check-Email $email $true }
                foreach ($name in (Read-Git @('show','-s','--format=%an%n%cn',$commit))) { Check-Name $name $true }
                Check-Text ((Read-Git @('show','-s','--format=%an%n%cn%n%B',$commit)) -join "`n")
                $paths = ((Read-Git @('-c','core.quotepath=false','diff-tree','--root','--no-commit-id','--name-only','-r','-z','--diff-filter=ACMR',$commit)) -join "`n") -split "`0"
                foreach ($path in $paths) { if ($path) { Check-File $commit $path } }
            }
        }
    }
    exit 0
} catch {
    # Never echo the rejected identity, secret, source text or absolute profile path.
    $reason = $_.Exception.Message
    if ($reason -notmatch '^(Blocked |Install |Could not |Commit identity|Invalid outgoing|A staged)') { $reason = 'Unable to inspect Git data; commit or push stopped.' }
    [Console]::Error.WriteLine('Privacy guard: '+$reason)
    exit 1
}
