param([ValidateSet('Balanced','Extreme','Restore','Preview')][string]$Mode = 'Preview')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-TuningPlan([string]$Profile) {
    $plan = [ordered]@{'*EEE'='0'; '*SelectiveSuspend'='0'; '*DeviceSleepOnDisconnect'='0'; '*RSS'='1'; '*SpeedDuplex'='0'}
    if ($Profile -eq 'Extreme') {
        foreach ($keyword in @('*InterruptModeration','*FlowControl','*RscIPv4','*RscIPv6',
            '*LsoV1IPv4','*LsoV2IPv4','*LsoV2IPv6','*IPChecksumOffloadIPv4',
            '*TCPChecksumOffloadIPv4','*TCPChecksumOffloadIPv6','*UDPChecksumOffloadIPv4','*UDPChecksumOffloadIPv6',
            '*TCPUDPChecksumOffloadIPv4','*TCPUDPChecksumOffloadIPv6','*TCPConnectionOffloadIPv4','*TCPConnectionOffloadIPv6')) {
            $plan[$keyword] = '0'
        }
        foreach ($keyword in @('*NumRSSQueues','*MaxRssProcessors','*ReceiveBuffers','*TransmitBuffers')) { $plan[$keyword] = 'maximum' }
    }
    return $plan
}

function Test-PropertyValue($Property, [string]$Value) {
    if ($Value -notmatch '^\d{1,10}$') { return $false }
    if (@($Property.ValidRegistryValues).Count -gt 0) { return @($Property.ValidRegistryValues) -contains $Value }
    $min = 0L; $max = 0L; $step = 0L; $base = 0L
    if (![long]::TryParse([string]$Property.NumericParameterMinValue,[ref]$min) -or
        ![long]::TryParse([string]$Property.NumericParameterMaxValue,[ref]$max) -or
        ![long]::TryParse([string]$Property.NumericParameterStepValue,[ref]$step) -or $step -le 0) { return $false }
    if (![long]::TryParse([string]$Property.NumericParameterBaseValue,[ref]$base)) { $base = $min }
    $number = [long]$Value
    return $number -ge $min -and $number -le $max -and (($number-$base) % $step -eq 0)
}

function Get-TargetValue($Property, [string]$Target, [string]$Keyword) {
    if ($Target -ne 'maximum') { if (Test-PropertyValue $Property $Target) { return $Target }; return $null }
    $limit = 1048576L
    if ($Keyword -eq '*MaxRssProcessors') { $limit = [Environment]::ProcessorCount }
    $values = @($Property.ValidRegistryValues | Where-Object { $_ -match '^\d{1,10}$' } |
        ForEach-Object { [long]$_ } | Where-Object { $_ -gt 0 -and $_ -le $limit })
    if ($values.Count -gt 0) { return [string]($values | Measure-Object -Maximum).Maximum }
    $max = 0L; $min = 0L; $base = 0L; $step = 0L
    if (![long]::TryParse([string]$Property.NumericParameterMaxValue,[ref]$max) -or $max -le 0 -or
        ![long]::TryParse([string]$Property.NumericParameterMinValue,[ref]$min) -or
        ![long]::TryParse([string]$Property.NumericParameterStepValue,[ref]$step) -or $step -le 0) { return $null }
    if ($Keyword -ne '*MaxRssProcessors' -and $max -gt $limit) { return $null }
    if (![long]::TryParse([string]$Property.NumericParameterBaseValue,[ref]$base)) { $base = $min }
    $max = [Math]::Min($max,$limit)
    $max -= (($max-$base) % $step)
    if ($max -gt 0 -and (Test-PropertyValue $Property ([string]$max))) { return [string]$max }
    return $null
}

function Get-Properties($Adapter) {
    return @(Get-NetAdapterAdvancedProperty -Name ([WildcardPattern]::Escape([string]$Adapter.Name)) -AllProperties -ErrorAction Stop |
        Where-Object Name -CEQ ([string]$Adapter.Name))
}
function Get-Current($Property) {
    $values = @($Property.RegistryValue)
    if ($values.Count -ne 1 -or [string]$values[0] -notmatch '^\d{1,10}$') { return $null }
    return [string]$values[0]
}
function Set-PropertyValue($Property, [string]$Value) {
    Set-NetAdapterAdvancedProperty -InputObject $Property -RegistryValue @($Value) -NoRestart -Confirm:$false -ErrorAction Stop
}

