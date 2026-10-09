using Bloxstrap.Networking;

namespace Bloxstrap.Integrations
{
    /// <summary>DepthStrap's public server browser. Browsing never requests a join ticket or account cookie.</summary>
    public sealed class DepthStrapServerBrowser
    {
        private static readonly HttpClient PublicClient = CreateClient();
        private readonly HttpClient _client;
        private List<DatacenterEntry>? _datacenters;
        public DepthStrapServerBrowser() : this(PublicClient) { }
        internal DepthStrapServerBrowser(HttpClient client) => _client = client;
        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new SocketsHttpHandler { UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) }) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"DepthStrap/{App.Version}");
            return client;
        }
        public async Task<List<DatacenterEntry>?> GetDatacenterEntriesAsync(CancellationToken token = default)
        {
            if (_datacenters is not null) return _datacenters;
            try
            {
                string json = await PublicNetworkHttp.GetAsync(_client, "https://apis.rovalra.com/v1/datacenters/list", token);
                var entries = JsonSerializer.Deserialize<List<DatacenterEntry>>(json);
                // An empty response must not permanently suppress retries for this browser.
                if (entries?.Count > 0) _datacenters = entries;
                return entries?.Count > 0 ? entries : null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { App.Logger.WriteException("DepthStrapServerBrowser::Datacenters", ex); return null; }
        }
        public async Task<(List<string> regions, Dictionary<int, string> datacenterMap)?> GetDatacentersAsync(CancellationToken token = default)
        {
            var entries = await GetDatacenterEntriesAsync(token);
            if (entries is null) return null;
            var map = new Dictionary<int, string>();
            foreach (var entry in entries.Where(x => x is not null && !x.Inactive && x.Location is not null && x.DataCenterIds is not null))
            {
                string region = string.Join(", ", new[] { entry.Location.City, entry.Location.Country }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (region.Length == 0) continue;
                foreach (int id in entry.DataCenterIds) map[id] = region;
            }
            return (map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(), map);
        }
        public async Task<DateTime?> GetServerUptime(string jobId, long placeId, CancellationToken token = default)
        {
            try
            {
                string json = await PublicNetworkHttp.GetAsync(_client, MetadataUrl(placeId, new[] { jobId }), token);
                using var document = JsonDocument.Parse(json);
                var item = document.RootElement.GetProperty("servers").EnumerateArray().FirstOrDefault(x => Text(x, "server_id") == jobId);
                return item.ValueKind == JsonValueKind.Object && DateTimeOffset.TryParse(Text(item, "first_seen"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen) ? seen.UtcDateTime : null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return null; }
        }
        private static string MetadataUrl(long placeId, IEnumerable<string> jobs) =>
            $"https://apis.rovalra.com/v1/servers/details?place_id={placeId}&server_ids={Uri.EscapeDataString(string.Join(',', jobs))}";
        private static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        internal static List<ServerInstance> ParsePublicList(string json)
        {
            using var document = JsonDocument.Parse(json);
            var servers = new List<ServerInstance>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                string id = Text(item, "id");
                if (!Guid.TryParse(id, out var job) || item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("playing", out var playing) || playing.ValueKind != JsonValueKind.Number || !playing.TryGetInt32(out int count) ||
                    !item.TryGetProperty("maxPlayers", out var maximum) || maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetInt32(out int capacity) ||
                    capacity <= 0 || count < 0 || count >= capacity || !seen.Add(job.ToString())) continue;
                servers.Add(new() { Id = job.ToString(), Playing = count, MaxPlayers = capacity });
            }
            return servers;
        }
        internal static void ApplyMetadata(List<ServerInstance> servers, string json)
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("servers", out var details) || details.ValueKind != JsonValueKind.Array) return;
            var byId = servers.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var item in details.EnumerateArray())
            {
                if (!byId.TryGetValue(Text(item, "server_id"), out var server)) continue;
                var parts = new[] { Text(item, "city"), Text(item, "region"), Text(item, "country") }
                    .Where(x => x.Length > 0 && !x.Equals("Unknown", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
                string location = string.Join(", ", parts);
                if (location.Length > 0) { server.Region = location; server.RegionSource = "RoValra public metadata"; }
                if (item.TryGetProperty("datacenter_id", out var dc) && dc.ValueKind == JsonValueKind.Number && dc.TryGetInt32(out int id) && id > 0) server.DataCenterId = id;
                if (DateTimeOffset.TryParse(Text(item, "first_seen"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen)) server.FirstSeen = seen.UtcDateTime;
            }
        }
        public async Task<FetchResult> FetchServerInstancesAsync(long placeId, string cursor = "", int sortOrder = 2, CancellationToken cancellationToken = default)
        {
            if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId));
            var started = DateTimeOffset.UtcNow;
            string url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder={(sortOrder == 1 ? "Asc" : "Desc")}&excludeFullGames=true&limit=100&cursor={Uri.EscapeDataString(cursor)}";
            string json = await PublicNetworkHttp.GetAsync(_client, url, cancellationToken);
            var servers = ParsePublicList(json);
            Dictionary<string, KnownServerRegions.Entry> known = new();
            try { known = KnownServerRegions.ForPlace(placeId); }
            catch (Exception ex) { App.Logger.WriteException("DepthStrapServerBrowser::CacheRead", ex); }
            foreach (var server in servers)
                if (known.TryGetValue(server.Id, out var entry)) { server.Region = entry.Region; server.DataCenterId = entry.DataCenterId; server.RegionSource = entry.Source; }
            // Metadata failure must not discard a successfully loaded public list.
            if (servers.Count > 0)
            {
                using var metadataBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                metadataBudget.CancelAfter(TimeSpan.FromSeconds(8));
                try
                {
                    // Remember only fresh metadata, never extend the TTL by rereading a cache entry.
                    var fresh = servers.Select(x => new ServerInstance { Id = x.Id }).ToList();
                    ApplyMetadata(fresh, await PublicNetworkHttp.GetAsync(_client, MetadataUrl(placeId, servers.Select(x => x.Id)), metadataBudget.Token));
                    foreach (var item in fresh)
                    {
                        var server = servers.First(x => x.Id == item.Id);
                        if (item.Region != "Unknown") { server.Region = item.Region; server.RegionSource = item.RegionSource; }
                        server.DataCenterId = item.DataCenterId ?? server.DataCenterId;
                        server.FirstSeen = item.FirstSeen;
                    }
                    KnownServerRegions.Remember(placeId, fresh.Where(x => x.Region != "Unknown").Select(x =>
                        new KeyValuePair<string, KnownServerRegions.Entry>(x.Id, new(x.Region, x.DataCenterId, x.RegionSource, DateTimeOffset.UtcNow))), started);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { App.Logger.WriteException("DepthStrapServerBrowser::Metadata", ex); }
            }
            using var document = JsonDocument.Parse(json);
            return new() { Servers = servers, NextCursor = Text(document.RootElement, "nextPageCursor"), NewlyFetchedCount = servers.Count };
        }
    }
}
