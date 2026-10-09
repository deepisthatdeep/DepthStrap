param([string]$Version='1.0.17')
$ErrorActionPreference='Stop'
$privateEmails = @(& git log depthstrap/main --format='%ae%n%ce' | Where-Object { $_ -match '@yahoo\.|@hotmail\.|@outlook\.' } | Sort-Object -Unique)
$needles = @($env:USERNAME, $env:USERPROFILE, $env:USERPROFILE.Replace('\','/')) + $privateEmails
$needles = @($needles | Where-Object { $_ -and $_.Length -ge 4 } | Sort-Object -Unique)
$needles += @(Get-Content -LiteralPath (Join-Path $PWD '.git/privacy-denylist.txt') | Where-Object { $_ -and $_.Length -ge 4 })
$expressions = foreach ($needle in $needles) {
    [Regex]::Escape($needle)
    [Regex]::Escape([Text.Encoding]::Latin1.GetString([Text.Encoding]::Unicode.GetBytes($needle)))
}
$matcher = [Regex]::new(($expressions -join '|'), [Text.RegularExpressions.RegexOptions]::IgnoreCase)
$secretMatcher = [Regex]::new('gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{70,}|AKIA[A-Z0-9]{16}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')
function Scan-Stream([IO.Stream]$stream, [string]$label) {
    $buffer=[byte[]]::new(1048576)
    $carry=''
    while (($read=$stream.Read($buffer,0,$buffer.Length)) -gt 0) {
        $text=$carry+[Text.Encoding]::Latin1.GetString($buffer,0,$read)
        if ($matcher.IsMatch($text)) { throw "Personal identifier found in $label" }
        if ($secretMatcher.IsMatch($text)) { throw "Credential signature found in $label" }
        $carry=$text.Substring([Math]::Max(0,$text.Length-1024))
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($zipFile in Get-ChildItem (Join-Path $PSScriptRoot '../artifacts/release') -Filter ("DepthStrap-"+$Version+"-*.zip")) {
    $zip=[IO.Compression.ZipFile]::OpenRead($zipFile.FullName)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -match '(^|/)(\.git|\.codex|\.agents|Logs|Cache|Accounts|bin|obj|Tools)(/|$)|\.(pdb|log|jsonl|user|suo|pfx|pem)$|(^|/)(Settings|State|PlayerState|StudioState)\.json$') {
                throw ('Runtime or private file found in '+$zipFile.Name+': '+$entry.FullName)
            }
            $stream=$entry.Open()
            try { Scan-Stream $stream ($zipFile.Name+':'+$entry.FullName) } finally {$stream.Dispose()}
        }
        Write-Output ('Privacy and credential scan passed: '+$zipFile.Name+' ('+$zip.Entries.Count+' files)')
    } finally { $zip.Dispose() }
}
$exePath=Join-Path $PSScriptRoot '../artifacts/release/DepthStrap.exe'
$stream=[IO.File]::OpenRead($exePath)
try {Scan-Stream $stream 'DepthStrap.exe'} finally {$stream.Dispose()}
$metadata=[Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
if ($metadata.ProductVersion -ne $Version -or $metadata.FileVersion -ne $Version) {throw 'Incorrect executable version'}
Write-Output ('Privacy scan passed: '+$metadata.ProductName+' '+$metadata.ProductVersion)
foreach ($line in Get-Content (Join-Path $PSScriptRoot '../artifacts/release/SHA256SUMS.txt')) {
    $parts=$line -split '  ',2
    if ((Get-FileHash (Join-Path $PSScriptRoot ('../artifacts/release/'+$parts[1])) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $parts[0]) {throw 'Checksum mismatch'}
}
Write-Output ($Version+" release checksums verified.")
