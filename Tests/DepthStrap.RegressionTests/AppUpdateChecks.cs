using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Models.APIs.GitHub;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Utility;

internal static class AppUpdateChecks
{
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; token.ThrowIfCancellationRequested(); return Task.FromResult(response()); }
    }
    internal static void Run(Action<bool, string> check)
    {
        byte[] bytes = Enumerable.Range(0, 90000).Select(x => (byte)x).ToArray();
        var release = new GithubRelease { TagName = "v1.0.12", Assets = new() { new GithubReleaseAsset {
            Name = "DepthStrap.exe", BrowserDownloadUrl = "https://github.com/deepisthatdeep/DepthStrap/releases/download/v1.0.12/DepthStrap.exe",
            Size = bytes.Length, Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)) } } };
        var plan = AppUpdater.SelectRelease(release, "1.0.11")!;
        check(plan is not null && plan.Version == new Version(1, 0, 12, 0), "Stable GitHub release selects a newer verified DepthStrap asset");
        check(AppUpdater.SelectRelease(release, "1.0.12.0") is null && AppUpdater.SelectRelease(release, "1.0.13") is null,
            "App updater treats three/four-part versions equally and never downgrades");
        release.Prerelease = true; check(AppUpdater.SelectRelease(release, "1.0.11") is null, "App updater rejects prereleases");
        release.Prerelease = false; release.Draft = true; check(AppUpdater.SelectRelease(release, "1.0.11") is null, "App updater rejects drafts"); release.Draft = false;
        release.TagName = "v1.0.12/../../other"; check(AppUpdater.SelectRelease(release, "1.0.11") is null, "App updater rejects unsafe or malformed release tags"); release.TagName = "v1.0.12";
        var asset = release.Assets![0];
        string url = asset.BrowserDownloadUrl;
        foreach (string invalid in new[] { url.Replace("https:", "http:"), url.Replace("deepisthatdeep", "other"), url.Replace("v1.0.12", "v1.0.13"), url + "?redirect=1" })
        { asset.BrowserDownloadUrl = invalid; check(AppUpdater.SelectRelease(release, "1.0.11") is null, "App updater rejects mismatched repository, version or download URL"); }
        asset.BrowserDownloadUrl = url;
        string digest = asset.Digest!; asset.Digest = null;
        check(AppUpdater.SelectRelease(release, "1.0.11") is null, "Missing GitHub SHA256 digest prevents update selection"); asset.Digest = digest;
        asset.Size = 0; check(AppUpdater.SelectRelease(release, "1.0.11") is null, "Missing asset size prevents update selection"); asset.Size = bytes.Length;
        release.Assets.Add(asset); check(AppUpdater.SelectRelease(release, "1.0.11") is null, "Ambiguous duplicate app assets prevent update selection"); release.Assets.RemoveAt(1);
        check(new Settings().AutomaticDepthStrapUpdates && JsonSerializer.Deserialize<Settings>("{\"UpdateChecks\":0}")!.AutomaticDepthStrapUpdates,
            "New installs and legacy manual-update settings enable the new automatic app updates by default");
        var settings = new Settings();
        string binary = typeof(App).Assembly.Location;
        check(AppUpdater.IsInstalledVersion(binary, App.Version) && AppUpdater.IsInstalledVersion(binary, App.Version + ".0"),
            "Update completion accepts matching three/four-part DepthStrap file versions");
        check(!AppUpdater.IsInstalledVersion(binary, "99.0.0") && !AppUpdater.IsInstalledVersion(binary, "invalid"),
            "Update completion refuses a wrong or invalid advertised version");
        check(AppUpdater.ShouldCheck(settings, new LaunchSettings(Array.Empty<string>()), false), "Normal app startup checks for app updates");
        settings.AutomaticDepthStrapUpdates = false;
        check(!AppUpdater.ShouldCheck(settings, new LaunchSettings(Array.Empty<string>()), false), "Disabling app updates skips startup update checks"); settings.AutomaticDepthStrapUpdates = true;
        check(!AppUpdater.ShouldCheck(settings, new LaunchSettings(Array.Empty<string>()), true), "Active clients defer app updates");
        var savedSettings = App.Settings.Prop;
        try
        {
            App.Settings.Prop = new Settings { PauseRobloxUpdates = true, UpdateRoblox = false,
                RobloxPlayerVersionOverride = "version-0123456789abcdef", BackgroundUpdatesEnabled = false };
            var model = new Bloxstrap.UI.ViewModels.Settings.ChannelViewModel();
            model.AutomaticDepthStrapUpdates = false; model.AutomaticDepthStrapUpdates = true;
            check(App.Settings.Prop.PauseRobloxUpdates && !App.Settings.Prop.UpdateRoblox &&
                App.Settings.Prop.RobloxPlayerVersionOverride == "version-0123456789abcdef" && !App.Settings.Prop.BackgroundUpdatesEnabled,
                "DepthStrap automatic-update setting preserves Roblox pause, pinned build and background-update policy");
        }
        finally { App.Settings.Prop = savedSettings; }
        foreach (string flag in new[] { "-upgrade", "-watcher", "-multiinstancewatcher", "-backgroundupdater", "-uninstall", "-nolaunch", "-testmode", "-autologhome", "-quiet" })
            check(!AppUpdater.ShouldCheck(settings, new LaunchSettings(new[] { flag }), false), "Internal or maintenance launches skip app updates: " + flag);
        string[] arguments = { "-player", "roblox-player:launchmode:play+gameinfo:fixture token", "-channel", "LIVE" };
        var start = AppUpdater.StartInfo("C:\\fixture\\DepthStrap.exe", arguments);
        check(!start.UseShellExecute && start.ArgumentList.SequenceEqual(new[] { "-upgrade" }.Concat(arguments)), "Updater handoff retains arguments and embedded spaces without shell interpolation");
        string directory = Path.Combine(Paths.Base, "app-update-download");
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var client = new HttpClient(handler);
        var task = AppUpdater.DownloadAsync(client, plan!, directory, CancellationToken.None); HistoryChecks.Wait(task); string target = task.GetAwaiter().GetResult();
        check(File.ReadAllBytes(target).SequenceEqual(bytes), "App update download is staged and checksum verified before promotion");
        task = AppUpdater.DownloadAsync(client, plan!, directory, CancellationToken.None); HistoryChecks.Wait(task);
        check(handler.Requests == 1, "Only a size/hash-verified cached update can avoid downloading again");
        File.WriteAllText(target, "corrupt cache");
        task = AppUpdater.DownloadAsync(client, plan!, directory, CancellationToken.None); HistoryChecks.Wait(task);
        check(handler.Requests == 2 && File.ReadAllBytes(target).SequenceEqual(bytes), "Corrupt cached app updater is redownloaded before use");
        foreach (var content in new[] { new byte[bytes.Length], bytes[..100], bytes.Concat(new byte[] { 1 }).ToArray() })
        {
            using var badHandler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
            using var badClient = new HttpClient(badHandler);
            string badDir = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            var failed = AppUpdater.DownloadAsync(badClient, plan!, badDir, CancellationToken.None);
            bool rejected = false; try { HistoryChecks.Wait(failed); failed.GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
            check(rejected && !File.Exists(Path.Combine(badDir, "DepthStrap.exe")) && Directory.GetFiles(badDir, "*.partial").Length == 0,
                "Corrupt/truncated/oversized updates cannot be promoted and leave no partial download");
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = AppUpdater.DownloadAsync(client, plan!, directory, cancel.Token);
        bool cancellationObserved = false; try { HistoryChecks.Wait(cancelled); cancelled.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved && File.ReadAllBytes(target).SequenceEqual(bytes), "Cancellation preserves a previously verified updater");
        string installed = Path.Combine(directory, "installed.exe"); File.WriteAllText(installed, "old");
        using (var locked = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false; try { AppUpdater.ReplaceExecutable(target, installed); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            check(failed && File.ReadAllText(installed) == "old", "A locked installed app remains intact when updater replacement fails");
        }
        AppUpdater.ReplaceExecutable(target, installed);
        check(File.ReadAllBytes(installed).SequenceEqual(bytes) && Directory.GetFiles(directory, "*.new").Length == 0,
            "Atomic app replacement installs verified bytes and removes staging files");
    }
}
