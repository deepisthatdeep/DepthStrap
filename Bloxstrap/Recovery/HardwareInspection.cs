namespace DepthStrap.Recovery;

internal sealed record HardwareInfo(string Manufacturer, string Model, string MaskedUuid, string MaskedBoardSerial, string[] Monitors);
internal static class HardwareInspection
{
    internal const string Script = """
        $ErrorActionPreference='Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        function Mask([string]$value) { if ([string]::IsNullOrWhiteSpace($value)) { return 'Unavailable' }; if ($value.Length -le 4) { return '****' }; return '****'+$value.Substring($value.Length-4) }
        $system = Get-CimInstance Win32_ComputerSystemProduct
        $board = Get-CimInstance Win32_BaseBoard | Select-Object -First 1
        $monitors = @()
        try { $monitors = @(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorID | ForEach-Object { -join ($_.UserFriendlyName | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) }) } catch { }
        [pscustomobject]@{Manufacturer=[string]$system.Vendor;Model=[string]$system.Name;MaskedUuid=(Mask $system.UUID);MaskedBoardSerial=(Mask $board.SerialNumber);Monitors=@($monitors)} | ConvertTo-Json -Compress
        """;
    internal static ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
          StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            info.ArgumentList.Add(argument);
        return info;
    }
    internal static async Task<HardwareInfo> InspectAsync(CancellationToken token = default)
    {
        using var process = Process.Start(StartInfo()) ?? throw new IOException("Hardware inspection could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(budget.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            token.ThrowIfCancellationRequested();
            throw new IOException("Hardware inspection timed out.");
        }
        await errors;
        if (process.ExitCode != 0) throw new IOException("Windows hardware information is unavailable.");
        var result = JsonSerializer.Deserialize<HardwareInfo>(await output) ?? throw new IOException("Hardware information was empty.");
        if (!IsMasked(result.MaskedUuid) || !IsMasked(result.MaskedBoardSerial) || result.Monitors is null)
            throw new IOException("Hardware information did not pass the privacy check.");
        return result;
    }
    internal static bool IsMasked(string? value) => value == "Unavailable" || (value is not null && value.StartsWith("****", StringComparison.Ordinal) && value.Length <= 8);
}
