param([Parameter(Mandatory=$true)][string]$Source)
$ErrorActionPreference = 'Stop'
$tokens=$null; $errors=$null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Source,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Bundled tuning script failed syntax validation.' }
$functions = $ast.FindAll({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst]},$true)
foreach ($function in $functions) { . ([scriptblock]::Create($function.Extent.Text)) }
$script:checks = 0
function Assert($Condition,[string]$Name) { if (!$Condition) { throw $Name }; $script:checks++ }
function Stats { return [ordered]@{Changed=0; Restored=0; Unchanged=0; Unsupported=0; Unavailable=0; Preserved=0; Failed=0; WindowsApplied=0; WindowsRestored=0; WindowsFailed=0; CpuSettings=0} }
function Store {
    $store = [pscustomobject]@{Data=@{}; FailSave=$false}
    $store | Add-Member ScriptMethod GetValueNames { return @($this.Data.Keys) }
    $store | Add-Member ScriptMethod GetValue { param($Name,$Default=$null) if ($this.Data.ContainsKey($Name)) { return $this.Data[$Name] }; return $Default }
    $store | Add-Member ScriptMethod SetValue { param($Name,$Value,$Kind) if ($this.FailSave) { throw 'Fixture backup failure' }; $this.Data[$Name]=$Value }
    $store | Add-Member ScriptMethod DeleteValue { param($Name) $this.Data.Remove($Name) }
    $store | Add-Member ScriptMethod Flush { }
    return $store
}
function Property([string]$Keyword,[string]$Value='1') {
    return [pscustomobject]@{Name='Fixture'; RegistryKeyword=$Keyword; RegistryValue=@($Value); ValidRegistryValues=@('0','1','2','3','4','8');
        NumericParameterMinValue=''; NumericParameterMaxValue=''; NumericParameterBaseValue=''; NumericParameterStepValue=''}
}
$script:properties = @((Get-TuningPlan 'Extreme').Keys | ForEach-Object { Property $_ })
$adapter = [pscustomobject]@{InterfaceGuid=[guid]'11111111-2222-3333-4444-555555555555'; Name='Fixture'; Status='Up'}
function Get-Properties($Adapter) { return $script:properties }
$script:setCalls=0; $script:failSet=''
function Set-PropertyValue($Property,[string]$Value) {
    if ($Property.RegistryKeyword -eq $script:failSet) { throw 'Fixture driver failure' }
    $script:setCalls++; $Property.RegistryValue=@($Value)
}
$balanced = Get-TuningPlan 'Balanced'; $extreme = Get-TuningPlan 'Extreme'
Assert ($balanced.Count -eq 5 -and $extreme.Count -eq 25 -and $balanced['*SpeedDuplex'] -eq '0' -and $extreme['*RSS'] -eq '1') 'Profiles preserve auto-negotiation and RSS; Extreme includes 25 bounded controls'
Assert (!$extreme.Contains('*PriorityVLANTag') -and !$extreme.Contains('*NetworkAddress')) 'Profiles leave VLAN and hardware identity alone'
$numeric = Property '*ReceiveBuffers' '256'; $numeric.ValidRegistryValues=@()
$numeric.NumericParameterMinValue='64'; $numeric.NumericParameterMaxValue='2050'; $numeric.NumericParameterBaseValue='64'; $numeric.NumericParameterStepValue='64'
Assert ((Get-TargetValue $numeric 'maximum' '*ReceiveBuffers') -eq '2048') 'Maximum respects driver numeric range and step'
Assert (!(Test-PropertyValue $numeric '2050')) 'Unaligned driver values are rejected'
$numeric.ValidRegistryValues=$null
Assert ((Get-TargetValue $numeric 'maximum' '*ReceiveBuffers') -eq '2048') 'Null enum metadata still allows a supported numeric driver range'
$numeric.ValidRegistryValues=@($null,'')
Assert (Test-PropertyValue $numeric '256') 'Empty enum entries cannot hide valid numeric limits'
$numeric.NumericParameterMaxValue='9999999999'
Assert ($null -eq (Get-TargetValue $numeric 'maximum' '*ReceiveBuffers')) 'Implausible maxima are skipped instead of creating oversized allocations'
$enum = Property '*NumRSSQueues'
Assert ((Get-TargetValue $enum 'maximum' '*NumRSSQueues') -eq '8') 'Maximum comes from supported enum values'
$enum.ValidRegistryValues=@('1','banana','65536')
Assert ([long](Get-TargetValue $enum 'maximum' '*MaxRssProcessors') -le [Environment]::ProcessorCount) 'RSS processor count cannot exceed available logical CPUs'
$unknown = Property '*ReceiveBuffers'; $unknown.ValidRegistryValues=@()
Assert ($null -eq (Get-TargetValue $unknown 'maximum' '*ReceiveBuffers')) 'Missing capability limits are skipped without guessing'
$store = Store; $stats = Stats
Apply-Settings 'Extreme' $store @($adapter) $stats
Assert ($stats.Changed -gt 15 -and $stats.Failed -eq 0 -and $store.Data.Count -eq $stats.Changed) 'Aggressive profile backs up and verifies every changed property'
Assert (@(Get-Records $store).Count -eq $stats.Changed) 'Production backup schema reads the generated records'
($script:properties | Where-Object RegistryKeyword -EQ '*EEE').RegistryValue=@('2')
$stats = Stats; Restore-Settings $store @($adapter) $stats
Assert ($stats.Preserved -eq 1 -and $stats.Failed -eq 0 -and $store.Data.Count -eq 0 -and
    (Get-Current ($script:properties | Where-Object RegistryKeyword -EQ '*EEE')) -eq '2') 'Restore preserves later user edits and restores remaining owned changes'
