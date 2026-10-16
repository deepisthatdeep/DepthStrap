using System.Text;

namespace DepthStrap.Recovery;

internal static class BrowserCookieResetTests
{
    internal static string CreateChromium(string profile)
    {
        string path = Path.Combine(profile, "AppData", "Local", "Google", "Chrome", "User Data", "Default", "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var database = new BrowserCookieDatabase(path, true, createFixture: true);
        database.Execute("CREATE TABLE cookies(host_key TEXT,name TEXT,encrypted_value BLOB);"
            + "INSERT INTO cookies VALUES('.roblox.com','.ROBLOSECURITY',X'53594E544845544943524F424C4F58434F4F4B4945');"
            + "INSERT INTO cookies VALUES('www.roblox.com','session','SYNTHETICROBLOXCOOKIE');"
            + "INSERT INTO cookies VALUES('AUTH.ROBLOX.COM','session','SYNTHETICROBLOXCOOKIE');"
            + "INSERT INTO cookies VALUES('roblox.com','session','SYNTHETICROBLOXCOOKIE');"
            + "INSERT INTO cookies VALUES('roblox.com.evil.test','preserve','OTHER-SITE');"
            + "INSERT INTO cookies VALUES('notroblox.com','preserve','OTHER-SITE');"
            + "INSERT INTO cookies VALUES('.google.com','preserve','OTHER-SITE');");
        return path;
    }
    internal static void Run(string fixture, Action<bool, string> check)
    {
        void Reject(Action action, string message)
        { bool failed = false; try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { failed = true; } check(failed, message); }
        string basis = Path.Combine(fixture, "browser-cookies");
        var profiles = new[] { new RobloxWipeProfile("S-1-5-21-1-2-3-1001", Path.Combine(basis, "One")), new RobloxWipeProfile("S-1-5-21-1-2-3-1002", Path.Combine(basis, "Two")) };
        string chrome = CreateChromium(profiles[0].Directory);
        string firefox = Path.Combine(profiles[1].Directory, "AppData", "Roaming", "Mozilla", "Firefox", "Profiles", "fixture.default", "cookies.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(firefox)!);
        using (var database = new BrowserCookieDatabase(firefox, true, createFixture: true))
        { database.Execute("CREATE TABLE moz_cookies(host TEXT,name TEXT,value TEXT,originAttributes TEXT); INSERT INTO moz_cookies VALUES('.roblox.com','.ROBLOSECURITY','SYNTHETICROBLOXCOOKIE','^userContextId=3'); INSERT INTO moz_cookies VALUES('auth.roblox.com','login','SYNTHETICROBLOXCOOKIE',''); INSERT INTO moz_cookies VALUES('unrelated.test','keep','KEEP','');"); }
        var targets = BrowserCookieReset.Discover(profiles);
        check(targets.Length == 2 && targets.Any(target => target.Firefox), "Browser discovery covers Chromium and Firefox across multiple synthetic Windows users");
        var service = new BrowserCookieReset(() => BrowserCookieReset.Discover(profiles), () => false);
        var preview = service.Preview();
        check(preview.Databases.Sum(entry => entry.Cookies) == 6, "Preview counts only Roblox and its subdomain cookies, including partitioned Firefox containers");
        Reject(() => service.Prepare(preview, false), "Browser cookie deletion requires explicit confirmation");
        Reject(() => new BrowserCookieReset(() => targets, () => false).Prepare(preview, true), "Browser cookie approvals cannot cross service scopes");
        Reject(() => new BrowserCookieReset(() => targets, () => true).Preview(), "Running browsers stop the cookie reset before preview or mutation");
        using (var held = new FileStream(chrome, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject(() => service.Prepare(preview, true), "A locked browser cookie file aborts preparation before any cookie deletion");
        using (var database = new BrowserCookieDatabase(chrome, true)) database.Execute("INSERT INTO cookies VALUES('.roblox.com','new','SYNTHETICROBLOXCOOKIE');");
        File.SetLastWriteTimeUtc(chrome, DateTime.UtcNow.AddMinutes(1));
        Reject(() => service.Prepare(preview, true), "Browser database changes invalidate the old preview");
        preview = service.Preview();
        using (var prepared = service.Prepare(preview, true)) check(prepared.Delete() == 7, "Confirmed native SQLite maintenance removes every matching Roblox cookie in both engines");
        using (var database = new BrowserCookieDatabase(chrome, false))
        {
            check(database.Count(false) == 0 && database.Scalar("SELECT count(*) FROM cookies;") == 3, "Chromium cleanup preserves unrelated and lookalike-domain cookies");
        }
        using (var database = new BrowserCookieDatabase(firefox, false))
            check(database.Count(true) == 0 && database.Scalar("SELECT count(*) FROM moz_cookies;") == 1, "Firefox cleanup preserves another site's cookie and removes Roblox cookies in all containers");
        check(!Encoding.Latin1.GetString(File.ReadAllBytes(chrome)).Contains("SYNTHETICROBLOXCOOKIE") && !Encoding.Latin1.GetString(File.ReadAllBytes(firefox)).Contains("SYNTHETICROBLOXCOOKIE"),
            "Secure-delete removes the synthetic Roblox cookie marker from database pages without exporting credentials");
        using (var prepared = service.Prepare(service.Preview(), true)) check(prepared.Delete() == 0, "An already-clean browser-cookie reset is idempotent");
        using (var database = new BrowserCookieDatabase(chrome, true)) database.Execute("CREATE TRIGGER unexpected AFTER DELETE ON cookies BEGIN DELETE FROM cookies; END;");
        Reject(() => service.Preview(), "Unexpected database triggers are rejected before a scoped delete could affect other sites");
        using (var database = new BrowserCookieDatabase(chrome, true)) database.Execute("DROP TRIGGER unexpected;");
        string redirected = Path.Combine(basis, "Redirected"); Directory.CreateDirectory(redirected);
        string targetDirectory = Path.GetDirectoryName(chrome)!;
        string link = Path.Combine(redirected, "Profile"); JunctionFixture.Create(fixture, link, targetDirectory);
        Reject(() => new BrowserCookieReset(() => [new("Fixture", Path.Combine(link, "Cookies"), false)], () => false).Preview(), "Browser maintenance rejects redirected cookie paths");
        string walPath = Path.Combine(basis, "wal", "Cookies"); Directory.CreateDirectory(Path.GetDirectoryName(walPath)!);
        using (var writer = new BrowserCookieDatabase(walPath, true, createFixture: true))
        {
            writer.Execute("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE cookies(host_key TEXT,name TEXT,encrypted_value BLOB); INSERT INTO cookies VALUES('.roblox.com','login','SYNTHETICROBLOXCOOKIE'); INSERT INTO cookies VALUES('other.test','keep','KEEP');");
            var wal = new BrowserCookieReset(() => [new("WAL fixture", walPath, false)], () => false);
            using (var prepared = wal.Prepare(wal.Preview(), true)) check(prepared.Delete() == 1, "WAL-mode cookie deletion commits and checkpoints through SQLite");
            check(writer.Count(false) == 0 && writer.Scalar("SELECT count(*) FROM cookies;") == 1, "An existing connection sees committed cookie deletion without losing another site's data");
            check(!File.Exists(walPath + "-wal") || new FileInfo(walPath + "-wal").Length == 0, "The WAL is truncated rather than left with a prior Roblox login record");
            writer.Begin();
            Reject(() => wal.Prepare(wal.Preview(), true), "An existing browser write transaction prevents the reset before any deletion");
            writer.Rollback();
        }
        string app = Path.Combine(basis, "app"); Directory.CreateDirectory(app); File.WriteAllText(Path.Combine(app, "RobloxCookies.dat"), "fixture");
        var browser = new BrowserCookieReset(() => targets, () => false);
        using var combined = new RobloxCompleteUninstall(() => profiles, () => [], () => new([app], [], []), new CompleteUninstallTests.Store(), () => false, null, browserCookies: browser);
        var combinedPreview = combined.PreviewAsync().GetAwaiter().GetResult();
        using (var held = new FileStream(chrome, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject(() => combined.ExecuteAsync(combinedPreview, true).GetAwaiter().GetResult(), "Combined reset preflights all browser databases before removing any Roblox app files");
        check(File.Exists(Path.Combine(app, "RobloxCookies.dat")), "Failed browser preparation leaves the app's cookie fixture untouched");
        string recovery = Path.Combine(Path.GetDirectoryName(firefox)!, "sessionstore-backups", "recovery.jsonlz4"); Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
        string sessionJson = "{\"windows\":[{\"tabs\":[{\"entries\":[{\"url\":\"https://example.test/keep-tab\"}]}]}],\"cookies\":[{\"host\":\".roblox.com\",\"name\":\".ROBLOSECURITY\",\"value\":\"SESSIONROBLOXMARKER\"},{\"host\":\"other.test\",\"value\":\"KEEP\"}]}";
        byte[] json = Encoding.UTF8.GetBytes(sessionJson);
        File.WriteAllBytes(recovery, FirefoxSessionCookies.Encode(json));
        check(FirefoxSessionCookies.Decode(File.ReadAllBytes(recovery)).SequenceEqual(json), "Mozilla LZ4 encoding preserves the exact JSON content before editing");
        var sessionService = new BrowserCookieReset(() => BrowserCookieReset.Discover(profiles), () => false);
        var sessionPreview = sessionService.Preview();
        check(sessionPreview.Databases.Any(entry => entry.Target.Session && entry.Cookies == 1), "Firefox's session-recovery Roblox cookies appear in the same deletion preview");
        using (var preflight = new RobloxCompleteUninstall(() => profiles, () => [], () => new([app], [], []), new CompleteUninstallTests.Store(), () => false, null, browserCookies: sessionService))
        {
            var approved = preflight.PreviewAsync().GetAwaiter().GetResult();
            File.SetAttributes(recovery, File.GetAttributes(recovery) | FileAttributes.ReadOnly);
            try
            {
                Reject(() => preflight.ExecuteAsync(approved, true).GetAwaiter().GetResult(), "Read-only Firefox recovery files block the combined reset before destructive work");
                check(File.Exists(Path.Combine(app, "RobloxCookies.dat")), "A Firefox recovery-file permission failure preserves the application-data fixture");
            }
            finally { File.SetAttributes(recovery, File.GetAttributes(recovery) & ~FileAttributes.ReadOnly); }
        }
        using (var prepared = sessionService.Prepare(sessionPreview, true)) check(prepared.Delete() == 1, "Combined browser cleanup removes Firefox session cookies as well as its cookie database records");
        string cleanJson = Encoding.UTF8.GetString(FirefoxSessionCookies.Decode(File.ReadAllBytes(recovery)));
        check(!cleanJson.Contains("SESSIONROBLOXMARKER") && cleanJson.Contains("https://example.test/keep-tab") && cleanJson.Contains("other.test"), "Firefox recovery cleanup preserves saved tabs and another site's session cookie");
        check(!Directory.EnumerateFiles(Path.GetDirectoryName(recovery)!, ".depthstrap-cookie-reset-*").Any(), "No session-cookie backup or temporary export remains after cleanup");
        foreach (byte[] invalid in new[] { Array.Empty<byte>(), "mozLz40\0"u8.ToArray(), new byte[] { 109,111,122,76,122,52,48,0,255,255,255,127,0 } })
            Reject(() => FirefoxSessionCookies.Decode(invalid), "Malformed or oversized Mozilla LZ4 data is rejected before writing");
        // Independent raw LZ4 vector: 1 literal, overlapping 16-byte match, 5 final literals.
        byte[] vector = new byte[] { 109,111,122,76,122,52,48,0,22,0,0,0,0x1c,65,1,0,0x50,66,67,68,69,70 };
        check(Encoding.ASCII.GetString(FirefoxSessionCookies.Decode(vector)) == new string('A', 17) + "BCDEF", "Mozilla LZ4 decoding handles an independently constructed overlapping-match block");
        File.WriteAllText(recovery, "invalid recovery fixture");
        Reject(() => sessionService.Preview(), "Unparseable Firefox session data blocks a complete-clean claim without deleting tabs");
    }
}
