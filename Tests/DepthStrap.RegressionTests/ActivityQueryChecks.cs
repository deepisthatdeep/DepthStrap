using System.Net;
using System.Net.Http;
using Bloxstrap;
using Bloxstrap.Models.Entities;

internal static class ActivityQueryChecks
{
    internal static void Run(Action<bool, string> check) => Task.Run(async () =>
    {
        int errors = 0;
        using var handler = new FixtureHandler();
        using var client = new HttpClient(handler);
        var data = new ActivityData
        {
            JobId = Guid.NewGuid().ToString(), PlaceId = 4111023553, MachineAddress = "192.0.2.243",
            QueryClient = client, QueryErrorReporter = _ => errors++
        };
        GlobalCache.ServerLocation.TryRemove(data.MachineAddress, out _);
        handler.Reply = (_, _) => throw new HttpRequestException("Fixture unavailable");
        check(await data.QueryServerTime(showErrors: false) is null && errors == 0,
            "Background uptime failures return unavailable without opening a dialog");
        check(!GlobalCache.ServerTime.ContainsKey(data.JobId), "Failed uptime lookups never cache an invented start time");
        check(await data.QueryServerTime() is null && errors == 1,
            "Explicit uptime requests still report errors and can retry after failure");
        check(await data.QueryServerLocation(showErrors: false) is null && errors == 1,
            "Failed primary and fallback background location requests stay silent");
        check(!GlobalCache.ServerLocation.ContainsKey(data.MachineAddress), "Failed location lookups do not poison the cache");
        check(await data.QueryServerLocation() is null && errors == 2,
            "Explicit location requests report failure and release their semaphore for retry");

        int before = handler.Requests;
        using (var cancel = new CancellationTokenSource())
        {
            handler.Reply = async (_, token) =>
            {
                cancel.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            };
            bool canceled = false;
            try { await data.QueryServerLocation(cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            check(canceled && handler.Requests == before + 1 && errors == 2,
                "Canceled location queries neither start a fallback nor display an error");
        }
        using (var cancel = new CancellationTokenSource())
        {
            handler.Reply = async (_, token) =>
            {
                cancel.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException();
            };
            bool canceled = false;
            try { await data.QueryServerTime(cancel.Token); } catch (OperationCanceledException) { canceled = true; }
            check(canceled && errors == 2 && !GlobalCache.ServerTime.ContainsKey(data.JobId),
                "Canceled uptime queries remain uncached and do not display an error");
        }

        handler.Reply = (request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post ? "{}" : "{\"servers\":[]}"));
        check(await data.QueryServerTime(showErrors: false) is null && !GlobalCache.ServerTime.ContainsKey(data.JobId),
            "An untracked server remains unavailable so its later registered time can be queried");
        handler.Reply = (request, _) => Task.FromResult(Json(request.Method == HttpMethod.Post ? "{}" :
            "{\"servers\":[{\"first_seen\":\"2026-10-01T12:00:00Z\"}]}"));
        var firstSeen = await data.QueryServerTime(showErrors: false);
        check(firstSeen == new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), "Uptime retries parse the actual registered UTC start time");
        before = handler.Requests;
        check(await data.QueryServerTime(showErrors: false) == firstSeen && handler.Requests == before,
            "Successful uptime lookups use their cached start time");

        handler.Reply = (request, _) => Task.FromResult(request.RequestUri!.Host == "ipinfo.io"
            ? Json("{\"city\":\"Dallas\",\"region\":\"Texas\",\"country\":\"US\"}") : Json("{\"location\":{}}"));
        check(await data.QueryServerLocation(showErrors: false) == "Dallas, Texas, US" && data.LastLocationSource == "ipinfo.io",
            "Incomplete primary geolocation falls back to a usable location");
        before = handler.Requests;
        check(await data.QueryServerLocation(showErrors: false) == "Dallas, Texas, US" && handler.Requests == before,
            "A successful fallback location is cached");

        GlobalCache.ServerLocation.TryRemove(data.MachineAddress, out _);
        handler.Reply = (_, _) => Task.FromResult(Json("{\"location\":{\"city\":\"Paris\",\"region\":\"Paris\",\"country_name\":\"France\"}}"));
        check(await data.QueryServerLocation(showErrors: false) == "Paris, France" && data.LastLocationSource == "RoValra IP geolocation",
            "Primary geolocation succeeds after earlier failures and cancellation");
        GlobalCache.ServerLocation.TryRemove(data.MachineAddress, out _);
        GlobalCache.ServerTime.TryRemove(data.JobId, out _);
    }).GetAwaiter().GetResult();

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class FixtureHandler : HttpMessageHandler
    {
        internal int Requests;
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Reply = (_, _) => throw new InvalidOperationException();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Requests); return Reply(request, token); }
    }
}
