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
    private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position > 0) return ValueTask.FromException<int>(new IOException("Fixture interrupted transfer"));
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], token);
        }
    }
    internal static void Run(Action<bool, string> check)
    {
        byte[] bytes = Enumerable.Range(0, 90000).Select(x => (byte)x).ToArray();
        var release = new GithubRelease { TagName = "v1.0.12", Assets = new() { new GithubReleaseAsset {
            Name = "DepthStrap.exe", BrowserDownloadUrl = "https://github.com/deepisthatdeep/DepthStrap/releases/download/v1.0.12/DepthStrap.exe",
            Size = bytes.Length, Digest = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)) } } };
        var plan = AppUpdater.SelectRelease(release, "1.0.11")!;
        check(plan is not null && plan.Version == new Version(1, 0, 12, 0), "Stable GitHub release selects a newer verified DepthStrap asset");
        var renamedRelease = new GithubRelease { TagName = "v1.2.0", Assets = new() { new GithubReleaseAsset {
            Name = "DepthStrap.exe", BrowserDownloadUrl = "https://github.com/deepisthatdeep/DepthStrap/releases/download/v1.2.0/DepthStrap.exe",
            Size = bytes.Length, Digest = release.Assets![0].Digest } } };
        check(AppUpdater.SelectRelease(renamedRelease, "1.0.17")?.Version == new Version(1, 2, 0, 0),
            "Earlier 1.0.17 installations recognize the renamed 1.2 release");
        check(AppUpdater.SelectRelease(renamedRelease, "1.2") is null && AppUpdater.SelectRelease(renamedRelease, "1.2.0") is null,
            "The two-part app label and three-part release tag cannot trigger an update loop");
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
        foreach (string flag in new[] { "-upgrade", "-watcher", "-multiinstancewatcher", "-backgroundupdater", "-uninstall", "-nolaunch", "-testmode", "-autologhome", "-quiet", "-skipappupdate", "-updatehandoff" })
            check(!AppUpdater.ShouldCheck(settings, new LaunchSettings(new[] { flag }), false), "Internal or maintenance launches skip app updates: " + flag);
        string[] arguments = { "-player", "roblox-player:launchmode:play+gameinfo:fixture token", "-channel", "LIVE" };
        var start = AppUpdater.StartInfo("C:\\fixture\\DepthStrap.exe", arguments);
        check(!start.UseShellExecute && start.ArgumentList.SequenceEqual(arguments.Concat(new[] { "-upgrade" })), "Updater handoff retains arguments and embedded spaces without shell interpolation");
        foreach (string[] original in new[] {
            new[] { "roblox-player:launchmode:play+gameinfo:fixture token" },
            new[] { "roblox://experiences/start?placeId=123&gameInstanceId=fixture" },
            new[] { "version-0123456789abcdef", "-channel", "LIVE" },
            new[] { "-player", "roblox-player:launchmode:play+gameinfo:fixture token", "-channel", "LIVE" },
            new[] { "-studio", "C:\\fixture with spaces\\place.rbxl" },
            new[] { "-menu" }, Array.Empty<string>() })
        {
            var before = new LaunchSettings(original);
            var after = new LaunchSettings(AppUpdater.StartInfo("C:\\fixture\\DepthStrap.exe", original).ArgumentList.ToArray());
            check(after.UpgradeFlag.Active && after.UpgradeFlag.Data is null && before.RobloxLaunchMode == after.RobloxLaunchMode &&
                before.RobloxLaunchArgs == after.RobloxLaunchArgs && before.VersionFlag.Data == after.VersionFlag.Data &&
                before.ChannelFlag.Data == after.ChannelFlag.Data && before.MenuFlag.Active == after.MenuFlag.Active,
                "App update preserves the parser's original launch intent: " + (original.FirstOrDefault()?.Split(':')[0] ?? "default"));
        }
        foreach (string implicitLaunch in new[] { "roblox://experiences/start?placeId=123", "roblox-player:launchmode:play+gameinfo:fixture token", "version-0123456789abcdef" })
        {
            var direct = new LaunchSettings(new[] { implicitLaunch, "-channel", "LIVE" });
            var legacy = new LaunchSettings(new[] { "-upgrade", implicitLaunch, "-channel", "LIVE" });
            check(legacy.UpgradeFlag.Active && legacy.UpgradeFlag.Data is null && direct.RobloxLaunchMode == legacy.RobloxLaunchMode &&
                direct.RobloxLaunchArgs == legacy.RobloxLaunchArgs && direct.VersionFlag.Data == legacy.VersionFlag.Data && legacy.ChannelFlag.Data == "LIVE",
                "New builds recover implicit launch arguments forwarded by older updaters");
        }
        foreach (string[] original in new[] {
            new[] { "roblox://experiences/start?placeId=123&gameInstanceId=fixture", "-upgrade", "-updatehandoff", Guid.NewGuid().ToString("N") },
            new[] { "-upgrade", "roblox-player:launchmode:play+gameinfo:fixture token" },
            new[] { "-studio", "C:\\fixture with spaces\\place.rbxl", "-upgrade", "-updatehandoff", Guid.NewGuid().ToString("N") },
            new[] { "version-0123456789abcdef", "-channel", "LIVE", "-upgrade" } })
        {
            var expected = new LaunchSettings(original);
            var resumedInfo = AppUpdater.ResumeStartInfo(@"C:\fixture\DepthStrap.exe", original);
            var resumed = new LaunchSettings(resumedInfo.ArgumentList.ToArray());
            check(!resumedInfo.UseShellExecute && resumed.UpgradeFlag.Active && resumed.UpgradeFlag.Data is null && !resumed.UpdateHandoffFlag.Active && resumed.SkipAppUpdateFlag.Active &&
                resumed.RobloxLaunchMode == expected.RobloxLaunchMode && resumed.RobloxLaunchArgs == expected.RobloxLaunchArgs &&
                resumed.VersionFlag.Data == expected.VersionFlag.Data && resumed.ChannelFlag.Data == expected.ChannelFlag.Data &&
                !AppUpdater.ShouldCheck(new Settings(), resumed, false),
                "Failed-update fallback resumes the original action once without another update loop");
            var legacy = new LegacyLaunchSettings(resumedInfo.ArgumentList.ToArray());
            check(legacy.UpgradeFlag.Active && legacy.UpgradeFlag.Data is null &&
                legacy.RobloxLaunchMode == expected.RobloxLaunchMode && legacy.RobloxLaunchArgs == expected.RobloxLaunchArgs &&
                legacy.VersionFlag.Data == expected.VersionFlag.Data && legacy.ChannelFlag.Data == expected.ChannelFlag.Data &&
                !LegacyLaunchSettings.ShouldCheck(new Settings(), legacy, false),
                "The released legacy parser and update policy preserve the fallback action and suppress repeated updates");
            check(!AppUpdater.CanResumeFailedUpdate(resumed), "A recovered app cannot enter another automatic fallback loop");
        }
        check(AppUpdater.CanResumeFailedUpdate(new LaunchSettings(new[] { "-upgrade" })) &&
            !AppUpdater.CanResumeFailedUpdate(new LaunchSettings(Array.Empty<string>())) && !AppUpdater.CanResumeFailedUpdate(null),
            "Only an unrecovered automatic-update launch can invoke installed-app fallback");
        check(AppUpdater.ShouldDeferAutomaticReplacement("DepthStrap", "1.0.17.0", "1.0.16") &&
            !AppUpdater.ShouldDeferAutomaticReplacement("DepthStrap", "1.0.16.0", "1.0.16") &&
            !AppUpdater.ShouldDeferAutomaticReplacement("DepthStrap", "1.0.15", "1.0.16.0"),
            "The under-lock replacement policy refuses stale updates while allowing equal/newer versions");
        check(!AppUpdater.ShouldDeferAutomaticReplacement("DepthStrap", "1.5.1.0", "1.0.0.0") &&
            AppUpdater.ShouldDeferAutomaticReplacement("Other app", "1.5.1.0", "1.0.0.0"),
            "Stale-update protection retains only the established DepthStrap prototype migration exception");
        int resumeStarts = 0;
        check(AppUpdater.TryResumeInstalled(binary, arguments, _ => { resumeStarts++; return true; }) && resumeStarts == 1,
            "A recognized installed DepthStrap build can resume the pending launch");
        check(!AppUpdater.TryResumeInstalled(binary, arguments, _ => false) &&
            !AppUpdater.TryResumeInstalled(binary, arguments, _ => throw new IOException("Fixture failed resume")),
            "Failed fallback starts are reported without recursively restarting");
        check(!AppUpdater.TryResumeInstalled(Path.Combine(Paths.Base, "missing-app.exe"), arguments, _ => { resumeStarts++; return true; }) && resumeStarts == 1,
            "Missing installed apps are never started during fallback");
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
            check(rejected && badHandler.Requests == 1 && !File.Exists(Path.Combine(badDir, "DepthStrap.exe")) && Directory.GetFiles(badDir, "*.partial").Length == 0,
                "Corrupt/truncated/oversized updates cannot be promoted and leave no partial download");
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = AppUpdater.DownloadAsync(client, plan!, directory, cancel.Token);
        bool cancellationObserved = false; try { HistoryChecks.Wait(cancelled); cancelled.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved && File.ReadAllBytes(target).SequenceEqual(bytes), "Cancellation preserves a previously verified updater");
        int queryAttempts = 0;
        using (var retryHandler = new Handler(() => ++queryAttempts < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(release)) }))
        using (var retryClient = new HttpClient(retryHandler))
        {
            var query = AppUpdater.FetchReleaseAsync(retryClient, default, 1); HistoryChecks.Wait(query);
            check(queryAttempts == 3 && AppUpdater.SelectRelease(query.Result, "1.0.11") is not null,
                "Transient release API failures are retried before deferring the update");
        }
        using (var rateHandler = new Handler(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)))
        using (var rateClient = new HttpClient(rateHandler))
        {
            var query = AppUpdater.FetchReleaseAsync(rateClient, default, 1); bool rejected = false;
            try { HistoryChecks.Wait(query); } catch (HttpRequestException) { rejected = true; }
            check(rejected && rateHandler.Requests == 1, "Rate-limited update checks defer without repeatedly hitting GitHub");
        }
        int timeoutAttempts = 0;
        using (var timeoutHandler = new Handler(() => ++timeoutAttempts == 1
            ? throw new TaskCanceledException("Fixture per-request timeout")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(release)) }))
        using (var timeoutClient = new HttpClient(timeoutHandler))
        {
            var query = AppUpdater.FetchReleaseAsync(timeoutClient, default, 1); HistoryChecks.Wait(query);
            check(timeoutAttempts == 2 && query.Result?.TagName == release.TagName, "A single request timeout does not permanently suppress a startup update");
        }
        int transferAttempts = 0;
        using (var retryHandler = new Handler(() => ++transferAttempts switch
        {
            1 => throw new HttpRequestException("Fixture connection failure"),
            2 => new HttpResponseMessage(HttpStatusCode.BadGateway),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
        }))
        using (var retryClient = new HttpClient(retryHandler))
        {
            var download = AppUpdater.DownloadWithRetriesAsync(retryClient, plan!, Path.Combine(directory, "retry"), default, 1);
            HistoryChecks.Wait(download);
            check(transferAttempts == 3 && File.ReadAllBytes(download.Result).SequenceEqual(bytes), "Connection and gateway failures retry the app download and verify its final bytes");
        }
        int interruptedAttempts = 0;
        using (var interruptedHandler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = ++interruptedAttempts == 1 ? new StreamContent(new InterruptedStream(bytes)) : new ByteArrayContent(bytes) }))
        using (var interruptedClient = new HttpClient(interruptedHandler))
        {
            string retryDirectory = Path.Combine(directory, "interrupted");
            var download = AppUpdater.DownloadWithRetriesAsync(interruptedClient, plan!, retryDirectory, default, 1);
            HistoryChecks.Wait(download);
            check(interruptedAttempts == 2 && File.ReadAllBytes(download.Result).SequenceEqual(bytes) && Directory.GetFiles(retryDirectory, "*.partial").Length == 0,
                "An interrupted app transfer is cleaned up, retried and cannot expose partial executable bytes");
        }
        using (var permanentHandler = new Handler(() => new HttpResponseMessage(HttpStatusCode.NotFound)))
        using (var permanentClient = new HttpClient(permanentHandler))
        {
            var download = AppUpdater.DownloadWithRetriesAsync(permanentClient, plan!, Path.Combine(directory, "not-found"), default, 1);
            bool rejected = false; try { HistoryChecks.Wait(download); } catch (HttpRequestException) { rejected = true; }
            check(rejected && permanentHandler.Requests == 1, "Permanent missing update assets are not retried");
        }
        string installed = Path.Combine(directory, "installed.exe"); File.WriteAllText(installed, "old");
        using (var locked = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false; try { AppUpdater.ReplaceExecutable(target, installed); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            check(failed && File.ReadAllText(installed) == "old", "A locked installed app remains intact when updater replacement fails");
        }
        using (var locked = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool failed = false;
            try { AppUpdater.ReplaceExecutableWithRetries(target, installed, 3, 1); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            check(failed && File.ReadAllText(installed) == "old" && Directory.GetFiles(directory, "*.new").Length == 0,
                "Exhausted replacement retries preserve the previous app and remove staging files");
        }
        var temporaryLock = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read);
        var unlock = Task.Run(async () => { await Task.Delay(30); temporaryLock.Dispose(); });
        try { AppUpdater.ReplaceExecutableWithRetries(target, installed, 100, 5); }
        finally { unlock.GetAwaiter().GetResult(); temporaryLock.Dispose(); }
        check(File.ReadAllBytes(installed).SequenceEqual(bytes), "App replacement recovers when a temporary executable lock clears");
        AppUpdater.ReplaceExecutable(target, installed);
        check(File.ReadAllBytes(installed).SequenceEqual(bytes) && Directory.GetFiles(directory, "*.new").Length == 0,
            "Atomic app replacement installs verified bytes and removes staging files");
    }
}
