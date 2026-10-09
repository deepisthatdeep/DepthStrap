using DepthStrap.Toolkit;

internal static class MacChangeTests
{
    internal static void RunHelperDispatchChecks(Action<bool, string> check)
    {
        var previous = SynchronizationContext.Current;
        var context = new RejectDispatcherPost();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var driver = new FakeDriver { AsyncRead = true };
            var change = new MacChange(driver);
            var apply = DepthStrap.Recovery.MacControls.ExecuteHelperOperationAsync(new(false, driver.Id, "021122334455"), change);
            check(apply.Wait(TimeSpan.FromSeconds(5)) && apply.Result.State == MacResultState.Applied,
                "Administrator startup can wait for asynchronous apply without a dispatcher deadlock");
            var restore = DepthStrap.Recovery.MacControls.ExecuteHelperOperationAsync(new(true, driver.Id, null), change);
            check(restore.Wait(TimeSpan.FromSeconds(5)) && restore.Result.State == MacResultState.Restored,
                "Administrator startup can wait for asynchronous restore without a dispatcher deadlock");
            check(context.Posts == 0 && driver.Backup is null && !driver.Locked,
                "Helper backend continuations avoid the blocked UI dispatcher and release their transaction");
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private sealed class RejectDispatcherPost : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        { Interlocked.Increment(ref Posts); throw new InvalidOperationException("A blocked dispatcher cannot run a helper continuation."); }
    }
    internal static async Task Run(Action<bool, string> check)
    {
        async Task Reject(Func<Task> operation, string description)
        { bool failed = false; try { await operation(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or OperationCanceledException) { failed = true; } check(failed, description); }
        var driver = new FakeDriver(); var change = new MacChange(driver);
        const string target = "021122334455";
        await Reject(() => change.ApplyAsync(driver.Id, target, false), "MAC changes require confirmation");
        await Reject(() => change.ApplyAsync(driver.Id, "001122334455", true), "Global addresses are not accepted as generated changes");
        await Reject(() => change.ApplyAsync(driver.Id, "031122334455", true), "Multicast addresses are refused");
        await Reject(() => change.ApplyAsync(Guid.Empty, target, true), "A single explicit adapter is required");
        await Reject(() => change.ApplyAsync(driver.Id, target + ";cmd", true), "MAC command injection strings are refused");
        check(driver.Writes == 0, "Rejected MAC requests perform no writes");
        driver.Supported = false;
        check((await change.ApplyAsync(driver.Id, target, true)).State == MacResultState.Unavailable && driver.Backup is null, "Unsupported drivers do not receive fabricated registry settings");
        driver.Supported = true; driver.SaveFails = true;
        await Reject(() => change.ApplyAsync(driver.Id, target, true), "Backup failure stops before the first write");
        check(driver.Writes == 0, "Failed backup preserved original MAC");
        driver.SaveFails = false; driver.CorruptBackup = true;
        await Reject(() => change.ApplyAsync(driver.Id, target, true), "Unverified backup stops before mutation");
        check(driver.Writes == 0, "Unverified backup preserved original MAC");
        driver.CorruptBackup = false; driver.Backup = null;
        check((await change.ApplyAsync(driver.Id, target, true)).State == MacResultState.Applied && driver.Backup?.OriginalOverride is null, "Successful apply retains absent-original override state");
        await Reject(() => change.ApplyAsync(driver.Id, "061122334455", true), "Repeated applies cannot overwrite the original backup");
        check((await change.RestoreAsync(driver.Id, true)).State == MacResultState.Restored && driver.Backup is null && driver.Override is null, "Restore removes an added override and verifies original effective MAC");
        driver.Override = "00AABBCCDDEE"; driver.Effective = driver.Override;
        await change.ApplyAsync(driver.Id, target, true);
        check((await change.RestoreAsync(driver.Id, true)).State == MacResultState.Restored && driver.Override == "00AABBCCDDEE", "Restore preserves an existing original override");
        driver.Override = null; driver.Effective = FakeDriver.Factory;
        driver.Online = ConnectionState.Offline;
        check((await change.ApplyAsync(driver.Id, target, true)).State == MacResultState.RestoreOffered && driver.Backup is not null, "Offline result offers restore and retains backup");
        driver.Override = "0A9988776655"; driver.Effective = driver.Override;
        check((await change.RestoreAsync(driver.Id, true)).State == MacResultState.UserChangePreserved && driver.Override == "0A9988776655", "Restore preserves subsequent manual changes");
        driver.Override = target; driver.Effective = target; driver.Missing = true;
        check((await change.RestoreAsync(driver.Id, true)).State == MacResultState.Unavailable && driver.Backup is not null, "Disconnected adapter backup is retained");
        driver.Missing = false; driver.IgnoreRestore = true;
        check((await change.RestoreAsync(driver.Id, true)).State == MacResultState.Failed && driver.Backup is not null, "Driver restore failure never reports success");
        driver.IgnoreRestore = false; await change.RestoreAsync(driver.Id, true);
        driver.FailApplyAfterWrite = true;
        check((await change.ApplyAsync(driver.Id, target, true)).State == MacResultState.Failed && driver.Override is null && driver.Backup is null, "Failure after a possible write verifies rollback");
        driver.FailApplyAfterWrite = false; driver.Online = ConnectionState.Unknown;
        check((await change.ApplyAsync(driver.Id, target, true)).State == MacResultState.RestoreOffered, "Inconclusive connectivity offers restore rather than claiming online");
        await change.RestoreAsync(driver.Id, true);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        int before = driver.Writes;
        await Reject(() => change.ApplyAsync(driver.Id, target, true, cancellation.Token), "Pre-cancelled MAC transaction makes no changes");
        check(driver.Writes == before, "Cancellation preserved adapter settings");
        check(Enumerable.Range(0, 128).Select(_ => MacChange.GenerateLocalAddress()).All(MacChange.IsLocalUnicast), "Generated MACs are six-byte locally administered unicast");
        check(!driver.Locked, "MAC transaction gate is released on all tested paths");
        await Reject(() => change.RestoreAsync(Guid.Empty, true), "Restore requires a single nonempty adapter identity");
        var emptyOverride = new FakeDriver { Override = "" };
        var emptyChange = new MacChange(emptyOverride);
        check((await emptyChange.ApplyAsync(emptyOverride.Id, target, true)).State == MacResultState.Applied && emptyOverride.Backup?.OriginalOverride == "", "An existing empty override is backed up distinctly from absence");
        check((await emptyChange.RestoreAsync(emptyOverride.Id, true)).State == MacResultState.Restored && emptyOverride.Override == "", "Restore preserves the original empty override value");
        var readFailure = new FakeDriver { FailReadAfterWrite = true };
        var failedChange = new MacChange(readFailure);
        check((await failedChange.ApplyAsync(readFailure.Id, target, true)).State == MacResultState.Failed && readFailure.Backup is not null, "Provider failure after mutation returns failure and retains recovery backup");
        var identityRace = new FakeDriver { ChangeIdentityAfterWrite = true };
        var identityChange = new MacChange(identityRace);
        check((await identityChange.ApplyAsync(identityRace.Id, target, true)).State == MacResultState.Failed && identityRace.Backup is not null, "A different adapter returned after apply cannot establish success or trigger its restoration");
        var restoreRace = new FakeDriver(); var restoreChange = new MacChange(restoreRace);
        await restoreChange.ApplyAsync(restoreRace.Id, target, true);
        restoreRace.ChangeIdentityAfterWrite = true;
        check((await restoreChange.RestoreAsync(restoreRace.Id, true)).State == MacResultState.Failed && restoreRace.Backup is not null, "Restore verifies the identity returned after writing before deleting its backup");
        var alreadyRestored = new FakeDriver(); var noRestart = new MacChange(alreadyRestored);
        await noRestart.ApplyAsync(alreadyRestored.Id, target, true);
        alreadyRestored.Override = null; alreadyRestored.Effective = FakeDriver.Factory;
        int writes = alreadyRestored.Writes;
        check((await noRestart.RestoreAsync(alreadyRestored.Id, true)).State == MacResultState.Restored && alreadyRestored.Writes == writes && alreadyRestored.Backup is null, "Already restored adapters need no additional connection interruption");
        foreach (string invalid in new[] { "000000000000", "FFFFFFFFFFFF", "011122334455" })
        {
            var unusable = new FakeDriver { Effective = invalid };
            check((await new MacChange(unusable).ApplyAsync(unusable.Id, target, true)).State == MacResultState.Unavailable && unusable.Writes == 0 && unusable.Backup is null, "Unusable original MAC cannot enter a mutation transaction");
        }
        using var afterBackupCancellation = new CancellationTokenSource();
        var interrupted = new FakeDriver { AfterSave = afterBackupCancellation.Cancel };
        await Reject(() => new MacChange(interrupted).ApplyAsync(interrupted.Id, target, true, afterBackupCancellation.Token), "Cancellation after verified backup stops before adapter mutation");
        check(interrupted.Writes == 0 && interrupted.Backup is not null && !interrupted.Locked, "Interrupted preparation retains the original backup and releases the transaction gate");
        check((await new MacChange(interrupted).RestoreAsync(interrupted.Id, true)).State == MacResultState.Restored && interrupted.Writes == 0 && interrupted.Backup is null, "A fresh recovery instance clears an interrupted preparation without restarting the adapter");
        var cleanupFailure = new FakeDriver();
        await new MacChange(cleanupFailure).ApplyAsync(cleanupFailure.Id, target, true);
        cleanupFailure.DeleteFails = true;
        check((await new MacChange(cleanupFailure).RestoreAsync(cleanupFailure.Id, true)).State == MacResultState.Failed && cleanupFailure.Override is null && cleanupFailure.Backup is not null, "Failed backup deletion does not falsely report a completed restore");
        int restoredWrites = cleanupFailure.Writes;
        cleanupFailure.DeleteFails = false;
        check((await new MacChange(cleanupFailure).RestoreAsync(cleanupFailure.Id, true)).State == MacResultState.Restored && cleanupFailure.Writes == restoredWrites && cleanupFailure.Backup is null, "Retry after backup deletion failure does not interrupt an already restored adapter");
        var probeFailure = new FakeDriver { ProbeFails = true };
        check((await new MacChange(probeFailure).ApplyAsync(probeFailure.Id, target, true)).State == MacResultState.Failed && probeFailure.Override is null && probeFailure.Backup is null && !probeFailure.Locked, "An exception in connectivity verification restores the original setting and releases the gate");
    }

    private sealed class FakeDriver : IMacChangeBackend
    {
        internal const string Factory = "001122AABBCC";
        internal Guid Id = Guid.NewGuid();
        internal string Effective = Factory;
        internal string? Override;
        internal MacBackup? Backup;
        internal bool Supported = true, Missing, SaveFails, CorruptBackup, IgnoreRestore, FailApplyAfterWrite, Locked, FailReadAfterWrite, ChangeIdentityAfterWrite;
        private bool _identityChanged;
        internal int Writes;
        internal ConnectionState Online = ConnectionState.Online;
        internal Action? AfterSave;
        internal bool DeleteFails, ProbeFails, AsyncRead;
        public ValueTask<IAsyncDisposable> LockAsync(Guid adapter, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (Locked) throw new IOException("busy"); Locked = true; return ValueTask.FromResult<IAsyncDisposable>(new Gate(() => Locked = false)); }
        public async Task<MacAdapter?> ReadAsync(Guid adapter, CancellationToken token)
        {
            if (AsyncRead) await Task.Yield();
            if (FailReadAfterWrite && Writes > 0) throw new IOException("fixture provider unavailable");
            return Missing ? null : new MacAdapter(_identityChanged ? Guid.NewGuid() : Id, Supported, Effective, Override);
        }
        public Task<MacBackup?> LoadBackupAsync(Guid adapter, CancellationToken token) => Task.FromResult(CorruptBackup ? null : Backup);
        public Task SaveBackupAsync(MacBackup backup, CancellationToken token) { if (SaveFails) throw new IOException("fixture"); Backup = backup; AfterSave?.Invoke(); return Task.CompletedTask; }
        public Task DeleteBackupAsync(Guid adapter, CancellationToken token) { if (DeleteFails) throw new IOException("fixture backup cleanup failure"); Backup = null; return Task.CompletedTask; }
        public Task SetOverrideAsync(Guid adapter, string? address, CancellationToken token)
        { Writes++; if (address is null && IgnoreRestore) return Task.CompletedTask; Override = address; Effective = string.IsNullOrEmpty(address) ? Factory : address; _identityChanged |= ChangeIdentityAfterWrite; if (address is not null && FailApplyAfterWrite) throw new IOException("fixture partial write"); return Task.CompletedTask; }
        public Task<ConnectionState> CheckConnectivityAsync(Guid adapter, CancellationToken token) { if (ProbeFails) throw new IOException("fixture connectivity failure"); return Task.FromResult(Online); }
        private sealed class Gate(Action release) : IAsyncDisposable { public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; } }
    }
}
