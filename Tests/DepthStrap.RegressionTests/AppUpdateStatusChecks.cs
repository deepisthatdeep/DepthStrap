using System.IO;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Utility;

internal static class AppUpdateStatusChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string directory = Path.Combine(Paths.Base, "update-status");
        Directory.CreateDirectory(directory);
        string installed = Path.Combine(directory, "installed.exe");
        string incoming = Path.Combine(directory, "incoming.exe");
        File.WriteAllText(installed, "installed fixture");
        File.WriteAllText(incoming, "incoming fixture");
        check(AppUpdater.DescribeReplacementFailure(installed, new IOException("locked", unchecked((int)0x80070020)))
            .Contains("busy"), "Update diagnostics recognize Windows sharing violations");
        check(AppUpdater.DescribeReplacementFailure(installed, new IOException("locked", unchecked((int)0x80070021)))
            .Contains("busy"), "Update diagnostics recognize Windows lock violations");
        check(AppUpdater.DescribeReplacementFailure(installed, new UnauthorizedAccessException())
            .Contains("cause is not confirmed"), "Access-denied diagnostics do not invent an antivirus or ACL cause");
        check(AppUpdater.DescribeReplacementFailure(installed, new IOException("disk", unchecked((int)0x80070070)))
            .Contains("112"), "Other replacement failures retain the Windows error code");
        File.SetAttributes(installed, FileAttributes.ReadOnly);
        try
        {
            check(AppUpdater.DescribeReplacementFailure(installed, new UnauthorizedAccessException()).Contains("read-only"),
                "Read-only replacement failures give a specific diagnostic");
            bool refused = false;
            try { AppUpdater.ReplaceExecutable(incoming, installed); }
            catch (UnauthorizedAccessException) { refused = true; }
            check(refused && File.ReadAllText(installed) == "installed fixture" &&
                (File.GetAttributes(installed) & FileAttributes.ReadOnly) != 0 && Directory.GetFiles(directory, "*.new").Length == 0,
                "Read-only update preserves installed bytes, attributes and clean staging");
        }
        finally { File.SetAttributes(installed, FileAttributes.Normal); }

        var previous = App.State.Prop.DeferredAppUpdate;
        try
        {
            AppUpdater.RecordDeferredUpdate("1.2.4", "1.2.5", "Fixture: installed executable is busy.");
            string status = AppUpdater.UpdateStatus("1.2.4", App.State.Prop.DeferredAppUpdate);
            check(status.Contains("Update deferred") && status.Contains("1.2.4") && status.Contains("1.2.5") && status.Contains("busy"),
                "Deferred update status exposes installed/incoming versions and reason");
            var restored = JsonSerializer.Deserialize<State>(JsonSerializer.Serialize(App.State.Prop))!;
            check(restored.DeferredAppUpdate == App.State.Prop.DeferredAppUpdate,
                "Deferred update status survives state serialization for resumed startup");
            check(!AppUpdater.UpdateStatus("1.2.5", restored.DeferredAppUpdate).Contains("deferred") &&
                !AppUpdater.UpdateStatus("1.3.0", restored.DeferredAppUpdate).Contains("deferred"),
                "Installed or superseded updates do not show stale deferred warnings");
            check(AppUpdater.UpdateStatus("1.2.4", new("1.2.4", null, "Network check failed.")).Contains("not determined"),
                "Failed release lookup does not invent an incoming version");
            AppUpdater.ClearDeferredUpdate();
            check(App.State.Prop.DeferredAppUpdate is null && AppUpdater.UpdateStatus("1.2.5", null).Contains("1.2.5"),
                "Completed update clears its warning while preserving installed version display");
        }
        finally { App.State.Prop.DeferredAppUpdate = previous; }
    }
}
