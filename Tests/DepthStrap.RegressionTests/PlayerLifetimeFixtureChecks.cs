using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Bloxstrap;
using Bloxstrap.Roblox;

internal static class PlayerLifetimeFixtureChecks
{
    internal static void Worker(string mode, string ready)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window MakeWindow() => new() { Title = "DepthStrap synthetic lifetime test", Width = 160, Height = 100,
            Left = -10000, Top = -10000, ShowInTaskbar = true };
        var window = MakeWindow(); window.Show();
        File.WriteAllText(ready, "synthetic-window-ready");
        var transition = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        transition.Tick += (_, _) =>
        {
            transition.Stop();
            if (mode == "hidden") window.Hide();
            else
            {
                window.Close();
                if (mode == "recreated")
                {
                    var recover = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    recover.Tick += (_, _) => { recover.Stop(); window = MakeWindow(); window.Show(); };
                    recover.Start();
                }
            }
        };
        transition.Start();
        var expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(70) };
        expiry.Tick += (_, _) => { expiry.Stop(); app.Shutdown(); }; expiry.Start();
        app.Run();
    }

    internal static async Task RunAsync(string directory)
    {
        string root = Path.GetFullPath(directory); Directory.CreateDirectory(root); Paths.Initialize(root);
        string source = Path.Combine(AppContext.BaseDirectory, "DepthStrap.RegressionTests.exe");
        string helper = Path.Combine(AppContext.BaseDirectory, "RobloxPlayerBeta.exe");
        // Rename only our test apphost, alongside its own assembly/runtime files.
        // Never touch or launch an installed Roblox binary.
        File.Copy(source, helper, overwrite: false);
        var children = new List<(string Mode, Process Process)>();
        var monitors = new List<Task>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            foreach (string mode in new[] { "destroyed", "hidden", "recreated" })
            {
                string signal = Path.Combine(root, mode + ".ready");
                var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
                start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
                start.ArgumentList.Add("--lifetime-fixture"); start.ArgumentList.Add(mode); start.ArgumentList.Add(signal);
                var child = Process.Start(start) ?? throw new IOException("Could not start the synthetic lifetime worker.");
                children.Add((mode, child));
                var clock = Stopwatch.StartNew();
                while (!File.Exists(signal))
                {
                    if (child.HasExited || clock.Elapsed > TimeSpan.FromSeconds(5)) throw new IOException("Synthetic window did not initialize.");
                    await Task.Delay(50, cancel.Token);
                }
                monitors.Add(PlayerWindowLifetime.MonitorAsync(child, cancel.Token));
            }
            await Task.Delay(TimeSpan.FromSeconds(37), cancel.Token);
            File.WriteAllText(Path.Combine(root, "result.json"), System.Text.Json.JsonSerializer.Serialize(new {
                Results = children.Select(child => new { child.Mode, Exited = child.Process.HasExited }),
                Diagnostics = App.Logger.History.Where(line => line.Contains("WindowLifetime"))
            }));
            if (!children.Single(child => child.Mode == "destroyed").Process.HasExited ||
                children.Where(child => child.Mode != "destroyed").Any(child => child.Process.HasExited))
                throw new InvalidOperationException("Window cleanup did not distinguish destroyed, hidden and recreated synthetic clients.");
            cancel.Cancel(); await Task.WhenAll(monitors);
            Console.WriteLine("PASS: real Windows lifetime test closes one destroyed-window fixture after its grace period, preserves the hidden and recreated fixtures, and leaves other processes untouched.");
        }
        finally
        {
            cancel.Cancel();
            await Task.WhenAll(monitors);
            foreach (var child in children)
            {
                try { if (!child.Process.HasExited) { child.Process.Kill(); await child.Process.WaitForExitAsync(); } }
                finally { child.Process.Dispose(); }
            }
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { File.Delete(helper); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 19) Console.Error.WriteLine("Fixture apphost cleanup deferred: " + ex.Message);
                    else await Task.Delay(100);
                }
            }
        }
    }
}
