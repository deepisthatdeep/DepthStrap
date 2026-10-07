using System.IO;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Networking;
using Bloxstrap.Utility;

internal static class SettingsChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var manager = new JsonManager<Settings>("SettingsReliabilityFixture");
        manager.Prop = new Settings { CompetitivePreferredCity = "Dallas", AutoLeaveBadChimeRegion = true };
        check(manager.TrySave(), "Settings save atomically and report success");
        string original = AtomicFile.ReadText(manager.FileLocation);
        var unread = new JsonManager<Settings>("SettingsReliabilityFixture");
        using (var locked = new FileStream(manager.FileLocation, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            check(!unread.Load(false) && unread.LastLoadFailed && !unread.Loaded,
                "An unavailable settings file is reported as a read failure rather than loaded defaults");
            check(!unread.TrySave() && !unread.LastSaveSucceeded,
                "A failed read blocks saving defaults over the original settings");
        }
        check(AtomicFile.ReadText(manager.FileLocation) == original, "A locked settings poll preserves every byte of the saved file");
        check(unread.Load(false) && unread.Prop.AutoLeaveBadChimeRegion && unread.Prop.CompetitivePreferredCity == "Dallas",
            "Settings recover on the next successful read with warning/autolog preferences intact");
        File.WriteAllText(manager.FileLocation, "{broken");
        check(!unread.Load(false) && unread.Prop.CompetitivePreferredCity == "Dallas" && !unread.TrySave() &&
            File.ReadAllText(manager.FileLocation) == "{broken", "Malformed background reads retain the last good in-memory settings and never rewrite the file");
        AtomicFile.WriteText(manager.FileLocation, original);
        check(unread.Load(false), "Repairing the settings file releases the failed-read save guard");
        File.WriteAllText(manager.FileLocation, original, new System.Text.UTF8Encoding(true));
        check(unread.Load(false) && !unread.HasFileOnDiskChanged(), "A UTF-8 BOM does not produce perpetual false external-change warnings");
        File.Delete(manager.FileLocation);
        check(unread.HasFileOnDiskChanged(), "A deleted settings file is reported as changed without throwing");
        AtomicFile.WriteText(manager.FileLocation, original);
        var attributes = File.GetAttributes(manager.FileLocation);
        try
        {
            File.SetAttributes(manager.FileLocation, attributes | FileAttributes.ReadOnly);
            manager.Prop.CompetitivePreferredCity = "London";
            check(!manager.TrySave() && AtomicFile.ReadText(manager.FileLocation) == original,
                "A failed atomic replacement preserves the previous settings and reports failure");
        }
        finally { File.SetAttributes(manager.FileLocation, attributes); }
        check(!Directory.EnumerateFiles(Paths.Base, "SettingsReliabilityFixture.json.*.tmp").Any(),
            "Failed writes remove their temporary files");

        string concurrent = Path.Combine(Paths.Cache, "atomic-fixture.json");
        AtomicFile.WriteText(concurrent, "{\"generation\":0}");
        int reads = 0;
        var writer = Task.Run(() =>
        {
            for (int i = 1; i <= 100; i++) AtomicFile.WriteText(concurrent,
                JsonSerializer.Serialize(new { generation = i, body = new string('x', 32768) }));
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            do
            {
                using var document = JsonDocument.Parse(AtomicFile.ReadText(concurrent));
                if (document.RootElement.GetProperty("generation").GetInt32() < 0) throw new InvalidDataException();
                Interlocked.Increment(ref reads);
            } while (!writer.IsCompleted);
        })).ToArray();
        Task.WhenAll(readers.Append(writer)).GetAwaiter().GetResult();
        check(reads > 0 && JsonDocument.Parse(AtomicFile.ReadText(concurrent)).RootElement.GetProperty("generation").GetInt32() == 100,
            "Concurrent readers only observe complete JSON while one hundred atomic saves replace the file");

        App.Settings.Prop = new Settings { PreferNorthAmericaOnly = true, CompetitivePreferredCity = "Dallas" };
        App.Settings.Save();
        string saved = AtomicFile.ReadText(App.Settings.FileLocation);
        check(!AdaptiveRegionService.RefreshLearnedFields(), "Unchanged learned preferences do not notify and rebuild settings fields on every poll");
        int logs = App.Logger.History.Count;
        for (int i = 0; i < 10; i++) AdaptiveRegionService.RefreshLearnedFields();
        check(App.Logger.History.Count == logs, "Ten unchanged UI polls reuse learned settings without reading and logging the full settings file again");
        var external = new JsonManager<Settings>(); external.Load(false);
        external.Prop.CompetitivePreferredCity = "Houston"; external.TrySave();
        check(AdaptiveRegionService.RefreshLearnedFields() && App.Settings.Prop.CompetitivePreferredCity == "Houston",
            "A watcher settings update invalidates the poll cache and reaches the existing settings window");
        saved = AtomicFile.ReadText(App.Settings.FileLocation);
        using (var locked = new FileStream(App.Settings.FileLocation, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AdaptiveRegionService.PersistLearnedFields(new Settings { PreferNorthAmericaOnly = true, CompetitivePreferredCity = "London" });
            bool saveRejected = false, resetRejected = false;
            try { AdaptiveRegionService.SaveUserSettings(); } catch (IOException) { saveRejected = true; }
            string marker = Path.Combine(Paths.Cache, "ServerRegions.json");
            File.WriteAllText(marker, "{}");
            try { NetworkHistory.Reset(); } catch (IOException) { resetRejected = true; }
            check(saveRejected && resetRejected && File.Exists(marker),
                "Unavailable saved settings stop a stale UI save and network reset before deleting history");
        }
        check(AtomicFile.ReadText(App.Settings.FileLocation) == saved,
            "Background learning and failed user operations cannot overwrite unreadable preferences");
        string knownPath = KnownServerRegions.FilePath;
        string knownSaved = AtomicFile.ReadText(knownPath);
        using (var locked = new FileStream(knownPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool rejected = false;
            try { KnownServerRegions.Remember(999999, Array.Empty<KeyValuePair<string, KnownServerRegions.Entry>>(), DateTimeOffset.UtcNow); }
            catch (IOException) { rejected = true; }
            check(rejected && KnownServerRegions.ForPlace(999999).Count == 0,
                "An unreadable server cache keeps browsing available but cannot be replaced by an empty history");
        }
        check(AtomicFile.ReadText(knownPath) == knownSaved, "Failed cache reads preserve observed server metadata");
        CheckRobloxSettings(check);
        CheckMalformedSettings(check);
        CheckSharedQualityLock(check);
        App.Settings.Prop = new Settings();
    }

    private static void CheckMalformedSettings(Action<bool, string> check)
    {
        var manager = new JsonManager<Settings>("NullSettingsFixture");
        string malformed = "{\"CompetitiveFallbackCities\":[null,\"\",\"Dallas\",\"dallas\"],\"CustomGradientStops\":null,\"CustomIntegrations\":[null],\"CleanerDirectories\":null,\"CompetitivePreferredCity\":null,\"Locale\":null,\"CompetitiveRunLabel\":null,\"Theme\":999,\"CompetitiveProcessPriority\":999}";
        AtomicFile.WriteText(manager.FileLocation, malformed);
        check(manager.Load(false) && manager.Prop.CompetitiveFallbackCities.SequenceEqual(new[] { "Dallas" }) &&
            manager.Prop.CompetitivePreferredCity == "" && manager.Prop.CustomIntegrations.Count == 0 &&
            manager.Prop.CustomGradientStops.Count == 3 && manager.Prop.CleanerDirectories.Count == 0 &&
            manager.Prop.Locale == "nil" && manager.Prop.CompetitiveRunLabel == "",
            "Explicit null settings and list entries recover usable defaults without breaking region or appearance controls");
        check(manager.Prop.Theme == new Settings().Theme && manager.Prop.CompetitiveProcessPriority == new Settings().CompetitiveProcessPriority &&
            File.ReadAllText(manager.FileLocation) == malformed,
            "Unknown enum values recover defaults in memory without silently rewriting the user's file");
        check(manager.TrySave() && new JsonManager<Settings>("NullSettingsFixture").Load(false),
            "Recovered settings can be explicitly saved and loaded again");
        var state = new JsonManager<State>("NullStateFixture");
        AtomicFile.WriteText(state.FileLocation, "{\"SettingsWindow\":null,\"Mods\":[null,{\"FolderName\":null}]}");
        check(state.Load(false) && state.Prop.SettingsWindow is not null && state.Prop.Mods.Count == 0,
            "Null window state and invalid mod entries cannot crash settings or bootstrapper startup");
        AtomicFile.WriteText(state.FileLocation, "{\"SettingsWindow\":{\"Width\":-1,\"Height\":-2,\"Left\":-200},\"Mods\":null}");
        check(state.Load(false) && state.Prop.SettingsWindow is { Width: 0, Height: 0, Left: -200 },
            "Invalid window sizes recover defaults while negative multi-monitor positions are preserved");
        var appState = new JsonManager<AppState>("NullAppStateFixture");
        AtomicFile.WriteText(appState.FileLocation, "{\"VersionGuid\":null,\"PackageHashes\":null}");
        check(appState.Load(false) && appState.Prop.VersionGuid == "" && appState.Prop.PackageHashes.Count == 0,
            "Null installed-version metadata recovers an empty verified package state");
        string backup = Path.Combine(Paths.Cache, "CompetitiveSettingsBackup.json");
        foreach (string invalid in new[] { "null", "{\"Global\":null}", "{\"Flags\":{\"Rendering.MSAA1\":null}}" })
        {
            AtomicFile.WriteText(backup, invalid);
            check(!Bloxstrap.Roblox.CompetitiveSettingsBackup.Restore() && File.ReadAllText(backup) == invalid,
                "An incomplete preset backup is retained instead of being treated as an empty successful restore");
        }
        File.Delete(backup);
        var flags = new FastFlagManager();
        string? original = File.Exists(flags.FileLocation) ? File.ReadAllText(flags.FileLocation) : null;
        try
        {
            AtomicFile.WriteText(flags.FileLocation, "{\"FFlagNullFixture\":null,\"FIntFixture\":123}");
            check(flags.Load(false) && !flags.Prop.ContainsKey("FFlagNullFixture") && flags.GetValue("FIntFixture") == "123",
                "Null FastFlag values are treated as deleted while valid values remain available");
            flags.Prop["FFlagNullFixture"] = null!;
            flags.Save();
            check(flags.LastSaveSucceeded && !flags.Prop.ContainsKey("FFlagNullFixture") && !File.ReadAllText(flags.FileLocation).Contains("null"),
                "Saving a null FastFlag cannot crash or emit an invalid null flag value");
            flags.SetValue("FIntFixture", "456");
            File.WriteAllText(flags.FileLocation, "{broken");
            check(!flags.Load(false) && flags.Changed && flags.GetValue("FIntFixture") == "456",
                "A failed FastFlag reload preserves unsaved edits and their changed-state snapshot");
        }
        finally { if (original is null) File.Delete(flags.FileLocation); else AtomicFile.WriteText(flags.FileLocation, original); }
    }

    private static void CheckSharedQualityLock(Action<bool, string> check)
    {
        var globals = App.GlobalSettings;
        string original = File.ReadAllText(globals.FileLocation);
        var attributes = File.GetAttributes(globals.FileLocation);
        var flags = new Dictionary<string, object>(App.FastFlags.Prop);
        var settings = App.Settings.Prop;
        var presence = Bloxstrap.Roblox.CompetitiveSettingsBackup.PlayerPresence;
        int players = 0;
        string backup = Path.Combine(Paths.Cache, "CompetitiveSettingsBackup.json");
        try
        {
            Bloxstrap.Roblox.CompetitiveSettingsBackup.PlayerPresence = () => players > 0;
            globals.Load();
            string quality = globals.GetPreset("Rendering.SavedQualityLevel")!;
            Bloxstrap.Roblox.CompetitiveSettingsBackup.SetGlobal("Rendering.SavedQualityLevel", "3");
            globals.Save();
            App.FastFlags.SetPreset("Rendering.MSAA1", "2");
            Bloxstrap.Roblox.CompetitiveSettingsBackup.SetFlag("Rendering.MSAA1", "1");
            App.FastFlags.Save();
            Bloxstrap.Roblox.CompetitiveSettingsBackup.LockQuality();
            string locked = File.ReadAllText(globals.FileLocation);
            players = 2;
            check(globals.TrySave() && globals.GetReadOnly() && File.ReadAllText(globals.FileLocation) == locked,
                "An unchanged settings save leaves active clients' shared graphics file untouched and locked");
            globals.SetPreset("Rendering.FramerateCap", 777);
            check(!globals.TrySave() && globals.GetReadOnly() && File.ReadAllText(globals.FileLocation) == locked &&
                !globals.TrySetReadOnly(false),
                "Changed settings or a manual unlock cannot bypass the shared quality lock of active clients");
            globals.Load();
            check(Bloxstrap.Roblox.CompetitiveSettingsBackup.Restore() && globals.GetReadOnly() &&
                File.ReadAllText(globals.FileLocation) == locked && App.FastFlags.GetPreset("Rendering.MSAA1") == "2" && File.Exists(backup),
                "A second-client restore keeps the shared quality lock and original graphics backup while restoring launch-only flags");
            App.Settings.Prop = new Settings { MatchFpsToMonitorRefreshRate = false, CompetitiveFpsCap = 360 };
            Bloxstrap.Competitive.CompetitivePerformanceManager.ApplyPreLaunchSettings();
            check(globals.GetReadOnly() && File.ReadAllText(globals.FileLocation) == locked,
                "Launching another profile cannot temporarily unlock or rewrite shared graphics while a Player is active");
            players = 1;
            check(!Bloxstrap.Roblox.CompetitiveSettingsBackup.ReleaseQualityLock() && globals.GetReadOnly(),
                "One client closing cannot release the final active client's quality lock");
            players = 0;
            check(Bloxstrap.Roblox.CompetitiveSettingsBackup.ReleaseQualityLock() && !globals.GetReadOnly() &&
                Bloxstrap.Roblox.CompetitiveSettingsBackup.Restore() && globals.GetPreset("Rendering.SavedQualityLevel") == quality && !File.Exists(backup),
                "After the last client exits, the original lock state and graphics preset are restored without losing the backup");
        }
        finally
        {
            players = 0;
            Bloxstrap.Roblox.CompetitiveSettingsBackup.ReleaseQualityLock();
            Bloxstrap.Roblox.CompetitiveSettingsBackup.Restore();
            Bloxstrap.Roblox.CompetitiveSettingsBackup.PlayerPresence = presence;
            globals.SetReadOnly(false);
            AtomicFile.WriteText(globals.FileLocation, original);
            File.SetAttributes(globals.FileLocation, attributes);
            globals.Load();
            App.FastFlags.Prop = flags; App.FastFlags.Save();
            App.Settings.Prop = settings;
        }
    }

    private static void CheckRobloxSettings(Action<bool, string> check)
    {
        var editor = new GBSEditor();
        string original = File.ReadAllText(editor.FileLocation);
        var attributes = File.GetAttributes(editor.FileLocation);
        try
        {
            editor.Load();
            check(editor.Loaded && !editor.LastLoadFailed, "Roblox XML settings load as a validated user-settings document");
            editor.SetPreset("Rendering.FramerateCap", 120);
            editor.SetReadOnly(true);
            check(editor.TrySave() && editor.LastSaveSucceeded && editor.GetReadOnly() &&
                File.ReadAllText(editor.FileLocation).Contains(">120</int>"),
                "Atomic Roblox settings save preserves an existing read-only quality lock");
            string saved = File.ReadAllText(editor.FileLocation);
            editor.SetPreset("Rendering.FramerateCap", 144);
            using (var held = new FileStream(editor.FileLocation, FileMode.Open, FileAccess.Read, FileShare.Read))
                check(!editor.TrySave() && !editor.LastSaveSucceeded,
                    "A blocked Roblox settings replacement reports failure instead of save success");
            check(editor.GetReadOnly() && File.ReadAllText(editor.FileLocation) == saved,
                "Failed Roblox settings save preserves the full original XML and restores its quality lock");
            string imported = Path.Combine(Paths.Cache, "import-settings-fixture.xml");
            foreach (string invalid in new[] { "<broken", "<roblox version='4'/>" })
            {
                File.WriteAllText(imported, invalid);
                check(!editor.ImportSettings(imported) && editor.GetReadOnly() && File.ReadAllText(editor.FileLocation) == saved,
                    "Invalid XML or missing user properties cannot replace Roblox settings or remove their lock");
            }
            File.WriteAllText(imported, saved.Replace(">120</int>", ">165</int>"));
            check(editor.ImportSettings(imported) && editor.GetReadOnly() && editor.GetPreset("Rendering.FramerateCap") == "165",
                "Validated Roblox XML import updates memory and disk while retaining the original lock");
            editor.SetReadOnly(false);
            File.WriteAllText(editor.FileLocation, "<corrupt");
            editor.Load();
            check(editor.LastLoadFailed && editor.GetPreset("Rendering.FramerateCap") == "165" && !editor.TrySave() &&
                File.ReadAllText(editor.FileLocation) == "<corrupt",
                "A corrupt Roblox XML read keeps prior memory but blocks overwriting the damaged file");
            AtomicFile.WriteText(editor.FileLocation, saved);
            editor.Load();
            check(!editor.LastLoadFailed && editor.TrySave(), "Repairing Roblox XML releases its failed-read save guard");
            check(!Directory.EnumerateFiles(Paths.Roblox, "GlobalBasicSettings_13.xml.*.tmp").Any(),
                "Failed Roblox XML writes leave no temporary files");
            App.GlobalSettings.Load();
            string quality = App.GlobalSettings.GetPreset("Rendering.SavedQualityLevel")!;
            Bloxstrap.Roblox.CompetitiveSettingsBackup.SetGlobal("Rendering.SavedQualityLevel", quality == "3" ? "4" : "3");
            App.GlobalSettings.Save();
            string applied = File.ReadAllText(editor.FileLocation);
            string backup = Path.Combine(Paths.Cache, "CompetitiveSettingsBackup.json");
            using (var held = new FileStream(editor.FileLocation, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Bloxstrap.Roblox.CompetitiveSettingsBackup.Restore();
                check(File.Exists(backup), "A failed preset restoration retains its backup for a later retry");
            }
            check(File.ReadAllText(editor.FileLocation) == applied, "A blocked preset restore cannot rewrite the current Roblox XML");
            Bloxstrap.Roblox.CompetitiveSettingsBackup.Restore();
            check(!File.Exists(backup) && App.GlobalSettings.GetPreset("Rendering.SavedQualityLevel") == quality,
                "A later successful preset restore recovers the original quality before removing its backup");
        }
        finally
        {
            File.SetAttributes(editor.FileLocation, FileAttributes.Normal);
            AtomicFile.WriteText(editor.FileLocation, original);
            File.SetAttributes(editor.FileLocation, attributes);
            App.GlobalSettings.Load();
        }
    }
}
