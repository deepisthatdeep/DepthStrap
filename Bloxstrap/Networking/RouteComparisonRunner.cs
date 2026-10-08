using Bloxstrap.Integrations;

namespace Bloxstrap.Networking
{
    internal sealed record ComparisonRun(RouteDecision? Decision, List<RoutingSample> Samples, CloudflareTraceResult? FinalState, string Error, string DirectCountry = "");
    internal static class RouteComparisonRunner
    {
        internal static async Task<List<RoutingSample>> ProbeVerifiedAsync(CloudflareTraceResult? expected,
            Func<CancellationToken, Task<CloudflareTraceResult?>> state,
            Func<string, CancellationToken, Task<List<RoutingSample>>> probe, CancellationToken token)
        {
            string route = AdaptiveRegionService.RouteKey(expected);
            var measured = await probe(route, token);
            var after = await state(token);
            if (after?.WarpActive != expected?.WarpActive || AdaptiveRegionService.RouteKey(after) != route)
                throw new IOException("The network route changed during measurement. No route recommendation was applied; retry the comparison.");
            return measured;
        }
        internal static async Task<CloudflareTraceResult> SwitchAsync(IWarpClient client, bool connected,
            Func<CancellationToken, Task<CloudflareTraceResult?>> state, CancellationToken token, int settlingMs = NetworkCalibrationProfile.SettlingMs, int pollingMs = 1000)
        {
            using var transition = CancellationTokenSource.CreateLinkedTokenSource(token);
            transition.CancelAfter(TimeSpan.FromSeconds(NetworkCalibrationProfile.TransitionSeconds));
            token = transition.Token;
            await client.SetConnectedAsync(connected, token);
            int confirmed = 0;
            string previousRoute = "";
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(pollingMs, token);
                var actual = await state(token);
                string route = AdaptiveRegionService.RouteKey(actual);
                confirmed = actual?.WarpActive == connected ? route == previousRoute ? confirmed + 1 : 1 : 0;
                previousRoute = route;
                if (confirmed >= 3)
                {
                    await Task.Delay(settlingMs, token);
                    var settled = await state(token);
                    if (settled?.WarpActive == connected && AdaptiveRegionService.RouteKey(settled) == route) return settled;
                    confirmed = 0;
                    previousRoute = "";
                }
            }
            throw new IOException("WARP route change could not be verified. The comparison was stopped.");
        }
        public static async Task<ComparisonRun> RunAsync(IWarpClient client, bool original,
            Func<CancellationToken, Task<CloudflareTraceResult?>> state,
            Func<string, CancellationToken, Task<List<RoutingSample>>> probe,
            IProgress<string>? progress, CancellationToken token, int settlingMs = NetworkCalibrationProfile.SettlingMs, int pollingMs = 1000)
        {
            var samples = new List<RoutingSample>();
            RouteDecision? decision = null;
            CloudflareTraceResult? final = null;
            string error = "", directCountry = "";
            bool selected = false;
            string? testedWarpRoute = null;
            try
            {
                for (int pass = 1; pass <= NetworkCalibrationProfile.PassesPerRoute; pass++)
                {
                    progress?.Report($"Testing normal routing, pass {pass} of {NetworkCalibrationProfile.PassesPerRoute}…");
                    var direct = await SwitchAsync(client, false, state, token, settlingMs, pollingMs);
                    directCountry = direct.Location;
                    await ProbeVerifiedAsync(direct);
                    progress?.Report($"Testing Cloudflare WARP, pass {pass} of {NetworkCalibrationProfile.PassesPerRoute}…");
                    var tunneled = await SwitchAsync(client, true, state, token, settlingMs, pollingMs);
                    string warpRoute = AdaptiveRegionService.RouteKey(tunneled);
                    if (testedWarpRoute is not null && testedWarpRoute != warpRoute)
                        throw new IOException("WARP ingress changed between passes. No route recommendation was applied; retry the comparison.");
                    testedWarpRoute = warpRoute;
                    await ProbeVerifiedAsync(tunneled);
                }
                // The observed Roblox gameplay endpoints are IPv4. IPv6 coverage must not
                // select a tunnel on the strength of a different address family alone.
                decision = DecideRepeatedSamples(samples);
                // A partial match can be a transient failure rather than blocked ICMP.
                // Give it one additional pair of verified passes without weakening evidence.
                if (decision.UseWarp is null && HasIpv4Replies(samples, false) && HasIpv4Replies(samples, true))
                {
                    progress?.Report("Partial routing coverage; retrying normal routing and WARP once more…");
                    await ProbeVerifiedAsync(await SwitchAsync(client, false, state, token, settlingMs, pollingMs));
                    var retryWarp = await SwitchAsync(client, true, state, token, settlingMs, pollingMs);
                    if (AdaptiveRegionService.RouteKey(retryWarp) != testedWarpRoute)
                        throw new IOException("WARP ingress changed during the retry. No route recommendation was applied; retry the comparison.");
                    await ProbeVerifiedAsync(retryWarp);
                    decision = DecideRepeatedSamples(samples);
                }
                bool target = decision.UseWarp ?? original;
                progress?.Report("Applying and verifying the selected route…");
                final = await SwitchAsync(client, target, state, token, settlingMs, pollingMs);
                if (decision.UseWarp == true && AdaptiveRegionService.RouteKey(final) != testedWarpRoute)
                    throw new IOException("The final WARP ingress differs from the tested route. No route recommendation was applied; retry the comparison.");
                selected = true;

                async Task ProbeVerifiedAsync(CloudflareTraceResult expected)
                {
                    samples.AddRange(await RouteComparisonRunner.ProbeVerifiedAsync(expected, state, probe, token));
                }
            }
            catch (Exception ex)
            {
                decision = null; // A failed application must not retain a successful recommendation.
                error = ex is OperationCanceledException ? "Network comparison was cancelled or timed out." : ex.Message;
                App.Logger.WriteException("NetworkComparison", ex);
            }
            finally
            {
                if (!selected)
                {
                    progress?.Report("Restoring the starting WARP state…");
                    // Restoration has its own budget even after cancellation of the comparison.
                    using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(NetworkCalibrationProfile.TransitionSeconds + 30));
                    try { final = await SwitchAsync(client, original, state, restore.Token, settlingMs, pollingMs); }
                    catch (Exception ex) { final = null; error += " Starting route could not be restored automatically. Open Cloudflare WARP to check your connection. " + ex.Message; }
                }
            }
            return new(decision, samples, final, error, directCountry);
        }
        private static bool HasIpv4Replies(IEnumerable<RoutingSample> samples, bool warp) =>
            samples.Any(x => x.Usable && (warp ? x.Route.StartsWith("warp:", StringComparison.Ordinal) : x.Route == "direct") &&
                IPAddress.TryParse(x.Address, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

        internal static RouteDecision DecideRepeatedSamples(IEnumerable<RoutingSample> samples)
        {
            var repeated = samples.Where(x => IPAddress.TryParse(x.Address, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .GroupBy(x => (x.Route, x.Address)).Where(group => group.Count(x => x.Usable) >= 2).SelectMany(group => group).ToList();
            return NetworkComparison.Decide(repeated.Where(x => x.Route == "direct"), repeated.Where(x => x.Route.StartsWith("warp:", StringComparison.Ordinal)));
        }
    }
}
