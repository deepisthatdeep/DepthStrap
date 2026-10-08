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

    public static async Task<string> DownloadAsync(HttpClient client, AppUpdatePlan plan, string directory, CancellationToken token)
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
        info.ArgumentList.Add("-upgrade");
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public static void ReplaceExecutable(string source, string destination)
    {
        string staged = destination + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            File.Copy(source, staged);
            File.Move(staged, destination, overwrite: true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    public static async Task<bool> TryUpdateAsync()
    {
        const string log = "AppUpdater";
        try
        {
            if (!App.SupportsAppUpdates || !ShouldCheck(App.Settings.Prop, App.LaunchSettings, ClientsRunning()) ||
                !App.Settings.Loaded || App.Settings.LastLoadFailed || !App.State.Loaded || App.State.LastLoadFailed) return false;
            Directory.CreateDirectory(Paths.TempUpdates);
            // A file lock can safely span awaits; a Windows mutex is tied to its owning thread.
            using var updateCheckLock = new FileStream(Path.Combine(Paths.TempUpdates, "update-check.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var queryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await App.HttpClient.GetAsync(LatestReleaseUrl, queryTimeout.Token);
            response.EnsureSuccessStatusCode();
            var release = JsonSerializer.Deserialize<GithubRelease>(await response.Content.ReadAsStringAsync(queryTimeout.Token));
            var plan = SelectRelease(release, App.Version);
            if (plan is null) return false;
            App.Logger.WriteLine(log, $"Downloading verified stable release {plan.Tag}.");
            using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            string path = await DownloadAsync(App.HttpClient, plan, Path.Combine(Paths.TempUpdates, plan.Tag), downloadTimeout.Token);
            if (!IsInstalledVersion(path, plan.Version.ToString()))
                throw new InvalidDataException("Update executable does not match the advertised DepthStrap version.");
            if (ClientsRunning() || !App.Settings.TrySave() || !App.State.TrySave()) return false;
            using var handoff = new InterProcessLock("AutoUpdater");
            if (!handoff.IsAcquired) return false;
            using var process = Process.Start(StartInfo(path, App.LaunchSettings.Args));
            return process is not null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(log, "App update deferred; continuing with the installed version.");
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
