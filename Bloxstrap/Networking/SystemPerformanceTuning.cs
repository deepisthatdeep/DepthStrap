using System.ComponentModel;
using Microsoft.Win32;

namespace Bloxstrap.Networking;

internal enum SystemTuningMode { Balanced, Extreme, Restore, Preview }

internal static class SystemPerformanceTuning
{
    internal static bool RestartRecommended { get; private set; }
    internal static ProcessStartInfo RestartStartInfo()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "shutdown.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "/r", "/t", "0" }) start.ArgumentList.Add(argument);
        return start; // Do not force-close applications or schedule a restart during installation.
    }
    internal static bool ConfirmRestart()
    {
        if (Frontend.ShowMessageBox("DepthStrap setup is complete. Save your work and close other applications before restarting. Restart your PC now?",
            System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
            return false;
        try { using var process = Process.Start(RestartStartInfo()); return process is not null; }
        catch (Exception ex)
        {
            App.Logger.WriteException("SystemPerformanceTuning::Restart", ex);
            Frontend.ShowMessageBox("Windows could not start the restart. Use Start → Power → Restart to apply the changes.", System.Windows.MessageBoxImage.Error);
            return false;
        }
    }
    internal static string Script
    {
        get
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream("Bloxstrap.Resources.NetworkAdapterTuning.ps1")
                ?? throw new InvalidDataException("The bundled tuning script is missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
    internal static ProcessStartInfo StartInfo(SystemTuningMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Script);
            gzip.Write(bytes);
        }
        // Keep ShellExecute arguments below Windows' command-line limit without staging
        // an elevated script in a user-writable directory.
        string script = "$m=[IO.MemoryStream]::new([Convert]::FromBase64String('" + Convert.ToBase64String(compressed.ToArray()) + "'));" +
            "$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);" +
            "$r=[IO.StreamReader]::new($g); & ([scriptblock]::Create($r.ReadToEnd())) -Mode '" + mode + "'";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = mode != SystemTuningMode.Preview,
            Verb = mode == SystemTuningMode.Preview ? "" : "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            RedirectStandardOutput = mode == SystemTuningMode.Preview,
            RedirectStandardError = mode == SystemTuningMode.Preview
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return start;
    }
    internal sealed record Preview(string Adapter, string LinkSpeed, string Profile, string[] Settings);
    internal static async Task<Preview[]> InspectAsync()
    {
        using var process = Process.Start(StartInfo(SystemTuningMode.Preview)) ?? throw new IOException("Unable to inspect adapters.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(); throw new IOException("Adapter inspection timed out."); }
        await errors;
        if (process.ExitCode != 0) throw new IOException("Windows could not inspect the adapter's supported settings.");
        return JsonSerializer.Deserialize<Preview[]>(await output) ?? Array.Empty<Preview>();
    }
    internal static async Task<string> ApplyAsync(SystemTuningMode mode)
    {
        if (mode == SystemTuningMode.Preview || !Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (InstallPackagePipeline.HasProcess("RobloxPlayerBeta") || InstallPackagePipeline.HasProcess("RobloxStudioBeta"))
            return "Close Roblox and Studio before changing Windows or adapter settings.";
        try
        {
            using var process = Process.Start(StartInfo(mode)) ?? throw new IOException("Unable to start the tuning step.");
            await process.WaitForExitAsync(); // Never kill a privileged worker while it owns a backup/change transaction.
            if (process.ExitCode is not (0 or 3)) return "The tuning step could not finish. Any saved backups remain available through Restore. Windows may not support this plan or may block adapter access.";
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\DepthStrap\NetworkAdapterTuning");
            var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(key?.GetValue("Report") as string ?? "{}");
            int Count(string name) => counts?.TryGetValue(name, out int value) == true ? Math.Max(0, value) : 0;
            RestartRecommended |= Count("Changed") + Count("Restored") + Count("WindowsApplied") + Count("WindowsRestored") > 0;
            return $"Adapter changes: {Count("Changed")}; restored: {Count("Restored")}; already set: {Count("Unchanged")}; unsupported: {Count("Unsupported")}. " +
                $"Windows plans applied: {Count("WindowsApplied")}; restored: {Count("WindowsRestored")}; CPU options configured: {Count("CpuSettings")}. " +
                $"Failed: {Count("Failed") + Count("WindowsFailed")}; unavailable: {Count("Unavailable")}; later user changes preserved: {Count("Preserved")}.\n\n" +
                "Restart Windows to activate staged adapter settings. No adapter was forcibly restarted. After reboot, use Reset Network to compare routing again. Restore is available here; missing-device backups are retained.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Administrator approval was cancelled. No tuning commands ran."; }
        catch (Exception ex) { App.Logger.WriteException("SystemPerformanceTuning", ex); return "The tuning step could not finish. Saved backups are retained for Restore."; }
    }
}
