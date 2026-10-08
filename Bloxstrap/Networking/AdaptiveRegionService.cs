using Bloxstrap.Integrations;
using Bloxstrap.Models;

namespace Bloxstrap.Networking
{
    internal static class AdaptiveRegionService
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static string HistoryPath => Path.Combine(Paths.Cache, "LearnedRegions.json");
        private static string? _status;
        private static (string Path, DateTime Written, long Length)? _settingsStamp;
        private static Settings? _settingsSnapshot;
        public static string Status
        {
            get
            {
                if (_status is not null) return _status;
                try
                {
                    string path = Path.Combine(Paths.Cache, "RegionCalibration.json");
                    if (File.Exists(path))
                    {
                        using var saved = JsonDocument.Parse(File.ReadAllText(path));
                        return saved.RootElement.GetProperty("status").GetString() ?? "Calibration completed.";
                    }
                }
                catch (Exception ex) { App.Logger.WriteException("AdaptiveRegionService::Status", ex); }
                return "Region calibration has not run.";
            }
            private set => _status = value;
        }
        internal static void ClearStatus() => _status = null;
        public static string RouteKey(CloudflareTraceResult? cf) => cf?.WarpActive switch
        {
            true => "warp:" + cf.Colo.ToUpperInvariant(),
            false => "direct",
            _ => "unknown"
        };

