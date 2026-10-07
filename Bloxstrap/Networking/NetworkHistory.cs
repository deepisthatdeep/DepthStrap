namespace Bloxstrap.Networking
{
    internal static class NetworkHistory
    {
        private static string CutoffPath => Path.Combine(Paths.Cache, "NetworkResetEpoch.txt");
        public static bool Accept(DateTimeOffset timestamp)
        {
            if (!File.Exists(CutoffPath)) return true;
            if (!DateTimeOffset.TryParse(AtomicFile.ReadText(CutoffPath), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cutoff))
                throw new InvalidDataException("Network reset timestamp is invalid.");
            return timestamp >= cutoff;
        }
        // All history writers and resets share this short synchronous lock, including
        // separate launcher/watcher processes. Never hold a thread-affine mutex across await.
        internal static InterProcessLock DataLock(string name, TimeSpan timeout)
        {
            string identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Paths.Base).ToUpperInvariant())))[..16];
            return new InterProcessLock(name + "-" + identity, timeout);
        }
        public static InterProcessLock Lock()
        {
            var gate = DataLock("NetworkHistory", TimeSpan.FromSeconds(10));
            if (!gate.IsAcquired) { gate.Dispose(); throw new IOException("Network history is busy."); }
            return gate;
        }
        public static void Reset()
        {
            using var gate = Lock();
            using var settingsGate = DataLock("AdaptiveRegionSettings", TimeSpan.FromSeconds(2));
            if (!settingsGate.IsAcquired) throw new IOException("Network settings are busy.");
            var disk = new JsonManager<Settings>();
            if (disk.IsSaved) { if (!disk.Load(false)) throw new IOException("Saved settings are unavailable; network history was not reset."); }
            else disk.Prop = App.Settings.Prop;
            Directory.CreateDirectory(Paths.Cache);
            // Leave the epoch in place so diagnostics that started before reset cannot return old data.
            AtomicFile.WriteText(CutoffPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            foreach (string file in new[] { "LearnedRegions.json", "LearnedRegions.json.tmp", "ServerRegions.json", "ServerRegions.json.tmp", "server_cache.json", "RegionCalibration.json", "NetworkTest.json", "NetworkTest.json.tmp", "CompetitiveRegionHistory.json", "CompetitiveNetworkState.json", "CompetitiveNetworkState.json.tmp", "CompetitivePendingJoin.json", "AutoLogRetries.json", "AutoLogRetries.json.tmp" })
                DeleteFile(Path.Combine(Paths.Cache, file), Paths.Cache);
            foreach (string folder in new[] { "CompetitiveSessions", "CompetitiveRoutes" })
            {
                string path = SafePath(Path.Combine(Paths.Logs, folder), Paths.Logs);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            if (Directory.Exists(Paths.Logs))
                foreach (string file in Directory.EnumerateFiles(Paths.Logs, "CompetitiveSession-*.log", SearchOption.TopDirectoryOnly)) DeleteFile(file, Paths.Logs);
            App.Settings.Prop.AutomaticRegionalPreference = true;
            App.Settings.Prop.PreferNorthAmericaOnly = App.Settings.Prop.PreferEuropeOnly = false;
            App.Settings.Prop.CompetitivePreferredCity = "";
            App.Settings.Prop.CompetitiveFallbackCities.Clear();
            App.Settings.Prop.RegionCalibrationCompleted = false;
            AdaptiveRegionService.ClearStatus();
            disk.Prop.AutomaticRegionalPreference = true;
            disk.Prop.PreferNorthAmericaOnly = disk.Prop.PreferEuropeOnly = false;
            disk.Prop.CompetitivePreferredCity = "";
            disk.Prop.CompetitiveFallbackCities = new();
            disk.Prop.RegionCalibrationCompleted = false;
            disk.Prop.NetworkSetupVersion = 0;
            if (!disk.TrySave()) throw new IOException("Reset network preferences could not be saved.");
        }
        private static void DeleteFile(string path, string root) { path = SafePath(path, root); if (File.Exists(path)) File.Delete(path); }
        private static string SafePath(string path, string root)
        {
            string resolved = Path.GetFullPath(path);
            if (!resolved.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Network reset path leaves its data directory.");
            // Never traverse a junction/symlink into other application data.
            for (string? current = resolved; current is not null && !string.Equals(current, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Network reset cannot follow a linked data path.");
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Network data directory is linked.");
            return resolved;
        }
    }
}
