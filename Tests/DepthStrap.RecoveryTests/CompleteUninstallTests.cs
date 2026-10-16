using Microsoft.Win32;

namespace DepthStrap.Recovery;

internal static class CompleteUninstallTests
{
    internal sealed class Store : IRobloxStorePackages
    {
        internal RobloxStoreInventory State = new(["ROBLOXCORPORATION.ROBLOX_1.2.3.4_x64__55nm5eh3cm0pr"], []);
        internal int Removals; internal bool Fail;
        internal Action? OnRemove;
        public Task<RobloxStoreInventory> InventoryAsync() => Task.FromResult(State);
        public Task RemoveAsync(RobloxStoreInventory approved)
        {
            if (!RobloxStorePackages.Same(State, approved)) throw new IOException("Changed package fixture.");
            if (Fail) throw new IOException("Synthetic servicing failure.");
            Removals++; OnRemove?.Invoke(); State = new([], []); return Task.CompletedTask;
        }
    }
    internal static void Run(string fixture, Action<bool, string> check)
    {
        void Reject(Action action, string message)
        { bool failed = false; try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { failed = true; } check(failed, message); }
        string basis = Path.Combine(fixture, "complete-uninstall");
        var profiles = new[] { new RobloxWipeProfile("S-1-5-21-1-2-3-1001", Path.Combine(basis, "Alice")), new RobloxWipeProfile("S-1-5-21-1-2-3-1002", Path.Combine(basis, "Bob")) };
        string original = Path.Combine(profiles[0].Directory, "AppData", "Local", "DepthStrap", "DepthStrap.exe");
        var projects = new List<string>();
        foreach (var profile in profiles)
        {
            string local = Path.Combine(profile.Directory, "AppData", "Local");
            foreach (string app in new[] { "Roblox", "DepthStrap" })
            {
                Directory.CreateDirectory(Path.Combine(local, app, "Versions"));
                File.WriteAllText(Path.Combine(local, app, "Versions", "RobloxStudioBeta.exe"), "fixture");
                File.WriteAllText(Path.Combine(local, app, "RobloxCookies.dat"), "fixture");
            }
            File.WriteAllText(Path.Combine(local, "DepthStrap", "DepthStrap.exe"), "fixture-app");
            File.WriteAllText(Path.Combine(local, "DepthStrap", "Settings.json"), "{}");
            Directory.CreateDirectory(Path.Combine(profile.Directory, "Documents"));
            string project = Path.Combine(profile.Directory, "Documents", "MyProject.rbxlx"); File.WriteAllText(project, "synthetic-project"); projects.Add(project);
            File.WriteAllText(Path.Combine(profile.Directory, "Documents", "Keep.txt"), "unrelated");
        }
        string shared = Path.Combine(basis, "Shared", "Roblox"); Directory.CreateDirectory(shared); File.WriteAllText(Path.Combine(shared, "RobloxStudioBeta.exe"), "synthetic");
        Guid id = Guid.NewGuid(); string registryPath = @"Software\DepthStrapRecoveryLabFixtures\" + id;
        using var root = Registry.CurrentUser.CreateSubKey(registryPath);
        var roots = new[] { new RobloxWipeRegistryScope.Root(root, "SYNTHETIC", false, RegistryView.Default), new RobloxWipeRegistryScope.Root(root, "SYNTHETIC duplicate view", false, RegistryView.Default) };
        try
        {
            using (var key = root.CreateSubKey(@"Software\ROBLOX Corporation\RobloxStudio")) key.SetValue("fixture", 1);
            using (var key = root.CreateSubKey(@"Software\DepthStrap")) key.SetValue("fixture", 1);
            using (var key = root.CreateSubKey(@"Software\Classes\roblox-player\shell\open\command")) key.SetValue("", '"' + original + "\" -player %1");
            using (var key = root.CreateSubKey(@"Software\Classes\Unrelated\shell\open\command")) key.SetValue("", @"C:\OtherApp.exe %1");
            using (var key = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DepthStrap"))
            { key.SetValue("DisplayName", "DepthStrap"); key.SetValue("UninstallString", '"' + original + "\" -uninstall"); }
            var items = RobloxWipeCatalog.RegistryItems(roots, () => false);
            check(items.Count == 4, "All-user registry catalogue includes vendor, app, owned protocol and uninstall entries, deduplicating physical keys");
            var catalog = RobloxWipeCatalog.Files(profiles, [shared], original, true, CancellationToken.None);
            check(catalog.Projects.Length == 2 && catalog.Locations.Contains(Path.GetDirectoryName(original)!), "Every synthetic profile and full DepthStrap installation are previewed with Studio projects");
            var noProjects = RobloxWipeCatalog.Files(profiles, [shared], original, false, CancellationToken.None);
            check(noProjects.Projects.Length == 0 && !noProjects.Locations.Any(RobloxWipeCatalog.IsProject), "Fixture-only project preservation does not add personal creations to deletion");
            var store = new Store();
            using var service = new RobloxCompleteUninstall(() => profiles, () => RobloxWipeCatalog.RegistryItems(roots, () => false),
                () => RobloxWipeCatalog.Files(profiles, [shared], original, true, CancellationToken.None), store, () => false, original);
            var preview = service.PreviewAsync().GetAwaiter().GetResult();
            Reject(() => service.ExecuteAsync(preview, false).GetAwaiter().GetResult(), "Complete uninstall requires explicit approval");
            check(File.Exists(original) && projects.All(File.Exists) && store.Removals == 0, "Preview and refused confirmation preserve every file and package");
            using (var changed = root.CreateSubKey(@"Software\ROBLOX Corporation")) changed.SetValue("new", 1);
            Reject(() => service.ExecuteAsync(preview, true).GetAwaiter().GetResult(), "Changed registry revision blocks the entire uninstall before file deletion");
            check(projects.All(File.Exists), "Failed registry preflight preserves synthetic creations");
            preview = service.PreviewAsync().GetAwaiter().GetResult();
            store.State = new([], []);
            Reject(() => service.ExecuteAsync(preview, true).GetAwaiter().GetResult(), "Changed Store inventory blocks all file deletion");
            preview = service.PreviewAsync().GetAwaiter().GetResult();
            var result = service.ExecuteAsync(preview, true).GetAwaiter().GetResult();
            check(result.Complete && store.Removals == 1, "Confirmed complete uninstall removes the reviewed registry and package fixture");
            check(File.Exists(original) && Directory.GetFiles(Path.GetDirectoryName(original)!, "*", SearchOption.AllDirectories).SequenceEqual(new[] { original }), "Only the exact original installed DepthStrap.exe remains");
            check(!Directory.Exists(Path.Combine(profiles[1].Directory, "AppData", "Local", "DepthStrap")), "Another user's DepthStrap executable is not mistakenly preserved");
            check(projects.All(path => !File.Exists(path)) && !Directory.Exists(shared), "Full production scope removes synthetic Studio creations and shared installations");
            check(profiles.All(profile => File.Exists(Path.Combine(profile.Directory, "Documents", "Keep.txt"))), "Unrelated user documents are preserved");
            using (var key = root.OpenSubKey(@"Software\Classes\Unrelated")) check(key is not null, "Other applications' registrations are preserved");
            check(RobloxWipeCatalog.RegistryItems(roots, () => false).Count == 0, "Post-uninstall verification confirms all targeted synthetic registry trees are gone");
            Reject(() => service.ExecuteAsync(preview, true).GetAwaiter().GetResult(), "A used uninstall approval cannot be replayed");
            foreach (string[] args in new[] { Array.Empty<string>(), new[] { CompleteUninstallControls.Flag, "C:\\arbitrary" }, new[] { "--other" } })
                Reject(() => CompleteUninstallControls.ValidateArguments(args), "Elevated helper refuses arbitrary paths and extra arguments");
            CompleteUninstallControls.ValidateArguments([CompleteUninstallControls.Flag]); check(true, "Elevated helper accepts only the fixed uninstall action");
            check(RobloxWipeRegistryScope.IsUserSid(profiles[0].Sid) && !RobloxWipeRegistryScope.IsUserSid("S-1-5-18") && !RobloxWipeRegistryScope.IsUserSid(profiles[0].Sid + ".bak"), "Profile inventory accepts interactive user SIDs, excluding system and backup registrations");
            Reject(() => RobloxWipeCatalog.InstallationDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DepthStrap.exe")), "A profile root can never be treated as an eraseable app folder");
            check(RobloxStorePackages.ValidName("ROBLOXCORPORATION.ROBLOX_1.2.3.4_neutral_~_55nm5eh3cm0pr") && !RobloxStorePackages.ValidName("OtherApp_1.2.3.4_x64__55nm5eh3cm0pr"), "Windows package identity validation rejects other apps");
            Reject(() => RobloxStorePackages.StartInfo(new(["Roblox;Remove-Item C:\\"], [])), "Package command injection is rejected before starting PowerShell");
            Reject(() => RobloxProcessCleanup.Close([new(Environment.ProcessId, 0, original, "DepthStrap")], true), "Client cleanup can never terminate its own uninstall helper");
            Reject(() => RobloxProcessCleanup.Close([new(int.MaxValue, 0, @"C:\Windows\explorer.exe", "explorer")], true), "Client cleanup rejects unrelated process identities before opening a process");
            Reject(() => RobloxProcessCleanup.Close([], false), "Client cleanup requires explicit confirmation even for an empty selection");
            var start = RobloxStorePackages.StartInfo(null);
            check(!start.UseShellExecute && start.ArgumentList.Contains("-EncodedCommand") && start.ArgumentList.Last().Length < 28000, "Store servicing uses bounded embedded code, never an elevated writable disk script");
        }
        finally { root.Dispose(); Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); }
        string partial = Path.Combine(basis, "partial"); Directory.CreateDirectory(partial); File.WriteAllText(Path.Combine(partial, "fixture"), "data");
        var failureStore = new Store { Fail = true };
        using var failedService = new RobloxCompleteUninstall(() => [], () => [], () => new([partial], [], []), failureStore, () => false, null);
        var failedPreview = failedService.PreviewAsync().GetAwaiter().GetResult();
        check(!failedService.ExecuteAsync(failedPreview, true).GetAwaiter().GetResult().Complete && !Directory.Exists(partial), "A servicing failure after file deletion reports incomplete, never full success");
        var storeProfile = new RobloxWipeProfile("S-1-5-21-1-2-3-1003", Path.Combine(basis, "StoreUser"));
        string storeData = Path.Combine(storeProfile.Directory, "AppData", "Local", "Packages", "ROBLOXCORPORATION.ROBLOX_55nm5eh3cm0pr");
        Directory.CreateDirectory(storeData); string settings = Path.Combine(storeData, "settings.dat"); File.WriteAllText(settings, "locked-store-fixture");
        using var held = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None);
        var servicing = new Store { OnRemove = () => { held.Dispose(); File.Delete(settings); Directory.Delete(storeData); } };
        using var storeService = new RobloxCompleteUninstall(() => [storeProfile], () => [], () => new([storeData], [], []), servicing, () => false, null);
        var storePreview = storeService.PreviewAsync().GetAwaiter().GetResult();
        check(storePreview.ServicedStoreLocations.SequenceEqual(new[] { storeData }) && storePreview.Files.Files == 0, "A locked Store settings hive is explicitly delegated to Windows servicing, rather than blocking uninstall preflight");
        check(storeService.ExecuteAsync(storePreview, true).GetAwaiter().GetResult().Complete && !Directory.Exists(storeData), "Store-owned data is verified absent after the simulated Windows servicing step");
        Directory.CreateDirectory(storeData); File.WriteAllText(settings, "residual-fixture");
        var leftoverStore = new Store();
        using var leftoverService = new RobloxCompleteUninstall(() => [storeProfile], () => [], () => new([storeData], [], []), leftoverStore, () => false, null);
        check(!leftoverService.ExecuteAsync(leftoverService.PreviewAsync().GetAwaiter().GetResult(), true).GetAwaiter().GetResult().Complete,
            "A Store uninstaller leaving residual package data is reported as incomplete");
        using var retry = new RobloxCompleteUninstall(() => [storeProfile], () => [], () => new([storeData], [], []), leftoverStore, () => false, null);
        var retryPreview = retry.PreviewAsync().GetAwaiter().GetResult();
        check(retryPreview.ServicedStoreLocations.Length == 0 && retryPreview.Files.Files == 1 && retry.ExecuteAsync(retryPreview, true).GetAwaiter().GetResult().Complete,
            "After package removal, a fresh approved preview can safely remove residual Store data");
    }
    internal static void LargeTree(string fixture, Action<bool, string> check)
    {
        string root = Path.Combine(fixture, "large-preview"); Directory.CreateDirectory(root);
        for (int i = 0; i < 50001; i++) File.WriteAllBytes(Path.Combine(root, i.ToString("D5") + ".fixture"), []);
        var service = new RobloxDataReset([root], () => false);
        var preview = service.Preview();
        check(preview.Files == 50001, "The reported 50,000-entry failure is reproduced and fixed with a real large synthetic tree");
        var result = service.Execute(preview, true);
        check(result.Complete && result.RemovedFiles == 50001 && !Directory.Exists(root), "Large-tree deletion completes with bounded file-handle use");
    }
}
