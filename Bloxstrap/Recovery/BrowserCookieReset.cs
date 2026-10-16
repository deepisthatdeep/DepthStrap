using System.Security.Cryptography;

namespace DepthStrap.Recovery;

internal sealed record BrowserCookieTarget(string Browser, string Path, bool Firefox, bool Session = false);
internal sealed record BrowserCookieEntry(BrowserCookieTarget Target, long Cookies, string Revision);
internal sealed record BrowserCookiePreview(Guid Scope, BrowserCookieEntry[] Databases);

internal sealed class BrowserCookieReset(Func<BrowserCookieTarget[]> targets, Func<bool> browsersRunning)
{
    private readonly Guid _scope = Guid.NewGuid();
    internal static readonly string[] BrowserProcesses = ["chrome", "msedge", "firefox", "brave", "vivaldi", "opera", "waterfox", "librewolf", "chromium"];
    internal static bool BrowsersRunning() => BrowserProcesses.Any(name =>
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Any(process => !process.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    });
    internal static BrowserCookieReset ForProfiles(RobloxWipeProfile[] profiles) => new(() => Discover(profiles), BrowsersRunning);
    internal static BrowserCookieTarget[] Discover(IEnumerable<RobloxWipeProfile> profiles)
    {
        var result = new List<BrowserCookieTarget>();
        foreach (var profile in profiles)
        {
            string local = Path.Combine(profile.Directory, "AppData", "Local"), roaming = Path.Combine(profile.Directory, "AppData", "Roaming");
            foreach (var (name, relative) in new[] { ("Chrome", @"Google\Chrome\User Data"), ("Chrome Beta", @"Google\Chrome Beta\User Data"),
                ("Chrome Dev", @"Google\Chrome Dev\User Data"), ("Chrome Canary", @"Google\Chrome SxS\User Data"),
                ("Edge", @"Microsoft\Edge\User Data"), ("Edge Beta", @"Microsoft\Edge Beta\User Data"), ("Edge Dev", @"Microsoft\Edge Dev\User Data"), ("Edge Canary", @"Microsoft\Edge SxS\User Data"),
                ("Brave", @"BraveSoftware\Brave-Browser\User Data"), ("Vivaldi", @"Vivaldi\User Data"), ("Chromium", @"Chromium\User Data") })
            {
                string root = Path.Combine(local, relative);
                if (!ExistsDirectory(root)) continue;
                RejectRedirects(root);
                foreach (string directory in Directory.EnumerateDirectories(root))
                    if (Path.GetFileName(directory) is "Default" or "Guest Profile" || Path.GetFileName(directory).StartsWith("Profile ", StringComparison.Ordinal))
                    { Add(name, Path.Combine(directory, "Network", "Cookies"), false); Add(name, Path.Combine(directory, "Cookies"), false); }
            }
            foreach (string name in new[] { "Opera Stable", "Opera GX Stable" })
            {
                string root = Path.Combine(roaming, "Opera Software", name);
                if (!ExistsDirectory(root)) continue;
                RejectRedirects(root); Add(name, Path.Combine(root, "Network", "Cookies"), false); Add(name, Path.Combine(root, "Cookies"), false);
                foreach (string directory in Directory.EnumerateDirectories(root).Where(path => Path.GetFileName(path) is "Default" or "Guest Profile" || Path.GetFileName(path).StartsWith("Profile ", StringComparison.Ordinal)))
                { Add(name, Path.Combine(directory, "Network", "Cookies"), false); Add(name, Path.Combine(directory, "Cookies"), false); }
            }
            foreach (var (name, relative) in new[] { ("Firefox", @"Mozilla\Firefox\Profiles"), ("Waterfox", @"Waterfox\Profiles"), ("LibreWolf", @"librewolf\Profiles") })
            {
                string root = Path.Combine(roaming, relative);
                if (!ExistsDirectory(root)) continue;
                RejectRedirects(root);
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    RejectRedirects(directory); Add(name, Path.Combine(directory, "cookies.sqlite"), true);
                    foreach (string backup in Directory.EnumerateFiles(directory, "cookies.sqlite.bak*")) Add(name + " cookie backup", backup, true);
                    foreach (string file in new[] { "sessionstore.jsonlz4", "sessionstore.js", "sessionstore.bak" }) Add(name + " recovery", Path.Combine(directory, file), true, true);
                    string recovery = Path.Combine(directory, "sessionstore-backups");
                    if (ExistsDirectory(recovery))
                    {
                        RejectRedirects(recovery);
                        foreach (string file in Directory.EnumerateFiles(recovery).Where(path => Path.GetFileName(path) is "recovery.jsonlz4" or "recovery.baklz4" or "previous.jsonlz4"
                            || Path.GetFileName(path).StartsWith("upgrade.jsonlz4-", StringComparison.Ordinal))) Add(name + " recovery", file, true, true);
                    }
                }
            }
        }
        return result.DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        void Add(string name, string path, bool firefox, bool session = false)
        {
            try { File.GetAttributes(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
            RejectRedirects(path);
            if (result.Count >= 2048) throw new IOException("Browser profile discovery exceeded its supported limit. No reset was started.");
            result.Add(new(name, Path.GetFullPath(path), firefox, session));
        }
    }
    private static bool ExistsDirectory(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
    internal static void RejectRedirects(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("A browser profile is redirected: " + path + ". Close the browser and review its location; no browser database was modified.");
    }
    private static string Stamp(string path)
    {
        var result = new StringBuilder();
        foreach (string file in new[] { path, path + "-wal" })
        {
            var info = new FileInfo(file);
            result.Append(file).Append('|');
            if (info.Exists) result.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(info.CreationTimeUtc.Ticks);
            result.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.ToString())));
    }
    internal BrowserCookiePreview Preview()
    {
        if (browsersRunning()) throw new InvalidOperationException("Close all supported browsers, including background browser processes, before previewing a complete reset. Their Roblox cookies must not be recreated during deletion.");
        var entries = new List<BrowserCookieEntry>();
        foreach (var target in targets())
        {
            if (target.Session)
            { entries.Add(new(target, FirefoxSessionCookies.Read(target.Path).Cookies, Stamp(target.Path))); continue; }
            using var database = new BrowserCookieDatabase(target.Path, false);
            database.ValidateSchema(target.Firefox);
            entries.Add(new(target, database.Count(target.Firefox), Stamp(target.Path)));
        }
        return new(_scope, entries.ToArray());
    }
    internal Prepared Prepare(BrowserCookiePreview preview, bool confirmed)
    {
        if (!confirmed || preview.Scope != _scope) throw new InvalidOperationException("Confirm the current browser-cookie reset preview first.");
        if (browsersRunning()) throw new InvalidOperationException("A browser is still running. Close it completely before resetting Roblox cookies.");
        if (!targets().SequenceEqual(preview.Databases.Select(entry => entry.Target))) throw new IOException("The browser profile inventory changed. Review a new reset preview.");
        var prepared = new Prepared(browsersRunning);
        try
        {
            foreach (var entry in preview.Databases)
            {
                if (Stamp(entry.Target.Path) != entry.Revision) throw new IOException("Browser cookies changed after the preview. Review a new preview before deleting.");
                if (entry.Target.Session)
                {
                    var edit = FirefoxSessionCookies.Read(entry.Target.Path);
                    if (edit.Cookies != entry.Cookies) throw new IOException("Firefox recovery cookies changed. Review a new preview.");
                    FirefoxSessionCookies.ValidateWritable(entry.Target.Path, edit);
                    prepared.AddSession(entry.Target.Path, edit); continue;
                }
                var database = new BrowserCookieDatabase(entry.Target.Path, true);
                prepared.Add(database, entry.Target.Firefox);
                database.ValidateSchema(entry.Target.Firefox); database.Begin();
                if (database.Count(entry.Target.Firefox) != entry.Cookies || Stamp(entry.Target.Path) != entry.Revision)
                    throw new IOException("Browser cookies changed during preparation. Nothing was deleted; review a new preview.");
            }
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }
    internal void VerifyClean(BrowserCookiePreview approved)
    {
        var remaining = Preview();
        if (!remaining.Databases.Select(entry => entry.Target).SequenceEqual(approved.Databases.Select(entry => entry.Target))
            || remaining.Databases.Any(entry => entry.Cookies != 0)) throw new IOException("Browser profiles changed or Roblox browser cookies remain. Reset is incomplete.");
    }
    internal sealed class Prepared(Func<bool> running) : IDisposable
    {
        private readonly List<(BrowserCookieDatabase Database, bool Firefox)> _databases = new();
        private readonly List<(string Path, FirefoxSessionCookies.Edited Edit)> _sessions = new();
        internal void Add(BrowserCookieDatabase database, bool firefox) => _databases.Add((database, firefox));
        internal void AddSession(string path, FirefoxSessionCookies.Edited edit) => _sessions.Add((path, edit));
        internal long Delete()
        {
            if (running()) throw new IOException("A browser opened during reset. Browser cookie deletion was cancelled.");
            long deleted = 0;
            foreach (var item in _databases) deleted += item.Database.DeleteRoblox(item.Firefox);
            foreach (var item in _databases)
            {
                if (running()) throw new IOException("A browser opened during reset. Browser cookie deletion is incomplete.");
                item.Database.Commit(); item.Database.Checkpoint();
                if (item.Database.Count(item.Firefox) != 0) throw new IOException("Roblox browser cookies remain. Reset is incomplete.");
            }
            foreach (var item in _sessions)
            {
                if (running()) throw new IOException("A browser opened during reset. Firefox recovery-cookie deletion is incomplete.");
                FirefoxSessionCookies.Write(item.Path, item.Edit); deleted += item.Edit.Cookies;
            }
            return deleted;
        }
        public void Dispose() { foreach (var item in _databases) { item.Database.Rollback(); item.Database.Dispose(); } _databases.Clear(); _sessions.Clear(); }
    }
}
