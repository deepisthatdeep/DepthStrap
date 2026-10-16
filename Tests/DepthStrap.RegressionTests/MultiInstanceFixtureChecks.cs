using System.Diagnostics;
using System.IO;
using System.Reflection;
using Bloxstrap;

internal static class MultiInstanceFixtureChecks
{
    internal static void Worker(string key, string state)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("Fixture namespace required.");
        string prefix = "DepthStrap-MultiFixture-" + key;
        MultiInstanceWatcher.RunForObjects(prefix + "-ready", prefix + "-failed", prefix + "-guard", prefix + "-singleton", prefix + "-event",
            () => int.TryParse(File.ReadAllText(state), out int count) ? count : -1, () => false);
    }

    internal static void Run(string directory)
    {
        string root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        string key = Guid.NewGuid().ToString("N"), prefix = "DepthStrap-MultiFixture-" + key;
        string state = Path.Combine(root, "players.txt"); File.WriteAllText(state, "2");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-ready");
        using var failed = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-failed");
        Process Start()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--multi-fixture"); start.ArgumentList.Add(key); start.ArgumentList.Add(state);
            return Process.Start(start)!;
        }
        using var helper = Start();
        try
        {
            if (!ready.WaitOne(TimeSpan.FromSeconds(10)) || failed.WaitOne(0)) throw new IOException("Fixture helper did not become ready.");
            foreach (string name in new[] { prefix + "-singleton", prefix + "-event" })
            {
                using var reservation = Mutex.OpenExisting(name);
                if (reservation.WaitOne(0)) { reservation.ReleaseMutex(); throw new IOException("Fixture singleton was not reserved."); }
            }
            using (var duplicate = Start())
                if (!duplicate.WaitForExit(5000) || duplicate.ExitCode != 0 || !ready.WaitOne(0)) throw new IOException("Duplicate helper disrupted the active reservation.");
            File.WriteAllText(state, "1"); Thread.Sleep(800);
            if (helper.HasExited || !ready.WaitOne(0)) throw new IOException("Closing one client released the reservations too early.");
            File.WriteAllText(state, "0");
            if (!helper.WaitForExit(5000) || helper.ExitCode != 0 || ready.WaitOne(0)) throw new IOException("The last-client exit did not release readiness.");
            Console.WriteLine("PASS: separate-process multi-instance helper reserves both Windows objects, tolerates duplicate helpers, stays for the second client and releases after the last. Only fixture names were used.");
        }
        finally { if (!helper.HasExited) { helper.Kill(); helper.WaitForExit(5000); } }
    }
}
