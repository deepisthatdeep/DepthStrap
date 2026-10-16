using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Bloxstrap.Networking;

internal static class PublicNetworkHttpChecks
{
    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(++Requests)); }
    }
    internal static void Run(Action<bool, string> check)
    {
        static HttpResponseMessage Response(HttpStatusCode status, RetryConditionHeaderValue? retry = null)
        { var result = new HttpResponseMessage(status) { Content = new StringContent("fixture") }; result.Headers.RetryAfter = retry; return result; }
        var waits = new List<TimeSpan>();
        Task Wait(TimeSpan duration, CancellationToken token) { token.ThrowIfCancellationRequested(); waits.Add(duration); return Task.CompletedTask; }
        string Get(HttpClient client, CancellationToken token = default) => PublicNetworkHttp.GetAsync(client, "https://example.test/fixture", token, 500, Wait).GetAwaiter().GetResult();
        using (var handler = new Handler(n => Response(n < 3 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, new(TimeSpan.FromSeconds(12)))))
        using (var client = new HttpClient(handler))
            check(Get(client) == "fixture" && handler.Requests == 3 && waits.SequenceEqual(new[] { TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(12) }),
                "Public discovery honors Retry-After seconds on every allowed retry instead of immediately repeating a throttled request");
        waits.Clear();
        using (var handler = new Handler(n => Response(n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, new(DateTimeOffset.UtcNow.AddSeconds(30)))))
        using (var client = new HttpClient(handler))
            check(Get(client) == "fixture" && waits.Count == 1 && waits[0] > TimeSpan.FromSeconds(25) && waits[0] <= TimeSpan.FromSeconds(30),
                "Public discovery also respects the HTTP-date form of Retry-After");
        waits.Clear();
        using (var handler = new Handler(n => Response(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)))
        using (var client = new HttpClient(handler))
            check(Get(client) == "fixture" && waits.SequenceEqual(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) }),
                "Missing retry headers use bounded exponential backoff and recover a transient server failure");
        waits.Clear();
        using (var handler = new Handler(_ => Response(HttpStatusCode.TooManyRequests, new(TimeSpan.FromMinutes(10)))))
        using (var client = new HttpClient(handler))
        {
            bool rejected = false; try { Get(client); } catch (HttpRequestException error) { rejected = error.StatusCode == HttpStatusCode.TooManyRequests; }
            check(rejected && handler.Requests == 1 && waits.Count == 0, "A long rate-limit cooldown defers discovery without retrying early or indefinitely blocking setup");
        }
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Forbidden, HttpStatusCode.NotFound })
        {
            waits.Clear();
            using var handler = new Handler(_ => Response(status)); using var client = new HttpClient(handler);
            bool rejected = false; try { Get(client); } catch (HttpRequestException error) { rejected = error.StatusCode == status; }
            bool transient = status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
            check(rejected && handler.Requests == (transient ? 3 : 1) && waits.Count == (transient ? 2 : 0),
                "Public request retry bounds preserve the final HTTP error: " + status);
        }
        using (var cancellation = new CancellationTokenSource())
        using (var handler = new Handler(_ => Response(HttpStatusCode.TooManyRequests, new(TimeSpan.FromSeconds(30)))))
        using (var client = new HttpClient(handler))
        {
            bool cancelled = false;
            try { PublicNetworkHttp.GetAsync(client, "https://example.test/fixture", cancellation.Token, wait: (delay, token) => { cancellation.Cancel(); return Task.Delay(delay, token); }).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && handler.Requests == 1, "Cancelling during a server cooldown stops immediately without issuing another request");
        }
    }
}
