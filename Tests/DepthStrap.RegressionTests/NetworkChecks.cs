using System.IO;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Enums;
using Bloxstrap.Integrations;
using Bloxstrap.Models;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Networking;
using Bloxstrap.Roblox;
using Bloxstrap.UI.ViewModels.Settings;

internal static class NetworkChecks
{
    private sealed class FakeWarp : IWarpClient
    {
        public bool Connected;
        public bool FailNextConnect;
        public bool FailDisconnect;
        public List<bool> Changes = new();
        public Task EnsureReadyAsync(bool accepted, IProgress<string>? progress, CancellationToken token) => Task.CompletedTask;
        public Task SetConnectedAsync(bool connected, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Changes.Add(connected);
            if (connected && FailNextConnect) { FailNextConnect = false; throw new IOException("Fixture connection failure"); }
            if (!connected && FailDisconnect) throw new IOException("Fixture restoration failure");
            Connected = connected; return Task.CompletedTask;
        }
        public Task<CloudflareTraceResult?> State(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<CloudflareTraceResult?>(new() { Warp = Connected ? "on" : "off", Colo = "FIXTURE" });
        }
    }
    private static List<RoutingSample> Samples(string route, double score, double loss = 0) => Enumerable.Range(1, 3)
        .Select(i => new RoutingSample("192.0.2." + i, "Fixture " + i, "US", route, score + i, 0, loss)).ToList();
    public static void Run(Action<bool, string> check, string? verifiedPackage)
    {
        check(NetworkComparison.Decide(Samples("direct", 60), Samples("warp:FIXTURE", 20)).UseWarp == true, "WARP needs meaningful improvement across matching targets");
        check(NetworkComparison.Decide(Samples("direct", 20), Samples("warp:FIXTURE", 60)).UseWarp == false, "Slower WARP selects normal routing");
        check(NetworkComparison.Decide(Samples("direct", 20), Samples("warp:FIXTURE", 18)).UseWarp == false, "Small timing differences do not enable WARP");
        check(NetworkComparison.Decide(Samples("direct", 60), Samples("warp:FIXTURE", 20, 30)).UseWarp == false, "Higher echo loss prevents an automatic tunnel recommendation");
        check(NetworkComparison.Decide(Samples("direct", 60), Samples("warp:FIXTURE", 20).Take(1)).UseWarp is null, "Insufficient matched targets stay inconclusive");
        var unavailable = NetworkComparison.Decide(Array.Empty<RoutingSample>(), Array.Empty<RoutingSample>());
        check(unavailable.UseWarp is null && unavailable.Reason.Contains("normal routing had 0") && unavailable.Reason.Contains("does not mean Roblox cannot connect") &&
              unavailable.Reason.Contains("manual WARP toggle"), "Blocked routing probes explain the coverage and retain playable manual controls");
        check(NetworkComparison.Decide(Samples("direct", 60), Samples("warp:FIXTURE", double.NaN)).UseWarp is null, "Invalid probe values are excluded");
        var oneWarpTarget = Enumerable.Repeat(Samples("warp:FIXTURE", 20)[0], 5);
        check(NetworkComparison.Decide(Samples("direct", 60), oneWarpTarget).UseWarp is null, "Repeated replies from one address cannot count as independent WARP targets");
        check(NetworkComparison.Decide(Samples("direct", 60).Select(x => x with { City = "Same city" }), Samples("warp:FIXTURE", 20).Select(x => x with { City = "Same city" })).UseWarp is null,
            "Multiple interfaces at one routing location cannot count as independent evidence");
        check(NetworkComparison.Decide(Samples("direct", 60), Samples("warp:FIXTURE", 20).Concat(Samples("warp:FIXTURE", 100))).UseWarp == false,
            "The repeated WARP pass prevents one transient fast pass from enabling a worse tunnel");
        Task.Run(async () =>
        {
            var warp = new FakeWarp();
            int polls = 0;
            var settled = await RouteComparisonRunner.SwitchAsync(warp, true, _ => Task.FromResult<CloudflareTraceResult?>(new CloudflareTraceResult { Warp = ++polls < 9 ? "off" : "on" }), CancellationToken.None, 0, 0);
            check(settled.WarpActive == true && polls == 12, "A slower WARP transition requires three verified states and a post-settling check");
            warp = new FakeWarp(); polls = 0;
            settled = await RouteComparisonRunner.SwitchAsync(warp, true, _ => Task.FromResult<CloudflareTraceResult?>(new CloudflareTraceResult { Warp = ++polls == 4 ? "off" : "on" }), CancellationToken.None, 0, 0);
            check(settled.WarpActive == true && polls == 8, "A route that flaps during settling must be confirmed again before measurement");
            warp = new FakeWarp();
            Task<List<RoutingSample>> Faster(string route, CancellationToken token) => Task.FromResult(Samples(route, route == "direct" ? 60 : 20));
            var run = await RouteComparisonRunner.RunAsync(warp, false, warp.State, Faster, null, CancellationToken.None, 0, 0);
            check(run.FinalState?.WarpActive == true && run.Decision?.UseWarp == true && warp.Changes.SequenceEqual(new[] { false, true, false, true, false, true, true }), "A/B comparison verifies three passes on both routes and enables the faster tunnel");
            warp = new FakeWarp { Connected = true };
            Task<List<RoutingSample>> Slower(string route, CancellationToken token) => Task.FromResult(Samples(route, route == "direct" ? 20 : 60));
            run = await RouteComparisonRunner.RunAsync(warp, true, warp.State, Slower, null, CancellationToken.None, 0, 0);
            check(run.FinalState?.WarpActive == false && run.Decision?.UseWarp == false, "Normal routing is selected and reported when the tunnel is slower");
            warp = new FakeWarp { Connected = true };
            run = await RouteComparisonRunner.RunAsync(warp, true, warp.State, (_, _) => Task.FromResult(new List<RoutingSample>()), null, CancellationToken.None, 0, 0);
            check(run.Decision?.UseWarp is null && run.FinalState?.WarpActive == true, "No replies restores the starting WARP state");
            warp = new FakeWarp { FailNextConnect = true };
            run = await RouteComparisonRunner.RunAsync(warp, false, warp.State, Faster, null, CancellationToken.None, 0, 0);
            check(run.Error.Length > 0 && run.FinalState?.WarpActive == false && warp.Changes.Last() == false, "Connection failure restores normal routing");
            warp = new FakeWarp { Connected = true };
            using var cancellation = new CancellationTokenSource();
            run = await RouteComparisonRunner.RunAsync(warp, true, warp.State, (_, _) => { cancellation.Cancel(); throw new OperationCanceledException(); }, null, cancellation.Token, 0, 0);
            check(run.Error.Contains("cancelled") && run.FinalState?.WarpActive == true, "Cancellation restores the original state with a separate restoration budget");
            warp = new FakeWarp { FailDisconnect = true };
            run = await RouteComparisonRunner.RunAsync(warp, false, warp.State, Faster, null, CancellationToken.None, 0, 0);
            check(run.FinalState is null && run.Error.Contains("could not be restored"), "Failed restoration is reported as unknown, never a claimed success");
            warp = new FakeWarp();
            run = await RouteComparisonRunner.RunAsync(warp, false, warp.State, (route, _) => Task.FromResult(new List<RoutingSample>
                { new("2001:db8::1", "Fixture", "US", route, route == "direct" ? 90 : 1, 0, 0), new("2001:db8::2", "Fixture", "US", route, route == "direct" ? 90 : 1, 0, 0) }), null, CancellationToken.None, 0, 0);
            check(run.Decision?.UseWarp is null && run.FinalState?.WarpActive == false, "Fast IPv6 alone cannot recommend a tunnel for IPv4 gameplay");
            warp = new FakeWarp();
            run = await RouteComparisonRunner.RunAsync(warp, false, warp.State, (route, _) => { var samples = Samples(route, 20); if (route.StartsWith("warp:")) warp.Connected = false; return Task.FromResult(samples); }, null, CancellationToken.None, 0, 0);
            check(run.Decision is null && run.Error.Contains("during measurement") && run.FinalState?.WarpActive == false, "A route change during probing rejects mixed measurements and restores the original route");
            warp = new FakeWarp(); int connectedPasses = 0;
            Task<CloudflareTraceResult?> ChangingIngress(CancellationToken token) => Task.FromResult<CloudflareTraceResult?>(new() { Warp = warp.Connected ? "on" : "off", Colo = connectedPasses >= 1 ? "SECOND" : "FIRST" });
            run = await RouteComparisonRunner.RunAsync(warp, false, ChangingIngress, (route, _) => { if (route.StartsWith("warp:")) connectedPasses++; return Task.FromResult(Samples(route, route == "direct" ? 60 : 20)); }, null, CancellationToken.None, 0, 0);
            check(run.Decision is null && run.Error.Length > 0 && run.FinalState?.WarpActive == false, "Measurements from changing WARP ingress cannot produce a recommendation");
            warp = new FakeWarp();
            Task<CloudflareTraceResult?> FinalIngress(CancellationToken token) => Task.FromResult<CloudflareTraceResult?>(new() { Warp = warp.Connected ? "on" : "off", Colo = warp.Changes.Count(x => x) >= 4 ? "SECOND" : "FIRST" });
            run = await RouteComparisonRunner.RunAsync(warp, false, FinalIngress, Faster, null, CancellationToken.None, 0, 0);
            check(run.Decision is null && run.Error.Contains("final WARP ingress") && run.FinalState?.WarpActive == false, "A different final ingress cannot apply the tested WARP recommendation");
            warp = new FakeWarp(); int passes = 0;
            run = await RouteComparisonRunner.RunAsync(warp, false, warp.State, (route, _) => Task.FromResult(++passes <= 2 ? Samples(route, route == "direct" ? 60 : 20) : new List<RoutingSample>()), null, CancellationToken.None, 0, 0);
            check(run.Decision?.UseWarp is null && run.FinalState?.WarpActive == false, "One successful pass per route is insufficient to recommend WARP");
            warp = new FakeWarp(); int directPasses = 0, warpPasses = 0;
            run = await RouteComparisonRunner.RunAsync(warp, false, warp.State, (route, _) =>
            {
                int pass = route == "direct" ? ++directPasses : ++warpPasses;
                return Task.FromResult(pass is 1 or 4 ? Samples(route, route == "direct" ? 60 : 20) : new List<RoutingSample>());
            }, null, CancellationToken.None, 0, 0);
            check(directPasses == 4 && warpPasses == 4 && run.Decision?.UseWarp == true && run.FinalState?.WarpActive == true,
                "Partial coverage receives a fourth verified pair and only recommends after repeat evidence is recovered");
            warp = new FakeWarp { Connected = true }; int emptyPasses = 0;
            run = await RouteComparisonRunner.RunAsync(warp, true, warp.State, (route, _) => { emptyPasses++; return Task.FromResult(new List<RoutingSample>()); }, null, CancellationToken.None, 0, 0);
            check(emptyPasses == 6 && run.Decision?.UseWarp is null && run.Error.Length == 0 && run.FinalState?.WarpActive == true,
                "Wholly blocked probes finish inconclusively and retain the original ON state without endless retests");
            if (verifiedPackage is not null)
            {
                try { await WarpClient.VerifyPublisherAsync(Path.GetFullPath(verifiedPackage), CancellationToken.None); }
                catch { foreach (string message in App.Logger.History.Where(x => x.Contains("WarpSignature"))) Console.WriteLine(message); throw; }
                check(true, "Official MSI passes the production Authenticode verification path");
                string fake = Path.Combine(Paths.Cache, "unsigned-fixture.msi"); File.WriteAllText(fake, "fixture");
                bool rejected = false;
                try { await WarpClient.VerifyPublisherAsync(fake, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
                check(rejected, "Unsigned installation packages are rejected");
            }
        }).GetAwaiter().GetResult();
        check(NetworkCalibrationProfile.PassesPerRoute == 3 && NetworkCalibrationProfile.ProbesPerTarget == 24 &&
            NetworkCalibrationProfile.MinimumPassSeconds >= 45 && NetworkCalibrationProfile.SettlingMs >= 15000 && NetworkCalibrationProfile.BudgetMinutes >= 45,
            "Initialization gives each route three longer passes, 24 probes, settling time and a 45-minute total budget");
        var parsed = FastFlagImport.Parse("{\"FFlagDebugSkyGray\":true,\"FIntDebugForceMSAASamples\":1}");
        check(parsed.Count == 2 && parsed["FIntDebugForceMSAASamples"] == "1", "FastFlags accepts scalar JSON values");
        var legacy = FastFlagImport.ParseDetailed("{\"CSGLevelOfDetailSwitchingDistance\":\"0\",\"DFIntCSGLevelOfDetailSwitchingDistance\":\"1\",\"DebugDisplayFPS\":false,\"FFlagDebugDisplayFPS\":true,\"UnknownShortName\":1}");
        check(legacy.Flags.Count == 5 && legacy.Flags["CSGLevelOfDetailSwitchingDistance"] == "0" && legacy.Flags["DFIntCSGLevelOfDetailSwitchingDistance"] == "1" &&
            legacy.Flags["DebugDisplayFPS"] == "False" && legacy.Flags["FFlagDebugDisplayFPS"] == "True" && legacy.Flags["UnknownShortName"] == "1",
            "Froststrap exports retain every short and full key separately without alias guesses or conflicting-value loss");
        var reversed = FastFlagImport.Parse("{\"FFlagDebugDisplayFPS\":true,\"DebugDisplayFPS\":false}");
        check(reversed["FFlagDebugDisplayFPS"] == "True" && reversed["DebugDisplayFPS"] == "False",
            "Short and full flag preservation is independent of JSON property order");
        var ambiguous = FastFlagImport.Parse("{\"FFlagExample\":true,\"DFFlagExample\":false,\"Example\":true}");
        check(ambiguous.Count == 3 && ambiguous["Example"] == "True",
            "Shared suffixes do not rename or discard any imported key");
        var fixture = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string prefix in new[] { "FFlag", "DFFlag", "SFFlag", "FInt", "DFInt", "SFInt", "FString", "DFString", "SFString", "FLog", "DFLog", "SFLog" })
        {
            fixture[prefix + "Example"] = prefix.Contains("Flag") ? "False" : "123";
            fixture[prefix + "Example_PlaceFilter"] = "true;123;456";
            fixture[prefix + "Example_DataCenterFilter"] = "value;1;2";
        }
        fixture["DFIntCaseSensitive"] = "001";
        fixture["DFIntCasesensitive"] = "002";
        fixture["FStringLongValue"] = new string('a', 8192);
        fixture["FFlagUnknownFutureFlag"] = "True";
        for (int i = 0; i < 11_000; i++) fixture["FFlagBulk" + i] = "False";
        var complete = FastFlagImport.Parse(JsonSerializer.Serialize(fixture));
        check(complete.Count == fixture.Count && fixture.All(x => complete[x.Key] == x.Value),
            "All flag families, filters, long strings, case-distinct keys and a registry over 10,000 entries import without a catalog");
        App.FastFlags.Prop.Clear();
        App.FastFlags.suspendUndoSnapshot = true;
        try { foreach (var flag in complete) App.FastFlags.SetValue(flag.Key, flag.Value); }
        finally { App.FastFlags.suspendUndoSnapshot = false; }
        App.FastFlags.Save();
        App.FastFlags.Prop.Clear();
        App.FastFlags.Load();
        var roundTrip = FastFlagImport.Parse(File.ReadAllText(App.FastFlags.FileLocation));
        check(roundTrip.Count == fixture.Count && fixture.All(x => roundTrip[x.Key] == x.Value) && fixture.All(x => App.FastFlags.GetValue(x.Key) == x.Value),
            "Compatible imported flags survive the production manager save, reload and export unchanged");
        App.FastFlags.Prop.Clear(); App.FastFlags.Save();
        var members = FastFlagImport.Parse("\uFEFF \"FFlagExample\":\"True\", // comment\n \"DFIntExample\":-1,");
        check(members.Count == 2 && members["FFlagExample"] == "True" && members["DFIntExample"] == "-1",
            "Pasted object members support a BOM, comments, omitted outer braces and a trailing comma");
        var nulls = FastFlagImport.ParseDetailed("{\"FFlagIgnored\":null,\"FStringEmpty\":\"\",\"DFIntNegative\":-10}");
        check(nulls.SkippedNulls == 1 && nulls.Flags.Count == 2 && nulls.Flags["FStringEmpty"] == "" && nulls.Flags["DFIntNegative"] == "-10",
            "Upstream null entries are skipped while empty strings and negative values are preserved");
        foreach (string input in new[] { "[]", "{\"FFlagFoo\":{}}", "{\"bad name\":1}", "{\"FFlagFoo\":1,\"FFlagFoo\":2}", "{\"FFlagFoo\":null,\"FFlagFoo\":1}", "{\"FFlagFoo\":1} trailing text" })
        {
            bool rejected = false;
            try { FastFlagImport.Parse(input); } catch { rejected = true; }
            check(rejected, "Invalid or duplicate FastFlags input fails before mutation: " + input);
        }
        check(!new CookiesManager().Loaded && !JsonSerializer.Serialize(new Settings()).Contains("AllowCookieAccess"), "Roblox account access is absent from runtime and saved settings");
        App.Settings.Prop = new Settings { CompetitiveModeEnabled = true, CompetitivePerformanceEnabled = true, CompetitiveProcessPriority = ProcessPriorityOption.High, SelectedProcessPriority = ProcessPriorityOption.Normal };
        var competitive = new CompetitivePageViewModel();
        check(competitive.SelectedCompetitivePriority == ProcessPriorityOption.High, "Roblox Settings displays the effective Competitive priority");
        competitive.SelectedCompetitivePriority = ProcessPriorityOption.AboveNormal;
        check(competitive.SelectedCompetitivePriority == ProcessPriorityOption.AboveNormal && App.Settings.Prop.SelectedProcessPriority == ProcessPriorityOption.AboveNormal,
            "The single CPU priority control updates both Competitive and normal launch priority");
        App.Settings.Prop.MatchFpsToMonitorRefreshRate = false; App.Settings.Prop.CompetitiveFpsCap = 540;
        check(competitive.SelectedFpsPreset.Label == "Custom" && competitive.FpsStatus.Contains("540"), "A custom cap is not displayed as uncapped");
        var policy = new NetworkTestResult { IcmpAvailable = false, CloudflareAvailable = true, RegionsAvailable = true, TraceAvailable = false };
        policy.Apply(App.Settings.Prop);
        check(!App.Settings.Prop.CompetitiveIcmpEnabled && !App.Settings.Prop.AutoSelectPreferredServerOnLaunch && !App.Settings.Prop.CompetitiveTracerouteEnabled && App.Settings.Prop.ChimeRegionMonitorEnabled && App.Settings.Prop.CompetitiveCloudflareDetectionEnabled, "Features follow measured capability without mistaking blocked ICMP for absent region lookup");
        new NetworkTestResult { IcmpAvailable = true, RegionsAvailable = true, CloudflareAvailable = true, TraceAvailable = true }.Apply(App.Settings.Prop);
        check(App.Settings.Prop.CompetitiveIcmpEnabled && App.Settings.Prop.AutoSelectPreferredServerOnLaunch && App.Settings.Prop.CompetitiveTracerouteEnabled && !App.Settings.Prop.StoreFullEgressIpInLogs, "Successful follow-up tests enable capabilities and preserve masked logging");
        new NetworkTestResult { SetupFinished = true, Completed = false, RegionsAvailable = true, CloudflareAvailable = true,
            Comparison = unavailable, FinalWarpState = false }.Save();
        var inconclusiveSettings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(App.Settings.FileLocation))!;
        check(inconclusiveSettings.NetworkSetupVersion == 1 && !inconclusiveSettings.RegionCalibrationCompleted && inconclusiveSettings.WarnOnBadChimeRegion,
            "An inconclusive route comparison finishes setup without falsely claiming calibration or rerunning setup on every launch");
        var old = DateTime.UtcNow.AddMinutes(-1);
        Directory.CreateDirectory(Path.Combine(Paths.Logs, "CompetitiveRoutes"));
        File.WriteAllText(Path.Combine(Paths.Logs, "CompetitiveRoutes", "fixture.txt"), "fixture");
        File.WriteAllText(Path.Combine(Paths.Cache, "CompetitiveNetworkState.json"), "{}");
        File.WriteAllText(Path.Combine(Paths.Logs, "keep-app.log"), "keep");
        App.Settings.Prop.CompetitivePreferredCity = "Fixture"; App.Settings.Save();
        NetworkHistory.Reset();
        check(!File.Exists(Path.Combine(Paths.Cache, "LearnedRegions.json")) && !Directory.Exists(CompetitiveSessionLogger.GetSessionsDir()) && !Directory.Exists(Path.Combine(Paths.Logs, "CompetitiveRoutes")) && File.Exists(Path.Combine(Paths.Logs, "keep-app.log")), "Reset clears network history while retaining other application data");
        CompetitiveSessionLogger.WriteNetworkEventAsync(new CompetitiveNetworkEvent { Timestamp = old, JobId = "stale" }).GetAwaiter().GetResult();
        CompetitiveNetworkState.Write(new CompetitiveNetworkEvent { Timestamp = old, JobId = "stale" });
        check(!File.Exists(CompetitiveSessionLogger.GetJsonlPath()) && !File.Exists(CompetitiveNetworkState.FilePath), "Late diagnostics from before reset cannot repopulate history or live state");
        var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(App.Settings.FileLocation))!;
        check(saved.CompetitivePreferredCity.Length == 0 && saved.CompetitiveFallbackCities.Count == 0 && saved.CompetitiveFpsCap == 540, "Reset persists cleared learned regions without replacing graphics settings");
        App.Settings.Prop = new Settings();
    }
}