# Only this machine-wide, administrator-owned key is used. No user-supplied paths,
# scripts or command text are read by the elevated process.
function Open-Backup {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    try { return $base.CreateSubKey('SOFTWARE\DepthStrap\NetworkAdapterTuning') }
    finally { $base.Dispose() }
}
function Get-Records($Store) {
    return @($Store.GetValueNames() | Where-Object { $_ -notin @('Report','PowerPlan') } | ForEach-Object {
        $record = [string]$Store.GetValue($_)
        if ($record.Length -gt 4096) { throw 'Oversized tuning backup.' }
        $item = $record | ConvertFrom-Json
        $guid = [guid]::Empty
        if (![guid]::TryParse([string]$item.Adapter,[ref]$guid) -or
            !(Get-TuningPlan 'Extreme').Contains([string]$item.Keyword) -or
            [string]$item.Original -notmatch '^\d{1,10}$' -or [string]$item.Applied -notmatch '^\d{1,10}$' -or
            $_ -cne ($guid.ToString('D') + ':' + $item.Keyword)) { throw 'Invalid tuning backup.' }
        $item
    })
}
function Save-Record($Store,$Record) {
    $Store.SetValue(([guid]$Record.Adapter).ToString('D') + ':' + $Record.Keyword, ($Record | ConvertTo-Json -Compress), [Microsoft.Win32.RegistryValueKind]::String)
    $Store.Flush()
}
function Remove-Record($Store,$Record) { $Store.DeleteValue(([guid]$Record.Adapter).ToString('D') + ':' + $Record.Keyword); $Store.Flush() }

function Invoke-PowerCfg([string[]]$Options) {
    $exe = Join-Path ([Environment]::GetFolderPath('Windows')) 'System32\powercfg.exe'
    $output = & $exe @Options 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Windows rejected this power-plan command.' }
    return ($output -join "`n")
}
function Get-ActivePlan {
    $output = Invoke-PowerCfg @('/getactivescheme')
    $match = [regex]::Match($output,'[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}')
    if (!$match.Success) { throw 'Cannot identify the active Windows power plan.' }
    return ([guid]$match.Value).ToString('D')
}
function Get-PowerRecord($Store) {
    $raw = [string]$Store.GetValue('PowerPlan','')
    if (!$raw) { return $null }
    if ($raw.Length -gt 4096) { throw 'Invalid power-plan backup.' }
    $record = $raw | ConvertFrom-Json
    $original = [guid]::Empty; $created = [guid]::Empty
    if (![guid]::TryParse([string]$record.Original,[ref]$original) -or
        ![guid]::TryParse([string]$record.Created,[ref]$created) -or $original -eq $created) { throw 'Invalid power-plan backup.' }
    return $record
}
function Restore-PowerPlan($Store,$Stats) {
    $record = Get-PowerRecord $Store
    if ($null -eq $record) { return }
    $active = Get-ActivePlan
    if ($active -eq $record.Created) {
        $null = Invoke-PowerCfg @('/setactive',[string]$record.Original)
        if ((Get-ActivePlan) -ne $record.Original) { throw 'Power-plan restore verification failed.' }
        $Stats.WindowsRestored++
    } else { $Stats.Preserved++ }
    $list = Invoke-PowerCfg @('/list')
    if ($list -match [regex]::Escape([string]$record.Created)) { $null = Invoke-PowerCfg @('/delete',[string]$record.Created) }
    $Store.DeleteValue('PowerPlan'); $Store.Flush()
}
function Apply-PowerPlan([string]$Profile,$Store,$Stats) {
    $original = Get-ActivePlan
    $created = [guid]::NewGuid().ToString('D')
    $template = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' # High performance
    if ($Profile -eq 'Extreme') { $template = 'e9a42b02-d5df-448d-aa00-03f14749eb61' } # Ultimate Performance
    # Store the original before creating or activating anything; configure only our copy.
    $Store.SetValue('PowerPlan',(@{Original=$original; Created=$created} | ConvertTo-Json -Compress),[Microsoft.Win32.RegistryValueKind]::String)
    $Store.Flush()
    $null = Invoke-PowerCfg @('/duplicatescheme',$template,$created)
    $null = Invoke-PowerCfg @('/changename',$created,('DepthStrap ' + $Profile))
    if ($Profile -eq 'Extreme') {
        foreach ($setting in @(@('PROCTHROTTLEMIN','100'),@('PROCTHROTTLEMAX','100'),@('CPMINCORES','100'),
            @('CPMINCORES1','100'),@('PERFEPP','0'),@('PERFEPP1','0'))) {
            try { $null = Invoke-PowerCfg @('/setacvalueindex',$created,'SUB_PROCESSOR',$setting[0],$setting[1]); $Stats.CpuSettings++ }
            catch { $Stats.Unsupported++ }
        }
    }
    $null = Invoke-PowerCfg @('/setactive',$created)
    if ((Get-ActivePlan) -ne $created) { throw 'Power-plan activation verification failed.' }
    $Stats.WindowsApplied++
}

