using System.Diagnostics;
using Bloxstrap.Utility;

internal static class PostLaunchProcessChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(PostLaunchProcess.TryParseIdentity("123", out int legacyId, out long? legacyTime) && legacyId == 123 && legacyTime is null,
            "Legacy PID-only post-launch arguments remain supported");
        check(PostLaunchProcess.TryParseIdentity("123:456", out int identityId, out long? identityTime) && identityId == 123 && identityTime == 456,
            "New post-launch arguments preserve both PID and UTC start ticks");
        foreach (string? invalid in new[] { null, "", "0", "-1", "abc", "123:", "123:0", "123:bad", "123:456:789", "123:9223372036854775807" })
            check(!PostLaunchProcess.TryParseIdentity(invalid, out _, out _), "Invalid post-launch identity is rejected: " + invalid);
        using var absent = PostLaunchProcess.Open(int.MaxValue, "RobloxPlayerBeta");
        check(absent is null, "An already absent post-launch PID is a normal completion");
        using var earlyExit = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
        check(earlyExit.WaitForExit(5000), "The disposable post-launch process exits before helper work");
        using var exited = PostLaunchProcess.Open(earlyExit.Id, "cmd");
        check(exited is null, "A real early-exit process produces no post-launch worker failure");
        using var current = Process.GetCurrentProcess();
        using var wrongIdentity = PostLaunchProcess.Open(current.Id, "RobloxPlayerBeta");
        check(wrongIdentity is null, "A non-Roblox PID is rejected before any priority or crash-handler action");
        long started = current.StartTime.ToUniversalTime().Ticks;
        using var reusedIdentity = PostLaunchProcess.Open(current.Id, current.ProcessName, started - 1);
        check(reusedIdentity is null, "A reused PID with a different process start time is rejected before mutation");
        var retained = PostLaunchProcess.Open(current.Id, current.ProcessName, started);
        check(retained is not null && !retained.HasExited && retained.StartTime == current.StartTime,
            "The post-launch target retains a handle to the identified original process");
        var handle = retained!.SafeHandle;
        retained.Dispose();
        check(handle.IsClosed, "Disposing a post-launch target releases its retained native handle");
    }
}
