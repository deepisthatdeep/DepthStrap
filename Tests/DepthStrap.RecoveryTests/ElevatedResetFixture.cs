using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DepthStrap.Recovery;

internal static class ElevatedResetFixture
{
    internal static void Run(string reportDirectory)
    {
        string report = Path.GetFullPath(reportDirectory);
        if (!Path.GetFileName(report).StartsWith("admin-reset-fixture-", StringComparison.Ordinal) || Directory.Exists(report))
            throw new IOException("The elevated fixture requires a fresh, uniquely named artifact directory.");
        Directory.CreateDirectory(report);
        var checks = new List<string>(); string? fixtureKey = null; string? machineKey = null;
        try
        {
            void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
            Check(WindowsMacBackend.IsAdministrator(), "Windows granted administrator execution through the requested UAC handoff");
            Guid id = Guid.NewGuid(); fixtureKey = @"Software\DepthStrapRecoveryLabFixtures\" + id; machineKey = @"SOFTWARE\DepthStrapRecoveryLabFixtures\" + id;
            string sid = "S-1-5-21-" + Random.Shared.Next(100000, int.MaxValue) + "-" + Random.Shared.Next(100000, int.MaxValue) + "-" + Random.Shared.Next(100000, int.MaxValue) + "-1001";
            using (var existing = Registry.Users.OpenSubKey(sid)) Check(existing is null, "The generated offline SID has no real loaded user hive");
            using (var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid)) Check(list is null, "The generated offline SID is not a real Windows profile");
            // The native profile loader must be tested on a local Windows temp
            // filesystem, independent of the source checkout's cloud/sync provider.
            string working = Path.Combine(Path.GetTempPath(), "DepthStrapHive-" + id.ToString("N"));
            if (Directory.Exists(working)) throw new IOException("The synthetic hive fixture directory already exists.");
            string profile = Path.Combine(working, "SyntheticProfile"); Directory.CreateDirectory(profile);
            string classes = Path.Combine(profile, "AppData", "Local", "Microsoft", "Windows"); Directory.CreateDirectory(classes);
            using var fixture = Registry.CurrentUser.CreateSubKey(fixtureKey);
            using (var key = fixture.CreateSubKey(@"Software\ROBLOX Corporation\RobloxStudio")) key.SetValue("Synthetic", 1);
            string unrelatedPermissions;
            using (var other = fixture.CreateSubKey(@"Software\UnrelatedSyntheticApplication"))
            {
                other.SetValue("Keep", "synthetic unrelated registry data");
                var acl = other.GetAccessControl();
                acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), RegistryRights.ReadKey, AccessControlType.Allow));
                other.SetAccessControl(acl);
                unrelatedPermissions = other.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                Check(other.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) != fixture.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                    "The disposable hive contains genuinely different child permissions, matching ordinary Windows profiles");
            }
            using (var privilege = new RobloxWipeRegistryScope.Privilege("SeBackupPrivilege"))
            {
                Check(RegSaveKeyEx(fixture.Handle.DangerousGetHandle(), Path.Combine(profile, "NTUSER.DAT"), IntPtr.Zero, 2) == 0, "Saved a disposable registry hive created exclusively from synthetic keys");
                Check(RegSaveKeyEx(fixture.Handle.DangerousGetHandle(), Path.Combine(classes, "UsrClass.dat"), IntPtr.Zero, 2) == 0, "Saved a separate disposable classes hive");
            }
            string saved = Path.Combine(report, "SyntheticProfile"), savedClasses = Path.Combine(saved, "AppData", "Local", "Microsoft", "Windows");
            Directory.CreateDirectory(savedClasses);
            File.Copy(Path.Combine(profile, "NTUSER.DAT"), Path.Combine(saved, "NTUSER.DAT"));
            File.Copy(Path.Combine(classes, "UsrClass.dat"), Path.Combine(savedClasses, "UsrClass.dat"));
            File.WriteAllText(Path.Combine(report, "synthetic-working-directory.txt"), working);
            using var scope = RobloxWipeRegistryScope.Open([new(sid, profile)]);
            var mounted = scope.Roots.Where(root => root.Label.Contains(sid, StringComparison.Ordinal)).ToArray();
            Check(mounted.Length == 2, "Production offline-profile loading opens both synthetic hive files");
            bool globalLoader = mounted.All(root => root.Label.StartsWith("HKU\\", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(report, "selected-loader.json"), System.Text.Json.JsonSerializer.Serialize(new { GlobalProfileLoader = globalLoader,
                ApplicationFallback = !globalLoader, RootsOpened = mounted.Length }));
            foreach (var root in mounted)
            {
                // Use the exact fixture-relative path, never a live Windows profile or global machine inventory.
                var service = RobloxRegistryReset.ForOpenRoot(root.Key, @"Software\ROBLOX Corporation", "SYNTHETIC OFFLINE", () => false);
                service.Execute(service.Preview(), true);
                using var remaining = root.Key.OpenSubKey(@"Software\ROBLOX Corporation");
                Check(remaining is null, "Native registry deletion removes the synthetic offline vendor tree");
                using var unrelated = root.Key.OpenSubKey(@"Software\UnrelatedSyntheticApplication");
                Check(unrelated?.GetValue("Keep") as string == "synthetic unrelated registry data", "Mixed-permission cleanup preserves unrelated synthetic registry data");
                Check(unrelated!.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access) == unrelatedPermissions,
                    "Offline loading and deletion preserve unrelated key permissions");
            }
            using (var machine = Registry.LocalMachine.CreateSubKey(machineKey))
            {
                using (var child = machine.CreateSubKey(@"Software\ROBLOX Corporation\RobloxStudio")) child.SetValue("Synthetic", 1);
                var service = RobloxRegistryReset.ForOpenRoot(machine, @"Software\ROBLOX Corporation", "SYNTHETIC MACHINE", () => false);
                service.Execute(service.Preview(), true);
                using var remaining = machine.OpenSubKey(@"Software\ROBLOX Corporation");
                Check(remaining is null, "Native elevated deletion removes only the synthetic machine registry tree");
            }
            RunCombined(scope, mounted, new(sid, profile), machineKey, Check);
            scope.Dispose(); Check(scope.UnloadFailures.Count == 0, "Production offline hive cleanup unloads both temporary mounts");
            using (var reopened = RobloxWipeRegistryScope.Open([new(sid, profile)]))
            {
                foreach (var root in reopened.Roots.Where(root => root.Label.Contains(sid, StringComparison.Ordinal)))
                {
                    using var removed = root.Key.OpenSubKey(root.ClassesOnly ? "roblox-player" : @"Software\ROBLOX Corporation");
                    using var unrelated = root.Key.OpenSubKey(@"Software\UnrelatedSyntheticApplication");
                    Check(removed is null && unrelated?.GetValue("Keep") as string == "synthetic unrelated registry data",
                        "Combined elevated cleanup persists after closing and reopening the synthetic hive");
                }
                reopened.Dispose(); Check(reopened.UnloadFailures.Count == 0, "Reopened synthetic hives close without pending unloads");
            }
            File.WriteAllText(Path.Combine(report, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed = true, Checks = checks, RealDataModified = false }));
        }
        catch (Exception ex)
        { File.WriteAllText(Path.Combine(report, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = ex.ToString(), RealDataModified = false })); throw; }
        finally
        {
            if (fixtureKey is not null) Registry.CurrentUser.DeleteSubKeyTree(fixtureKey, false);
            if (machineKey is not null) Registry.LocalMachine.DeleteSubKeyTree(machineKey, false);
        }
    }
    private static void RunCombined(RobloxWipeRegistryScope scope, RobloxWipeRegistryScope.Root[] mounted,
        RobloxWipeProfile profile, string machinePath, Action<bool, string> check)
    {
        string local = Path.Combine(profile.Directory, "AppData", "Local"), app = Path.Combine(local, "DepthStrap");
        Directory.CreateDirectory(app);
        string original = Path.Combine(app, "DepthStrap.exe");
        File.WriteAllText(original, "synthetic original executable"); File.WriteAllText(Path.Combine(app, "Settings.json"), "{}");
        string player = Path.Combine(local, "Roblox"); Directory.CreateDirectory(player);
        File.WriteAllText(Path.Combine(player, "RobloxCookies.dat"), "synthetic client cookie");
        string documents = Path.Combine(profile.Directory, "Documents"); Directory.CreateDirectory(documents);
        string reservedUnrelated = @"\\?\" + Path.Combine(profile.Directory, "nul");
        File.WriteAllText(reservedUnrelated, "unrelated reserved-name fixture");
        string project = Path.Combine(documents, "SyntheticProject.rbxlx"), unrelatedFile = Path.Combine(documents, "Keep.txt");
        File.WriteAllText(project, "synthetic Studio creation"); File.WriteAllText(unrelatedFile, "unrelated fixture document");
        foreach (var root in mounted)
        {
            using var key = root.Key.CreateSubKey(root.ClassesOnly ? @"roblox-player\shell\open\command" : @"Software\ROBLOX Corporation");
            key.SetValue(root.ClassesOnly ? "" : "Synthetic", root.ClassesOnly ? '"' + Path.Combine(player, "RobloxPlayerBeta.exe") + "\" %1" : "fixture");
        }
        using var machine = Registry.LocalMachine.CreateSubKey(machinePath);
        using (var vendor = machine.CreateSubKey(@"Software\ROBLOX Corporation")) vendor.SetValue("Synthetic", 1);
        var roots = mounted.Append(new RobloxWipeRegistryScope.Root(machine, "SYNTHETIC MACHINE ONLY", false, RegistryView.Default)).ToArray();
        string database = Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var cookies = new BrowserCookieDatabase(database, true, true))
            cookies.Execute("CREATE TABLE cookies(host_key TEXT, encrypted_value BLOB); INSERT INTO cookies VALUES('.roblox.com',X'0102'),('example.org',X'0304');");
        var browser = new BrowserCookieReset(() => BrowserCookieReset.Discover([profile]), () => false);
        var store = new CompleteUninstallTests.Store { State = new([], []) };
        using var service = new RobloxCompleteUninstall(() => [profile], () => RobloxWipeCatalog.RegistryItems(roots, () => false),
            () => RobloxWipeCatalog.Files([profile], [], original, true, CancellationToken.None), store, () => false, original, scope, browser);
        var outcome = service.ExecuteAsync(service.PreviewAsync().GetAwaiter().GetResult(), true).GetAwaiter().GetResult();
        check(outcome.Complete, "The production combined wipe completes with native offline hives, an isolated machine registry tree and synthetic browser data: " + outcome.Message);
        check(File.Exists(original) && Directory.GetFiles(app, "*", SearchOption.AllDirectories).SequenceEqual(new[] { original }), "Combined elevated wipe keeps only the exact synthetic original executable");
        check(!Directory.Exists(player) && !File.Exists(project) && File.Exists(unrelatedFile), "Combined elevated wipe deletes synthetic client data and Studio creations while retaining unrelated documents");
        check(File.ReadAllText(reservedUnrelated) == "unrelated reserved-name fixture", "The complete native wipe scans past a literal nul file without treating it as a device or deleting unrelated data");
        using var remainingCookies = new BrowserCookieDatabase(database, false);
        check(remainingCookies.Count(false) == 0 && remainingCookies.Scalar("SELECT count(*) FROM cookies WHERE host_key='example.org'") == 1,
            "Combined elevated wipe clears only Roblox cookies from the synthetic browser database");
        using var remainingRegistry = machine.OpenSubKey(@"Software\ROBLOX Corporation");
        check(remainingRegistry is null, "Combined elevated wipe removes the isolated machine registry fixture");
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSaveKeyExW")]
    private static extern int RegSaveKeyEx(IntPtr key, string file, IntPtr security, uint flags);
}
