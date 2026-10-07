namespace Bloxstrap.Networking
{
    internal sealed record RoutingSample(string Address, string City, string Country, string Route, double AverageMs, double JitterMs, double LossPercent)
    {
        public double Cost => AverageMs + 2 * JitterMs + .5 * LossPercent;
        public bool Usable => double.IsFinite(Cost) && AverageMs >= 0 && JitterMs >= 0 && LossPercent is >= 0 and <= 30;
    }
    internal sealed record RouteDecision(bool? UseWarp, double? DirectScore, double? WarpScore, int MatchedTargets, string Reason);
    internal static class NetworkComparison
    {
        // Combine repeat passes per address, then compare identical independent targets.
        public static RouteDecision Decide(IEnumerable<RoutingSample> direct, IEnumerable<RoutingSample> warp)
        {
            var normal = Combine(direct).ToDictionary(x => x.Address);
            var tunneled = Combine(warp).ToList();
            var paired = tunneled.Where(x => normal.ContainsKey(x.Address))
                .Select(x => (Direct: normal[x.Address], Warp: x))
                // Multiple interfaces in one city are not independent location evidence.
                .GroupBy(x => (x.Direct.City.Trim().ToUpperInvariant(), x.Direct.Country.Trim().ToUpperInvariant()))
                .Select(group => (Direct: Collapse(group.Select(x => x.Direct)), Warp: Collapse(group.Select(x => x.Warp))))
                .OrderBy(x => x.Direct.Cost).Take(3).ToList();
            if (paired.Count < 2)
            {
                int directLocations = normal.Values.Select(x => (x.City, x.Country)).Distinct().Count();
                int warpLocations = tunneled.Select(x => (x.City, x.Country)).Distinct().Count();
                return new(null, null, null, paired.Count,
                    $"Route comparison is inconclusive: normal routing had {directLocations} reliable IPv4 locations, WARP had {warpLocations}, and {paired.Count} matched on both. At least two matching locations with repeat measurements are needed. Some published routing addresses do not answer ICMP on your connection; this does not mean Roblox cannot connect. The starting WARP state was retained. You can keep playing, use the manual WARP toggle, or retry Reset Network later.");
            }
            double d = Median(paired.Select(x => x.Direct.Cost));
            double w = Median(paired.Select(x => x.Warp.Cost));
            bool benefit = d - w >= Math.Max(5, d * .15) && paired.Count(x => x.Warp.Cost < x.Direct.Cost) >= 2 &&
                paired.All(x => x.Warp.LossPercent <= x.Direct.LossPercent + 10);
            return new(benefit, d, w, paired.Count, benefit
                ? "WARP may improve this connection based on matching routing probes. Gameplay ping may differ."
                : "WARP did not show a meaningful advantage in this comparison. Normal routing was selected.");
        }
        private static IEnumerable<RoutingSample> Combine(IEnumerable<RoutingSample> samples) =>
            samples.Where(x => x.Usable).GroupBy(x => x.Address).Select(Collapse);
        private static RoutingSample Collapse(IEnumerable<RoutingSample> samples)
        {
            var group = samples.ToList();
            return group[0] with
            {
                AverageMs = Median(group.Select(x => x.AverageMs)),
                JitterMs = Median(group.Select(x => x.JitterMs)),
                LossPercent = group.Max(x => x.LossPercent)
            };
        }
        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        }
    }
    internal sealed record NetworkTestResult
    {
        public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
        public string DirectCountry { get; init; } = "";
        public List<string> UnprobedRegions { get; init; } = new();
        public bool Completed { get; init; }
        public bool SetupFinished { get; init; }
        public bool IcmpAvailable { get; init; }
        public bool CloudflareAvailable { get; init; }
        public bool RegionsAvailable { get; init; }
        public bool TraceAvailable { get; init; }
        public bool? FinalWarpState { get; init; }
        public RouteDecision? Comparison { get; init; }
        public string Status { get; init; } = "Network setup has not run.";
        public static string FilePath => Path.Combine(Paths.Cache, "NetworkTest.json");
        public static NetworkTestResult? Read()
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<NetworkTestResult>(AtomicFile.ReadText(FilePath)) : null; }
            catch { return null; }
        }
        public void Apply(Settings s)
        {
            RegionGeography.ApplyDetectedPreference(s, DirectCountry);
            s.CompetitiveNetworkMonitorEnabled = true; // endpoint tracking remains useful without ICMP
            s.LogCompetitiveSessions = true;
            s.CompetitiveIcmpEnabled = IcmpAvailable;
            s.CompetitiveCloudflareDetectionEnabled = CloudflareAvailable;
            s.CompetitiveTracerouteEnabled = TraceAvailable;
            s.CompetitiveExperimentMode = false;
            s.StoreFullEgressIpInLogs = false; // diagnostics must not opt users into full-IP logging
            s.AdaptiveRegionPreferencesEnabled = IcmpAvailable && CloudflareAvailable;
            s.PreferredRegionEnabled = RegionsAvailable && IcmpAvailable && CloudflareAvailable;
            s.AutoSelectPreferredServerOnLaunch = s.PreferredRegionEnabled;
            // Alert preferences depend on each live join's evidence, not registry/ICMP
            // availability during setup. Preserve the user's warning and autolog choices.
        }
        public void Save()
        {
            Directory.CreateDirectory(Paths.Cache);
            AtomicFile.WriteText(FilePath, JsonSerializer.Serialize(this));
            Apply(App.Settings.Prop);
            App.Settings.Prop.NetworkSetupVersion = SetupFinished || Completed ? 1 : 0;
            App.Settings.Prop.RegionCalibrationCompleted = Completed;
            // Settings/watcher can coexist. Only persist the network fields in this transaction.
            using var gate = Bloxstrap.Networking.NetworkHistory.DataLock("AdaptiveRegionSettings", TimeSpan.FromSeconds(2));
            if (!gate.IsAcquired) throw new IOException("Network settings are busy. Try again.");
            var disk = new JsonManager<Settings>();
            if (disk.IsSaved) { if (!disk.Load(false)) throw new IOException("Saved settings are unavailable; the network result was retained but preferences were not overwritten."); }
            else disk.Prop = App.Settings.Prop;
            Apply(disk.Prop); disk.Prop.NetworkSetupVersion = SetupFinished || Completed ? 1 : 0; disk.Prop.RegionCalibrationCompleted = Completed;
            disk.Prop.CloudflareTermsAccepted = App.Settings.Prop.CloudflareTermsAccepted;
            if (!disk.TrySave()) throw new IOException("Network preferences could not be saved.");
        }
    }
}
