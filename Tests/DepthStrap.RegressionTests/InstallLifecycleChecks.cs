using System.IO;
using Bloxstrap;
using Bloxstrap.Models.Manifest;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Utility;

internal static class InstallLifecycleChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var packages = Enumerable.Range(0, 4).Select(i => new Package { Name = $"fixture-{i}.zip",
            Signature = new string('a', 32), PackedSize = 1500000000, Size = 1800000000 }).ToArray();
        check(InstallPackagePipeline.PackedBytes(packages) == 6000000000L &&
            InstallPackagePipeline.RequiredBytes(packages) == 13200000000L &&
            InstallPackagePipeline.EstimatedKilobytes(packages) == 12890625,
            "Multi-gigabyte install, download and registry size calculations do not overflow 32-bit byte totals");
        check(InstallPackagePipeline.RequiredFreeBytes(packages, _ => 1500000000) == 7200000000L &&
            InstallPackagePipeline.RequiredFreeBytes(packages, _ => 500000000) == 11200000000L,
            "Disk estimates account for cached bytes and replacement downloads without treating truncated caches as complete");
        var distribution = new JsonManager<DistributionState>("FixtureInstallState");
        distribution.TrySave();
        var savedState = App.State.Prop;
        string stateFile = App.State.FileLocation;
        bool existed = File.Exists(stateFile);
        string savedText = existed ? File.ReadAllText(stateFile) : "";
        var attributes = existed ? File.GetAttributes(stateFile) : FileAttributes.Normal;
        try
        {
            App.State.Prop = new State(); App.State.TrySave();
            File.SetAttributes(distribution.FileLocation, FileAttributes.ReadOnly);
            bool rejected = false;
            try { InstallPackagePipeline.BeginRecovery(distribution); } catch (IOException) { rejected = true; }
            check(rejected && !distribution.Prop.InstallationPending, "An unwritable recovery marker prevents destructive install work and restores the previous in-memory state");
            File.SetAttributes(distribution.FileLocation, FileAttributes.Normal);
            InstallPackagePipeline.BeginRecovery(distribution);
            check(distribution.Prop.InstallationPending && File.ReadAllText(distribution.FileLocation).Contains("\"InstallationPending\": true"),
                "Installation recovery is persisted before any build is replaced");
            rejected = false;
            File.SetAttributes(distribution.FileLocation, FileAttributes.ReadOnly);
            try { InstallPackagePipeline.CompleteRecovery(distribution); } catch (IOException) { rejected = true; }
            check(rejected && distribution.Prop.InstallationPending, "Failed distribution-state writes cannot clear install recovery or report success");
            File.SetAttributes(distribution.FileLocation, FileAttributes.Normal);
            File.SetAttributes(stateFile, FileAttributes.ReadOnly);
            rejected = false;
            try { InstallPackagePipeline.CompleteRecovery(distribution); } catch (IOException) { rejected = true; }
            check(rejected && distribution.Prop.InstallationPending && File.ReadAllText(distribution.FileLocation).Contains("\"InstallationPending\": true"),
                "A failed completion save preserves a recoverable installation on disk and in memory");
            File.SetAttributes(stateFile, FileAttributes.Normal);
            InstallPackagePipeline.CompleteRecovery(distribution);
            check(!distribution.Prop.InstallationPending && File.ReadAllText(distribution.FileLocation).Contains("\"InstallationPending\": false"),
                "Only successful distribution and recovery saves complete an installation");
        }
        finally
        {
            File.SetAttributes(stateFile, FileAttributes.Normal);
            if (existed) { File.WriteAllText(stateFile, savedText); File.SetAttributes(stateFile, attributes); }
            else File.Delete(stateFile);
            App.State.Prop = savedState;
            File.SetAttributes(distribution.FileLocation, FileAttributes.Normal);
            File.Delete(distribution.FileLocation);
        }

        using (var gate = new ManualResetEventSlim())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failedDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var original = new IOException("Fixture download failure");
            int downloads = 0;
            Task run = InstallPackagePipeline.RunAsync(packages, async _ =>
            {
                if (++downloads != 2) return;
                await started.Task; failedDownload.TrySetResult(); throw original;
            }, _ => { started.TrySetResult(); gate.Wait(); throw new IOException("Fixture secondary extraction failure"); }, CancellationToken.None);
            try
            {
                HistoryChecks.Wait(failedDownload.Task);
                check(!run.IsCompleted, "A failed download waits for active extraction work before returning control to cleanup");
            }
            finally { gate.Set(); }
            Exception? result = null;
            try { HistoryChecks.Wait(run); } catch (IOException ex) { result = ex; }
            check(ReferenceEquals(result, original) && downloads == 2,
                "Secondary extraction errors cannot hide the initiating download failure or start later transfers");
        }
        using (var gate = new ManualResetEventSlim())
        {
            var twoActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0, completed = 0, excessive = 0;
            Task run = InstallPackagePipeline.RunAsync(packages, _ => Task.CompletedTask, _ =>
            {
                int current = Interlocked.Increment(ref active);
                if (current == 2) twoActive.TrySetResult();
                if (current > 2) Interlocked.Increment(ref excessive);
                gate.Wait(); Interlocked.Decrement(ref active); Interlocked.Increment(ref completed);
            }, CancellationToken.None);
            try { HistoryChecks.Wait(twoActive.Task); check(active == 2 && !run.IsCompleted, "Package extraction overlaps downloads with bounded concurrency"); }
            finally { gate.Set(); }
            HistoryChecks.Wait(run);
            check(completed == 4 && active == 0 && excessive == 0, "All package extractions finish without exceeding two simultaneous workers");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var gate = new ManualResetEventSlim())
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int downloads = 0, active = 0;
            Task run = InstallPackagePipeline.RunAsync(packages, async _ =>
            {
                if (++downloads == 2) { await started.Task; cancellation.Cancel(); }
            }, _ => { Interlocked.Increment(ref active); started.TrySetResult(); gate.Wait(); Interlocked.Decrement(ref active); }, cancellation.Token);
            try { HistoryChecks.Wait(started.Task); check(active == 1 && !run.IsCompleted, "Cancellation retains ownership of active extraction work"); }
            finally { gate.Set(); }
            bool cancelled = false;
            try { HistoryChecks.Wait(run); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && active == 0 && downloads == 2, "Cancelled installs drain extraction workers before allowing cleanup and never schedule later packages");
        }
    }
}
