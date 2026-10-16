using System.Reflection;
using System.Text.Json;

namespace DepthStrap.Recovery;

internal static class OfflineHiveFixture
{
    internal static void Run(string sourceDirectory, string reportDirectory)
    {
        string source = Path.GetFullPath(sourceDirectory), report = Path.GetFullPath(reportDirectory);
        if (!Path.GetFileName(source).StartsWith("admin-reset-fixture-", StringComparison.Ordinal) || Directory.Exists(report))
            throw new IOException("Use a saved disposable admin fixture and a new report directory.");
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "result.json")));
        if (evidence.RootElement.GetProperty("RealDataModified").GetBoolean()) throw new IOException("The source is not a synthetic fixture.");
        Directory.CreateDirectory(report);
        string profile = Path.Combine(report, "SyntheticProfile"); Directory.CreateDirectory(profile);
        string classes = Path.Combine(profile, "AppData", "Local", "Microsoft", "Windows"); Directory.CreateDirectory(classes);
        File.Copy(Path.Combine(source, "SyntheticProfile", "NTUSER.DAT"), Path.Combine(profile, "NTUSER.DAT"));
        File.Copy(Path.Combine(source, "SyntheticProfile", "AppData", "Local", "Microsoft", "Windows", "UsrClass.dat"), Path.Combine(classes, "UsrClass.dat"));
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        string sid = "S-1-5-21-" + Random.Shared.Next(100000, int.MaxValue) + "-" + Random.Shared.Next(100000, int.MaxValue) + "-" + Random.Shared.Next(100000, int.MaxValue) + "-1001";
        using (var existing = Microsoft.Win32.Registry.Users.OpenSubKey(sid)) Check(existing is null, "The fixture never opens an existing real user hive");
        MethodInfo open = typeof(RobloxWipeRegistryScope).GetMethod("OpenHive", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var scope = new RobloxWipeRegistryScope();
        try
        {
            open.Invoke(scope, [sid, Path.Combine(profile, "NTUSER.DAT"), "SYNTHETIC", false]);
            open.Invoke(scope, [sid + "_Classes", Path.Combine(classes, "UsrClass.dat"), "SYNTHETIC classes", true]);
            Check(scope.Roots.Count == 2, "The production offline loader opens both saved synthetic hive files");
            foreach (var root in scope.Roots)
            {
                var reset = RobloxRegistryReset.ForOpenRoot(root.Key, @"Software\ROBLOX Corporation", "SYNTHETIC OFFLINE", () => false);
                reset.Execute(reset.Preview(), true);
                using var key = root.Key.OpenSubKey(@"Software\ROBLOX Corporation"); Check(key is null, "Production deletion removes the synthetic offline vendor tree");
            }
            scope.Dispose(); Check(scope.UnloadFailures.Count == 0, "Closing production hive handles flushes the synthetic files successfully");
            using var reopened = new RobloxWipeRegistryScope();
            open.Invoke(reopened, [sid, Path.Combine(profile, "NTUSER.DAT"), "SYNTHETIC reopened", false]);
            using (var key = reopened.Roots.Single().Key.OpenSubKey(@"Software\ROBLOX Corporation")) Check(key is null, "Deletion persists after closing and reopening the offline hive from disk");
            reopened.Dispose(); Check(reopened.UnloadFailures.Count == 0, "Reopened fixture hive closes cleanly");
            File.WriteAllText(Path.Combine(report, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, RealDataModified = false }));
            Console.WriteLine("PASS: " + checks.Count + " native offline-hive checks using saved synthetic hives only.");
        }
        catch (Exception ex)
        { File.WriteAllText(Path.Combine(report, "result.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = ex.ToString(), RealDataModified = false })); throw; }
    }
}
