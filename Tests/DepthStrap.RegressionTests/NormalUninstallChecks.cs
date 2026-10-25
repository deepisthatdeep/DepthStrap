using System.Diagnostics;
using System.IO;
using System.Text;
using Bloxstrap.Enums;
using Bloxstrap.Utility;
using Microsoft.Win32;

internal static class NormalUninstallChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        string fixture = Path.Combine(root, "normal-uninstall-" + Guid.NewGuid().ToString("N"));
        string desktop = Path.Combine(fixture, "Desktop");
        Directory.CreateDirectory(desktop);
        string application = Path.Combine(fixture, "DepthStrap.exe");
        File.WriteAllText(application, "fixture executable");
        string malformed = Path.Combine(desktop, "00-malformed.lnk");
        string owned = Path.Combine(desktop, "01-owned.LNK");
        string unrelated = Path.Combine(desktop, "02-unrelated.lnk");
        string suffix = Path.Combine(desktop, "03-not-a-shortcut-lnk");
        File.WriteAllBytes(malformed, new byte[76]);
        ShellLink.Shortcut.CreateShortcut(application, "", application, 0).WriteToFile(owned);
        ShellLink.Shortcut.CreateShortcut(Path.Combine(fixture, "Other.exe"), "", application, 0).WriteToFile(unrelated);
        File.WriteAllText(suffix, "not a shortcut");

        var result = new NormalUninstallResult();
        NormalUninstallCleanup.RemoveDesktopShortcuts(desktop, application, result);
        check(!File.Exists(owned), "A malformed desktop shortcut cannot block a later owned uppercase .LNK from cleanup");
        check(File.Exists(malformed) && File.Exists(unrelated) && File.Exists(suffix),
            "Malformed, unrelated and non-extension shortcut lookalikes are preserved");
        check(result.UncheckedShortcuts == 1 && result.Failures.Count == 0 && !result.Complete && result.Message.Contains("preserved"),
            "Uncertain shortcut ownership is reported without claiming complete removal");

        result = new NormalUninstallResult();
        NormalUninstallCleanup.Run(result, "Absent cursors", () => NormalUninstallCleanup.DeleteDirectory(Path.Combine(fixture, "missing-cursors")));
        check(result.Complete, "An absent optional uninstall folder is already complete");
        string protectedDirectory = Path.Combine(fixture, "protected");
        Directory.CreateDirectory(protectedDirectory);
        string protectedFile = Path.Combine(protectedDirectory, "keep.txt");
        File.WriteAllText(protectedFile, "retained");
        File.SetAttributes(protectedFile, FileAttributes.ReadOnly);
        try
        {
            NormalUninstallCleanup.Run(result, "Denied folder", () => NormalUninstallCleanup.DeleteDirectory(protectedDirectory));
            check(File.Exists(protectedFile) && result.Failures.Count == 1 && !result.Complete &&
                result.ExitCode == ErrorCode.ERROR_INSTALL_FAILURE && result.Message.Contains("partly removed"),
                "A failed owned-folder cleanup retains its file and reports partial removal with a failure exit code");
            string later = Path.Combine(fixture, "later.txt");
            File.WriteAllText(later, "delete");
            NormalUninstallCleanup.Run(result, "Later operation", () => File.Delete(later));
            check(!File.Exists(later) && result.Failures.Count == 1, "A failed cleanup does not prevent later safe cleanup actions");
        }
        finally { File.SetAttributes(protectedFile, FileAttributes.Normal); }

        result = new NormalUninstallResult { ExecutableDeletionPending = true };
        check(!result.Complete && result.Message.Contains("has not been verified") && !result.Message.Contains("successfully uninstalled"),
            "Starting delayed executable removal cannot certify completed uninstall");
        check(new NormalUninstallResult { Cancelled = true }.ExitCode == ErrorCode.ERROR_CANCELLED,
            "Cancelling uninstall preserves the cancelled outcome");

        string deletionDirectory = Path.Combine(fixture, "literal & ' path");
        Directory.CreateDirectory(deletionDirectory);
        string deletionFile = Path.Combine(deletionDirectory, "DepthStrap.exe");
        string retained = Path.Combine(deletionDirectory, "keep.txt");
        File.WriteAllText(deletionFile, "fixture executable");
        File.WriteAllText(retained, "unrelated fixture");
        string uninstallKey = @"Software\DepthStrap.RegressionTests\NormalUninstall-" + Guid.NewGuid().ToString("N");
        using (var key = Registry.CurrentUser.CreateSubKey(uninstallKey)) key.SetValue("Fixture", "disposable");
        try
        {
            var info = NormalUninstallCleanup.DeferredDeleteStartInfo(deletionFile, deletionDirectory, int.MaxValue, uninstallKey);
            string script = Encoding.Unicode.GetString(Convert.FromBase64String(info.Arguments.Split(' ')[^1]));
            check(info.CreateNoWindow && !info.UseShellExecute && !script.Contains("-Recurse") && script.Contains("-LiteralPath"),
                "Deferred removal is hidden and uses literal exact paths without recursive or wildcard deletion");
            using var remover = Process.Start(info)!;
            check(remover.WaitForExit(10000) && remover.ExitCode == 0 && !File.Exists(deletionFile) && File.ReadAllText(retained) == "unrelated fixture",
                "Deferred removal deletes only the disposable executable and preserves unrelated files in a quoted path");
            using var removedKey = Registry.CurrentUser.OpenSubKey(uninstallKey);
            check(removedKey is null, "Successful deferred executable removal also removes its disposable uninstall registration");

            using (var key = Registry.CurrentUser.CreateSubKey(uninstallKey)) key.SetValue("Fixture", "disposable");
            File.WriteAllText(deletionFile, "blocked fixture executable");
            File.SetAttributes(deletionFile, FileAttributes.ReadOnly);
            using var blocked = Process.Start(info)!;
            check(blocked.WaitForExit(10000) && blocked.ExitCode != 0 && File.Exists(deletionFile),
                "Deferred executable removal reports a blocked disposable executable as failure");
            using var preservedKey = Registry.CurrentUser.OpenSubKey(uninstallKey);
            check(preservedKey is not null, "Failed deferred executable removal preserves its uninstall registration for retry");
        }
        finally
        {
            if (File.Exists(deletionFile)) File.SetAttributes(deletionFile, FileAttributes.Normal);
            Registry.CurrentUser.DeleteSubKey(uninstallKey, false);
        }
    }
}
