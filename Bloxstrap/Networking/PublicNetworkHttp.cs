namespace Bloxstrap.Networking
{
    /// <summary>Bounded public requests; never attach Roblox authentication.</summary>
    internal static class PublicNetworkHttp
    {
        internal static async Task<string> GetAsync(HttpClient client, string url, CancellationToken token, int retryDelayMs = 500,
            Func<TimeSpan, CancellationToken, Task>? wait = null)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(retryDelayMs);
            wait ??= Task.Delay;
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan delay = TimeSpan.FromMilliseconds((long)retryDelayMs * (1 << attempt));
                try
                {
                    using var response = await client.GetAsync(url, token);
                    if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt < 2)
                    {
                        var retry = response.Headers.RetryAfter;
                        TimeSpan requested = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.Zero;
                        // Never retry sooner than requested. A long server cooldown
                        // defers discovery instead of holding setup indefinitely.
                        if (requested > TimeSpan.FromMinutes(2)) response.EnsureSuccessStatusCode();
                        if (requested > delay) delay = requested;
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                        return await response.Content.ReadAsStringAsync(token);
                    }
                }
                catch (HttpRequestException ex) when (attempt < 2 && ex.StatusCode is null)
                { /* Retry a connection failure with the same bounded backoff. */ }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < 2)
                { /* Per-request timeout; caller cancellation must still propagate. */ }
                // Release the response/connection before waiting for the cooldown.
                await wait(delay, token);
            }
        }
    }
}
