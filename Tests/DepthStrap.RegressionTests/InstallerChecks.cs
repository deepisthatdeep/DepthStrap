using System.IO;
using System.Net;
using System.Net.Http;
using Bloxstrap;
using Bloxstrap.Models.Manifest;
using Bloxstrap.Models.SettingTasks.Base;
using Bloxstrap.Utility;

internal static class InstallerChecks
{
    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(response());
    }
    private sealed class FixtureTask(string name, Action execute) : BaseTask(name)
    {
        public override bool Changed => true;
        public override void Execute() => execute();
    }
    internal static void Run(Action<bool, string> check)
    {
        byte[] bytes = Enumerable.Range(0, 120000).Select(x => (byte)x).ToArray();
        var package = new Package { Name = "fixture.zip", Signature = MD5Hash.FromBytes(bytes).ToUpperInvariant(), PackedSize = bytes.Length, Size = bytes.Length };
        Directory.CreateDirectory(Paths.Downloads);
        string source = Path.Combine(Paths.Base, "fixture-cache");
        File.WriteAllBytes(source, bytes);
        bool copied = VerifiedPackageCache.TryCopyAsync(source, package, CancellationToken.None).GetAwaiter().GetResult();
        check(copied && File.ReadAllBytes(package.DownloadPath).SequenceEqual(bytes), "Stock cache copies are staged and verified, including uppercase manifest hashes");
        File.WriteAllBytes(source, new byte[bytes.Length]);
        check(!VerifiedPackageCache.TryCopyAsync(source, package, CancellationToken.None).GetAwaiter().GetResult() &&
            File.ReadAllBytes(package.DownloadPath).SequenceEqual(bytes), "A corrupt stock cache falls back to download without overwriting a verified package");
        File.WriteAllBytes(source, bytes[..100]);
        check(!VerifiedPackageCache.TryCopyAsync(source, package, CancellationToken.None).GetAwaiter().GetResult(), "A truncated stock cache is rejected");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try { VerifiedPackageCache.TryCopyAsync(source, package, cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && !Directory.EnumerateFiles(Paths.Downloads, "*.tmp").Any(), "Cancelled cache copies propagate cancellation and remove staged files");
        }
        void Download(byte[] body, HttpStatusCode status, CancellationToken token = default, Action<int>? progress = null)
        {
            using var client = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) }));
            VerifiedPackageCache.DownloadAsync(client, "https://fixture.invalid/package", package, token, progress).GetAwaiter().GetResult();
        }
        foreach (var fixture in new[] { (bytes[..100], HttpStatusCode.OK), (new byte[bytes.Length], HttpStatusCode.OK),
            (bytes.Concat(new byte[] { 1 }).ToArray(), HttpStatusCode.OK), (bytes, HttpStatusCode.NotFound) })
        {
            bool rejected = false;
            try { Download(fixture.Item1, fixture.Item2); }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException) { rejected = true; }
            check(rejected && File.ReadAllBytes(package.DownloadPath).SequenceEqual(bytes) &&
                !Directory.EnumerateFiles(Paths.Downloads, "*.tmp").Any(), "Failed, oversized, truncated or corrupt HTTP responses cannot replace the verified cache");
        }
        File.Delete(package.DownloadPath);
        using (var cancellation = new CancellationTokenSource())
        {
            bool cancelled = false;
            try { Download(bytes, HttpStatusCode.OK, cancellation.Token, _ => cancellation.Cancel()); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && !File.Exists(package.DownloadPath) && !Directory.EnumerateFiles(Paths.Downloads, "*.tmp").Any(),
                "Cancellation after receiving package data never exposes a partial package to extraction");
        }
        int received = 0;
        Download(bytes, HttpStatusCode.OK, progress: count => received += count);
        check(received == bytes.Length && File.ReadAllBytes(package.DownloadPath).SequenceEqual(bytes), "A successful HTTP transfer commits exactly the verified manifest bytes");
        string record = $"fixture.zip\n{package.Signature}\n{bytes.Length}\n{bytes.Length}\n";
        check(new PackageManifest("v0\n" + record).Count == 1, "A complete Roblox package manifest remains supported");
        check(new PackageManifest("v0\n" + record.Replace("fixture.zip", "RobloxPlayerLauncher.exe") + record).Count == 1,
            "Skipping the stock launcher does not silently drop subsequent packages");
        foreach (string invalid in new[] { "v0\n", "v0\nfixture.zip\n", "v0\n" + record + record,
            "v0\n" + record.Replace("fixture.zip", "../fixture.zip"), "v0\n" + record.Replace(package.Signature, "../hash"),
            "v0\n" + record.Replace(bytes.Length.ToString(), "-1"), "v0\n" + record.Replace(bytes.Length.ToString(), "999999999999999") })
        {
            bool rejected = false;
            try { _ = new PackageManifest(invalid); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Malformed, empty, truncated and duplicate manifests cannot be treated as a completed installation");
        }
        var pending = App.PendingSettingTasks.ToArray();
        App.PendingSettingTasks.Clear();
        try
        {
            int first = 0, last = 0; bool fail = true;
            App.PendingSettingTasks["first"] = new FixtureTask("first", () => first++);
            App.PendingSettingTasks["failure"] = new FixtureTask("failure", () => { if (fail) throw new IOException("Fixture setting failure"); });
            App.PendingSettingTasks["last"] = new FixtureTask("last", () => last++);
            check(!PendingSettingsExecutor.TryExecute(out string? failed) && failed == "failure" && first == 1 && last == 0 &&
                !App.PendingSettingTasks.ContainsKey("first") && App.PendingSettingTasks.Count == 2,
                "A setting failure retains failed and unexecuted tasks without rerunning completed changes");
            fail = false;
            check(PendingSettingsExecutor.TryExecute(out _) && first == 1 && last == 1 && App.PendingSettingTasks.Count == 0,
                "Retrying Save applies the remaining changes exactly once");
            int obsolete = 0, replacement = 0;
            App.PendingSettingTasks.Clear();
            App.PendingSettingTasks["mutator"] = new FixtureTask("mutator", () =>
                App.PendingSettingTasks["next"] = new FixtureTask("next", () => replacement++));
            App.PendingSettingTasks["next"] = new FixtureTask("next", () => obsolete++);
            check(!PendingSettingsExecutor.TryExecute(out failed) && failed == "next" && obsolete == 0 && replacement == 0,
                "Changes queued during Save cannot execute a stale task or incorrectly report all changes applied");
            check(PendingSettingsExecutor.TryExecute(out _) && replacement == 1 && App.PendingSettingTasks.Count == 0,
                "A replacement task remains available for a subsequent Save");
        }
        finally
        {
            App.PendingSettingTasks.Clear();
            foreach (var pair in pending) App.PendingSettingTasks[pair.Key] = pair.Value;
        }
    }
}
