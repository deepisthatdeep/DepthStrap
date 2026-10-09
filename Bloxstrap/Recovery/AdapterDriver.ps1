param(
    [ValidateSet('Read','Set','Inventory')][string]$Mode,
    [string]$Adapter,
    [string]$Address,
    [bool]$RemoveOverride = $true
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Get-NetFacts {
    try { @(Get-NetAdapter -IncludeHidden -ErrorAction Stop) } catch { @() }
}
function Test-RobloxClientsRunning {
    # Launchers can start a client immediately after the user closes its window.
    $processErrors = @()
    $clients = @(Get-Process -Name RobloxPlayerBeta,RobloxStudioBeta,RobloxPlayerLauncher,RobloxStudioLauncherBeta -ErrorAction SilentlyContinue -ErrorVariable +processErrors)
    if (@($processErrors | Where-Object { $_.CategoryInfo.Category -ne 'ObjectNotFound' }).Count -gt 0) { throw 'Roblox process inventory is unavailable. No MAC change was attempted.' }
    $clients.Count -gt 0
}
function Get-CimFacts {
    try {
        @(Get-CimInstance -ClassName Win32_NetworkAdapter -OperationTimeoutSec 6 -ErrorAction Stop |
            ForEach-Object { [pscustomobject]@{ Device=$_; Provider='CIM' } })
    } catch {
        try {
            @(Get-WmiObject -Class Win32_NetworkAdapter -ErrorAction Stop |
                ForEach-Object { [pscustomobject]@{ Device=$_; Provider='WMI' } })
        } catch { @() }
    }
}
function Get-RegistryProfiles {
    $base = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}')
    if ($null -eq $base) { return @() }
    try {
        foreach ($child in $base.GetSubKeyNames()) {
            if ($child -cnotmatch '\A[0-9]{4}\z') { continue }
            $key = $base.OpenSubKey($child)
            if ($null -eq $key) { continue }
            try {
                if ($key.GetValueNames() -contains 'SymbolicLinkValue') { continue }
                $id = [guid]::Empty
                if (![guid]::TryParse([string]$key.GetValue('NetCfgInstanceId'), [ref]$id) -or $id -eq [guid]::Empty) { continue }
                $parameter = $key.OpenSubKey('Ndi\Params\NetworkAddress')
                $advertised = $false
                if ($null -ne $parameter) {
                    try { $advertised = [string]$parameter.GetValue('Type') -ieq 'edit' -and !($parameter.GetValueNames() -contains 'SymbolicLinkValue') }
                    finally { $parameter.Dispose() }
                }
                $override = $null; $valid = $true
                if ($key.GetValueNames() -contains 'NetworkAddress') {
                    if ($key.GetValueKind('NetworkAddress') -ne [Microsoft.Win32.RegistryValueKind]::String) { $valid = $false }
                    else { $override = [string]$key.GetValue('NetworkAddress', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
                }
                [pscustomobject]@{ Id=$id.ToString('D'); Subkey=$child; Name=[string]$key.GetValue('DriverDesc'); Advertised=$advertised; Valid=$valid; Override=$override }
            } finally { $key.Dispose() }
        }
    } finally { $base.Dispose() }
}
function Get-Capability($candidate) {
    $reason = ''; $backend = ''
    if (!$candidate.Physical) { $reason = 'Physical adapter identity is unavailable or this is a virtual adapter.' }
    elseif ($candidate.Profiles.Count -ne 1) { $reason = 'A unique driver registry identity could not be verified.' }
    elseif (!$candidate.Profiles[0].Valid) { $reason = 'The existing override uses an unsupported registry format.' }
    elseif (!$candidate.Advertised) { $reason = 'The driver does not advertise the standard NetworkAddress setting.' }
    elseif (!$candidate.Enabled) { $reason = 'Enable this adapter in Windows before changing its address.' }
    elseif ($null -ne $candidate.Net) { $backend = 'NDIS / NetAdapter' }
    elseif ($null -ne $candidate.Cim) { $backend = 'NDIS / ' + $candidate.Cim.Provider }
    else { $reason = 'No supported adapter restart interface is available.' }
    [pscustomobject]@{ Supported=($backend.Length -gt 0); Backend=$backend; Reason=$reason }
}
function Get-Catalog {
    $net = @(Get-NetFacts); $cim = @(Get-CimFacts); $profiles = @(Get-RegistryProfiles)
    $ids = @{}
    foreach ($item in $net) {
        $id = [guid]::Empty
        if ([guid]::TryParse([string]$item.InterfaceGuid, [ref]$id) -and $id -ne [guid]::Empty) { $ids[$id.ToString('D')] = $true }
    }
    foreach ($item in $cim) {
        $id = [guid]::Empty
        if ([guid]::TryParse([string]$item.Device.GUID, [ref]$id) -and $id -ne [guid]::Empty) { $ids[$id.ToString('D')] = $true }
    }
    foreach ($profile in $profiles) { $ids[$profile.Id] = $true }
    foreach ($id in @($ids.Keys | Sort-Object)) {
        $n = @($net | Where-Object { $parsed = [guid]::Empty; [guid]::TryParse([string]$_.InterfaceGuid, [ref]$parsed) -and $parsed.ToString('D') -eq $id })
        $c = @($cim | Where-Object { $parsed = [guid]::Empty; [guid]::TryParse([string]$_.Device.GUID, [ref]$parsed) -and $parsed.ToString('D') -eq $id })
        $p = @($profiles | Where-Object { $_.Id -eq $id })
        # Conflicting provider identities are never guessed away.
        $ambiguous = $n.Count -gt 1 -or $c.Count -gt 1
        $nic = $null; $legacy = $null
        if ($n.Count -eq 1) { $nic = $n[0] }
        if ($c.Count -eq 1) { $legacy = $c[0] }
        $physical = ($null -ne $nic -and $nic.HardwareInterface -eq $true) -or ($null -ne $legacy -and $legacy.Device.PhysicalAdapter -eq $true)
        if ($ambiguous) { $physical = $false }
        if ($null -ne $nic -and $null -ne $legacy -and (($nic.HardwareInterface -eq $true) -ne ($legacy.Device.PhysicalAdapter -eq $true))) { $physical = $false }
        $enabled = $false; $name = ''
        if ($null -ne $nic) { $enabled = [string]$nic.AdminStatus -eq 'Up'; $name = [string]$nic.Name }
        elseif ($null -ne $legacy) { $enabled = $legacy.Device.NetEnabled -eq $true; $name = [string]$legacy.Device.NetConnectionID; if (!$name) { $name = [string]$legacy.Device.Name } }
        elseif ($p.Count -eq 1) { $name = $p[0].Name }
        if (!$name) { $name = 'Unidentified network adapter' }
        $advertised = $p.Count -eq 1 -and $p[0].Advertised
        if (!$advertised -and $null -ne $nic) {
            try {
                $properties = @(Get-NetAdapterAdvancedProperty -Name ([WildcardPattern]::Escape($nic.Name)) -AllProperties -ErrorAction Stop |
                    Where-Object { $_.Name -ceq $nic.Name -and $_.RegistryKeyword -ieq 'NetworkAddress' })
                $advertised = $properties.Count -eq 1
            } catch {}
        }
        [pscustomobject]@{ Id=$id; Name=$name; Physical=$physical; Enabled=$enabled; Net=$nic; Cim=$legacy; Profiles=$p; Advertised=$advertised }
    }
}
function Get-EffectiveAddress([string]$id) {
    $interfaces = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object {
        $parsed = [guid]::Empty
        [guid]::TryParse($_.Id, [ref]$parsed) -and $parsed.ToString('D') -eq $id
    })
    if ($interfaces.Count -ne 1) { return '' }
    $bytes = $interfaces[0].GetPhysicalAddress().GetAddressBytes()
    if ($bytes.Length -ne 6) { return '' }
    return [BitConverter]::ToString($bytes).Replace('-', '')
}
function Restart-SelectedAdapter($candidate) {
    if ($null -ne $candidate.Net) {
        try { Restart-NetAdapter -InputObject $candidate.Net -Confirm:$false -ErrorAction Stop }
        finally {
            $current = @(Get-NetFacts | Where-Object {
                $parsed = [guid]::Empty
                [guid]::TryParse([string]$_.InterfaceGuid, [ref]$parsed) -and $parsed.ToString('D') -eq $candidate.Id
            })
            if ($current.Count -eq 1 -and [string]$current[0].AdminStatus -eq 'Down') {
                Enable-NetAdapter -InputObject $current[0] -Confirm:$false -ErrorAction Stop
            }
        }
        return
    }
    $legacy = $candidate.Cim
    if ($null -eq $legacy -or $legacy.Device.NetEnabled -ne $true) { throw 'The adapter restart interface is unavailable.' }
    try {
        if ($legacy.Provider -eq 'CIM') { $result = Invoke-CimMethod -InputObject $legacy.Device -MethodName Disable -OperationTimeoutSec 10 -ErrorAction Stop }
        else { $result = $legacy.Device.Disable() }
        if ($result.ReturnValue -ne 0) { throw 'The adapter could not be disabled for restart.' }
    } finally {
        # Always attempt to restore the originally enabled state, even when
        # Disable reports a failure after taking effect.
        if ($legacy.Provider -eq 'CIM') { $result = Invoke-CimMethod -InputObject $legacy.Device -MethodName Enable -OperationTimeoutSec 10 -ErrorAction Stop }
        else { $result = $legacy.Device.Enable() }
        if ($result.ReturnValue -ne 0) { throw 'The adapter could not be enabled again.' }
    }
}

$catalog = @(Get-Catalog)
if ($Mode -eq 'Inventory') {
    $items = @($catalog | ForEach-Object {
        $capability = Get-Capability $_
        [pscustomobject]@{ Id=$_.Id; Name=$_.Name; Supported=$capability.Supported; Backend=$capability.Backend; Reason=$capability.Reason; Physical=$_.Physical }
    })
    ConvertTo-Json -InputObject $items -Depth 4 -Compress
    return
}
$id = [guid]::Empty
if (![guid]::TryParseExact($Adapter, 'D', [ref]$id) -or $id -eq [guid]::Empty) { throw 'Invalid adapter identifier.' }
$matches = @($catalog | Where-Object { $_.Id -eq $id.ToString('D') })
if ($matches.Count -ne 1) { 'null'; return }
$selected = $matches[0]; $capability = Get-Capability $selected
if ($Mode -eq 'Set') {
    if (!(New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval required.' }
    if (Test-RobloxClientsRunning) { throw 'Close Roblox and Studio, including their launchers, first.' }
    if (!$capability.Supported) { throw 'The selected driver is not supported for MAC changes.' }
    if (!$RemoveOverride -and $Address -ne '' -and $Address -cnotmatch '\A[0-9A-F]{12}\z') { throw 'Invalid MAC address.' }
    if (!$RemoveOverride -and $Address -ne '') {
        foreach ($interface in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($interface.Id.Trim('{}') -ine $id.ToString('D') -and [BitConverter]::ToString($interface.GetPhysicalAddress().GetAddressBytes()).Replace('-', '') -eq $Address) { throw 'Another adapter already uses that address.' }
        }
    }
    # A fixed class root and numeric child are resolved from the verified GUID.
    # Never create driver metadata or accept a registry path from the caller.
    $path = 'SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\' + $selected.Profiles[0].Subkey
    $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($path, $true)
    if ($null -eq $key) { throw 'The verified driver key is unavailable.' }
    try {
        if ($key.GetValueNames() -contains 'SymbolicLinkValue' -or [guid]$key.GetValue('NetCfgInstanceId') -ne $id) { throw 'Adapter registry identity changed.' }
        if ($RemoveOverride) { $key.DeleteValue('NetworkAddress', $false) }
        else { $key.SetValue('NetworkAddress', $Address, [Microsoft.Win32.RegistryValueKind]::String) }
        $key.Flush()
    } finally { $key.Dispose() }
    Restart-SelectedAdapter $selected
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $effective = Get-EffectiveAddress $id.ToString('D')
        if ($effective -and ($RemoveOverride -or $Address -eq '' -or $effective -eq $Address)) { break }
        Start-Sleep -Milliseconds 1000
    }
    $profiles = @(Get-RegistryProfiles | Where-Object { $_.Id -eq $id.ToString('D') })
    if ($profiles.Count -ne 1) { throw 'Driver identity unavailable after restart.' }
    $selected.Profiles = $profiles
}
$override = $null
if ($selected.Profiles.Count -eq 1) { $override = $selected.Profiles[0].Override }
[pscustomobject]@{ Id=$id.ToString('D'); Supported=$capability.Supported; EffectiveAddress=(Get-EffectiveAddress $id.ToString('D')); Override=$override } | ConvertTo-Json -Compress
