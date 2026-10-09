using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using Bloxstrap;
using Bloxstrap.Integrations;
using Bloxstrap.Models.APIs.Roblox;
using Bloxstrap.Models.Entities;
using Bloxstrap.UI.Elements.Settings.Pages;
using Bloxstrap.UI.ViewModels.Settings;

internal static class GameSearchChecks
{
    private sealed class Handler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond = (_, _) => Task.FromResult(Response("[]"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Respond(request, token);
    }
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static List<OmniSearchContent> Game(long id) => new() { new() { UniverseId = (ulong)id, RootPlaceId = id, Name = "Fixture " + id } };
    internal static void Run(Action<bool, string> check)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try { RunOnDispatcher(check); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunOnDispatcher(Action<bool, string> check)
    {
        var handler = new Handler();
        using var client = new HttpClient(handler);
        string? requestQuery = null;
        handler.Respond = (request, _) =>
        {
            requestQuery = request.RequestUri!.Query;
            return Task.FromResult(Response("{\"searchResults\":[null,{\"contents\":[null,{\"universeId\":1,\"rootPlaceId\":0},{\"universeId\":2,\"rootPlaceId\":20},{\"universeId\":2,\"rootPlaceId\":21},{\"universeId\":3,\"rootPlaceId\":30}]}]}"));
        };
        var results = GameSearching.GetGameSearchResultsAsync("two & three", default, client).GetAwaiter().GetResult();
        check(results.Select(x => x.RootPlaceId).SequenceEqual(new long[] { 20, 30 }), "Game search skips null, invalid and duplicate entries without losing valid games");
        check(requestQuery!.Contains("two%20%26%20three"), "Game names are URI-encoded for search");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            try { GameSearching.GetGameSearchResultsAsync("fixture", cancelled.Token, client).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped, "Game search propagates caller cancellation");
        }
        var oldResult = new TaskCompletionSource<List<OmniSearchContent>>();
        var newResult = new TaskCompletionSource<List<OmniSearchContent>>();
        var oldToken = default(CancellationToken);
        var vm = new RegionSelectorViewModel(new DepthStrapServerBrowser(client), searchGames: (query, token) =>
        {
            if (query == "old") { oldToken = token; return oldResult.Task; }
            return newResult.Task;
        }, loadThumbnails: (_, _) => Task.FromException<string?[]>(new HttpRequestException("Fixture thumbnail outage")));
        vm.SearchQuery = "old";
        Task first = vm.SearchGamesAsync();
        vm.SearchQuery = "new";
        Task second = vm.SearchGamesAsync();
        oldResult.SetResult(Game(1)); HistoryChecks.Wait(first);
        check(oldToken.IsCancellationRequested && vm.SearchResults.Count == 0 && vm.IsGameSearchLoading,
            "Superseded game results are rejected and cannot clear a newer request's loading state");
        newResult.SetResult(Game(2)); HistoryChecks.Wait(second);
        check(vm.SearchResults.Single().RootPlaceId == 2 && !vm.IsGameSearchLoading,
            "A thumbnail outage retains current game results and releases loading state");
        var lateResult = new TaskCompletionSource<List<OmniSearchContent>>();
        CancellationToken unloadToken = default;
        vm.StopPendingRequests();
        vm = new RegionSelectorViewModel(new DepthStrapServerBrowser(client), searchGames: (_, token) => { unloadToken = token; return lateResult.Task; });
        vm.SearchQuery = "unload"; Task pending = vm.SearchGamesAsync(); vm.StopPendingRequests();
        lateResult.SetResult(Game(3)); HistoryChecks.Wait(pending);
        check(unloadToken.IsCancellationRequested && vm.SearchResults.Count == 0 && !vm.IsGameSearchLoading,
            "Leaving ID Selector cancels manual game searches and ignores their late completion");
        vm = new RegionSelectorViewModel(new DepthStrapServerBrowser(client), searchGames: (_, _) => Task.FromException<List<OmniSearchContent>>(new HttpRequestException("Fixture outage")));
        vm.SearchQuery = "outage"; HistoryChecks.Wait(vm.SearchGamesAsync());
        check(!vm.IsGameSearchLoading && vm.LoadingMessage.Contains("unavailable") && vm.SearchGamesCommand.CanExecute(null),
            "A failed game search reports recovery options and permits retry");
        vm.StopPendingRequests();

        File.Delete(Path.Combine(Paths.Cache, "DataCentersCache.json"));
        int loads = 0;
        handler.Respond = async (_, token) =>
        {
            if (++loads == 1) await Task.Delay(Timeout.Infinite, token);
            return Response("[null,{\"location\":{\"city\":\"Dallas\",\"country\":\"US\"},\"dataCenterIds\":[42]},{\"location\":{\"city\":\"Invalid\"},\"dataCenterIds\":null}]");
        };
        vm = new RegionSelectorViewModel(new DepthStrapServerBrowser(client));
        var page = new RegionSelectorPage(vm);
        var host = new Window { Content = page };
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        check(host.InputBindings.Count == 2 && host.CommandBindings.Count == 2, "ID Selector installs its window shortcuts on load");
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        check(host.InputBindings.Count == 0 && host.CommandBindings.Count == 0, "Leaving ID Selector removes its window shortcuts");
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); HistoryChecks.Wait(page.RegionsLoadTask);
        check(loads == 2 && vm.Regions.Contains("Dallas, US") && !vm.IsLoading,
            "Returning after cancelled initialization retries and loads valid datacenters despite malformed neighbors");
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); HistoryChecks.Wait(page.RegionsLoadTask);
        check(loads == 2 && host.InputBindings.Count == 2 && host.CommandBindings.Count == 2,
            "Repeated page loads retain one shortcut pair and reuse populated regions");
        page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); host.Content = null; host.Close();
    }
}
