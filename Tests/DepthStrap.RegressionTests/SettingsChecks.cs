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
        App.Settings.Prop = new Settings();
    }
}
