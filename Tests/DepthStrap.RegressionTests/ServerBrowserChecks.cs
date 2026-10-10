using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Integrations;
using Bloxstrap.Models;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Networking;
using Bloxstrap.UI.ViewModels.Settings;

internal static class ServerBrowserChecks
{
    private const string Job = "22222222-2222-2222-2222-222222222222";
    private const string PublicList = "{\"data\":[{\"id\":\"22222222-2222-2222-2222-222222222222\",\"playing\":4,\"maxPlayers\":20},{\"id\":\"33333333-3333-3333-3333-333333333333\",\"playing\":20,\"maxPlayers\":20}],\"nextPageCursor\":\"next+page\"}";
    private const string Details = "{\"status\":\"success\",\"servers\":[{\"server_id\":\"22222222-2222-2222-2222-222222222222\",\"city\":\"Dallas\",\"region\":\"Texas\",\"country\":\"US\",\"datacenter_id\":42,\"first_seen\":\"2026-10-07T10:00:00\"},{\"server_id\":\"unrequested-job\",\"city\":\"London\"}]}";
    private sealed class Handler : HttpMessageHandler
    {
        internal bool MetadataUnavailable;
        internal bool ListUnavailable;
        internal bool DatacentersUnavailable;
        internal int FailFirstRequests;
        internal Func<HttpRequestMessage, string?>? ContentOverride;
        internal Action<HttpRequestMessage>? OnRequest;
        internal List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request); OnRequest?.Invoke(request);
            bool metadata = request.RequestUri!.AbsolutePath == "/v1/servers/details";
            bool centers = request.RequestUri!.AbsolutePath == "/v1/datacenters/list";
            bool unavailable = metadata ? MetadataUnavailable : centers ? DatacentersUnavailable : ListUnavailable;
            var code = unavailable || FailFirstRequests-- > 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(ContentOverride?.Invoke(request) ?? (metadata ? Details : centers ? "[]" : PublicList)) });
        }
    }
    internal static void Run(Action<bool, string> check)
    {
        check(typeof(App).Assembly.GetType("Bloxstrap.Integrations.AccountManager") is null &&
            typeof(App).Assembly.GetReferencedAssemblies().All(x => !x.Name!.StartsWith("Puppeteer", StringComparison.Ordinal)),
            "Legacy account manager and browser automation are absent from the compiled app");
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var browser = new DepthStrapServerBrowser(client);
        var parsed = DepthStrapServerBrowser.ParsePublicList(PublicList);
        DepthStrapServerBrowser.ApplyMetadata(parsed, Details);
        check(parsed.Count == 1 && parsed[0].Region == "Dallas, Texas, US" && parsed[0].DataCenterId == 42 && parsed[0].FirstSeen?.Kind == DateTimeKind.Utc,
            "Custom browser excludes full servers and applies public region/uptime metadata only to requested jobs");
        parsed = DepthStrapServerBrowser.ParsePublicList("{\"data\":[null,{\"id\":\"22222222-2222-2222-2222-222222222222\",\"playing\":\"bad\",\"maxPlayers\":20},{\"id\":\"22222222-2222-2222-2222-222222222222\",\"playing\":4,\"maxPlayers\":20}]}");
        DepthStrapServerBrowser.ApplyMetadata(parsed, "{\"servers\":[null,42,{\"server_id\":\"22222222-2222-2222-2222-222222222222\",\"city\":\"Dallas\"}]}");
        check(parsed.Count == 1 && parsed[0].Region == "Dallas", "Malformed individual public/metadata records cannot discard valid servers on the same page");
        Task.Run(async () =>
        {
            handler.FailFirstRequests = 2;
            var result = await browser.FetchServerInstancesAsync(123, "cursor+with&symbols", 1);
            check(result.Servers.Single().Region == "Dallas, Texas, US" && result.NextCursor == "next+page" && handler.Requests.Count == 4,
                "Public list retries transient failures and loads matching region metadata");
            check(handler.Requests.All(x => x.Method == HttpMethod.Get && !x.Headers.Contains("Cookie") && !x.Headers.Contains("Authorization") && x.RequestUri!.Host != "gamejoin.roblox.com") &&
                handler.Requests[0].RequestUri!.Query.Contains("cursor=cursor%2Bwith%26symbols") && handler.Requests[0].RequestUri!.Query.Contains("sortOrder=Asc"),
                "Custom browsing never authenticates or requests join tickets and preserves encoded pagination and sort order");
            handler.MetadataUnavailable = true;
            var recordedAt = DateTimeOffset.UtcNow.AddDays(-2);
            KnownServerRegions.Remember(123, new[] { new KeyValuePair<string, KnownServerRegions.Entry>(Job, new("Dallas, Texas, US", 42, "fixture", recordedAt)) }, DateTimeOffset.UtcNow);
            result = await browser.FetchServerInstancesAsync(123);
            check(result.Servers.Single().Region == "Dallas, Texas, US", "Public metadata outage retains a verified cached region for the same place/job");
            check(KnownServerRegions.ForPlace(123)[Job].RecordedAt == recordedAt, "Reading cached metadata cannot extend its expiry during an outage");
            result = await browser.FetchServerInstancesAsync(456);
            check(result.Servers.Single().Region == "Unknown", "Metadata outage still returns the public list and never copies another place's region");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { await browser.FetchServerInstancesAsync(123, cancellationToken: cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Public browser cancellation exits immediately instead of retrying");
            handler.MetadataUnavailable = false;
            File.Delete(KnownServerRegions.FilePath);
            Directory.CreateDirectory(KnownServerRegions.FilePath);
            try
            {
                result = await browser.FetchServerInstancesAsync(123);
                check(result.Servers.Single().Region == "Dallas, Texas, US", "An unwritable region cache cannot fail a successfully loaded public server list");
            }
            finally { Directory.Delete(KnownServerRegions.FilePath); }
        }).GetAwaiter().GetResult();
        var viewModel = new RegionSelectorViewModel(browser) { PlaceId = "123", UsePreferredRegionMode = false };
        check(viewModel.SearchCommand.CanExecute(null) && viewModel.Regions.Contains("All regions"), "ID Selector is enabled without remote configuration or an account cookie");
        handler.ListUnavailable = true;
        Task.Run(() => viewModel.SearchCommand.ExecuteAsync(null)).GetAwaiter().GetResult();
        check(!viewModel.IsLoading && viewModel.LoadingMessage.Contains("unavailable") && viewModel.SearchCommand.CanExecute(null),
            "Failed server search resets the loading state and allows retry");
        handler.ListUnavailable = false; handler.DatacentersUnavailable = true;
        Task.Run(() => viewModel.InitializeRegionsAsync()).GetAwaiter().GetResult();
        check(!viewModel.IsLoading && viewModel.SearchCommand.CanExecute(null), "Datacenter lookup failure cannot leave public browsing disabled");
        File.WriteAllText(Path.Combine(Paths.Cache, "DataCentersCache.json"), JsonSerializer.Serialize(new DatacentersCache
            { Regions = new() { "Dallas, US" }, DatacenterMap = new() { [42] = "Dallas, US" }, LastUpdated = DateTime.UtcNow }));
        Task.Run(() => viewModel.InitializeRegionsAsync()).GetAwaiter().GetResult();
        check(viewModel.Regions.Contains("Dallas, US") && !viewModel.IsLoading,
            "The typed datacenter cache keeps its timestamp and restores region choices during an API outage");
        handler.ContentOverride = request => request.RequestUri!.Host == "games.roblox.com" ? "{\"data\":[{\"id\":\"22222222-2222-2222-2222-222222222222\",\"playing\":4,\"maxPlayers\":20}],\"nextPageCursor\":null}" : null;
        string? launched = null;
        viewModel = new RegionSelectorViewModel(browser, uri => launched = uri) { PlaceId = "123", UsePreferredRegionMode = false };
        Task.Run(() => viewModel.SearchCommand.ExecuteAsync(null)).GetAwaiter().GetResult();
        var retained = viewModel.Servers.Single();
        viewModel.PlaceId = "456";
        check(!viewModel.HasSearched && viewModel.Servers.Count == 0 && !viewModel.LoadMoreCommand.CanExecute(null), "Changing the game invalidates previous results and pagination");
        retained.JoinCommand!.Execute(null);
        check(launched?.Contains("placeId=123&") == true, "A retained join action stays bound to the game its server belongs to");
        handler.OnRequest = request => { if (request.RequestUri!.Host == "games.roblox.com") viewModel.PlaceId = "789"; };
        Task.Run(() => viewModel.SearchCommand.ExecuteAsync(null)).GetAwaiter().GetResult();
        check(viewModel.Servers.Count == 0 && viewModel.NextCursor.Length == 0 && !viewModel.IsLoading, "A game change during a request cancels it and rejects late results");
        handler.OnRequest = null;
        viewModel.SearchQuery = "New game search";
        check(viewModel.PlaceId.Length == 0 && !viewModel.SearchCommand.CanExecute(null), "Typing another game name cannot search the previously selected game");
        viewModel.StopPendingRequests();
        handler.ContentOverride = request => request.RequestUri!.Host == "games.roblox.com" ? request.RequestUri.Query.Contains("cursor=next")
            ? "{\"data\":[{\"id\":\"22222222-2222-2222-2222-222222222222\",\"playing\":4,\"maxPlayers\":20}],\"nextPageCursor\":null}"
            : "{\"data\":[{\"id\":\"33333333-3333-3333-3333-333333333333\",\"playing\":20,\"maxPlayers\":20}],\"nextPageCursor\":\"next\"}" : null;
        viewModel = new RegionSelectorViewModel(browser) { PlaceId = "123", UsePreferredRegionMode = true };
        Task.Run(() => viewModel.SearchCommand.ExecuteAsync(null)).GetAwaiter().GetResult();
        check(viewModel.Servers.Count == 1 && !viewModel.IsLoading, "Preferred browsing follows pagination after a page becomes empty when full servers are filtered");
        handler.ContentOverride = null;
        new NetworkTestResult { Status = "Unavailable fixture" }.Save();
        check(App.Settings.Prop.NetworkSetupVersion == 0, "Failed initialization remains eligible for a fresh setup attempt");
        File.Delete(NetworkTestResult.FilePath);
        App.Settings.Prop = new Settings();
        NetworkHistory.Reset();
        check(!File.Exists(KnownServerRegions.FilePath), "Reset Network clears custom browser's learned server regions");
        KnownServerRegions.Remember(123, new[] { new KeyValuePair<string, KnownServerRegions.Entry>(Job, new("Old fixture", null, "fixture", DateTimeOffset.UtcNow)) }, DateTimeOffset.UtcNow.AddMinutes(-1));
        check(!File.Exists(KnownServerRegions.FilePath), "Late browser metadata cannot restore pre-reset server history");
    }
    internal static void VerifyPublicNetwork(Action<bool, string> check)
    {
        Task.Run(async () =>
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var targets = await RegionCalibrationService.DiscoverAsync(budget.Token);
            check(targets.Count > 0 && targets.Any(x => x.Address.Contains(':')) && targets.Any(x => !x.Address.Contains(':')),
                "Live routing discovery loads published IPv4 and IPv6 targets without changing the current route");
            var browser = new DepthStrapServerBrowser();
            var centers = await browser.GetDatacentersAsync(budget.Token);
            check(centers?.regions.Count > 0, "Live custom browser loads the public datacenter registry");
            var entries = await browser.GetDatacenterEntriesAsync(budget.Token);
            var activeLocations = entries!.Where(entry => !entry.Inactive && entry.DataCenterIds.Count > 0)
                .Select(entry => (entry.Location.City, entry.Location.Country)).Distinct().ToList();
            var uncovered = activeLocations.Where(location => !RoutingTargetDiscovery.CoversLocation(targets, location.City, location.Country)).ToList();
            Console.WriteLine($"Live datacenter probe coverage: {activeLocations.Count - uncovered.Count}/{activeLocations.Count} active locations have a published peering address.");
            Console.WriteLine("No address in the current peering inventory: " + string.Join("; ", uncovered.Select(location => $"{location.City}, {location.Country}")));
            var servers = await browser.FetchServerInstancesAsync(4111023553, cancellationToken: budget.Token);
            check(servers.Servers.Count > 0, "Live custom browser loads Deepwoken's public server list without an account cookie");
            Console.WriteLine($"Public network loading: {targets.Count} addresses across {targets.Select(x => (x.City, x.Country)).Distinct().Count()} locations; {centers!.Value.regions.Count} datacenter choices; {servers.Servers.Count} public Deepwoken servers.");
        }).GetAwaiter().GetResult();
    }
}