function Restore-Settings($Store,$Adapters,$Stats) {
    foreach ($record in (Get-Records $Store)) {
        try {
            $adapter = @($Adapters | Where-Object { ([guid]$_.InterfaceGuid) -eq ([guid]$record.Adapter) })
            if ($adapter.Count -ne 1) { $Stats.Unavailable++; continue }
            $property = @(Get-Properties $adapter[0] | Where-Object RegistryKeyword -EQ $record.Keyword)
            if ($property.Count -ne 1 -or !(Test-PropertyValue $property[0] $record.Original)) { $Stats.Unavailable++; continue }
            $current = Get-Current $property[0]
            if ($null -eq $current) { $Stats.Unavailable++; continue }
            if ($current -eq $record.Original) { Remove-Record $Store $record; continue }
            if ($current -ne $record.Applied) { $Stats.Preserved++; Remove-Record $Store $record; continue }
            Set-PropertyValue $property[0] $record.Original
            $verify = @(Get-Properties $adapter[0] | Where-Object RegistryKeyword -EQ $record.Keyword)
            if ($verify.Count -ne 1 -or (Get-Current $verify[0]) -ne $record.Original) { throw 'Restore verification failed.' }
            $Stats.Restored++; Remove-Record $Store $record
        } catch { $Stats.Failed++ }
    }
}

function Apply-Settings($Profile,$Store,$Adapters,$Stats) {
    $plan = Get-TuningPlan $Profile
    foreach ($adapter in @($Adapters | Where-Object Status -EQ 'Up')) {
        try { $properties = Get-Properties $adapter } catch { $Stats.Failed++; continue }
        foreach ($keyword in $plan.Keys) {
            $property = @($properties | Where-Object RegistryKeyword -EQ $keyword)
            if ($property.Count -ne 1) { $Stats.Unsupported++; continue }
            $target = Get-TargetValue $property[0] $plan[$keyword] $keyword
            $original = Get-Current $property[0]
            if ($null -eq $target -or $null -eq $original -or !(Test-PropertyValue $property[0] $original)) { $Stats.Unsupported++; continue }
            if ($original -eq $target) { $Stats.Unchanged++; continue }
            try {
                $record = [pscustomobject]@{ Adapter=([guid]$adapter.InterfaceGuid).ToString('D'); Keyword=$keyword; Original=$original; Applied=$target }
                Save-Record $Store $record # A failed backup prevents the setting change.
                Set-PropertyValue $property[0] $target
                $verify = @(Get-Properties $adapter | Where-Object RegistryKeyword -EQ $keyword)
                if ($verify.Count -ne 1 -or (Get-Current $verify[0]) -ne $target) { throw 'Setting verification failed.' }
                $Stats.Changed++
            } catch { $Stats.Failed++ }
        }
    }
}

$store = $null; $gate = $null; $ownsGate = $false
try {
    Import-Module NetAdapter -ErrorAction Stop
    $adapters = @(Get-NetAdapter -Physical -ErrorAction Stop)
    if ($Mode -eq 'Preview') {
        $preview = @()
        foreach ($adapter in @($adapters | Where-Object Status -EQ 'Up')) {
            $properties = Get-Properties $adapter
            foreach ($profile in @('Balanced','Extreme')) {
                $plan = Get-TuningPlan $profile; $details = @()
                foreach ($keyword in $plan.Keys) {
                    $property = @($properties | Where-Object RegistryKeyword -EQ $keyword)
                    if ($property.Count -ne 1) { continue }
                    $target = Get-TargetValue $property[0] $plan[$keyword] $keyword
                    if ($null -ne $target) { $details += "$keyword : $(Get-Current $property[0]) -> $target" }
                }
                $preview += [pscustomobject]@{Adapter=[string]$adapter.Name; LinkSpeed=[string]$adapter.LinkSpeed; Profile=$profile; Settings=$details}
            }
        }
        ConvertTo-Json -InputObject $preview -Depth 5 -Compress
        exit 0
    }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator permission is required.' }
    $gate = New-Object Threading.Mutex($false,'Global\DepthStrap-NetworkAdapterTuning')
    try { $ownsGate = $gate.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsGate = $true }
    if (!$ownsGate) { throw 'Another tuning operation is running.' }
    $store = Open-Backup
    $stats = [ordered]@{Changed=0; Restored=0; Unchanged=0; Unsupported=0; Unavailable=0; Preserved=0; Failed=0; WindowsApplied=0; WindowsRestored=0; WindowsFailed=0; CpuSettings=0}
    try { Restore-PowerPlan $store $stats } catch { $stats.WindowsFailed++ }
    Restore-Settings $store $adapters $stats
    # Avoid replacing an original backup when switching profiles could not restore it.
    if ($Mode -ne 'Restore' -and $stats.Failed -eq 0 -and $stats.Unavailable -eq 0 -and $stats.WindowsFailed -eq 0) {
        try { Apply-PowerPlan $Mode $store $stats } catch { $stats.WindowsFailed++ }
        Apply-Settings $Mode $store $adapters $stats
    }
    $store.SetValue('Report',($stats | ConvertTo-Json -Compress),[Microsoft.Win32.RegistryValueKind]::String)
    $store.Flush()
    if ($stats.Failed -gt 0 -or $stats.Unavailable -gt 0 -or $stats.WindowsFailed -gt 0) { exit 3 }
    exit 0
} catch { exit 1 }
finally {
    if ($null -ne $store) { $store.Dispose() }
    if ($ownsGate) { $gate.ReleaseMutex() }
    if ($null -ne $gate) { $gate.Dispose() }
}
