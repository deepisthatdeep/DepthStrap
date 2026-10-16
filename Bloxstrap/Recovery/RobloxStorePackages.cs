namespace DepthStrap.Recovery;

internal sealed record RobloxStoreInventory(string[] Installed, string[] Provisioned);
internal interface IRobloxStorePackages
{
    Task<RobloxStoreInventory> InventoryAsync();
    Task RemoveAsync(RobloxStoreInventory approved);
}

/// <summary>Uses Windows package servicing. Never deletes protected WindowsApps files manually.</summary>
internal sealed class RobloxStorePackages : IRobloxStorePackages
{
    internal static bool ValidName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name,
        @"\AROBLOXCORPORATION\.ROBLOX_[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+_(?:x64|x86|arm64|neutral)_[A-Za-z0-9.~\-]*_55nm5eh3cm0pr\z",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal const string Script = """
        param([string]$Approved)
        $ErrorActionPreference='Stop'
        $ProgressPreference='SilentlyContinue'
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        function Inventory {
            $installed=@(Get-AppxPackage -AllUsers -Name 'ROBLOXCORPORATION.ROBLOX' -PackageTypeFilter Main,Bundle |
                Where-Object {$_.Name -ceq 'ROBLOXCORPORATION.ROBLOX' -and $_.PublisherId -eq '55nm5eh3cm0pr'} |
                Select-Object -ExpandProperty PackageFullName -Unique | Sort-Object)
            $provisioned=@(Get-AppxProvisionedPackage -Online | Where-Object {
                $_.DisplayName -ceq 'ROBLOXCORPORATION.ROBLOX' -and $_.PackageName -match '_55nm5eh3cm0pr$'
            } | Select-Object -ExpandProperty PackageName -Unique | Sort-Object)
            return @{Installed=$installed;Provisioned=$provisioned}
        }
        $before=Inventory
        if ($Approved) {
            $approvedData=([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Approved)) | ConvertFrom-Json)
            if ([string]::Join('|', [string[]]@($approvedData.Installed | Sort-Object)) -cne [string]::Join('|', [string[]]@($before.Installed | Sort-Object)) -or
                [string]::Join('|', [string[]]@($approvedData.Provisioned | Sort-Object)) -cne [string]::Join('|', [string[]]@($before.Provisioned | Sort-Object))) {throw 'Package inventory changed; review a new preview.'}
            # Remove bundles before main packages, rechecking each after servicing.
            foreach ($package in @($before.Installed | Sort-Object {if ($_ -match '_neutral_~_') {0} else {1}})) {
                if (@((Inventory).Installed) -contains $package) { Remove-AppxPackage -Package $package -AllUsers -Confirm:$false }
            }
            foreach ($package in $before.Provisioned) { Remove-AppxProvisionedPackage -Online -PackageName $package -AllUsers | Out-Null }
        }
        Inventory | ConvertTo-Json -Compress -Depth 4
        """;
    internal static ProcessStartInfo StartInfo(RobloxStoreInventory? approved)
    {
        if (approved is not null && approved.Installed.Concat(approved.Provisioned).Any(name => !ValidName(name)))
            throw new ArgumentException("Unexpected Roblox package identity.");
        string data = approved is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(approved)));
        string invocation = "& ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(Script)) + "')))) -Approved '" + data + "'";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation)) }) start.ArgumentList.Add(arg);
        return start;
    }
    public Task<RobloxStoreInventory> InventoryAsync() => RunAsync(null);
    public async Task RemoveAsync(RobloxStoreInventory approved)
    {
        var remaining = await RunAsync(approved).ConfigureAwait(false);
        if (remaining.Installed.Length != 0 || remaining.Provisioned.Length != 0) throw new IOException("Windows still reports a Roblox Store package. Uninstall is incomplete.");
    }
    private static async Task<RobloxStoreInventory> RunAsync(RobloxStoreInventory? approved)
    {
        using var worker = Process.Start(StartInfo(approved)) ?? throw new IOException("Windows package servicing could not start.");
        var output = worker.StandardOutput.ReadToEndAsync(); var error = worker.StandardError.ReadToEndAsync();
        if (approved is null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { if (!worker.HasExited) worker.Kill(); await worker.WaitForExitAsync().ConfigureAwait(false); await Task.WhenAll(output, error).ConfigureAwait(false); throw new IOException("Windows package inventory timed out. No removal was requested."); }
        }
        else await worker.WaitForExitAsync().ConfigureAwait(false);
        await error.ConfigureAwait(false);
        if (worker.ExitCode != 0) throw new IOException("Windows package servicing failed. Uninstall cannot be reported as complete.");
        var result = JsonSerializer.Deserialize<RobloxStoreInventory>(await output.ConfigureAwait(false)) ?? throw new IOException("Windows package inventory is unavailable.");
        if (result.Installed is null || result.Provisioned is null || result.Installed.Concat(result.Provisioned).Any(name => !ValidName(name))) throw new IOException("Unexpected Windows package inventory.");
        return result;
    }
    internal static bool Same(RobloxStoreInventory left, RobloxStoreInventory right) =>
        left.Installed.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(right.Installed.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
        && left.Provisioned.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(right.Provisioned.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
}