        private static List<RegionObservation> Read()
        {
            if (!File.Exists(HistoryPath)) return new();
            try
            {
                using var file = new FileStream(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<List<RegionObservation>>(file) ?? new();
            }
            catch (JsonException ex) { App.Logger.WriteException("AdaptiveRegionService::Read", ex); return new(); }
            // An unavailable file must not be mistaken for an empty history and overwritten.
        }

        public static async Task RecordAsync(IEnumerable<RegionObservation> additions, string route, CancellationToken token)
        {
            await Gate.WaitAsync(token);
            try
            {
                using var resetLock = NetworkHistory.Lock();
                additions = additions.Where(x => NetworkHistory.Accept(x.Timestamp)).ToList();
                using var historyLock = Bloxstrap.Networking.NetworkHistory.DataLock("AdaptiveRegionHistory", TimeSpan.FromSeconds(2));
                if (!historyLock.IsAcquired) throw new IOException("Region history is busy; the measurement was not saved.");
                var samples = Read();
                samples.AddRange(additions);
                samples = samples.Where(x => x.Timestamp >= DateTimeOffset.UtcNow.AddDays(-30)).TakeLast(2000).ToList();
                Directory.CreateDirectory(Paths.Cache);
                token.ThrowIfCancellationRequested();
                AtomicFile.WriteText(HistoryPath, JsonSerializer.Serialize(samples));
                Apply(samples, route);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { App.Logger.WriteException("AdaptiveRegionService::Record", ex); }
            finally { Gate.Release(); }
        }

        private static void Apply(List<RegionObservation> samples, string route)
        {
            var s = App.Settings.Prop;
            if (!s.AdaptiveRegionPreferencesEnabled || !s.CompetitiveModeEnabled) return;
            // A failed trace is missing evidence, not a new route with no usable regions.
            // Keep the last measured preferences until the connection can be identified.
            if (route == "unknown" || route == "warp:")
            {
                Status = "Current route is unavailable. Previously measured region preferences were retained.";
                return;
            }
            var ranking = AdaptiveRegionPlanner.Rank(samples, route, DateTime.Now.Hour, DateTimeOffset.UtcNow, s.PreferNorthAmericaOnly, s.PreferEuropeOnly);
            ranking = AdaptiveRegionPlanner.Stabilize(ranking, s.CompetitivePreferredCity);
            if (ranking.Count == 0)
            {
                Status = "No usable measurements on this route yet. Roblox matchmaking remains available.";
                s.CompetitivePreferredCity = "";
                s.CompetitiveFallbackCities = new();
                PersistLearnedFields(s);
                return;
            }
            // Watcher and Settings run in separate processes. Persist only learned fields, preserving user changes.
            s.CompetitivePreferredCity = ranking[0].City;
            s.CompetitiveFallbackCities = ranking.Skip(1).Take(5).Select(x => x.City).ToList();
            Status = $"{ranking[0].City}: {ranking[0].CostMs:0} ms ICMP score; {ranking[0].Evidence}, n={ranking[0].Samples}.";
            PersistLearnedFields(s);
        }

        public static void PersistLearnedFields(Models.Persistable.Settings settings)
        {
            using var processLock = Bloxstrap.Networking.NetworkHistory.DataLock("AdaptiveRegionSettings", TimeSpan.FromSeconds(2));
            if (!processLock.IsAcquired) return;
            var disk = new JsonManager<Models.Persistable.Settings>();
            if (disk.IsSaved) { if (!disk.Load(false)) return; }
            else disk.Prop = settings;
            // A user turning off learning in another window wins over a late diagnostic result.
            if (!disk.Prop.AdaptiveRegionPreferencesEnabled || disk.Prop.PreferNorthAmericaOnly != settings.PreferNorthAmericaOnly ||
                disk.Prop.PreferEuropeOnly != settings.PreferEuropeOnly) return;
            disk.Prop.CompetitivePreferredCity = settings.CompetitivePreferredCity;
            disk.Prop.CompetitiveFallbackCities = settings.CompetitiveFallbackCities;
            disk.Prop.RegionCalibrationCompleted = settings.RegionCalibrationCompleted;
            if (!disk.TrySave()) throw new IOException("Learned region preferences could not be saved.");
        }

        private static bool MergeLearnedFields(Settings current, Settings disk)
        {
            if (!current.AdaptiveRegionPreferencesEnabled || !disk.AdaptiveRegionPreferencesEnabled ||
                current.PreferNorthAmericaOnly != disk.PreferNorthAmericaOnly || current.PreferEuropeOnly != disk.PreferEuropeOnly) return false;
            bool changed = current.CompetitivePreferredCity != disk.CompetitivePreferredCity ||
                !current.CompetitiveFallbackCities.SequenceEqual(disk.CompetitiveFallbackCities) ||
                current.RegionCalibrationCompleted != disk.RegionCalibrationCompleted;
            if (!changed) return false;
            current.CompetitivePreferredCity = disk.CompetitivePreferredCity;
            current.CompetitiveFallbackCities = disk.CompetitiveFallbackCities.ToList();
            current.RegionCalibrationCompleted = disk.RegionCalibrationCompleted;
            return true;
        }

        internal static bool RefreshLearnedFields()
        {
            using var gate = NetworkHistory.DataLock("AdaptiveRegionSettings", TimeSpan.Zero);
            if (!gate.IsAcquired || !App.Settings.Prop.AdaptiveRegionPreferencesEnabled) return false;
            var disk = new JsonManager<Settings>();
            try
            {
                var info = new FileInfo(disk.FileLocation);
                if (!info.Exists) { _settingsStamp = null; _settingsSnapshot = null; return false; }
                var stamp = (info.FullName, info.LastWriteTimeUtc, info.Length);
                if (_settingsStamp != stamp || _settingsSnapshot is null)
                {
                    if (!disk.Load(false)) return false;
                    _settingsSnapshot = disk.Prop;
                    _settingsStamp = stamp;
                }
                return MergeLearnedFields(App.Settings.Prop, _settingsSnapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { App.Logger.WriteException("AdaptiveRegionService::Refresh", ex); return false; }
        }

        internal static void SaveUserSettings()
        {
            using var gate = NetworkHistory.DataLock("AdaptiveRegionSettings", TimeSpan.FromSeconds(2));
            if (!gate.IsAcquired) throw new IOException("Network preferences are being saved. Try Save again.");
            var disk = new JsonManager<Settings>();
            if (disk.IsSaved)
            {
                if (!disk.Load(false)) throw new IOException("Saved settings are unavailable. They have been preserved; retry Save when the file is readable.");
                MergeLearnedFields(App.Settings.Prop, disk.Prop);
            }
            if (!App.Settings.TrySave()) throw new IOException("Settings could not be saved. Your previous file has been preserved.");
        }

        public static async Task RefreshAsync(CancellationToken token)
        {
            if (!App.Settings.Prop.AdaptiveRegionPreferencesEnabled) return;
            var cf = await CloudflareNetworkState.QueryAsync(token);
            await RecordAsync(Array.Empty<RegionObservation>(), RouteKey(cf), token);
        }

        public static async Task ObserveAsync(CompetitiveNetworkEvent evt, CancellationToken token)
        {
            if (!App.Settings.Prop.AdaptiveRegionPreferencesEnabled || evt.Latency?.Received < 3 ||
                evt.Latency?.AverageMs is not double ms || string.IsNullOrWhiteSpace(evt.Location) || evt.Location == "Unknown") return;
            var parts = evt.Location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;
            string country = parts[^1] switch { "United States" or "United States of America" => "US", "Canada" => "CA", "Mexico" => "MX", var value => value };
            await RecordAsync(new[] { new RegionObservation { Timestamp = evt.Timestamp, LocalHour = evt.Timestamp.Hour,
                City = parts[0], Country = country, Route = RouteKey(evt.Cloudflare), AverageMs = ms,
                JitterMs = evt.Latency.JitterMs ?? 0, IcmpLossPercent = evt.Latency.LossPercent } }, RouteKey(evt.Cloudflare), token);
        }
    }
}
