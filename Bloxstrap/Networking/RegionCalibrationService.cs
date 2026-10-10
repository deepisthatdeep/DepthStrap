using Bloxstrap.Integrations;
using Bloxstrap.Competitive;

namespace Bloxstrap.Networking
{
    internal static class RegionCalibrationService
    {
        private static readonly HttpClient Client = CreateClient();
        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"DepthStrap/{App.Version}"); return client;
        }
        internal static Semaphore OpenOperationGate() => new(1, 1, "DepthStrap-NetworkSetup");
        public static async Task RunAsync(CancellationToken token = default) => await RunComparisonAsync(false, false, null, token);
        public static async Task<NetworkTestResult> RunComparisonAsync(bool reset, bool setupWarp, IProgress<string>? progress, CancellationToken token)
        {
            using var operation = OpenOperationGate();
            if (!operation.WaitOne(0)) throw new InvalidOperationException("Another network test or WARP change is already running.");
            try
            {
                using var activeGame = Process.GetProcessesByName("RobloxPlayerBeta").FirstOrDefault();
                if (activeGame is not null) throw new InvalidOperationException("Close Roblox before testing. The comparison switches your computer's network route.");
                if (reset) { progress?.Report("Clearing network logs and learned preferences…"); NetworkHistory.Reset(); }
                progress?.Report("Checking the current connection and discovering Roblox routing targets…");
                var original = await CloudflareNetworkState.QueryAsync(token, true);
                List<RoutingTarget> targets;
                try { targets = await DiscoverAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var unavailable = new NetworkTestResult { Status = "Public routing metadata is unavailable. No comparison was made and WARP was not changed. Try Reset Network later.", FinalWarpState = original?.WarpActive, CloudflareAvailable = original?.WarpActive is not null };
                    unavailable.Save(); App.Logger.WriteException("NetworkSetup", ex); return unavailable;
                }
                var warp = new WarpClient();
                string setupError = "";
                if (setupWarp)
                {
                    try { await warp.EnsureReadyAsync(App.Settings.Prop.CloudflareTermsAccepted, progress, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { setupError = "WARP setup did not complete: " + ex.Message; setupWarp = false; }
                }
                if (original?.WarpActive is null)
                    for (int attempt = 0; attempt < 3 && original?.WarpActive is null; attempt++)
                    { token.ThrowIfCancellationRequested(); original = await CloudflareNetworkState.QueryAsync(token, true); if (original?.WarpActive is null) await Task.Delay(1000, token); }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(TimeSpan.FromMinutes(NetworkCalibrationProfile.BudgetMinutes));
                Task<CloudflareTraceResult?> State(CancellationToken ct) => CloudflareNetworkState.QueryAsync(ct, true);
                async Task<List<RoutingSample>> Probe(string route, CancellationToken ct)
                {
                    var duration = Stopwatch.StartNew();
                    var measurements = new System.Collections.Concurrent.ConcurrentBag<RoutingSample>();
                    var diagnostics = new CompetitiveNetworkDiagnostics();
                    int tested = 0;
                    await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (target, cancel) =>
                    {
                        var m = await diagnostics.MeasureIcmpAsync(target.Address, NetworkCalibrationProfile.ProbesPerTarget, NetworkCalibrationProfile.ProbeTimeoutMs, cancel, NetworkCalibrationProfile.ProbeSpacingMs);
                        cancel.ThrowIfCancellationRequested();
                        if (m.Received >= 18 && m.AverageMs is double average)
                            measurements.Add(new(target.Address, target.City, target.Country, route, average, m.JitterMs ?? 0, m.LossPercent));
                        progress?.Report($"Measuring {route}: {Interlocked.Increment(ref tested)}/{targets.Count} targets · {NetworkCalibrationProfile.ProbesPerTarget} probes per target. Allow 6–15 minutes; unresponsive targets can take longer.");
                    });
                    var remaining = TimeSpan.FromSeconds(NetworkCalibrationProfile.MinimumPassSeconds) - duration.Elapsed;
                    if (remaining > TimeSpan.Zero) { progress?.Report($"Allowing time between {route} measurement passes…"); await Task.Delay(remaining, ct); }
                    return measurements.ToList();
                }
                ComparisonRun run;
                if (setupWarp && original?.WarpActive is bool originalState)
                    run = await RouteComparisonRunner.RunAsync(warp, originalState, State, Probe, progress, budget.Token);
                else
                {
                    progress?.Report("Testing the current connection; WARP comparison is unavailable…");
                    var current = await State(budget.Token);
                    var samples = new List<RoutingSample>();
                    try { samples = await RouteComparisonRunner.ProbeVerifiedAsync(current, State, Probe, budget.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { setupError += " Routing probes timed out."; }
                    catch (IOException ex) { setupError += " " + ex.Message; }
                    run = new(null, samples, await State(CancellationToken.None), setupError.Length > 0 ? setupError : "WARP comparison was not completed. Accept Cloudflare's terms in network setup and retry. No automatic route recommendation was applied.");
                }
                progress?.Report("Checking region lookup and traceroute on the final route…");
                bool regions = false, trace = false;
                try { regions = await CompetitiveRegionService.EnsureRegistryLoadedAsync().WaitAsync(TimeSpan.FromSeconds(20), token); }
                catch (Exception ex) { App.Logger.WriteException("NetworkSetup::Regions", ex); }
                string routeKey = AdaptiveRegionService.RouteKey(run.FinalState);
                var finalSamples = run.Samples.Where(x => x.Route == routeKey).ToList();
                if (finalSamples.Count > 0 && !token.IsCancellationRequested)
                {
                    using var traceBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                    traceBudget.CancelAfter(TimeSpan.FromSeconds(12));
                    try
                    {
                        string? file = await new CompetitiveNetworkDiagnostics().RunTracerouteAsync(finalSamples.OrderBy(x => x.Cost).First().Address, "setup", traceBudget.Token);
                        trace = file is not null && Regex.IsMatch(File.ReadAllText(file), @"(?m)^\s*\d+\s+.*\b\d{1,3}(?:\.\d{1,3}){3}\b");
                    }
                    catch (OperationCanceledException) { /* unavailable diagnostic capability */ }
                }
                token.ThrowIfCancellationRequested();
                bool completed = run.Decision?.UseWarp is not null && run.Error.Length == 0 && run.FinalState?.WarpActive is not null;
                var ipv4Samples = finalSamples.Where(x => IPAddress.TryParse(x.Address, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToList();
                string finalState = run.FinalState?.WarpActive switch { true => "ON", false => "OFF", _ => "UNKNOWN — check Cloudflare WARP" };
                string scores = run.Decision?.DirectScore is double d && run.Decision.WarpScore is double w
                    ? $"\nNormal: {d:0.0} ms score · WARP: {w:0.0} ms score · {run.Decision.MatchedTargets} matched routing locations. Scores include ICMP jitter/loss; these are not gameplay ping." : "";
                string directCountry = run.DirectCountry.Length > 0 ? run.DirectCountry : original?.WarpActive == false ? original.Location : "";
                var unprobed = CompetitiveRegionService.UnprobedLocations(targets, directCountry);
                string coverage = $"\nPublished addresses were discovered across {targets.Select(x => (x.City, x.Country)).Distinct().Count()} routing locations; {finalSamples.Select(x => (x.City, x.Country)).Distinct().Count()} produced usable replies on the selected route. Published addresses that do not reply remain unmeasured.";
                if (unprobed.Count > 0) coverage += "\nThe public Roblox peering inventory has no address for: " + string.Join("; ", unprobed.Take(6)) + (unprobed.Count > 6 ? $" (and {unprobed.Count - 6} more)" : "") + ". These are separate from unresponsive probes. They remain untested by setup; usable measurements from actual Roblox joins can inform preferences later.";
                var result = new NetworkTestResult
                {
                    UnprobedRegions = unprobed,
                    DirectCountry = directCountry,
                    SetupFinished = completed || ((run.Error.Length == 0 || !setupWarp) && setupError.Length == 0 && regions && run.FinalState?.WarpActive is not null),
                    Completed = completed, IcmpAvailable = ipv4Samples.Count > 0, CloudflareAvailable = run.FinalState?.WarpActive is not null,
                    RegionsAvailable = regions, TraceAvailable = trace, FinalWarpState = run.FinalState?.WarpActive, Comparison = run.Decision,
                    Status = (run.Error.Length > 0 ? run.Error : run.Decision?.Reason ?? "Comparison unavailable.") + scores + coverage + "\nWARP is " + finalState + ". Network features were adjusted to the capabilities measured on this route."
                };
                result.Save();
                var observations = run.Samples.Where(x => IPAddress.TryParse(x.Address, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(x => new RegionObservation { City = x.City, Country = x.Country, Route = x.Route, LocalHour = DateTime.Now.Hour,
                    IsCalibration = true, AverageMs = x.AverageMs, JitterMs = x.JitterMs, IcmpLossPercent = x.LossPercent });
                await AdaptiveRegionService.RecordAsync(observations, routeKey, CancellationToken.None);
                Directory.CreateDirectory(Paths.Cache);
                File.WriteAllText(Path.Combine(Paths.Cache, "RegionCalibration.json"), JsonSerializer.Serialize(new { completedAt = result.CompletedAt, successfulTargets = finalSamples.Count, status = result.Status }));
                return result;
            }
            finally { operation.Release(); }
        }
        internal static async Task<List<RoutingTarget>> DiscoverAsync(CancellationToken token)
        {
            using var links = JsonDocument.Parse(await PublicNetworkHttp.GetAsync(Client, "https://www.peeringdb.com/api/netixlan?net_id=14578", token));
            var operational = links.RootElement.GetProperty("data").EnumerateArray()
                .Where(x => x.GetProperty("status").GetString() == "ok" && x.GetProperty("operational").GetBoolean())
                .Select(x => x.GetProperty("ix_id").GetInt32()).Distinct().ToList();
            string ids = string.Join(',', operational);
            if (ids.Length == 0) throw new InvalidDataException("No public Roblox routing targets are available.");
            using var exchanges = JsonDocument.Parse(await PublicNetworkHttp.GetAsync(Client, "https://www.peeringdb.com/api/ix?depth=0&id__in=" + ids, token));
            var targets = RoutingTargetDiscovery.Parse(links.RootElement.GetRawText(), exchanges.RootElement.GetRawText());
            if (targets.Count == 0) throw new InvalidDataException("Routing locations did not include usable published addresses.");
            return targets;
        }
    }
}
