using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Bloxstrap;
using Bloxstrap.Models.Manifest;
using Bloxstrap.Roblox;

internal static class LiveVersionChecks
{
    internal static async Task RunAsync(string reportRoot)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var labels = await RobloxReleaseLookup.GetReleaseLabelsAsync();
        if (!RobloxReleaseLookup.IsReleaseNumber(labels.Current) || !RobloxReleaseLookup.IsReleaseNumber(labels.Previous))
            throw new IOException("Current or downgrade release labels are unavailable.");
        string current = await RobloxReleaseLookup.ResolveAsync(labels.Current!, budget.Token);
        string previous = await RobloxReleaseLookup.ResolveAsync(labels.Previous!, budget.Token);
        if (previous != await WeaoDowngradeSource.GetPreviousAsync(budget.Token))
            throw new IOException("Downgrade lookup sources do not agree.");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90), MaxResponseContentBufferSize = 256 * 1024 * 1024 };
        foreach (var release in new[] { (Number: labels.Current!, Hash: current), (Number: labels.Previous!, Hash: previous) })
        {
            string url = "https://setup.rbxcdn.com/" + release.Hash + "-";
            var manifest = new PackageManifest(await client.GetStringAsync(url + "rbxPkgManifest.txt", budget.Token));
            var app = manifest.SingleOrDefault(package => package.Name == "RobloxApp.zip")
                ?? throw new IOException("This manifest has no Player application package.");
            if (app.PackedSize > 256 * 1024 * 1024) throw new IOException("Player application package exceeds audit limit.");
            byte[] data = await client.GetByteArrayAsync(url + app.Name, budget.Token);
            if (data.Length != app.PackedSize || !Convert.ToHexString(MD5.HashData(data)).Equals(app.Signature, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Player application package did not match its Roblox manifest.");
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var entry = zip.GetEntry(App.RobloxPlayerAppName) ?? throw new IOException("Player executable is missing from its package.");
            string folder = Path.Combine(Path.GetFullPath(reportRoot), "verified-build-" + release.Hash);
            Directory.CreateDirectory(folder);
            foreach (string name in new[] { App.RobloxPlayerAppName, "RobloxPlayerBeta.dll" })
            {
                var file = zip.GetEntry(name);
                if (file is not null) file.ExtractToFile(Path.Combine(folder, name), false);
            }
            string executable = Path.Combine(folder, App.RobloxPlayerAppName);
            if (!RobloxClientFiles.IsPlayerComplete(executable)) throw new IOException("Player package is missing or has invalid required binaries.");
            string? actual = RobloxClientVersion.ReadRelease(executable);
            if (actual != release.Number) throw new IOException("Downloaded Player version did not match its exact catalog release.");
            Console.WriteLine($"PASS: official Player package for {release.Number} matches manifest, executable version and required DLL checks. No installation or launch occurred.");
        }
        Console.WriteLine("PASS: live current/downgrade release lookup and package audit.");
    }
}
