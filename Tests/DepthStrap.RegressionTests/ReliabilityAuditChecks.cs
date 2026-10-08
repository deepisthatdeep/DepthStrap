using System.IO;
using System.Text;
using Bloxstrap;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Networking;
using Bloxstrap.Roblox;
using Bloxstrap.Utility;
using Bloxstrap.Integrations;
using Bloxstrap.Models;

internal static class ReliabilityAuditChecks
{
    internal static void Run(Action<bool, string> check)
    {
        App.Settings.Prop = new Settings { CompetitivePreferredCity = "Dallas", CompetitiveFallbackCities = new() { "Houston", "Santiago de Querétaro" } };
        App.Settings.Save();
        string saved = File.ReadAllText(App.Settings.FileLocation);
        AdaptiveRegionService.RecordAsync(Array.Empty<RegionObservation>(), "unknown", CancellationToken.None).GetAwaiter().GetResult();
        check(App.Settings.Prop.CompetitivePreferredCity == "Dallas" && App.Settings.Prop.CompetitiveFallbackCities.Count == 2 &&
            File.ReadAllText(App.Settings.FileLocation) == saved,
            "An unavailable Cloudflare route retains learned primary/fallback regions in memory and on disk");
        CheckSplitLog(check);
        CheckLogBuffer(check);
        CheckMalformedLog(check);
        CheckReconnect(check);
        CheckMutexCancellation(check);
        CheckRouteChange(check);
        App.Settings.Prop.NetworkSetupVersion = 1;
        App.Settings.Save();
        NetworkHistory.Reset();
        check(App.Settings.Prop.NetworkSetupVersion == 0 && new JsonManager<Settings>().Load(false),
            "Reset Network clears the in-memory setup marker as well as the saved marker");
        App.Settings.Prop = new Settings();
    }

    private static void CheckRouteChange(Action<bool, string> check)
    {
        Task.Run(async () =>
        {
            var direct = new CloudflareTraceResult { Warp = "off", Colo = "DFW" };
            var warp = new CloudflareTraceResult { Warp = "on", Colo = "DFW" };
            Task<List<RoutingSample>> Probe(string route, CancellationToken _) => Task.FromResult(new List<RoutingSample> { new("192.0.2.1", "Dallas", "US", route, 20, 0, 0) });
            var measurements = await RouteComparisonRunner.ProbeVerifiedAsync(direct, _ => Task.FromResult<CloudflareTraceResult?>(direct), Probe, CancellationToken.None);
            check(measurements.Count == 1 && measurements[0].Route == "direct", "Current-route-only tests retain samples from a verified stable route");
            bool rejected = false;
            try { await RouteComparisonRunner.ProbeVerifiedAsync(direct, _ => Task.FromResult<CloudflareTraceResult?>(warp), Probe, CancellationToken.None); }
            catch (IOException) { rejected = true; }
            check(rejected, "A WARP change during a current-route-only test rejects mixed measurements instead of learning incorrect fallback regions");
            rejected = false;
            try { await RouteComparisonRunner.ProbeVerifiedAsync(warp, _ => Task.FromResult<CloudflareTraceResult?>(new() { Warp = "on", Colo = "ORD" }), Probe, CancellationToken.None); }
            catch (IOException) { rejected = true; }
            check(rejected, "A changing WARP ingress also invalidates a current-route-only test");
        }).GetAwaiter().GetResult();
    }

    private static void CheckMutexCancellation(Action<bool, string> check)
    {
        string name = "DepthStrap-CancelFixture-" + Guid.NewGuid().ToString("N");
        using var owner = new Mutex(true, name);
        Task.Run(async () =>
        {
            for (int i = 0; i < 20; i++)
            {
                await using var mutex = new AsyncMutex(false, name);
                using var cancellation = new CancellationTokenSource();
                Task wait = mutex.AcquireAsync(cancellation.Token);
                cancellation.Cancel();
                bool cancelled = false;
                try { await wait.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) { cancelled = true; }
                if (!cancelled) throw new InvalidOperationException("Cancelled launch acquired another client's mutex.");
            }
        }).GetAwaiter().GetResult();
        owner.ReleaseMutex();
        check(true, "Twenty immediate launch cancellations complete their mutex waiters without hanging or releasing another owner's lock");
        Task.Run(async () =>
        {
            await using var mutex = new AsyncMutex(false, name);
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await mutex.AcquireAsync(budget.Token);
            await mutex.ReleaseAsync();
        }).GetAwaiter().GetResult();
        check(true, "A successful launch can acquire and release the mutex after cancelled launches");
    }

    private static void CheckLogBuffer(Action<bool, string> check)
    {
        var buffer = new LogLineBuffer();
        var lines = new List<string>();
        byte[] bytes = Encoding.UTF8.GetBytes("\uFEFFQuerétaro 😃\r\n東京\npartial");
        foreach (byte value in bytes) buffer.Append(new[] { value }, lines.Add);
        check(lines.SequenceEqual(new[] { "Querétaro 😃", "東京" }), "The log reader preserves split UTF-8 characters, BOM and CRLF without emitting an incomplete line");
        buffer.Append(Encoding.UTF8.GetBytes(" line\n"), lines.Add);
        check(lines[^1] == "partial line", "A log fragment at EOF is completed by the next write");
        buffer.Append(Encoding.UTF8.GetBytes(new string('x', LogLineBuffer.MaximumLineLength + 1) + "\nvalid\n"), lines.Add);
        check(lines.Count == 4 && lines[^1] == "valid", "Oversized log lines are discarded with bounded memory while later valid lines still load");
        buffer.Append(Encoding.UTF8.GetBytes("old partial"), lines.Add);
        buffer.Reset(); buffer.Append(Encoding.UTF8.GetBytes("new\n"), lines.Add);
        check(lines[^1] == "new", "Truncating a log resets partial text and decoder state");
    }