$store = Store; $store.FailSave=$true; $stats=Stats; $before=$script:setCalls
Apply-Settings 'Balanced' $store @($adapter) $stats
Assert ($stats.Failed -gt 0 -and $script:setCalls -eq $before) 'Backup failure prevents all setting writes'
$store = Store; $stats=Stats; $script:failSet='*EEE'
Apply-Settings 'Balanced' $store @($adapter) $stats
Assert ($stats.Failed -eq 1 -and @((Get-Records $store) | Where-Object Keyword -EQ '*EEE').Count -eq 1) 'Driver failure retains the original recovery record'
$script:failSet=''; $stats=Stats; Restore-Settings $store @() $stats
Assert ($stats.Unavailable -gt 0 -and $store.Data.Count -gt 0) 'Disconnected or removed adapter backups survive for later restore'
$store.Data['bad']='{"Adapter":"not-a-guid","Keyword":"*SpeedDuplex","Original":"0","Applied":"1"}'
$rejected=$false; try { Get-Records $store } catch { $rejected=$true }
Assert $rejected 'Malformed restore records fail validation'

$script:active='aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'; $script:plans=@{$script:active=$true}; $script:commands=@()
function Invoke-PowerCfg([string[]]$Options) {
    $script:commands += ,$Options
    switch ($Options[0]) {
        '/getactivescheme' { return "Scheme $script:active" }
        '/list' { return ($script:plans.Keys -join ' ') }
        '/duplicatescheme' { $script:plans[$Options[2]]=$true; return '' }
        '/setactive' { if (!$script:plans.ContainsKey($Options[1])) { throw 'Missing fixture power plan' }; $script:active=$Options[1]; return '' }
        '/delete' { if ($script:active -eq $Options[1]) { throw 'Cannot delete active plan' }; $script:plans.Remove($Options[1]); return '' }
        default { return '' }
    }
}
$store=Store; $stats=Stats; Apply-PowerPlan 'Extreme' $store $stats
$power=Get-PowerRecord $store
Assert ($stats.WindowsApplied -eq 1 -and $stats.CpuSettings -eq 6 -and $script:active -eq $power.Created -and
    ($script:commands | Where-Object { $_[0] -eq '/duplicatescheme' })[1] -eq 'e9a42b02-d5df-448d-aa00-03f14749eb61') 'Extreme clones Ultimate Performance and applies six supported AC CPU requests'
$stats=Stats; Restore-PowerPlan $store $stats
Assert ($stats.WindowsRestored -eq 1 -and $script:active -eq $power.Original -and !$script:plans.ContainsKey($power.Created) -and !$store.Data.ContainsKey('PowerPlan')) 'Power restoration reactivates the original and removes only the generated copy'
$store=Store; $stats=Stats; Apply-PowerPlan 'Balanced' $store $stats
$power=Get-PowerRecord $store; $script:active=$power.Original
$stats=Stats; Restore-PowerPlan $store $stats
Assert ($stats.Preserved -eq 1 -and $script:active -eq $power.Original) 'Power restoration preserves a later manually selected plan'
function Import-Module { [CmdletBinding()]param([string]$Name) throw 'Fixture missing NetAdapter module' }
Assert (@(Get-TuningAdapters).Count -eq 0) 'Unavailable NIC inspection does not prevent Windows power-plan operations'
function Import-Module { [CmdletBinding()]param([string]$Name) }
function Get-NetAdapter { [CmdletBinding()]param([switch]$Physical) return $adapter }
Assert (@(Get-TuningAdapters).Count -eq 1) 'Available physical adapters remain eligible for supported tuning'
Remove-Item Function:Import-Module,Function:Get-NetAdapter
Write-Output "PASS: $script:checks PowerShell fixture checks. No hardware, Windows plans or machine registry were changed."
