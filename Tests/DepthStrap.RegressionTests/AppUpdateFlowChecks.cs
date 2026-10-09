using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Models.APIs.GitHub;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Utility;

internal static class AppUpdateFlowChecks
{
    private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Paths.Base, "app-update-flow-" + Guid.NewGuid().ToString("N"));
        internal readonly Settings Settings = new() { PauseRobloxUpdates = true, UpdateRoblox = false,
            BackgroundUpdatesEnabled = false, RobloxPlayerVersionOverride = "version-0123456789abcdef" };
        internal readonly byte[] Binary = File.ReadAllBytes(typeof(App).Assembly.Location);
        internal readonly HttpClient Client;
        internal AppUpdateRuntime Runtime;
        internal string Tag = "v" + App.Version;
        internal int Queries, Downloads, Saves, Acquisitions, Starts, Releases;
        internal bool Running, SaveSucceeds = true, LockAvailable = true, StartSucceeds = true;
        internal HttpStatusCode ApiStatus = HttpStatusCode.OK;
        internal Action? OnDownload;
        internal Action<ProcessStartInfo>? OnStart;
        internal ProcessStartInfo? Captured;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "installed.exe"), "original app fixture");
            Directory.CreateDirectory(Path.Combine(Root, "Versions", "version-0123456789abcdef"));
            File.WriteAllText(Path.Combine(Root, "Versions", "version-0123456789abcdef", "RobloxPlayerBeta.exe"), "pinned Roblox fixture");
            File.WriteAllText(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(Settings));
            Client = new HttpClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsoluteUri == AppUpdater.LatestReleaseUrl)
                {
                    Queries++;
                    var release = new GithubRelease { TagName = Tag, Assets = new() { new() {
                        Name = "DepthStrap.exe", BrowserDownloadUrl = $"https://github.com/{App.ProjectRepository}/releases/download/{Tag}/DepthStrap.exe",
                        Size = Binary.Length, Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(Binary)) } } };
                    return new(ApiStatus) { Content = new StringContent(JsonSerializer.Serialize(release)) };
                }
                Downloads++; OnDownload?.Invoke();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Binary) };
            }));
            Runtime = new(Client, Settings, new LaunchSettings(new[] { "roblox://experiences/start?placeId=123&gameInstanceId=fixture" }),
                "0.0.1", Path.Combine(Root, "updates"), true, () => Running,
                () => { Saves++; return SaveSucceeds; },
                () => { Acquisitions++; return LockAvailable ? new Lease(() => Releases++) : null; },
                info => { Starts++; Captured = info; OnStart?.Invoke(info); return StartSucceeds; });
        }
        internal bool Run(CancellationToken token = default)
        { var task = AppUpdater.TryUpdateAsync(Runtime, token); HistoryChecks.Wait(task); return task.Result; }
        internal bool OriginalsIntact() =>
            File.ReadAllText(Path.Combine(Root, "installed.exe")) == "original app fixture" &&
            File.ReadAllText(Path.Combine(Root, "Versions", "version-0123456789abcdef", "RobloxPlayerBeta.exe")) == "pinned Roblox fixture" &&
            File.ReadAllText(Path.Combine(Root, "settings.json")) == JsonSerializer.Serialize(Settings);
        public void Dispose() => Client.Dispose();
    }

    internal static void Run(Action<bool, string> check)
    {
        using (var f = new Fixture())
        {
            check(f.Run() && f.Queries == 1 && f.Downloads == 1 && f.Saves == 1 && f.Acquisitions == 1 && f.Starts == 1 && f.Releases == 1,
                "Production updater orchestration queries, verifies, saves and hands off exactly once, releasing its lease");
            var launch = new LaunchSettings(f.Captured!.ArgumentList.ToArray());
            check(launch.UpgradeFlag.Active && launch.RobloxLaunchArgs == f.Runtime.Launch.RobloxLaunchArgs && f.OriginalsIntact(),
                "Complete updater preparation preserves the raw launch action and all installed app/Roblox/settings fixtures");
        }
        using (var f = new Fixture())
        {
            f.Settings.AutomaticDepthStrapUpdates = false;
            check(!f.Run() && f.Queries == 0 && f.Starts == 0, "Opted-out orchestration makes no update request");
        }
        using (var f = new Fixture())
        {
            f.Runtime = f.Runtime with { StorageAvailable = false };
            check(!f.Run() && f.Queries == 0 && f.Saves == 0, "Unreadable settings/state prevent any updater request or save");
        }
        using (var f = new Fixture())
        {
            f.Running = true;
            check(!f.Run() && f.Queries == 0, "An active client defers orchestration before networking");
            f.Running = false;
            check(f.Run(), "An earlier busy-client deferral cannot suppress a later eligible update");
        }
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(f.Runtime.UpdateDirectory);
            using (var held = new FileStream(Path.Combine(f.Runtime.UpdateDirectory, "update-check.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                check(!f.Run() && f.Queries == 0 && f.Starts == 0, "Concurrent update checks cannot start another download or handoff");
            check(f.Run(), "A released update-check lock permits the next attempt");
        }
        using (var f = new Fixture())
        {
            f.OnDownload = () => f.Running = true;
            check(!f.Run() && f.Saves == 0 && f.Starts == 0, "A client opened during download defers installation before saving or launching");
            f.Running = false; f.OnDownload = null;
            check(f.Run() && f.Downloads == 1, "A deferred verified download is reused after clients close");
        }
        using (var f = new Fixture())
        {
            f.OnDownload = () => f.Settings.AutomaticDepthStrapUpdates = false;
            check(!f.Run() && f.Saves == 0 && f.Starts == 0, "Disabling updates during preparation prevents the pending handoff");
        }
        using (var f = new Fixture())
        {
            f.SaveSucceeds = false;
            check(!f.Run() && f.Saves == 1 && f.Acquisitions == 0 && f.Starts == 0 && f.OriginalsIntact(),
                "Failed state persistence keeps the current app running and never starts a replacement");
        }
        using (var f = new Fixture())
        {
            f.Runtime = f.Runtime with { SaveState = () => { f.Running = true; return true; } };
            check(!f.Run() && f.Starts == 0 && f.Releases == 1, "A client appearing at the final handoff boundary still defers the update");
        }
        using (var f = new Fixture())
        {
            f.LockAvailable = false;
            check(!f.Run() && f.Starts == 0 && f.Releases == 0, "An unavailable handoff lease leaves the current app active");
        }
        using (var f = new Fixture())
        {
            f.OnStart = _ => throw new Win32Exception("Fixture launch failure");
            check(!f.Run() && f.Releases == 1 && f.OriginalsIntact(), "A failed updater process start releases locks and preserves the installed app");
            f.OnStart = null;
            check(f.Run() && f.Downloads == 1 && f.Releases == 2, "A failed process start can retry using the verified cached executable");
        }
        using (var f = new Fixture())
        {
            f.StartSucceeds = false;
            check(!f.Run() && f.Releases == 1, "A process start that returns no process never requests parent termination");
        }
        using (var f = new Fixture())
        {
            f.Tag = "v99.0.0";
            check(!f.Run() && f.Saves == 0 && f.Starts == 0 && f.OriginalsIntact(),
                "Even matching hashes cannot hand off an executable advertising a different app version");
        }
        using (var f = new Fixture())
        {
            f.Runtime = f.Runtime with { CurrentVersion = App.Version };
            check(!f.Run() && f.Queries == 1 && f.Downloads == 0 && f.Starts == 0, "An up-to-date install performs no download or handoff");
        }
        using (var f = new Fixture())
        {
            f.ApiStatus = HttpStatusCode.TooManyRequests;
            check(!f.Run() && f.Queries == 1 && f.Downloads == 0 && f.OriginalsIntact(), "Rate-limited orchestration returns to the installed app without a download");
        }
        using (var f = new Fixture())
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            check(!f.Run(cancelled.Token) && f.Queries == 0 && f.Saves == 0 && f.Starts == 0, "Cancelled orchestration cannot start an updater or write settings");
        }
        using (var f = new Fixture())
        {
            string output = Path.Combine(f.Root, "launch-capture.json");
            f.OnStart = info =>
            {
                var worker = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                worker.ArgumentList.Add(typeof(AppUpdateFlowChecks).Assembly.Location);
                worker.ArgumentList.Add("--update-launch-worker"); worker.ArgumentList.Add(output);
                worker.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info.ArgumentList.ToArray()))));
                using var process = Process.Start(worker)!;
                if (!process.WaitForExit(5000) || process.ExitCode != 0) throw new IOException("Fixture launch worker failed.");
            };
            check(f.Run() && File.Exists(output), "Updater handoff arguments reach a real owned child process");
            using var captured = JsonDocument.Parse(File.ReadAllText(output));
            check(captured.RootElement.GetProperty("Upgrade").GetBoolean() &&
                captured.RootElement.GetProperty("Args").GetString() == f.Runtime.Launch.RobloxLaunchArgs && f.OriginalsIntact(),
                "The child process parses the original Roblox URI without installing or starting Roblox");
        }
    }
}
