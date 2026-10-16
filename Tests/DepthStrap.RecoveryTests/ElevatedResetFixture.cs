using System.Runtime.InteropServices;
using Microsoft.Win32;

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
            string profile = Path.Combine(report, "SyntheticProfile"); Directory.CreateDirectory(profile);
            string classes = Path.Combine(profile, "AppData", "Local", "Microsoft", "Windows"); Directory.CreateDirectory(classes);
            using var fixture = Registry.CurrentUser.CreateSubKey(fixtureKey);
            using (var key = fixture.CreateSubKey(@"Software\ROBLOX Corporation\RobloxStudio")) key.SetValue("Synthetic", 1);
            using (var privilege = new RobloxWipeRegistryScope.Privilege("SeBackupPrivilege"))
            {
                Check(RegSaveKeyEx(fixture.Handle.DangerousGetHandle(), Path.Combine(profile, "NTUSER.DAT"), IntPtr.Zero, 2) == 0, "Saved a disposable registry hive created exclusively from synthetic keys");
                Check(RegSaveKeyEx(fixture.Handle.DangerousGetHandle(), Path.Combine(classes, "UsrClass.dat"), IntPtr.Zero, 2) == 0, "Saved a separate disposable classes hive");
            }
            using var scope = RobloxWipeRegistryScope.Open([new(sid, profile)]);
            var mounted = scope.Roots.Where(root => root.Label.Contains(sid, StringComparison.Ordinal)).ToArray();
            Check(mounted.Length == 2, "Production offline-profile loading opens both synthetic hive files");
            foreach (var root in mounted)
            {
                // Use the exact fixture-relative path, never a live Windows profile or global machine inventory.
                var service = RobloxRegistryReset.ForOpenRoot(root.Key, @"Software\ROBLOX Corporation", "SYNTHETIC OFFLINE", () => false);
                service.Execute(service.Preview(), true);
                using var remaining = root.Key.OpenSubKey(@"Software\ROBLOX Corporation");
                Check(remaining is null, "Native registry deletion removes the synthetic offline vendor tree");
            }
            using (var machine = Registry.LocalMachine.CreateSubKey(machineKey))
            {
                using (var child = machine.CreateSubKey(@"Software\ROBLOX Corporation\RobloxStudio")) child.SetValue("Synthetic", 1);
                var service = RobloxRegistryReset.ForOpenRoot(machine, @"Software\ROBLOX Corporation", "SYNTHETIC MACHINE", () => false);
                service.Execute(service.Preview(), true);
                using var remaining = machine.OpenSubKey(@"Software\ROBLOX Corporation");
                Check(remaining is null, "Native elevated deletion removes only the synthetic machine registry tree");
            }
            scope.Dispose(); Check(scope.UnloadFailures.Count == 0, "Production offline hive cleanup unloads both temporary mounts");
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
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSaveKeyExW")]
    private static extern int RegSaveKeyEx(IntPtr key, string file, IntPtr security, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegLoadKeyW")]
    private static extern int RegLoadKey(IntPtr key, string name, string file);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegUnLoadKeyW")]
    private static extern int RegUnLoadKey(IntPtr key, string name);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegLoadAppKeyW")]
    private static extern int RegLoadAppKey(string file, out IntPtr key, uint access, uint options, uint reserved);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(IntPtr key);
}
