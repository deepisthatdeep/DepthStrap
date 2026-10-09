namespace DepthStrap.Recovery;

internal static class RobloxDataResetTests
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        void Reject(Action operation, string description)
        {
            bool rejected = false;
            try { operation(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ArgumentException) { rejected = true; }
            check(rejected, description);
        }
        string root = Path.Combine(fixture, "full-reset"); Directory.CreateDirectory(Path.Combine(root, "nested"));
        string cookie = Path.Combine(root, "RobloxCookies.dat"); File.WriteAllText(cookie, "fixture-sign-in");
        File.WriteAllText(Path.Combine(root, "nested", "settings.json"), "fixture-settings");
        string other = Path.Combine(fixture, "DepthStrap-settings.json"); File.WriteAllText(other, "preserve");
        var reset = new RobloxDataReset(new[] { root }, () => false);
        var preview = reset.Preview();
        check(preview.Files == 2 && preview.Bytes > 0, "Full-reset preview counts files without reading their contents");
        check(File.Exists(cookie), "Preview does not delete cookies");
        Reject(() => reset.Execute(preview, false), "Full reset requires confirmation");
        Reject(() => new RobloxDataReset(new[] { root }, () => false).Execute(preview, true), "A preview cannot be used by another reset scope");
        File.WriteAllText(Path.Combine(root, "new-data"), "new");
        Reject(() => reset.Execute(preview, true), "Changed data invalidates the old preview");
        check(File.Exists(cookie), "A stale preview preserves existing cookies");
        preview = reset.Preview();
        using (var locked = new FileStream(cookie, FileMode.Open, FileAccess.Read, FileShare.None))
            Reject(() => reset.Execute(preview, true), "One locked entry prevents the entire reset preflight");
        check(File.Exists(Path.Combine(root, "new-data")), "Failed preflight preserves unrelated selected files");
        var running = new RobloxDataReset(new[] { root }, () => true);
        Reject(() => running.Execute(running.Preview(), true), "Running clients block full reset");
        using (var cancelled = new CancellationTokenSource())
        { cancelled.Cancel(); Reject(() => reset.Execute(preview, true, cancelled.Token), "Cancelled full reset preserves files"); }
        File.SetAttributes(cookie, FileAttributes.ReadOnly);
        preview = reset.Preview();
        var result = reset.Execute(preview, true);
        check(result.Complete && result.RemovedFiles == 3 && result.RemovedDirectories == 2 && !Directory.Exists(root), "Confirmed full reset removes nested data and read-only cookie fixtures");
        check(File.ReadAllText(other) == "preserve", "Full reset preserves DepthStrap settings outside the selected roots");
        foreach (string invalidRoot in new[] { "", "Roblox", @"C:Roblox" })
            Reject(() => new RobloxDataReset(new[] { invalidRoot }, () => false), "Relative or missing full-reset locations are rejected before path normalization");
        check(File.ReadAllText(other) == "preserve", "Rejected location inputs preserve unrelated fixture data");
        check(reset.Execute(reset.Preview(), true).Complete, "An already empty reset is idempotent");
        Reject(() => new RobloxDataReset(new[] { Path.GetPathRoot(fixture)! }, () => false), "Drive roots cannot be reset");
        Reject(() => new RobloxDataReset(new[] { root, Path.Combine(root, "nested") }, () => false), "Overlapping reset locations are rejected");
        string outside = Path.Combine(fixture, "full-reset-outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep"), "preserve");
        Directory.CreateDirectory(root);
        string link = Path.Combine(root, "redirected"); JunctionFixture.Create(fixture, link, outside);
        Reject(() => reset.Preview(), "Full reset rejects nested directory redirects");
        check(File.ReadAllText(Path.Combine(outside, "keep")) == "preserve", "Redirected external data is preserved");
        var redirectedRoot = new RobloxDataReset(new[] { link }, () => false);
        Reject(() => redirectedRoot.Preview(), "A redirected reset root is refused");
        string parentRedirect = Path.Combine(fixture, "full-reset-parent"); JunctionFixture.Create(fixture, parentRedirect, outside);
        Directory.CreateDirectory(Path.Combine(outside, "Roblox"));
        Reject(() => new RobloxDataReset(new[] { Path.Combine(parentRedirect, "Roblox") }, () => false).Preview(), "A reset root inside a redirected parent is refused");
        string shapedFile = Path.Combine(fixture, "reset-root-file"); File.WriteAllText(shapedFile, "preserve");
        Reject(() => new RobloxDataReset(new[] { shapedFile }, () => false).Preview(), "A file where a Roblox directory was expected is refused");
        string raceRoot = Path.Combine(fixture, "client-race"); Directory.CreateDirectory(raceRoot); File.WriteAllText(Path.Combine(raceRoot, "a"), "fixture");
        int calls = 0;
        var raceReset = new RobloxDataReset(new[] { raceRoot }, () => ++calls >= 3);
        var raceResult = raceReset.Execute(raceReset.Preview(), true);
        check(!raceResult.Complete && raceResult.RemovedFiles == 0 && File.Exists(Path.Combine(raceRoot, "a")), "A client starting at deletion time stops reset without claiming success");
        string catalogue = Path.Combine(fixture, "catalogue");
        string local = Path.Combine(catalogue, "Local"), roaming = Path.Combine(catalogue, "Roaming"), profile = Path.Combine(catalogue, "User"), temp = Path.Combine(catalogue, "Temp");
        string package = Path.Combine(local, "Packages", "ROBLOXCORPORATION.ROBLOX_55nm5eh3cm0pr"); Directory.CreateDirectory(package);
        string unrelated = Path.Combine(local, "Packages", "OtherApp_55nm5eh3cm0pr"); Directory.CreateDirectory(unrelated);
        var catalogueRoots = RobloxDataReset.CurrentUserRoots(local, roaming, profile, temp);
        check(catalogueRoots.Contains(package) && !catalogueRoots.Contains(unrelated), "Full-reset catalogue includes only the matching Roblox Store user-data family");
        check(catalogueRoots.Contains(Path.Combine(local, "DepthStrap", "Versions")) && !catalogueRoots.Contains(Path.Combine(local, "DepthStrap")), "Managed Roblox build reset excludes the DepthStrap app/settings root");
        Reject(() => RobloxDataReset.CurrentUserRoots("", roaming, profile, temp), "Missing profile locations cannot become relative reset paths");
        string custom = Path.Combine(catalogue, "Custom DepthStrap"); Directory.CreateDirectory(custom);
        string customExe = Path.Combine(custom, "DepthStrap.exe"); File.WriteAllText(customExe, "fixture-only marker");
        var customRoots = RobloxDataReset.ManagedInstallationRoots(customExe);
        check(customRoots.SequenceEqual(new[] { Path.Combine(custom, "Versions"), Path.Combine(custom, "Downloads") }), "Custom installation includes only its Roblox build/package folders");
        check(!customRoots.Contains(custom) && !customRoots.Contains(customExe), "Custom installation reset preserves the DepthStrap executable/settings root");
        var expanded = new RobloxDataReset(new[] { raceRoot }, () => false).IncludeManagedInstallation(customExe);
        check(expanded.Preview().Locations.Contains(raceRoot) && customRoots.All(expanded.Preview().Locations.Contains), "Adding a managed installation preserves previous reset locations");
        check(expanded.IncludeManagedInstallation(customExe).Preview().Locations.Length == 3, "Repeated installation selection is deduplicated");
        string anotherCustom = Path.Combine(catalogue, "Another installation"); Directory.CreateDirectory(anotherCustom);
        string anotherExe = Path.Combine(anotherCustom, "DepthStrap.exe"); File.WriteAllText(anotherExe, "fixture-only marker");
        var allLocations = expanded.IncludeManagedInstallation(anotherExe).Preview().Locations;
        check(allLocations.Length == 5 && allLocations.Contains(raceRoot) && customRoots.All(allLocations.Contains), "A second custom installation accumulates instead of replacing earlier selections");
        check(!allLocations.Contains(custom) && !allLocations.Contains(anotherCustom) && File.Exists(customExe) && File.Exists(anotherExe), "Multiple-installation previews preserve both app/settings roots");
        foreach (string location in allLocations.Where(path => path != raceRoot))
        { Directory.CreateDirectory(location); File.WriteAllText(Path.Combine(location, "fixture-package"), "synthetic build data"); }
        var multiReset = expanded.IncludeManagedInstallation(anotherExe);
        var multiResult = multiReset.Execute(multiReset.Preview(), true);
        check(multiResult.Complete && allLocations.All(path => !Directory.Exists(path)), "Confirmed multi-install reset deletes every previewed owned build/data folder");
        check(File.Exists(customExe) && File.Exists(anotherExe) && File.ReadAllText(other) == "preserve", "Multi-install deletion preserves executable markers and settings outside selected roots");
        string existingRoot = Path.Combine(fixture, "existing-before-preview"), futureRoot = Path.Combine(fixture, "appeared-after-preview");
        Directory.CreateDirectory(existingRoot); File.WriteAllText(Path.Combine(existingRoot, "keep"), "preserve");
        var appearingReset = new RobloxDataReset(new[] { existingRoot, futureRoot }, () => false);
        var absentPreview = appearingReset.Preview();
        Directory.CreateDirectory(futureRoot); File.WriteAllText(Path.Combine(futureRoot, "new"), "new data");
        Reject(() => appearingReset.Execute(absentPreview, true), "A listed absent root appearing after preview invalidates deletion consent");
        check(File.Exists(Path.Combine(existingRoot, "keep")) && File.Exists(Path.Combine(futureRoot, "new")), "A newly appearing root stops reset before deleting any selected data");
        string identityRoot = Path.Combine(fixture, "same-size-replacement"); Directory.CreateDirectory(identityRoot);
        string originalFile = Path.Combine(identityRoot, "data"); File.WriteAllText(originalFile, "old");
        var identityReset = new RobloxDataReset(new[] { identityRoot }, () => false);
        var identityPreview = identityReset.Preview();
        DateTime fileTime = File.GetLastWriteTimeUtc(originalFile), folderTime = Directory.GetLastWriteTimeUtc(identityRoot);
        string replacementFile = Path.Combine(fixture, "replacement-source"); File.WriteAllText(replacementFile, "new");
        File.SetLastWriteTimeUtc(replacementFile, fileTime); File.Move(replacementFile, originalFile, true);
        Directory.SetLastWriteTimeUtc(identityRoot, folderTime);
        Reject(() => identityReset.Execute(identityPreview, true), "A same-size replacement with preserved timestamps invalidates the old file-identity preview");
        check(File.ReadAllText(originalFile) == "new", "File-identity rejection preserves the replacement data");
        string deepRoot = Path.Combine(fixture, "deep-long-path"), deepLeaf = deepRoot;
        for (int level = 0; level < 128; level++) deepLeaf = Path.Combine(deepLeaf, "level" + level.ToString("D3"));
        Directory.CreateDirectory(deepLeaf); File.WriteAllText(Path.Combine(deepLeaf, "fixture"), "synthetic deep data");
        var deepReset = new RobloxDataReset(new[] { deepRoot }, () => false);
        var deepPreview = deepReset.Preview();
        check(deepPreview.Files == 1 && File.Exists(Path.Combine(deepLeaf, "fixture")), "Deep long-path data is previewed without mutation");
        var deepResult = deepReset.Execute(deepPreview, true);
        check(deepResult.Complete && deepResult.RemovedDirectories == 129 && !Directory.Exists(deepRoot), "Confirmed reset removes all owned deeply nested long-path folders");
        Reject(() => RobloxDataReset.ManagedInstallationRoots("DepthStrap.exe"), "Relative custom installation locations are refused");
        Reject(() => RobloxDataReset.ManagedInstallationRoots(Path.Combine(custom, "Other.exe")), "Other executable names cannot define a custom DepthStrap reset");
        Reject(() => RobloxDataReset.ManagedInstallationRoots(Path.Combine(custom, "missing", "DepthStrap.exe")), "A missing custom installation marker is refused");
    }
}
