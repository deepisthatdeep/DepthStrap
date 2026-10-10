using System.IO;
using System.Text;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Competitive;
using Bloxstrap.Integrations;
using Bloxstrap.Networking;

internal static class ProbeCoverageAudit
{
    internal static async Task RunAsync(string directory)
    {
        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        Paths.Initialize(Path.Combine(root, "fixture-" + Guid.NewGuid().ToString("N")));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var targets = await RegionCalibrationService.DiscoverAsync(budget.Token);
        var browser = new DepthStrapServerBrowser();
        var entries = await browser.GetDatacenterEntriesAsync(budget.Token)
            ?? throw new IOException("The live datacenter inventory is unavailable; no NO answers can be inferred.");
        if (!await CompetitiveRegionService.EnsureRegistryLoadedAsync(browser).WaitAsync(budget.Token))
            throw new IOException("Production region registry could not be loaded.");
        var rows = entries.Where(entry => !entry.Inactive && entry.DataCenterIds.Count > 0)
            .Select(entry => (City: entry.Location.City.Trim(), Country: entry.Location.Country.Trim()))
            .Distinct().OrderBy(location => location.Country).ThenBy(location => location.City)
            .Select(location => new {
                location.City,
                location.Country,
                PublishedProbe = RoutingTargetDiscovery.CoversLocation(targets, location.City, location.Country) ? "YES" : "NO",
                Addresses = targets.Where(target => RoutingTargetDiscovery.CoversLocation(new[] { target }, location.City, location.Country))
                    .Select(target => target.Address).Distinct().Order().ToArray()
            }).ToList();
        if (rows.Count == 0 || targets.Count == 0) throw new IOException("An empty inventory cannot prove coverage.");
        foreach (var row in rows)
        {
            bool reportedMissing = CompetitiveRegionService.UnprobedLocations(targets, row.Country)
                .Contains($"{row.City}, {row.Country}", StringComparer.OrdinalIgnoreCase);
            if (reportedMissing != (row.PublishedProbe == "NO") || (row.Addresses.Length > 0) != (row.PublishedProbe == "YES"))
                throw new InvalidOperationException("Production missing-location reporting disagrees with the address inventory.");
        }
        var report = new {
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Question = "Does setup's current public Roblox peering inventory contain an address for this active datacenter location?",
            Scope = "YES means a published address exists, not that it replies or measures gameplay latency. NO means absent from this verified inventory, not absent from every possible source.",
            Sources = new[] { "https://www.peeringdb.com/api/netixlan?net_id=14578", "https://www.peeringdb.com/api/ix", "https://apis.rovalra.com/v1/datacenters/list" },
            ProductionMissingLocationCheckPassed = true,
            DiscoveredTargets = targets.OrderBy(target => target.Country).ThenBy(target => target.City).ThenBy(target => target.Address).ToArray(),
            Locations = rows
        };
        File.WriteAllText(Path.Combine(root, "coverage.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var markdown = new StringBuilder("# Roblox probe-address coverage\n\n");
        markdown.AppendLine(report.Question).AppendLine().AppendLine(report.Scope).AppendLine();
        markdown.AppendLine("| Location | Country | Published probe address? | Address count |").AppendLine("|---|---|---|---:|");
        foreach (var row in rows) markdown.AppendLine($"| {row.City} | {row.Country} | **{row.PublishedProbe}** | {row.Addresses.Length} |");
        markdown.AppendLine().AppendLine($"Production reporting cross-check: PASS for all {rows.Count} locations. No WARP changes or ICMP probes ran.");
        markdown.AppendLine().AppendLine("Sources: [Roblox peering inventory](https://www.peeringdb.com/net/14578), [datacenter inventory](https://apis.rovalra.com/v1/datacenters/list).");
        File.WriteAllText(Path.Combine(root, "coverage.md"), markdown.ToString());
        Console.WriteLine($"PASS: {rows.Count} exact YES/NO answers, {rows.Count(row => row.PublishedProbe == "YES")} YES and {rows.Count(row => row.PublishedProbe == "NO")} NO; production missing-location reporting agrees for every row.");
    }
}
