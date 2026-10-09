param([string]$Source = (Join-Path $PSScriptRoot 'AdapterDriver.ps1'))
$ErrorActionPreference = 'Stop'
$testTokens = $null; $testErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($Source, [ref]$testTokens, [ref]$testErrors)
if ($testErrors.Count) { throw 'Adapter backend syntax errors.' }
# Load only these owned function definitions. The resource's registry reader and
# main write dispatch never run. Every hardware/provider command below is a fake.
$wanted = @('Get-NetFacts','Get-CimFacts','Get-Capability','Get-Catalog','Restart-SelectedAdapter','Test-RobloxClientsRunning')
foreach ($name in $wanted) {
    $definition = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($definition.Count -ne 1) { throw 'Fixture function selection failed.' }
    Invoke-Expression $definition[0].Extent.Text
}
$script:Checks = 0
function Check([bool]$condition, [string]$name) { if (!$condition) { throw $name }; $script:Checks++ }
function Reject([scriptblock]$operation, [string]$name) { $failed=$false; try { & $operation } catch { $failed=$true }; Check $failed $name }
function Get-NetAdapter { if ($script:FailNet) { throw 'fixture provider failure' }; $script:Net }
function Get-CimInstance { if ($script:FailCim) { throw 'fixture provider failure' }; $script:Cim }
function Get-WmiObject { if ($script:FailWmi) { throw 'fixture provider failure' }; $script:Wmi }
function Get-Process {
    [CmdletBinding()]param([string[]]$Name)
    if ($script:ProcessFailureCategory) { Write-Error 'fixture process inventory failure' -Category $script:ProcessFailureCategory; return }
    foreach ($processName in $Name) { if ($processName -eq $script:RunningClient) { [pscustomobject]@{ProcessName=$processName} } }
}
function Get-RegistryProfiles { $script:Profiles }
function Get-NetAdapterAdvancedProperty { param($Name) $script:QueriedName=$Name; if ($script:FailProperty) { throw 'fixture property failure' }; $script:Properties }
function Restart-NetAdapter {
    $script:Restarts++
    if ($script:FailRestart) { $script:Net[0].AdminStatus='Down'; throw 'fixture partial restart failure' }
}
function Enable-NetAdapter { $script:Enables++; $script:Net[0].AdminStatus='Up' }
function Invoke-CimMethod {
    param($InputObject, $MethodName)
    $script:Methods += $MethodName
    [pscustomobject]@{ReturnValue=$(if ($MethodName -eq 'Disable' -and $script:FailDisable) { 1 } else { 0 })}
}
function Reset-Fixture {
    $script:Id = [guid]::NewGuid().ToString('D')
    $script:Net = @([pscustomobject]@{InterfaceGuid=$script:Id; Name='Wi-Fi [2]*'; HardwareInterface=$true; AdminStatus='Up'})
    $script:Cim = @([pscustomobject]@{GUID=$script:Id; NetConnectionID='Fixture NIC'; Name='Fixture NIC'; PhysicalAdapter=$true; NetEnabled=$true})
    $script:Wmi = @(); $script:Profiles=@([pscustomobject]@{Id=$script:Id; Subkey='0001'; Name='Fixture'; Advertised=$true; Valid=$true; Override=$null})
    $script:Properties=@(); $script:FailNet=$false; $script:FailCim=$false; $script:FailWmi=$false; $script:FailProperty=$false
    $script:FailRestart=$false; $script:FailDisable=$false; $script:Restarts=0; $script:Enables=0; $script:Methods=@()
}
Reset-Fixture
$script:RunningClient=''
$script:ProcessFailureCategory=$null
Check (!(Test-RobloxClientsRunning)) 'An empty client inventory permits the closed-client prerequisite.'
foreach ($script:RunningClient in @('RobloxPlayerBeta','RobloxStudioBeta','RobloxPlayerLauncher','RobloxStudioLauncherBeta')) {
    Check (Test-RobloxClientsRunning) 'An open client or launcher blocks MAC mutation before registry writes.'
}
$script:RunningClient='UnrelatedApp'
Check (!(Test-RobloxClientsRunning)) 'Unrelated processes do not block the closed-client prerequisite.'
$script:ProcessFailureCategory='ObjectNotFound'
Check (!(Test-RobloxClientsRunning)) 'Expected missing-process errors mean no matching clients were found.'
$script:ProcessFailureCategory='PermissionDenied'
Reject { Test-RobloxClientsRunning } 'A failed process inventory cannot authorize MAC mutation.'
$script:ProcessFailureCategory=$null
$catalog=@(Get-Catalog)
Check ($catalog.Count -eq 1 -and (Get-Capability $catalog[0]).Backend -eq 'NDIS / NetAdapter') 'Matching providers merge into one eligible adapter.'
$script:Profiles[0].Advertised=$false
$script:Properties=@([pscustomobject]@{Name=$script:Net[0].Name; RegistryKeyword='NetworkAddress'})
$catalog=@(Get-Catalog)
Check ((Get-Capability $catalog[0]).Supported) 'Existing NetAdapter driver property supplies capability evidence.'
Check ($script:QueriedName -ceq [WildcardPattern]::Escape($script:Net[0].Name)) 'Wildcard adapter names are escaped before lookup.'
Reset-Fixture; $script:FailNet=$true
$catalog=@(Get-Catalog)
Check ((Get-Capability $catalog[0]).Backend -eq 'NDIS / CIM') 'CIM discovers and restarts eligible adapters without NetAdapter.'
Reset-Fixture; $script:FailNet=$true; $script:FailCim=$true; $script:Wmi=$script:Cim
$catalog=@(Get-Catalog)
Check ((Get-Capability $catalog[0]).Backend -eq 'NDIS / WMI') 'WMI supplies discovery fallback when CIM fails.'
Reset-Fixture; $script:FailNet=$true; $script:FailCim=$true; $script:FailWmi=$true
$catalog=@(Get-Catalog)
Check ($catalog.Count -eq 1 -and !(Get-Capability $catalog[0]).Supported) 'Registry-only records remain visible without a fabricated physical/restart capability.'
Reset-Fixture; $script:Net[0].HardwareInterface=$false; $script:Cim[0].PhysicalAdapter=$false
Check (!(Get-Capability @(Get-Catalog)[0]).Supported) 'Virtual adapters are inspection-only.'
Reset-Fixture; $script:Net[0].AdminStatus='Down'; $script:Cim[0].NetEnabled=$false
Check (!(Get-Capability @(Get-Catalog)[0]).Supported) 'Disabled adapters are not enabled as a side effect of selection.'
Reset-Fixture; $script:Profiles+= $script:Profiles[0]
Check (!(Get-Capability @(Get-Catalog)[0]).Supported) 'Duplicate driver registry identities block mutation.'
Reset-Fixture; $script:Net+= [pscustomobject]@{InterfaceGuid='not-a-guid'}
Check (@(Get-Catalog).Count -eq 1) 'Malformed provider identifiers cannot break other adapters.'
Reset-Fixture; $script:Cim[0].PhysicalAdapter=$false
Check (!(Get-Capability @(Get-Catalog)[0]).Supported) 'Conflicting physical-adapter evidence is not guessed away.'
Reset-Fixture; $script:Profiles[0].Valid=$false
Check (!(Get-Capability @(Get-Catalog)[0]).Supported) 'Unsupported existing override formats block changes.'
Reset-Fixture; $script:FailProperty=$true
Check ((Get-Capability @(Get-Catalog)[0]).Supported) 'Driver-advertised metadata avoids a failing advanced-property provider.'
Reset-Fixture; $script:Profiles[0].Advertised=$false; $script:FailProperty=$true
Check (!(Get-Capability @(Get-Catalog)[0]).Supported) 'No property or metadata evidence means inspection-only.'
Reset-Fixture; $script:Net+= $script:Net[0]
Check (!(Get-Capability @(Get-Catalog)[0]).Supported) 'Duplicate provider identities stay inspection-only.'
Reset-Fixture
Restart-SelectedAdapter @(Get-Catalog)[0]
Check ($script:Restarts -eq 1 -and $script:Enables -eq 0) 'A normal NetAdapter restart uses the selected provider once.'
Reset-Fixture; $script:FailRestart=$true
Reject { Restart-SelectedAdapter @(Get-Catalog)[0] } 'A failed restart cannot report success.'
Check ($script:Enables -eq 1 -and $script:Net[0].AdminStatus -eq 'Up') 'A failed restart re-enables the originally enabled adapter.'
Reset-Fixture; $script:FailRestart=$true; $script:Net+= [pscustomobject]@{InterfaceGuid='not-a-guid'}
Reject { Restart-SelectedAdapter @(Get-Catalog)[0] } 'A partial restart remains a failure with malformed unrelated provider records.'
Check ($script:Enables -eq 1 -and $script:Net[0].AdminStatus -eq 'Up') 'Malformed unrelated records cannot prevent re-enabling the selected adapter.'
Reset-Fixture; $script:FailNet=$true
Restart-SelectedAdapter @(Get-Catalog)[0]
Check (($script:Methods -join ',') -eq 'Disable,Enable') 'CIM fallback checks both restart operations.'
Reset-Fixture; $script:FailNet=$true; $script:FailDisable=$true
Reject { Restart-SelectedAdapter @(Get-Catalog)[0] } 'CIM Disable failure cannot report a successful restart.'
Check (($script:Methods -join ',') -eq 'Disable,Enable') 'CIM failure still attempts to restore the enabled state.'
Write-Output "PASS: $script:Checks adapter provider fixture checks. No hardware or registry writes ran."
