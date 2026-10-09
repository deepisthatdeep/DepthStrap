using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Utility;

internal static class AppUpdateHandoffChecks
{
    private static Process Worker(ProcessStartInfo forwarded, string output, string mode)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(AppUpdateHandoffChecks).Assembly.Location);
        info.ArgumentList.Add("--update-handoff-worker"); info.ArgumentList.Add(output);
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(forwarded.ArgumentList.ToArray()))));
        info.ArgumentList.Add(mode);
        return Process.Start(info)!;
    }
    private static bool WaitForOwnedExit(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.WaitForExit(3000); }
        catch (ArgumentException) { return true; }
    }
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Paths.Base, "app-update-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var requested = AppUpdater.StartInfo(@"C:\fixture\DepthStrap.exe", new[] { "roblox://experiences/start?placeId=123&gameInstanceId=fixture" });
        string output = Path.Combine(root, "ready.json"); int workerId = 0;
        bool accepted = AppUpdateHandoff.Start(requested, info => { var process = Worker(info, output, "ready"); workerId = process.Id; return process; }, TimeSpan.FromSeconds(5));
        check(accepted && WaitForOwnedExit(workerId), "Parent and real child complete the readiness/proceed handshake before launch continuation");
        using (var capture = JsonDocument.Parse(File.ReadAllText(output)))
            check(capture.RootElement.GetProperty("Accepted").GetBoolean() && capture.RootElement.GetProperty("Args").GetString() == requested.ArgumentList[0],
                "An acknowledged child retains the requested Roblox URI");
        check(!requested.ArgumentList.Contains("-updatehandoff"), "A handshake attempt does not mutate the reusable launch plan");
        foreach (string mode in new[] { "exit", "stall" })
        {
            string failedOutput = Path.Combine(root, mode + ".json"); workerId = 0;
            accepted = AppUpdateHandoff.Start(requested, info => { var process = Worker(info, failedOutput, mode); workerId = process.Id; return process; }, TimeSpan.FromMilliseconds(150));
            check(!accepted && WaitForOwnedExit(workerId) && !File.Exists(failedOutput),
                "A replacement that " + mode + "s before readiness cannot make the parent exit or continue a launch");
        }
        check(!AppUpdateHandoff.Start(requested, _ => null, TimeSpan.FromMilliseconds(50)), "A missing replacement process cannot acknowledge an update");
        check(!AppUpdateHandoff.Accept("../invalid") && !AppUpdateHandoff.Accept(Guid.NewGuid().ToString("N")),
            "Invalid or expired handoff tokens cannot authorize a child update");
        string id = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\{App.ProjectName}-UpdateReady-{id}");
        using var proceed = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\{App.ProjectName}-UpdateProceed-{id}");
        var orphan = AppUpdater.StartInfo(@"C:\fixture\DepthStrap.exe", Array.Empty<string>());
        orphan.ArgumentList.Add("-updatehandoff"); orphan.ArgumentList.Add(id);
        string orphanOutput = Path.Combine(root, "orphan.json");
        using (var worker = Worker(orphan, orphanOutput, "orphan"))
        {
            check(ready.WaitOne(2000) && worker.WaitForExit(3000), "A ready child stops waiting when its parent never permits replacement");
            using var capture = JsonDocument.Parse(File.ReadAllText(orphanOutput));
            check(!capture.RootElement.GetProperty("Accepted").GetBoolean(), "An orphaned ready child cannot begin updating");
        }
    }
}
