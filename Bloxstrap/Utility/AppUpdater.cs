using System.Security.Cryptography;

namespace Bloxstrap.Utility;

public sealed record AppUpdatePlan(string Tag, Version Version, Uri Download, long Size, string Sha256);

public static class AppUpdater
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/deepisthatdeep/DepthStrap/releases/latest";
    private const long MaximumSize = 512L * 1024 * 1024;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool IsInstalledVersion(string path, string expected)
    {
        if (!File.Exists(path) || !Version.TryParse(expected, out var target)) return false;
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName == "DepthStrap" && Version.TryParse(info.FileVersion, out var actual) && Normalize(actual) == Normalize(target);
    }

    public static bool ShouldCheck(Settings settings, LaunchSettings launch, bool clientsRunning) =>
        settings.AutomaticDepthStrapUpdates && !clientsRunning && !launch.BypassUpdateCheck &&
        !launch.UpgradeFlag.Active && !launch.BackgroundUpdaterFlag.Active &&
        !launch.UpdateHandoffFlag.Active && !launch.SkipAppUpdateFlag.Active &&
        !launch.MultiInstanceWatcherFlag.Active && !launch.NoLaunchFlag.Active && !launch.TestModeFlag.Active &&
        !launch.AutoLogHomeFlag.Active && !launch.PostLaunchFlag.Active && !launch.QuietFlag.Active;

    public static AppUpdatePlan? SelectRelease(GithubRelease? release, string current)
    {
        if (release is null || release.Draft || release.Prerelease ||
            !Regex.IsMatch(release.TagName ?? "", @"\Av?\d+\.\d+\.\d+(?:\.\d+)?\z") ||
            !Version.TryParse(release.TagName!.TrimStart('v'), out var next) ||
            !Version.TryParse(current.TrimStart('v'), out var installed)) return null;
        if (Normalize(next) <= Normalize(installed)) return null;
        var matches = release.Assets?.Where(x => x is not null && x.Name == "DepthStrap.exe").ToArray();
        if (matches?.Length != 1) return null;
        var asset = matches[0];
        string expected = $"https://github.com/{App.ProjectRepository}/releases/download/{release.TagName}/DepthStrap.exe";
        if (asset.BrowserDownloadUrl != expected || asset.Size <= 0 || asset.Size > MaximumSize ||
            !Regex.IsMatch(asset.Digest ?? "", @"\Asha256:[a-fA-F0-9]{64}\z")) return null;
        return new(release.TagName, Normalize(next), new Uri(expected), asset.Size, asset.Digest![7..].ToLowerInvariant());
    }

    private static bool IsTransient(Exception error, CancellationToken token) => !token.IsCancellationRequested && error switch
    {
        HttpRequestException http => http.StatusCode is null or HttpStatusCode.RequestTimeout || (int)http.StatusCode >= 500,
        OperationCanceledException => true, // Per-request timeout, rather than caller cancellation.
        IOException => true,
        _ => false
    };

    private static async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken token, int retryDelayMs)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await operation(); }
            catch (Exception ex) when (attempt < 2 && IsTransient(ex, token))
            {
                App.Logger.WriteLine("AppUpdater", $"Transient update failure; retry {attempt + 1}/2.");
                await Task.Delay(retryDelayMs * (attempt + 1), token);
            }
        }
    }

    internal static Task<GithubRelease?> FetchReleaseAsync(HttpClient client, CancellationToken token, int retryDelayMs = 500)
        => RetryAsync(async () =>
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"DepthStrap/{App.Version}");
            using var response = await client.SendAsync(request, attempt.Token);
            response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<GithubRelease>(await response.Content.ReadAsStringAsync(attempt.Token));
        }, token, retryDelayMs);

    public static Task<string> DownloadAsync(HttpClient client, AppUpdatePlan plan, string directory, CancellationToken token)
        => DownloadWithRetriesAsync(client, plan, directory, token);

    internal static Task<string> DownloadWithRetriesAsync(HttpClient client, AppUpdatePlan plan, string directory, CancellationToken token, int retryDelayMs = 500)
        => RetryAsync(() => DownloadAttemptAsync(client, plan, directory, token), token, retryDelayMs);

    private static async Task<string> DownloadAttemptAsync(HttpClient client, AppUpdatePlan plan, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "DepthStrap.exe");
        if (await VerifyAsync(destination, plan, token)) return destination;
        string staged = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            using var response = await client.GetAsync(plan.Download, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != plan.Size)
                throw new InvalidDataException("Update download size does not match the release.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > plan.Size) throw new InvalidDataException("Update download exceeds the release size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                await output.FlushAsync(token);
            }
            if (!await VerifyAsync(staged, plan, token)) throw new InvalidDataException("Update checksum verification failed.");
            token.ThrowIfCancellationRequested();
            File.Move(staged, destination, overwrite: true);
            return destination;
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static async Task<bool> VerifyAsync(string path, AppUpdatePlan plan, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != plan.Size) return false;
        await using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        return hash.Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        // Raw Roblox URIs and version IDs are recognized only in the first slot.
        info.ArgumentList.Add("-upgrade");
        return info;
    }

    internal static ProcessStartInfo ResumeStartInfo(string executable, string[] args)
    {
        var normalized = new LaunchSettings(args).Args;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        for (int i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] is "-upgrade" or "-skipappupdate") continue;
            if (normalized[i] == "-updatehandoff")
            {
                if (i + 1 < normalized.Length && !normalized[i + 1].StartsWith('-')) i++;
                continue;
            }
            info.ArgumentList.Add(normalized[i]);
        }
        // Older installed builds do not know -skipappupdate. Their existing
        // -upgrade flag also bypasses the app check; HandleUpgrade is a no-op
        // when the running file is already the installed executable.
        info.ArgumentList.Add("-upgrade");
        info.ArgumentList.Add("-skipappupdate");
        return info;
    }

    internal static bool CanResumeFailedUpdate(LaunchSettings? launch) =>
        launch?.UpgradeFlag.Active == true && !launch.SkipAppUpdateFlag.Active;

    internal static bool TryResumeInstalled(string executable, string[] args, Func<ProcessStartInfo, bool> start)
    {
        try
        {
            if (!File.Exists(executable) || FileVersionInfo.GetVersionInfo(executable).ProductName != "DepthStrap") return false;
            return start(ResumeStartInfo(executable, args));
        }
        catch (Exception ex) { App.Logger.WriteException("AppUpdater::ResumeInstalled", ex); return false; }
    }

    internal static bool TryResumeInstalled() => TryResumeInstalled(Paths.Application, App.LaunchSettings.Args,
        info => { using var process = Process.Start(info); return process is not null; });

    internal static bool ShouldDeferAutomaticReplacement(string? product, string? installed, string? incoming) =>
        !ReleaseMigration.IsPrototypeToFirstRelease(product, installed, incoming) &&
        Version.TryParse(installed, out var existing) && Version.TryParse(incoming, out var next) &&
        Normalize(next) < Normalize(existing);

    internal static string DescribeReplacementFailure(string destination, Exception error)
    {
        if ((error.HResult & 0xffff) is 32 or 33)
            return "The installed executable is busy (Windows sharing or lock violation). Close other DepthStrap windows and retry.";
        try
        {
            if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                return "The installed executable is read-only. Review its file properties before retrying.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (error is UnauthorizedAccessException || (error.HResult & 0xffff) == 5)
            return "Windows denied access to the installed executable. Check file permissions and security-software history; the cause is not confirmed.";
        return $"The executable could not be replaced (Windows error {error.HResult & 0xffff}). Check the app log and retry.";
    }

    internal static void RecordDeferredUpdate(string? installed, string? incoming, string reason)
    {
        App.State.Prop.DeferredAppUpdate = new(installed ?? "unknown", incoming, reason);
        App.Logger.WriteLine("AppUpdater", $"Update deferred. Installed: {installed ?? "unknown"}; incoming: {incoming ?? "unknown"}. {reason}");
        if (App.State.Loaded) App.State.TrySave();
    }

    internal static void ClearDeferredUpdate()
    {
        if (App.State.Prop.DeferredAppUpdate is null) return;
        App.State.Prop.DeferredAppUpdate = null;
        if (App.State.Loaded) App.State.TrySave();
    }

    internal static string UpdateStatus(string current, DeferredAppUpdate? deferred)
    {
        if (deferred is null || (Version.TryParse(current, out var running) &&
            Version.TryParse(deferred.IncomingVersion?.TrimStart('v'), out var incoming) && Normalize(running) >= Normalize(incoming)))
            return $"Installed DepthStrap version: {current}.";
        return $"Update deferred. Installed: {current}; incoming: {deferred.IncomingVersion ?? "not determined"}. {deferred.Reason} Your installed app was kept. DepthStrap retries on a later eligible startup.";
    }

    public static void ReplaceExecutable(string source, string destination)
    {
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("The installed executable is read-only.");
        string staged = destination + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.Copy(source, staged);
            File.Move(staged, destination, overwrite: true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    internal static void ReplaceExecutableWithRetries(string source, string destination, int attempts = 30, int retryDelayMs = 1000)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { ReplaceExecutable(source, destination); return; }
            catch (Exception ex) when (attempt + 1 < attempts && ex is IOException or UnauthorizedAccessException)
            { Thread.Sleep(retryDelayMs); }
        }
    }

    private static IDisposable? AcquireHandoff()
    {
        var handoff = new InterProcessLock("AutoUpdater");
        if (handoff.IsAcquired) return handoff;
        handoff.Dispose();
        return null;
    }

    public static Task<bool> TryUpdateAsync()
    {
        if (!App.SupportsAppUpdates) return Task.FromResult(false);
        return TryUpdateAsync(new AppUpdateRuntime(App.HttpClient, App.Settings.Prop, App.LaunchSettings,
            App.Version, Paths.TempUpdates,
            App.Settings.Loaded && !App.Settings.LastLoadFailed && App.State.Loaded && !App.State.LastLoadFailed,
            ClientsRunning, () => App.Settings.TrySave() && App.State.TrySave(), AcquireHandoff,
            info => AppUpdateHandoff.Start(info), RecordDeferredUpdate, ClearDeferredUpdate));
    }

    internal static async Task<bool> TryUpdateAsync(AppUpdateRuntime runtime, CancellationToken token = default)
    {
        const string log = "AppUpdater";
        AppUpdatePlan? plan = null;
        try
        {
            if (!runtime.StorageAvailable || !ShouldCheck(runtime.Settings, runtime.Launch, runtime.ClientsRunning())) return false;
            Directory.CreateDirectory(runtime.UpdateDirectory);
            // A file lock can safely span awaits; a Windows mutex is tied to its owning thread.
            using var updateCheckLock = new FileStream(Path.Combine(runtime.UpdateDirectory, "update-check.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            queryTimeout.CancelAfter(TimeSpan.FromSeconds(25));
            var release = await FetchReleaseAsync(runtime.Client, queryTimeout.Token);
            plan = SelectRelease(release, runtime.CurrentVersion);
            if (plan is null)
            {
                // Only a valid stable release can clear a previous failed-check notice.
                var available = SelectRelease(release, "0.0.0");
                if (available is not null && Version.TryParse(runtime.CurrentVersion, out var current) && available.Version <= Normalize(current))
                    runtime.ClearDeferred();
                return false;
            }
            App.Logger.WriteLine(log, $"Downloading verified stable release {plan.Tag}.");
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            downloadTimeout.CancelAfter(TimeSpan.FromMinutes(10));
            string path = await DownloadAsync(runtime.Client, plan, Path.Combine(runtime.UpdateDirectory, plan.Tag), downloadTimeout.Token);
            if (!IsInstalledVersion(path, plan.Version.ToString()))
                throw new InvalidDataException("Update executable does not match the advertised DepthStrap version.");
            token.ThrowIfCancellationRequested();
            if (!ShouldCheck(runtime.Settings, runtime.Launch, runtime.ClientsRunning())) return false;
            if (!runtime.SaveState())
            {
                runtime.RecordDeferred(runtime.CurrentVersion, plan.Version.ToString(), "Settings could not be saved safely before the update.");
                return false;
            }
            using var handoff = runtime.AcquireHandoff();
            if (handoff is null)
            {
                runtime.RecordDeferred(runtime.CurrentVersion, plan.Version.ToString(), "Another app update holds the update lock.");
                return false;
            }
            if (!ShouldCheck(runtime.Settings, runtime.Launch, runtime.ClientsRunning())) return false;
            bool started = runtime.StartUpdater(StartInfo(path, runtime.Launch.Args));
            if (!started) runtime.RecordDeferred(runtime.CurrentVersion, plan.Version.ToString(), "The downloaded updater did not acknowledge startup.");
            return started;
        }
        catch (Exception ex)
        {
            runtime.RecordDeferred(runtime.CurrentVersion, plan?.Version.ToString(),
                ex is OperationCanceledException ? "The update check or download was cancelled or timed out." :
                "The update check, download or handoff failed. Check the app log for details.");
            App.Logger.WriteException(log, ex);
            return false;
        }
    }

    private static bool ClientsRunning()
    {
        foreach (string name in new[] { "RobloxPlayerBeta", "RobloxStudioBeta", App.ProjectName })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Any(p => p.Id != Environment.ProcessId)) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }
}
