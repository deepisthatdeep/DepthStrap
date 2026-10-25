namespace Bloxstrap.Utility;

internal sealed class NormalUninstallResult
{
    internal List<string> Failures { get; } = new();
    internal int UncheckedShortcuts { get; set; }
    internal bool ExecutableDeletionPending { get; set; }
    internal bool Cancelled { get; init; }
    internal bool Complete => !Cancelled && Failures.Count == 0 && UncheckedShortcuts == 0 && !ExecutableDeletionPending;
    internal ErrorCode ExitCode => Cancelled ? ErrorCode.ERROR_CANCELLED :
        Failures.Count > 0 ? ErrorCode.ERROR_INSTALL_FAILURE : ErrorCode.ERROR_SUCCESS;

    internal string Message
    {
        get
        {
            string message = Failures.Count > 0
                ? "DepthStrap was only partly removed. Close any app using the remaining files, then run the uninstaller again.\n\n" + string.Join("\n", Failures)
                : Complete ? Strings.Bootstrapper_SuccessfullyUninstalled
                : "DepthStrap cleanup finished.";
            if (UncheckedShortcuts > 0)
                message += $"\n\n{UncheckedShortcuts} desktop shortcut(s) could not be checked and were preserved. Remove any remaining DepthStrap shortcut manually.";
            if (ExecutableDeletionPending)
                message += "\n\nThe executable is scheduled for removal after this window closes. Its removal has not been verified.";
            return message + "\n\nSaved data and unrecognized files may remain. Normal uninstall does not perform a complete data wipe.";
        }
    }
}

internal static class NormalUninstallCleanup
{
    internal static void Run(NormalUninstallResult result, string item, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception ex)
        {
            result.Failures.Add($"{item}: {ex.Message}");
            App.Logger.WriteException("Installer::DoUninstall::" + item, ex);
        }
    }

    internal static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, true); }
        catch (DirectoryNotFoundException) { }
    }

    internal static void RemoveDesktopShortcuts(string directory, string application, NormalUninstallResult result)
    {
        string[] files;
        try { files = Directory.GetFiles(directory); }
        catch (DirectoryNotFoundException) { return; }
        foreach (string file in files)
        {
            if (!Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
            string? target;
            try { target = ShellLink.Shortcut.ReadFromFile(file).ExtraData.EnvironmentVariableDataBlock?.TargetUnicode; }
            catch (FileNotFoundException) { continue; }
            catch (Exception ex)
            {
                // Unknown ownership must never turn a malformed shortcut into a deletion target.
                result.UncheckedShortcuts++;
                App.Logger.WriteException("Installer::DoUninstall::InspectShortcut", ex);
                continue;
            }
            if (string.Equals(target, application, StringComparison.OrdinalIgnoreCase))
                Run(result, "Desktop shortcut", () => File.Delete(file));
        }
    }

    internal static ProcessStartInfo DeferredDeleteStartInfo(string application, string directory, int processId, string uninstallKey)
    {
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        // Delete only the exact executable; never infer ownership from the number of files in a folder.
        string script = "$ErrorActionPreference = 'Stop'; " +
            $"Wait-Process -Id {processId} -Timeout 120 -ErrorAction SilentlyContinue; " +
            $"if (Get-Process -Id {processId} -ErrorAction SilentlyContinue) {{ exit 1 }}; " +
            $"Remove-Item -LiteralPath {Quote(application)}; " +
            $"([Microsoft.Win32.Registry]::CurrentUser).DeleteSubKey({Quote(uninstallKey)}, $false); " +
            $"if (-not (Get-ChildItem -LiteralPath {Quote(directory)} -Force | Select-Object -First 1)) {{ Remove-Item -LiteralPath {Quote(directory)} }}";
        return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }
}
