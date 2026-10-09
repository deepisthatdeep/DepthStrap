namespace Bloxstrap.Utility;

internal static class AppUpdateHandoff
{
    private static string EventName(string kind, string id) => $@"Local\{App.ProjectName}-Update{kind}-{id}";

    internal static bool Start(ProcessStartInfo requested, Func<ProcessStartInfo, Process?>? start = null, TimeSpan? timeout = null)
    {
        string id = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName("Ready", id));
        using var proceed = new EventWaitHandle(false, EventResetMode.ManualReset, EventName("Proceed", id));
        // Copy the launch plan so a failed attempt cannot leave a stale handshake ID.
        var info = new ProcessStartInfo(requested.FileName) { UseShellExecute = false,
            WorkingDirectory = requested.WorkingDirectory, CreateNoWindow = requested.CreateNoWindow,
            WindowStyle = requested.WindowStyle };
        foreach (string arg in requested.ArgumentList) info.ArgumentList.Add(arg);
        info.ArgumentList.Add("-updatehandoff"); info.ArgumentList.Add(id);
        using var process = (start ?? Process.Start)(info);
        if (process is null) return false;
        bool accepted = false;
        try
        {
            var clock = Stopwatch.StartNew();
            TimeSpan budget = timeout ?? TimeSpan.FromSeconds(15);
            while (!ready.WaitOne(50))
                if (process.HasExited || clock.Elapsed >= budget) return false;
            if (process.HasExited) return false;
            proceed.Set();
            accepted = true;
            return true;
        }
        finally
        {
            if (!accepted)
            {
                // Only the replacement process created by this attempt is stopped.
                // It has not received permission to replace files or launch Roblox.
                try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
                catch (Exception ex) { App.Logger.WriteException("AppUpdateHandoff::StopPending", ex); }
            }
        }
    }

    internal static bool Accept(string? id, TimeSpan? timeout = null)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return false;
        try
        {
            using var ready = EventWaitHandle.OpenExisting(EventName("Ready", id!));
            using var proceed = EventWaitHandle.OpenExisting(EventName("Proceed", id!));
            ready.Set();
            return proceed.WaitOne(timeout ?? TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        { App.Logger.WriteException("AppUpdateHandoff::Accept", ex); return false; }
    }
}
