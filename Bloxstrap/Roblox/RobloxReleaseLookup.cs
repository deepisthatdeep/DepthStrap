namespace Bloxstrap.Roblox;

internal static class RobloxReleaseLookup
{
    private static readonly object CatalogLock = new();
    private static Task<string[]>? _releaseCatalog;
    // Official Player packages independently checked against their manifest and
    // executable metadata. DeployHistory now hides these hashes.
    internal static readonly (string Release, string Hash)[] VerifiedHistory =
    [
        ("0.741.0.7411058", "version-02c37bc51a384b8f"),
        ("0.742.0.7421053", "version-cec3ad5889b447cf")
    ];
    private sealed record CatalogEntry(string Release, string Hash);
    private static string CatalogPath => Path.Combine(Paths.Cache, "PlayerReleaseCatalog.json");
    private static IEnumerable<CatalogEntry> KnownReleases()
    {
        var entries = VerifiedHistory.Select(entry => new CatalogEntry(entry.Release, entry.Hash)).ToList();
        try
        {
            if (Paths.Initialized && File.Exists(CatalogPath) && new FileInfo(CatalogPath).Length <= 256 * 1024)
                entries.AddRange((JsonSerializer.Deserialize<CatalogEntry[]>(AtomicFile.ReadText(CatalogPath)) ?? [])
                    .Where(entry => entry is not null && IsReleaseNumber(entry.Release) && RobloxVersionArchive.IsVersionId(entry.Hash)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return entries.Distinct().ToArray();
    }
    private static void RememberRelease(string json, bool weao)
    {
        string? release = ReadCatalogRelease(json, weao);
        if (!Paths.Initialized || release is null) return;
        try
        {
            using var gate = new InterProcessLock("PlayerReleaseCatalog", TimeSpan.FromSeconds(2));
            if (!gate.IsAcquired) return;
            var entries = ParseCatalog(json, release, weao).Select(hash => new CatalogEntry(release, hash))
                .Concat(KnownReleases()).Distinct().Take(256).ToArray();
            Directory.CreateDirectory(Paths.Cache);
            AtomicFile.WriteText(CatalogPath, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Metadata caching cannot block installation. */ }
    }

    internal static bool IsReleaseFamily(string value) => Regex.IsMatch(value, @"\A[0-9]{2,10}\z", RegexOptions.CultureInvariant);

    internal static string SelectFamily(IEnumerable<string> releases, string family)
    {
        if (!IsReleaseFamily(family)) throw new ArgumentException("Enter a release family such as 741.");
        return releases.Where(IsReleaseNumber).Where(release => release.Split('.')[1] == family)
            .OrderByDescending(release => string.Join(".", release.Split('.').Select(part => part.PadLeft(10, '0'))), StringComparer.Ordinal)
            .FirstOrDefault() ?? throw new InvalidDataException("No Windows Player release was found for " + family + ". Choose a listed release or paste a build hash.");
    }

    internal static async Task<string> GetCurrentAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 64 * 1024 };
        string json = await client.GetStringAsync("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer/channel/LIVE", token).ConfigureAwait(false);
        RememberRelease(json, false);
        string release = ReadCatalogRelease(json, false) ?? throw new InvalidDataException("Roblox did not return a valid current Windows Player release.");
        return SelectExact(ParseCatalog(json, release, false));
    }

    internal static void RefreshCatalog() { lock (CatalogLock) _releaseCatalog = null; }

    internal static string? ReadCatalogRelease(string json, bool weao)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (weao && (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("WindowsResponse", out root))) return null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var number) || number.ValueKind != JsonValueKind.String) return null;
        string? release = number.GetString();
        return IsReleaseNumber(release) && ParseCatalog(json, release!, weao).Any() ? release : null;
    }

    internal static async Task<(string? Current, string? Previous)> GetReleaseLabelsAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 64 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WEAO-3PService DepthStrap/" + App.Version);
        async Task<string?> Read(string url, bool weao)
        {
            try { string json = await client.GetStringAsync(url).ConfigureAwait(false); RememberRelease(json, weao); return ReadCatalogRelease(json, weao); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { return null; }
        }
        var current = Read("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer/channel/LIVE", false);
        var previous = Read(WeaoDowngradeSource.PreviousApi, true);
        await Task.WhenAll(current, previous).ConfigureAwait(false);
        return (await current, await previous);
    }

    internal static string[] ParseReleaseHistory(string text, bool requireHash = false) => Regex.Matches(text,
        @"^New (?:WindowsPlayer|Client) version-(?:[a-fA-F0-9]{16}|hidden) at [^\r\n]*?file version: ([0-9]+),\s*([0-9]+),\s*([0-9]+),\s*([0-9]+)(?:,|\s|$)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
        .Where(match => !requireHash || !match.Value.Contains("version-hidden ", StringComparison.Ordinal))
        .Select(match => string.Join(".", Enumerable.Range(1, 4).Select(i => match.Groups[i].Value)))
        .Where(IsReleaseNumber).Distinct(StringComparer.Ordinal).ToArray();

    internal static bool IsSuggestionQuery(string query) => Regex.IsMatch(query,
        @"\A(?:[0-9]{2,10}|0\.[0-9]{1,10}(?:\.[0-9]{0,10}){0,2})\z", RegexOptions.CultureInvariant) && !IsReleaseNumber(query);

    internal static string[] MatchReleases(IEnumerable<string> releases, string query)
    {
        query = query.Trim();
        if (!IsSuggestionQuery(query)) return Array.Empty<string>();
        return releases.Where(IsReleaseNumber).Where(release => query.Contains('.')
                ? release.StartsWith(query, StringComparison.Ordinal)
                : release.Split('.')[1].StartsWith(query, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).OrderByDescending(release => string.Join(".", release.Split('.').Select(part => part.PadLeft(10, '0'))), StringComparer.Ordinal)
            .Take(100).ToArray();
    }

    internal static async Task<string[]> GetReleaseCatalogAsync()
    {
        Task<string[]> catalog;
        lock (CatalogLock) catalog = _releaseCatalog ??= LoadReleaseCatalogAsync();
        try { return await catalog.ConfigureAwait(false); }
        catch
        {
            lock (CatalogLock) { if (ReferenceEquals(_releaseCatalog, catalog)) _releaseCatalog = null; }
            throw;
        }
    }

    private static async Task<string[]> LoadReleaseCatalogAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WEAO-3PService DepthStrap/" + App.Version);
        var sources = new[]
        {
            (Url: "https://setup.rbxcdn.com/DeployHistory.txt", Kind: 2),
            (Url: "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer/channel/LIVE", Kind: 0),
            (Url: "https://weao.gg/api/versions/current", Kind: 1),
            (Url: WeaoDowngradeSource.PreviousApi, Kind: 1)
        };
        var results = await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                string content = await client.GetStringAsync(source.Url).ConfigureAwait(false);
                if (source.Kind == 2) return ParseReleaseHistory(content, requireHash: true);
                RememberRelease(content, source.Kind == 1);
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                if (source.Kind == 1 && (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("WindowsResponse", out root))) return Array.Empty<string>();
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var number) || number.ValueKind != JsonValueKind.String) return Array.Empty<string>();
                string? release = number.GetString();
                return IsReleaseNumber(release) && ParseCatalog(content, release!, source.Kind == 1).Any() ? new[] { release! } : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or RegexMatchTimeoutException)
            { return Array.Empty<string>(); }
        })).ConfigureAwait(false);
        var releases = results.SelectMany(result => result).Concat(KnownReleases().Select(entry => entry.Release)).Distinct(StringComparer.Ordinal).ToArray();
        if (releases.Length == 0) throw new InvalidDataException("The Player release catalog was empty.");
        return releases;
    }

    internal static bool IsReleaseNumber(string? value) => value is not null &&
        Regex.IsMatch(value, @"\A[0-9]{1,10}(?:\.[0-9]{1,10}){3}\z", RegexOptions.CultureInvariant);

    internal static IEnumerable<string> ParseCatalog(string json, string release, bool weao)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (weao && !root.TryGetProperty("WindowsResponse", out root)) yield break;
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("version", out var number) || number.ValueKind != JsonValueKind.String || number.GetString() != release ||
            !root.TryGetProperty("clientVersionUpload", out var id) || id.ValueKind != JsonValueKind.String || !RobloxVersionArchive.IsVersionId(id.GetString())) yield break;
        string hash = id.GetString()!.ToLowerInvariant();
        if (weao && (!document.RootElement.TryGetProperty("Windows", out var windows) || windows.ValueKind != JsonValueKind.String ||
            !string.Equals(windows.GetString(), hash, StringComparison.OrdinalIgnoreCase))) yield break;
        yield return hash;
    }

    internal static IEnumerable<string> ParseHistory(string text, string release)
    {
        foreach (Match match in Regex.Matches(text,
            @"^New (?:WindowsPlayer|Client) (version-[a-fA-F0-9]{16}) at [^\r\n]*?file version: ([0-9]+),\s*([0-9]+),\s*([0-9]+),\s*([0-9]+)(?:,|\s|$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
        {
            if (string.Join(".", Enumerable.Range(2, 4).Select(i => match.Groups[i].Value)) == release)
                yield return match.Groups[1].Value.ToLowerInvariant();
        }
    }

    internal static string SelectExact(IEnumerable<string> candidates)
    {
        var matches = candidates.Where(RobloxVersionArchive.IsVersionId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return matches.Length switch
        {
            1 => matches[0].ToLowerInvariant(),
            0 => throw new InvalidDataException("No exact Windows Player build hash was found for that release. It may be a Studio release or an unpublished build. Paste a known build hash or choose an installed build."),
            _ => throw new InvalidDataException("That release maps to multiple Windows Player builds. Paste the specific version-… hash to choose one.")
        };
    }

    internal static async Task<string> ResolveAsync(string release, CancellationToken token)
    {
        if (!IsReleaseNumber(release)) throw new ArgumentException("Enter a full four-part Roblox release number.", nameof(release));
        var candidates = new List<string>();
        candidates.AddRange(KnownReleases().Where(entry => entry.Release == release).Select(entry => entry.Hash));
        foreach (string hash in RobloxVersionArchive.InstalledPlayerVersions())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (RobloxClientVersion.ReadRelease(Path.Combine(Paths.Versions, hash, App.RobloxPlayerAppName)) == release) candidates.Add(hash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WEAO-3PService DepthStrap/" + App.Version);
        var sources = new[]
        {
            (Url: "https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer/channel/LIVE", Kind: 0),
            (Url: "https://weao.gg/api/versions/current", Kind: 1),
            (Url: WeaoDowngradeSource.PreviousApi, Kind: 1),
            (Url: "https://setup.rbxcdn.com/DeployHistory.txt", Kind: 2)
        };
        var results = await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                string content = await client.GetStringAsync(source.Url, deadline.Token);
                if (source.Kind != 2) RememberRelease(content, source.Kind == 1);
                string[] found = (source.Kind == 2 ? ParseHistory(content, release) : ParseCatalog(content, release, source.Kind == 1)).ToArray();
                return (Success: true, Matches: found);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or RegexMatchTimeoutException)
            { return (Success: false, Matches: Array.Empty<string>()); }
        }));
        token.ThrowIfCancellationRequested();
        candidates.AddRange(results.SelectMany(result => result.Matches));
        if (candidates.Count == 0 && results.All(result => !result.Success))
            throw new IOException("Release lookup could not reach its catalogs. Check your connection and retry, or paste a build hash.");
        return SelectExact(candidates);
    }
}