    private static void CheckMalformedLog(Action<bool, string> check)
    {
        using var activity = new ActivityWatcher("malformed-fixture.log");
        string job = Guid.NewGuid().ToString();
        string huge = new string('9', 80);
        activity.ReadLogEntry($"[FLog::Output] ! Joining game '{job}' place {huge} at 192.0.2.10");
        activity.ReadLogEntry($"[FLog::Output] ! Joining game '{job}' place 999002 at 999.0.2.10");
        check(activity.Data.PlaceId == 0, "Overflowed place IDs and invalid addresses cannot mutate a pending join");
        Join(activity, job);
        activity.ReadLogEntry($"[FLog::GameJoinLoadTime] Report game_join_loadtime: universeid:{huge}, userid:1");
        activity.ReadLogEntry($"[FLog::Network] UDMUX Address = 192.0.2.11, Port = {huge} | RCC Server Address = 192.0.2.10, Port = 52535");
        activity.ReadLogEntry("[FLog::Network] UDMUX Address = 192.0.2.11, Port = 65536 | RCC Server Address = 192.0.2.10, Port = 52535");
        activity.ReadLogEntry($"[FLog::Network] serverId: 192.0.2.10|{huge}");
        activity.ReadLogEntry($"[FLog::Network] Sending disconnect with reason: {huge}");
        check(activity.Data.UniverseId == 0 && activity.Data.UdmuxAddress is null && !activity.InGame,
            "Malformed universe IDs, ports and disconnect reasons are ignored without crashing the watcher");
        activity.ReadLogEntry("[FLog::Network] serverId: 192.0.2.10|52535");
        check(activity.InGame, "A valid confirmation still loads after malformed log entries");
    }

    private static void Join(ActivityWatcher activity, string? job = null)
    {
        activity.ReadLogEntry($"[FLog::Output] ! Joining game '{job ?? Guid.NewGuid().ToString()}' place 999002 at 192.0.2.10");
    }

    private static void CheckReconnect(Action<bool, string> check)
    {
        App.Settings.Prop.AutoRejoin = true;
        foreach (string scenario in new[] { "idle", "pending", "replacement", "disposed", "disabled", "suppressed", "failed" })
        {
            using var activity = new ActivityWatcher("reconnect-fixture.log") { AutoRejoinDelay = TimeSpan.FromMilliseconds(150) };
            int rejoins = 0;
            activity.RejoinRequested = _ => { rejoins++; if (scenario == "failed") throw new IOException("Fixture reconnect failure"); };
            App.Settings.Prop.AutoRejoin = true;
            Join(activity);
            activity.ReadLogEntry("[FLog::Network] serverId: 192.0.2.10|52535");
            activity.ReadLogEntry("[FLog::Network] Sending disconnect with reason: 277");
            activity.ReadLogEntry("[FLog::Network] Time to disconnect replication data: 0.1");
            if (scenario is "pending" or "replacement") Join(activity);
            if (scenario == "replacement") activity.ReadLogEntry("[FLog::Network] serverId: 192.0.2.10|52535");
            if (scenario == "disposed") activity.Dispose();
            if (scenario == "disabled") App.Settings.Prop.AutoRejoin = false;
            if (scenario == "suppressed") activity.SuppressAutoRejoin = true;
            check(activity.AutoRejoinTask.Wait(TimeSpan.FromSeconds(3)) && rejoins == (scenario is "idle" or "failed" ? 1 : 0),
                "Delayed reconnect respects the current client lifecycle and contains failures: " + scenario);
        }
        App.Settings.Prop.AutoRejoin = false;
    }

    private static void CheckSplitLog(Action<bool, string> check)
    {
        string path = Path.Combine(Paths.Cache, "split_Player.log");
        Directory.CreateDirectory(Paths.Cache);
        File.WriteAllText(path, "[FLog::Output] ! Join", new UTF8Encoding(false));
        using var activity = new ActivityWatcher(path);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activity.OnLogOpen += (_, _) => opened.TrySetResult();
        activity.OnGameJoin += (_, _) => joined.TrySetResult();
        var reader = Task.Run(activity.RunAsync);
        try
        {
            check(opened.Task.Wait(TimeSpan.FromSeconds(5)), "Split-log fixture opens the active Player log");
            Thread.Sleep(1200); // Ensure the first fragment was read before Roblox appends the rest.
            File.AppendAllText(path, $"ing game '{Guid.NewGuid()}' place 999002 at 192.0.2.10\n[FLog::Network] serverId: 192.0.2.10|52535\n");
            check(joined.Task.Wait(TimeSpan.FromSeconds(5)), "A join split across separate Roblox log writes still reaches the activity watcher");
        }
        finally { activity.Dispose(); if (!reader.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Split-log reader did not stop."); }
    }
}
