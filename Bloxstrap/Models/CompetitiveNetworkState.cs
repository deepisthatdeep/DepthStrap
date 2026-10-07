using System.Text.Json.Serialization;

namespace Bloxstrap.Models
{
    /// <summary>
    /// Latest evaluated network state, written by the watcher process (per stage event) and polled
    /// once per second by the settings UI. Cross-process by design: the main app and the Roblox
    /// watcher are separate processes in Froststrap v1.5.1 - a small atomic file is simpler and
    /// more robust than any IPC framework here.
    /// </summary>
    public sealed class CompetitiveNetworkState
    {
        [JsonPropertyName("processId")] public int ProcessId { get; set; }
        [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }

        [JsonPropertyName("universeId")] public long UniverseId { get; set; }
        [JsonPropertyName("placeId")] public long PlaceId { get; set; }
        [JsonPropertyName("jobId")] public string JobId { get; set; } = "";

        [JsonPropertyName("isTeleport")] public bool IsTeleport { get; set; }
        [JsonPropertyName("reserved")] public bool Reserved { get; set; }

        [JsonPropertyName("location")] public string Location { get; set; } = "";
        [JsonPropertyName("regionQuality")] public string RegionQuality { get; set; } = "Unknown";
        [JsonPropertyName("regionScore")] public int RegionScore { get; set; }

        [JsonPropertyName("udmuxIp")] public string UdmuxIp { get; set; } = "";
        [JsonPropertyName("udmuxPort")] public int? UdmuxPort { get; set; }
        [JsonPropertyName("rccIp")] public string RccIp { get; set; } = "";

        /// <summary>"on" / "off" / "unknown".</summary>
        [JsonPropertyName("warp")] public string Warp { get; set; } = "unknown";
        [JsonPropertyName("colo")] public string Colo { get; set; } = "";

        [JsonPropertyName("icmpAvgMs")] public double? IcmpAvgMs { get; set; }
        [JsonPropertyName("icmpJitterMs")] public double? IcmpJitterMs { get; set; }

        [JsonPropertyName("runLabel")] public string RunLabel { get; set; } = "";
        [JsonPropertyName("tracerouteFile")] public string TracerouteFile { get; set; } = "";

        public static string FilePath => Path.Combine(Paths.Cache, "CompetitiveNetworkState.json");

        /// <summary>Builds the state object from a network event (called by the watcher).</summary>
        public static CompetitiveNetworkState FromEvent(CompetitiveNetworkEvent evt) => new()
        {
            ProcessId = evt.ProcessId,
            Timestamp = DateTime.Now,
            UniverseId = evt.UniverseId,
            PlaceId = evt.PlaceId,
            JobId = evt.JobId,
            IsTeleport = evt.IsTeleport,
            Reserved = evt.IsReservedServer,
            Location = string.IsNullOrEmpty(evt.Location) ? "Unknown" : evt.Location,
            RegionQuality = evt.RegionQuality.ToString(),
            RegionScore = evt.RegionScore,
            UdmuxIp = evt.UdmuxAddress ?? "",
            UdmuxPort = evt.UdmuxPort,
            RccIp = evt.RccAddress ?? "",
            Warp = evt.Cloudflare?.Warp ?? "unknown",
            Colo = evt.Cloudflare?.Colo ?? "",
            IcmpAvgMs = evt.Latency?.HasMeasurement == true ? evt.Latency.AverageMs : null,
            IcmpJitterMs = evt.Latency?.HasMeasurement == true ? evt.Latency.JitterMs : null,
            RunLabel = evt.RunLabel,
            TracerouteFile = evt.TracerouteFile
        };

        private static readonly JsonSerializerOptions _options = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Atomic write (temp + replace) so the UI never reads a torn file. Never throws.</summary>
        public static void Write(CompetitiveNetworkEvent evt)
        {
            try
            {
                using var resetLock = Networking.NetworkHistory.Lock();
                if (!Networking.NetworkHistory.Accept(evt.Timestamp)) return;
                string dir = Paths.Cache;
                Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(FromEvent(evt), _options);
                AtomicFile.WriteText(FilePath, json);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("CompetitiveNetworkState::Write", $"Latest-state write failed: {ex.Message}");
            }
        }

        /// <summary>Reads the latest state for UI display. Null when missing/corrupt.</summary>
        public static CompetitiveNetworkState? TryRead()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return null;

                return JsonSerializer.Deserialize<CompetitiveNetworkState>(AtomicFile.ReadText(FilePath), _options);
            }
            catch
            {
                return null; // torn read or bad json - just show "no data" this tick
            }
        }

        internal static void ClearIfOwned(int processId, string? jobId = null)
        {
            try
            {
                using var gate = Networking.NetworkHistory.Lock();
                var state = TryRead();
                if (state is not null && state.ProcessId == processId && (jobId is null || state.JobId == jobId))
                    File.Delete(FilePath);
            }
            catch (Exception ex) { App.Logger.WriteException("CompetitiveNetworkState::Clear", ex); }
        }
    }
}
