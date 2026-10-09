using System.Diagnostics;
using System.IO;
using System.Reflection;
using Bloxstrap;
using Bloxstrap.Competitive;
using Bloxstrap.Enums;
using Bloxstrap.Models;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Roblox;
using Bloxstrap.UI.ViewModels.Settings;

internal static class WatcherChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var lifetime = new PlayerWindowLifetime();
        check(lifetime.Observe(TimeSpan.Zero, false) is null && lifetime.Observe(TimeSpan.FromMinutes(2), false) is null,
            "Slow startup with no observed window cannot be mistaken for closing Roblox");
        lifetime.Observe(TimeSpan.FromMinutes(2), true);
        lifetime.Observe(TimeSpan.FromMinutes(3), false);
        check(lifetime.Observe(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(14)), false) is null,
            "Short window transitions do not report a lingering process");
        check(lifetime.Observe(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(15)), false) is not null &&
            lifetime.Observe(TimeSpan.FromMinutes(4), false) is null, "Sustained window disappearance reports once without requesting termination");
        check(lifetime.Observe(TimeSpan.FromMinutes(4), true) is not null, "A returning window clears the lingering-process diagnosis");
        lifetime.Observe(TimeSpan.FromMinutes(5), false); lifetime.Observe(TimeSpan.FromMinutes(6), null);
        check(lifetime.Observe(TimeSpan.FromMinutes(6), false) is null,
            "Failed window inspection resets the absence interval rather than assuming closure");
        string root = Path.Combine(Paths.Base, "startup-fixture");
        long startTime = DateTime.UtcNow.AddSeconds(2).Ticks;
        var workers = Enumerable.Range(0, 8).Select(_ =>
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            info.ArgumentList.Add("--startup-log-worker"); info.ArgumentList.Add(root); info.ArgumentList.Add(startTime.ToString());
            return Process.Start(info)!;
        }).ToList();
        foreach (var worker in workers)
        {
            check(worker.WaitForExit(10000) && worker.ExitCode == 0, "Simultaneous launcher/watcher helpers each initialize their diagnostic log");
            worker.Dispose();
        }
        check(Directory.GetFiles(Path.Combine(root, "Logs"), "DepthStrap_*.log").Length == 8, "Eight simultaneous helper processes retain eight distinct logs without treating another helper as a duplicate launch");

        foreach (string log in Directory.GetFiles(Path.Combine(root, "Logs"), "DepthStrap_*.log"))
        {
            string[] lines = File.ReadAllLines(log);
            check(lines.Count(line => line.Contains("[LoggerExitFixture] entry-")) == 64 &&
                Enumerable.Range(0, 64).All(i => lines.Count(line => line.Contains($"[LoggerExitFixture] entry-{i:D2}:")) == 1),
                "Immediate helper exit retains every concurrent diagnostic entry exactly once");
            check(lines.Last().EndsWith("terminal-entry-before-immediate-exit"),
                "UI synchronization context cannot strand the final diagnostic entry at process exit");
        }
        App.Settings.Prop = new Settings { CompetitiveModeEnabled = true, CompetitiveNetworkMonitorEnabled = true,
            CompetitiveCloudflareDetectionEnabled = false, CompetitiveIcmpEnabled = false, CompetitiveTracerouteEnabled = false,
            AdaptiveRegionPreferencesEnabled = false, ShowServerDetails = false, ShowServerUptime = false, AutoRejoin = false,
            CompetitivePreferredCity = "Dallas", PreferNorthAmericaOnly = true, AutoLeaveBadChimeRegion = true };
        using var activity = new ActivityWatcher("fixture.log");
        using var network = new CompetitiveNetworkMonitor(activity);
        using var regions = new CompetitiveRegionMonitor(network);
        var resolved = new TaskCompletionSource<CompetitiveRegionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var autoLog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        network.DiagnosticsCompleted += (_, _) => diagnosed.TrySetResult();
        regions.OnRegionEvaluated += (_, result) => resolved.TrySetResult(result);
        network.AutoLogHandler = evt =>
        {
            // Exercise the production decision without operating a real Roblox process.
            autoLog.TrySetResult(BadRegionAutoLog.ShouldLeave(evt, App.Settings.Prop, activity.Data.JobId, DateTime.Now,
                CompetitiveRegionService.Classify(evt.Location, source: evt.RegionSource)));
            return Task.FromResult(false);
        };
        GlobalCache.ServerLocation["192.0.2.2"] = "London, United Kingdom";
        string job = "11111111-1111-1111-1111-111111111111";
        activity.ReadLogEntry($"2026-10-07T14:42:41Z,0.0,1234,6,Warning [FLog::Output] ! Joining game '{job}' place 999001 at 192.0.2.1");
        activity.ReadLogEntry("2026-10-07T14:42:41Z,0.0,1234,6,Info [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:999001, universeid:1359573625, userid:1");
        activity.ReadLogEntry("2026-10-07T14:42:41Z,0.0,1234,7,Debug [FLog::Network] UDMUX Address = 192.0.2.2, Port = 52535 | RCC Server Address = 192.0.2.1, Port = 52535");
        activity.ReadLogEntry("2026-10-07T14:42:41Z,0.0,1234,7 [FLog::Network] serverId: 192.0.2.2|52535");
        check(activity.InGame && activity.Data.UniverseId == CompetitiveRegionService.DeepwokenUniverseId && activity.Data.UdmuxAddress == "192.0.2.2",
            "Modern log severity prefixes still produce a confirmed Deepwoken subplace join and UDMUX endpoint");
        check(resolved.Task.Wait(TimeSpan.FromSeconds(5)), "A parsed game join reaches the region-warning pipeline");
        check(CompetitiveRegionMonitor.BuildNotification(resolved.Task.Result) is not null, "A known foreign Deepwoken subplace produces the bad-region warning");
        check(autoLog.Task.Wait(TimeSpan.FromSeconds(5)) && autoLog.Task.Result, "The same confirmed bad join reaches the opt-in autolog decision");
        check(diagnosed.Task.Wait(TimeSpan.FromSeconds(5)), "The join completes diagnostics before lifecycle checks begin");
        var viewModel = new CompetitivePageViewModel(); viewModel.PollSession();
        check(viewModel.SessionLocation.Contains("London") && viewModel.SessionUdmux.Contains("192.0.2.2"), "Current Session receives the live watcher file and displays region and endpoint");
        var current = network.LatestEvent!;
        CompetitiveNetworkState.Write(current with { ProcessId = 1234 });
        CompetitiveNetworkState.ClearIfOwned(0);
        check(CompetitiveNetworkState.TryRead()?.ProcessId == 1234, "One client cannot clear Current Session owned by another client");
        CompetitiveNetworkState.Write(current);
        viewModel.PollSession();
        activity.ReadLogEntry("2026-10-07T14:42:51Z,0.0,1234,7,Info [FLog::Network] Time to disconnect replication data: 0.1");
        viewModel.PollSession();
        check(!activity.InGame && CompetitiveNetworkState.TryRead() is null && viewModel.SessionLocation == "—", "Leaving the game clears the live session instead of showing the old server");
        var rejoined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        network.DiagnosticsCompleted += (_, _) => rejoined.TrySetResult();
        activity.ReadLogEntry($"2026-10-07T14:43:01Z,0.0,1234,6,Warning [FLog::Output] ! Joining game '{job}' place 999001 at 192.0.2.1");
        activity.ReadLogEntry("2026-10-07T14:43:01Z,0.0,1234,6,Info [FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:999001, universeid:1359573625, userid:1");
        activity.ReadLogEntry("2026-10-07T14:43:01Z,0.0,1234,7,Debug [FLog::Network] UDMUX Address = 192.0.2.2, Port = 52535 | RCC Server Address = 192.0.2.1, Port = 52535");
        activity.ReadLogEntry("2026-10-07T14:43:01Z,0.0,1234,7 [FLog::Network] serverId: 192.0.2.2|52535");
        check(rejoined.Task.Wait(TimeSpan.FromSeconds(5)), "Rejoining the same server is evaluated again after leaving rather than discarded as a duplicate");
        viewModel.PollSession();
        check(viewModel.SessionLocation.Contains("London"), "Current Session loads again when the same place/job is rejoined");
        CompetitiveNetworkState.Write(current with { ProcessId = 1234 });
        network.Dispose();
        check(CompetitiveNetworkState.TryRead()?.ProcessId == 1234, "Exiting a watcher preserves another client's session");
        CompetitiveNetworkState.ClearIfOwned(1234);
        CompetitiveNetworkState.Write(current with { ProcessId = int.MaxValue });
        viewModel.PollSession();
        check(viewModel.SessionLocation == "—", "A stale state file from a terminated client cannot appear as Current Session even when its watcher exited abruptly");
        CompetitiveNetworkState.ClearIfOwned(int.MaxValue);
        File.WriteAllText(CompetitiveNetworkState.FilePath,
            "{\"processId\":0,\"warp\":null,\"regionQuality\":null,\"location\":null,\"jobId\":null,\"colo\":null,\"udmuxIp\":null,\"rccIp\":null,\"runLabel\":null,\"tracerouteFile\":null}");
        viewModel.PollSession();
        check(viewModel.SessionWarp == "UNKNOWN" && viewModel.SessionLocation == "Unknown" &&
            viewModel.SessionQuality == "Unknown" && viewModel.SessionJobId == "—" && viewModel.SessionTraceFile == "",
            "Explicit null optional fields in an older or damaged watcher state cannot crash Current Session polling");
        File.WriteAllText(CompetitiveNetworkState.FilePath, "{invalid json");
        viewModel.PollSession();
        check(viewModel.SessionLocation == "—" && viewModel.SessionWarp == "—",
            "A corrupt watcher state clears displayed session fields instead of preserving old data");
        CompetitiveNetworkState.Write(current with { ProcessId = 0 });
        viewModel.PollSession();
        check(viewModel.SessionLocation.Contains("London"), "Current Session recovers when the next valid watcher state arrives");
        CompetitiveNetworkState.ClearIfOwned(0);
        check(ActivityWatcher.IsPlayerSessionLog("fixture_Player_123_last.log") && !ActivityWatcher.IsPlayerSessionLog("fixture_Player_CrashHandler_last.log"),
            "Crash-handler logs cannot be selected as the Player activity log");
        var launched = DateTime.Now.AddMinutes(-1);
        check(ActivityWatcher.IsSessionLogTime(launched.AddSeconds(5), launched.AddSeconds(-2), DateTime.Now) &&
            !ActivityWatcher.IsSessionLogTime(launched.AddMinutes(-1), launched.AddSeconds(-2), DateTime.Now),
            "Delayed watcher startup still accepts this client's log while excluding an earlier session");
        GlobalCache.ServerLocation.TryRemove("192.0.2.2", out _);
        RunReliabilityChecks(check);
        App.Settings.Prop = new Settings();
    }

    private static void Join(ActivityWatcher activity, string job, bool universe = true)
    {
        activity.ReadLogEntry($"[FLog::Output] ! Joining game '{job}' place 999002 at 192.0.2.10");
        if (universe) activity.ReadLogEntry("[FLog::GameJoinLoadTime] Report game_join_loadtime: placeid:999002, universeid:1359573625, userid:1");
    }

    private static void RunReliabilityChecks(Action<bool, string> check)
    {
        using (var parser = new ActivityWatcher("parser-fixture.log"))
        {
            int joins = 0, leaves = 0, endpointUpdates = 0;
            parser.OnGameJoin += (_, _) => joins++;
            parser.OnConnectionUpdated += (_, _) => endpointUpdates++;
            parser.OnGameLeave += (_, _) => leaves++;
            Join(parser, Guid.NewGuid().ToString());
            parser.ReadLogEntry("[FLog::Network] serverId: 192.0.2.11|52535");
            check(!parser.InGame, "An unrelated confirmation cannot confirm a pending join");
            parser.ReadLogEntry("[FLog::Network] UDMUX Address = 192.0.2.11, Port = 52535 | RCC Server Address = 192.0.2.10, Port = 52535");
            check(parser.InGame && joins == 1, "A server confirmation arriving before its matching UDMUX line still confirms the join");
            parser.ReadLogEntry("[FLog::GameJoinUtil] GameJoinUtil::initiateTeleportToReservedServer");
            string second = Guid.NewGuid().ToString(); Join(parser, second);
            parser.ReadLogEntry("[FLog::Network] UDMUX Address = 192.0.2.11, Port = 52535 | RCC Server Address = 192.0.2.10, Port = 52535");
            parser.ReadLogEntry("[FLog::Network] serverId: 192.0.2.10|52535");
            check(parser.InGame && parser.Data.JobId == second && parser.Data.IsTeleport && parser.Data.ServerType == ServerType.Reserved && joins == 2 && leaves == 1,
                "A reserved teleport without a disconnect updates the session and accepts RCC confirmation behind UDMUX");
            parser.ReadLogEntry("[FLog::SingleSurfaceApp] leaveUGCGameInternal");
            check(!parser.InGame && parser.Data.PlaceId == 0 && leaves == 2,
                "Returning to Roblox Home clears the session even without a separate disconnect line");
            Join(parser, Guid.NewGuid().ToString());
            string replacement = Guid.NewGuid().ToString(); Join(parser, replacement);
            parser.ReadLogEntry("[FLog::Network] serverId: 192.0.2.10|52535");
            check(parser.InGame && parser.Data.JobId == replacement && joins == 3,
                "A canceled pending join cannot prevent the next join from being tracked");
            parser.ReadLogEntry("[FLog::Network] UDMUX Address = 192.0.2.11, Port = 52535 | RCC Server Address = 192.0.2.10, Port = 52535");
            parser.ReadLogEntry("[FLog::Network] UDMUX Address = 192.0.2.11, Port = 52535 | RCC Server Address = 192.0.2.10, Port = 52535");
            check(endpointUpdates == 1 && joins == 3 && parser.Data.UdmuxAddress == "192.0.2.11",
                "A late UDMUX endpoint refreshes monitoring once without inventing a second game join");
        }

        App.Settings.Prop.CompetitiveNetworkMonitorEnabled = false;
        using (var retryActivity = new ActivityWatcher("retry-fixture.log"))
        using (var retryMonitor = new CompetitiveNetworkMonitor(retryActivity))
        {
            int attempts = 0;
            retryMonitor.LocationRetryDelay = TimeSpan.Zero;
            retryMonitor.UniverseLogWait = TimeSpan.Zero;
            retryMonitor.UniverseLookup = (_, _) => Task.FromResult<long?>(CompetitiveRegionService.DeepwokenUniverseId);
            retryMonitor.LocationLookup = (_, _) => Task.FromResult(Interlocked.Increment(ref attempts) < 3 ? ("", "Unknown") : ("London, United Kingdom", "Fixture"));
            var resolved = new TaskCompletionSource<CompetitiveNetworkEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            retryMonitor.RegionResolved += (_, evt) => resolved.TrySetResult(evt);
            retryMonitor.DiagnosticsCompleted += (_, _) => done.TrySetResult();
            retryMonitor.AutoLogHandler = evt =>
            {
                acted.TrySetResult(BadRegionAutoLog.ShouldLeave(evt, App.Settings.Prop, retryActivity.Data.JobId, DateTime.Now,
                    CompetitiveRegionService.Classify(evt.Location, source: evt.RegionSource)));
                return Task.FromResult(false);
            };
            var diagnosticsGate = (SemaphoreSlim)typeof(CompetitiveNetworkMonitor).GetField("_diagSemaphore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(retryMonitor)!;
            diagnosticsGate.Wait(); diagnosticsGate.Wait();
            Join(retryActivity, Guid.NewGuid().ToString(), universe: false);
            retryActivity.ReadLogEntry("[FLog::Network] serverId: 192.0.2.10|52535");
            check(resolved.Task.Wait(TimeSpan.FromSeconds(5)) && attempts == 3 && resolved.Task.Result.IsDeepwoken,
                "Transient location failures are retried and a missing universe log uses confirmed public place identity");
            check(acted.Task.Wait(TimeSpan.FromSeconds(5)) && acted.Task.Result && !done.Task.IsCompleted &&
                retryActivity.Data.UniverseId == CompetitiveRegionService.DeepwokenUniverseId,
                "Warnings and autolog resolve before queued diagnostics, including when diagnostics are disabled");
            diagnosticsGate.Release(2);
            check(done.Task.Wait(TimeSpan.FromSeconds(5)), "Diagnostic workers complete after the early alert decision");
            check(!App.Settings.Prop.CompetitiveNetworkMonitorEnabled,
                "Starting a live watcher cannot overwrite saved monitoring choices with an old benchmark policy");
        }

        using (var staleActivity = new ActivityWatcher("stale-fixture.log"))
        using (var staleMonitor = new CompetitiveNetworkMonitor(staleActivity))
        {
            var blocked = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int lookups = 0, evaluations = 0;
            staleMonitor.LocationLookup = (_, _) =>
            {
                if (Interlocked.Increment(ref lookups) == 1) { started.TrySetResult(); return blocked.Task; }
                return Task.FromResult(("Dallas, United States", "Fixture"));
            };
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            staleMonitor.RegionResolved += (_, _) => Interlocked.Increment(ref evaluations);
            staleMonitor.DiagnosticsCompleted += (_, _) => done.TrySetResult();
            Join(staleActivity, Guid.NewGuid().ToString());
            staleActivity.ReadLogEntry("[FLog::Network] serverId: 192.0.2.10|52535");
            Task oldEvaluation = staleMonitor.EvaluationTask;
            check(started.Task.Wait(TimeSpan.FromSeconds(5)), "Slow location fixture reaches its in-flight lookup");
            string latestJob = Guid.NewGuid().ToString(); Join(staleActivity, latestJob);
            staleActivity.ReadLogEntry("[FLog::Network] serverId: 192.0.2.10|52535");
            check(done.Task.Wait(TimeSpan.FromSeconds(5)), "A new teleport resolves while the old location lookup is pending");
            blocked.TrySetResult(("London, United Kingdom", "Fixture"));
            check(oldEvaluation.Wait(TimeSpan.FromSeconds(5)) && evaluations == 1 && staleMonitor.LatestEvent?.JobId == latestJob,
                "A stale bad-region lookup cannot replace the new session or trigger another alert");
        }

        string lockedLog = Path.Combine(Paths.Cache, "locked-fixture_Player.log");
        File.WriteAllText(lockedLog, $"[FLog::Output] ! Joining game '{Guid.NewGuid()}' place 999002 at 192.0.2.10\n[FLog::Network] serverId: 192.0.2.10|52535\n");
        using (var lockedActivity = new ActivityWatcher(lockedLog))
        {
            var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lockedActivity.OnGameJoin += (_, _) => joined.TrySetResult();
            Task reader;
            using (var heldFile = new FileStream(lockedLog, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                reader = lockedActivity.RunAsync();
                check(!reader.IsCompleted && !lockedActivity.InGame, "A temporarily locked log waits instead of terminating the watcher");
            }
            check(joined.Task.Wait(TimeSpan.FromSeconds(5)), "The watcher recovers when Roblox's log becomes readable");
            lockedActivity.Dispose();
            check(reader.Wait(TimeSpan.FromSeconds(5)), "Recovered log reader stops cleanly when its client exits");
        }
    }
}
