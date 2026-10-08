using System.IO;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Competitive;
using Bloxstrap.Integrations;
using Bloxstrap.Models;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Networking;
using Bloxstrap.Roblox;
using Bloxstrap.RobloxInterfaces;

internal static class FeatureChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(ReleaseMigration.IsPrototypeToFirstRelease("DepthStrap", "1.5.1", "1.0.0") && !ReleaseMigration.NeedsLegacyMigrations("DepthStrap") && ReleaseMigration.NeedsLegacyMigrations("Froststrap"), "DepthStrap 1.0 can replace the prototype without triggering upstream version migrations");
        var settings = new Settings();
        var unavailable = new NetworkTestResult { RegionsAvailable = false };
        unavailable.Apply(settings);
        check(settings.ChimeRegionMonitorEnabled && settings.WarnOnBadChimeRegion,
            "An unavailable setup registry does not disable live region warnings");
        var migrated = new Settings { ChimeRegionMonitorEnabled = false, WarnOnBadChimeRegion = false, AutoLeaveBadChimeRegion = true };
        check(RegionMonitoringPolicy.RepairLegacySetup(migrated, unavailable) && migrated.WarnOnBadChimeRegion &&
            migrated.ChimeRegionMonitorEnabled && migrated.AutoLeaveBadChimeRegion,
            "Migration repairs alerts disabled by an old failed setup without changing autolog consent");
        migrated.WarnOnBadChimeRegion = migrated.ChimeRegionMonitorEnabled = false;
        check(!RegionMonitoringPolicy.RepairLegacySetup(migrated, unavailable) && !migrated.WarnOnBadChimeRegion,
            "Intentional alert choices remain unchanged after the one-time setup repair");
        new NetworkTestResult { RegionsAvailable = true }.Apply(migrated);
        check(!migrated.WarnOnBadChimeRegion && !migrated.ChimeRegionMonitorEnabled,
            "Follow-up network tests preserve disabled alert preferences");
        var alertsOnly = new Settings { CompetitiveNetworkMonitorEnabled = false, ChimeRegionMonitorEnabled = false,
            WarnOnBadChimeRegion = true, AutoLeaveBadChimeRegion = false };
        check(RegionMonitoringPolicy.NeedsWatcher(alertsOnly) && RegionMonitoringPolicy.NeedsAlerts(alertsOnly),
            "Warnings independently start their required watcher without diagnostics");
        alertsOnly.WarnOnBadChimeRegion = false; alertsOnly.AutoLeaveBadChimeRegion = true;
        check(RegionMonitoringPolicy.NeedsWatcher(alertsOnly) && RegionMonitoringPolicy.NeedsAlerts(alertsOnly),
            "Autolog independently starts its required watcher without other monitoring toggles");
        string fontFixture = Path.Combine(Paths.Cache, "font-fixture.ttf");
        Directory.CreateDirectory(Paths.Cache);
        File.Copy("Bloxstrap/Resources/Fonts/Rubik-VariableFont_wght.ttf", fontFixture, true);
        string storedFont = AppearanceFont.Store(fontFixture);
        File.Delete(fontFixture);
        check(File.Exists(storedFont), "Appearance keeps its own font copy when the selected download is removed");
        string fontBuild = Path.Combine(Paths.Cache, "font-build");
        string families = Path.Combine(fontBuild, "content", "fonts", "families");
        Directory.CreateDirectory(families);
        string originalFamily = "{\"name\":\"Fixture\",\"extraMetadata\":true,\"faces\":[{\"weight\":400,\"assetId\":\"rbxasset://fonts/Original.ttf\"},{\"weight\":700,\"assetId\":\"rbxasset://fonts/Bold.ttf\"}]}";
        File.WriteAllText(Path.Combine(families, "Fixture.json"), originalFamily);
        var fontFiles = AppearanceFont.CreateFiles(fontBuild, storedFont, new Dictionary<string, string>());
        string familyKey = Path.Combine("content", "fonts", "families", "Fixture.json");
        using (var patched = JsonDocument.Parse(File.ReadAllText(fontFiles[familyKey])))
            check(patched.RootElement.GetProperty("extraMetadata").GetBoolean() && patched.RootElement.GetProperty("faces").EnumerateArray().All(x =>
                x.GetProperty("assetId").GetString() == "rbxasset://fonts/DepthStrapCustomFont.ttf"), "Roblox font patch replaces all weights and preserves family metadata");
        check(File.ReadAllText(Path.Combine(families, "Fixture.json")) == originalFamily && fontFiles.Count == 2,
            "Appearance stages font and families for the restorable mod manifest without changing installed files directly");
        check(AppearanceFont.CreateFiles(fontBuild, null, new Dictionary<string, string>()).Count == 0,
            "Reset removes all Appearance font entries so the mod pipeline restores Roblox originals");
        string badFont = Path.Combine(Paths.Cache, "invalid-font.otf"); File.WriteAllText(badFont, "invalid");
        bool rejectedFont = false;
        try { AppearanceFont.Store(badFont); } catch (InvalidDataException) { rejectedFont = true; }
        check(rejectedFont, "Invalid custom fonts cannot enter Roblox's font patch");
        string truncatedFont = Path.Combine(Paths.Cache, "truncated-font.ttf");
        File.WriteAllBytes(truncatedFont, new byte[] { 0, 1, 0, 0, 1, 2, 3, 4 });
        bool rejectedTruncated = false;
        try { AppearanceFont.Store(truncatedFont); } catch (InvalidDataException) { rejectedTruncated = true; }
        check(rejectedTruncated, "A TrueType header alone cannot make a truncated file valid");
        string damagedFont = Path.Combine(Paths.Cache, "damaged-font.ttf");
        File.WriteAllBytes(damagedFont, new byte[256]);
        using (var file = File.OpenWrite(damagedFont)) file.Write(new byte[] { 0, 1, 0, 0 });
        bool rejectedDamaged = false;
        try { AppearanceFont.Store(damagedFont); } catch (InvalidDataException) { rejectedDamaged = true; }
        check(rejectedDamaged, "A longer file with a correct signature but no font tables is rejected");
        var appFamily = FontManager.LoadFontFromFile(storedFont)!;
        check(appFamily.GetTypefaces().Any(x => x.TryGetGlyphTypeface(out var glyph) && glyph.CharacterToGlyphMap.ContainsKey('A')),
            "The retained font resolves real app glyphs after its original download is deleted");
        File.WriteAllText(Path.Combine(families, "Empty.json"), "{\"faces\":[]}");
        File.WriteAllText(Path.Combine(families, "Malformed.json"), "[]");
        File.WriteAllText(Path.Combine(families, "Broken.json"), "{broken");
        check(AppearanceFont.CreateFiles(fontBuild, storedFont, new Dictionary<string, string>()).Count == 2,
            "Malformed or empty families do not prevent valid Roblox font families from being patched");
        string emptyBuild = Path.Combine(Paths.Cache, "empty-font-build");
        Directory.CreateDirectory(Path.Combine(emptyBuild, "content", "fonts", "families"));
        check(AppearanceFont.CreateFiles(emptyBuild, storedFont, new Dictionary<string, string>()).Count == 0,
            "No unused custom font asset is installed when the Roblox build contains no usable family");
        var fontSettings = App.Settings.Prop;
        App.Settings.Prop = new Settings();
        FontManager.SetCustomFont(storedFont);
        string savedSettings = File.ReadAllText(App.Settings.FileLocation);
        var settingsAttributes = File.GetAttributes(App.Settings.FileLocation);
        bool rejectedReset = false;
        try
        {
            File.SetAttributes(App.Settings.FileLocation, settingsAttributes | FileAttributes.ReadOnly);
            try { FontManager.RemoveCustomFont(); } catch (IOException) { rejectedReset = true; }
            check(rejectedReset && App.Settings.Prop.CustomFontPath == storedFont && FontManager.IsCustomFontApplied &&
                File.ReadAllText(App.Settings.FileLocation) == savedSettings,
                "Failed font reset retains the applied font and original saved setting");
        }
        finally { File.SetAttributes(App.Settings.FileLocation, settingsAttributes); }
        FontManager.RemoveCustomFont();
        check(App.Settings.Prop.CustomFontPath is null && !FontManager.IsCustomFontApplied,
            "Successful font reset restores the app default and removes the next-launch Roblox override");
        App.Settings.Prop = fontSettings;
        check(settings.PauseRobloxUpdates && settings.AutomaticRegionalPreference && settings.CompetitivePreferredCity.Length == 0 && settings.CompetitiveFallbackCities.Count == 0,
            "Fresh installs pause updates and learn regions without a seeded city");
        new NetworkTestResult { DirectCountry = "GB" }.Apply(settings);
        check(settings.PreferEuropeOnly && !settings.PreferNorthAmericaOnly, "A normal UK connection enables Prefer EU automatically");
        new NetworkTestResult { DirectCountry = "US" }.Apply(settings);
        check(settings.PreferNorthAmericaOnly && !settings.PreferEuropeOnly, "A normal US connection enables Prefer NA automatically");
        new NetworkTestResult { DirectCountry = "SG" }.Apply(settings);
        check(!settings.PreferNorthAmericaOnly && !settings.PreferEuropeOnly, "Players elsewhere are not restricted to NA or EU");
        settings.AutomaticRegionalPreference = false; settings.PreferEuropeOnly = true;
        new NetworkTestResult { DirectCountry = "US" }.Apply(settings);
        check(settings.PreferEuropeOnly, "Follow-up tests preserve a manual regional filter");
        App.Settings.Prop = new Settings { PreferEuropeOnly = true, CompetitiveModeEnabled = true, AdaptiveRegionPreferencesEnabled = true };
        check(CompetitiveRegionService.Classify("London, United Kingdom").Quality == RegionQuality.Acceptable,
            "EU players are not warned about an unlisted EU server");
        check(CompetitiveRegionService.Classify("Toronto, Canada").Quality == RegionQuality.Bad,
            "A known NA server outside the EU filter is classified as bad");
        check(CompetitiveRegionService.Classify("Unknown").Quality == RegionQuality.Unknown,
            "An unresolved server never becomes a bad-region warning");
        App.Settings.Save();
        var now = DateTimeOffset.UtcNow;
        var measured = new[] {
            new RegionObservation { Timestamp = now, City = "London", Country = "GB", Route = "fixture-eu", AverageMs = 25, IsCalibration = true },
            new RegionObservation { Timestamp = now, City = "Paris", Country = "FR", Route = "fixture-eu", AverageMs = 35, IsCalibration = true },
            new RegionObservation { Timestamp = now, City = "Toronto", Country = "CA", Route = "fixture-eu", AverageMs = 5, IsCalibration = true },
            new RegionObservation { Timestamp = now, City = "Wrong Route", Country = "GB", Route = "fixture-other", AverageMs = 1, IsCalibration = true }
        };
        AdaptiveRegionService.RecordAsync(measured, "fixture-eu", CancellationToken.None).GetAwaiter().GetResult();
        check(App.Settings.Prop.CompetitivePreferredCity == "London" && App.Settings.Prop.CompetitiveFallbackCities.SequenceEqual(new[] { "Paris" }),
            "Installation measurements populate preferred and fallback cities for the final route and selected area");
        App.Settings.Prop = new Settings { PreferNorthAmericaOnly = true };
        App.Settings.Save();
        AdaptiveRegionService.RecordAsync(new[] {
            new RegionObservation { City = "New York", Country = "US", Route = "direct", AverageMs = 65, IsCalibration = true },
            new RegionObservation { City = "Los Angeles", Country = "US", Route = "direct", AverageMs = 80, IsCalibration = true }
        }, "direct", CancellationToken.None).GetAwaiter().GetResult();
        for (int i = 0; i < 3; i++) AdaptiveRegionService.ObserveAsync(new CompetitiveNetworkEvent {
            Timestamp = DateTime.Now, Location = "Santiago de Querétaro, Mexico", Cloudflare = new CloudflareTraceResult { Warp = "off" },
            Latency = new NetworkLatencyMeasurement { Sent = 6, Received = 6, AverageMs = 25, JitterMs = 2 }
        }, CancellationToken.None).GetAwaiter().GetResult();
        check(App.Settings.Prop.CompetitivePreferredCity == "Santiago de Querétaro" && App.Settings.Prop.CompetitiveFallbackCities.Contains("New York"),
            "A faster Mexico region learned from joins outranks New York and California under Prefer NA");
        var links = new { data = Enumerable.Range(1, 30).Select(i => new { ix_id = i, status = "ok", operational = true, ipaddr4 = "192.0.2." + i }) };
        var exchanges = new { data = Enumerable.Range(1, 30).Select(i => new { id = i, city = i == 30 ? "Santiago de Querétaro" : "Fixture " + i, country = i == 30 ? "MX" : "US" }) };
        var discovered = RoutingTargetDiscovery.Parse(System.Text.Json.JsonSerializer.Serialize(links), System.Text.Json.JsonSerializer.Serialize(exchanges));
        check(discovered.Count == 30 && discovered.Any(x => x.Country == "MX"), "Discovery includes all published locations, including Mexico beyond the old 24-city cutoff");
        var expandedLinks = new { data = Enumerable.Range(1, 5).Select(i => new { ix_id = 1, status = "ok", operational = true, ipaddr4 = "192.0.2." + i, ipaddr6 = "2001:db8::" + i }) };
        discovered = RoutingTargetDiscovery.Parse(System.Text.Json.JsonSerializer.Serialize(expandedLinks), System.Text.Json.JsonSerializer.Serialize(exchanges));
        check(discovered.Count == 10 && discovered.Count(x => x.Address.Contains(':')) == 5,
            "Every published interface in the same city remains eligible, including IPv6 beyond the old two-address cap");
        const string installed = "version-0123456789abcdef";
        settings = new Settings();
        check(RobloxUpdatePolicy.RequestedVersion(true, null, settings, installed, true) == installed, "Paused updates retain the installed Player");
        check(RobloxUpdatePolicy.RequestedVersion(true, null, settings, installed, false) is null, "A fresh install still fetches a current Player");
        settings.PauseRobloxUpdates = false;
        check(RobloxUpdatePolicy.RequestedVersion(true, null, settings, installed, true) is null, "Unpausing allows the latest version check");
        settings.PauseRobloxUpdates = true;
        check(RobloxUpdatePolicy.RequestedVersion(false, null, settings, installed, true) is null, "Player update pausing does not pin Studio");
        check(WeaoDowngradeSource.ParsePrevious("{\"Windows\":\"0123456789abcdef\"}") == installed && WeaoDowngradeSource.DownloadLink(installed).Contains("rdd.weao.gg/?binaryType=WindowsPlayer&channel=LIVE&version="),
            "RDD previous versions are normalized and linked to the selected Windows Player build");
        bool invalid = false;
        try { WeaoDowngradeSource.ParsePrevious("{\"Windows\":\"../malicious\"}"); } catch (InvalidDataException) { invalid = true; }
        check(invalid, "Invalid downgrade catalog values cannot become deployment paths");
        check(!MultiInstanceLifetime.ShouldStop(TimeSpan.FromSeconds(5), false, 0, false) &&
              !MultiInstanceLifetime.ShouldStop(TimeSpan.FromSeconds(30), false, 0, true) &&
              !MultiInstanceLifetime.ShouldStop(TimeSpan.FromSeconds(30), true, 2, false) &&
              MultiInstanceLifetime.ShouldStop(TimeSpan.FromSeconds(30), true, 0, false),
            "The multi-client helper waits for startup, stays through launches and multiple players, and exits after the last player");
        string name = "DepthStrap-Fixture-" + Guid.NewGuid().ToString("N");
        using (var owner = MultiInstanceWatcher.Acquire(name))
        {
            bool rejected = Task.Run(() => { try { using var second = MultiInstanceWatcher.Acquire(name); second.ReleaseMutex(); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult();
            check(rejected, "The helper refuses a mutex owned by another launch thread");
            owner.ReleaseMutex();
        }
        using (var incompatible = new EventWaitHandle(false, EventResetMode.ManualReset, name))
        {
            bool rejected = false;
            try { using var mutex = MultiInstanceWatcher.Acquire(name); mutex.ReleaseMutex(); } catch (WaitHandleCannotBeOpenedException) { rejected = true; }
            check(rejected, "A preexisting Roblox-style event collision fails instead of claiming multi-client readiness");
        }
        using (var owner = MultiInstanceWatcher.Acquire(name))
        {
            using var shared = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            bool sharedBeforeRelease = false, ownedAfterRelease = false;
            var borrower = Task.Run(() =>
            {
                using var reservation = new MultiInstanceWatcher.Reservation(name);
                sharedBeforeRelease = !reservation.Owned;
                shared.Set();
                if (!released.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                reservation.TryOwn();
                ownedAfterRelease = reservation.Owned;
            });
            if (!shared.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            owner.ReleaseMutex(); released.Set(); borrower.GetAwaiter().GetResult();
            check(sharedBeforeRelease && ownedAfterRelease,
                "Multi-client reservations cooperate with another launcher's owner and acquire ownership after it releases");
        }
        using (var incompatible = new EventWaitHandle(false, EventResetMode.ManualReset, name))
        {
            bool rejected = false;
            try { using var reservation = new MultiInstanceWatcher.Reservation(name); } catch (WaitHandleCannotBeOpenedException) { rejected = true; }
            check(rejected, "Cooperative reservations still reject incompatible event objects");
        }
        Task.Run(async () =>
        {
            int polls = 0;
            check(await MultiInstanceWatcher.WaitForReadyAsync(() => ++polls >= 4, () => false, () => false, TimeSpan.FromSeconds(2), CancellationToken.None),
                "Helper startup waits for delayed readiness");
            check(!await MultiInstanceWatcher.WaitForReadyAsync(() => false, () => true, () => false, TimeSpan.FromSeconds(2), CancellationToken.None),
                "Explicit helper failure returns promptly");
            check(!await MultiInstanceWatcher.WaitForReadyAsync(() => false, () => false, () => true, TimeSpan.FromSeconds(2), CancellationToken.None),
                "An exited helper does not become a generic timeout");
            check(!await MultiInstanceWatcher.WaitForReadyAsync(() => false, () => false, () => false, TimeSpan.Zero, CancellationToken.None),
                "Helper startup has a bounded timeout");
            bool cancelled = false;
            try { await MultiInstanceWatcher.WaitForReadyAsync(() => false, () => false, () => false, TimeSpan.FromSeconds(2), new CancellationToken(true)); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Helper startup observes launch cancellation");
        }).GetAwaiter().GetResult();
        App.Settings.Prop.WarnOnBadChimeRegion = false; App.Settings.Prop.LogCompetitiveSessions = true;
        var bad = new CompetitiveRegionResult { Timestamp = DateTime.Now, IsDeepwoken = true, JobId = "bad-fixture", Location = "Toronto, Canada", Quality = RegionQuality.Bad };
        CompetitiveSessionLogger.WriteBadRegionAsync(bad).GetAwaiter().GetResult();
        check(File.ReadAllLines(CompetitiveSessionLogger.GetJsonlPath()).Any(x => x.Contains("bad_region") && x.Contains("bad-fixture")),
            "Bad-region joins automatically log even when popups are turned off");
        check(CompetitiveRegionMonitor.BuildNotification(bad) is not null && CompetitiveRegionMonitor.BuildNotification(new CompetitiveRegionResult { IsDeepwoken = true, Quality = RegionQuality.Unknown }) is null,
            "Known bad joins generate warnings while unknown initial joins do not");
        var autolog = new Settings { CompetitiveModeEnabled = true, AutoLeaveBadChimeRegion = true, PreferNorthAmericaOnly = true,
            CompetitivePreferredCity = "Dallas", CompetitiveFallbackCities = new() { "Santiago de Querétaro" } };
        var joined = new CompetitiveNetworkEvent { Timestamp = DateTime.Now, UniverseId = CompetitiveRegionService.DeepwokenUniverseId,
            PlaceId = 999001, IsDeepwoken = true, JobId = "current", RegionSource = "Roblox DataCenterId", Location = "London, United Kingdom" };
        var outside = new RegionClassification { Quality = RegionQuality.Bad };
        check(BadRegionAutoLog.ShouldLeave(joined, autolog, "current", DateTime.Now, outside), "A confirmed NA-to-EU Deepwoken join qualifies for opt-in autolog");
        check(!BadRegionAutoLog.ShouldLeave(joined with { PlaceId = BadRegionAutoLog.EntryPlaceId }, autolog, "current", DateTime.Now, outside),
            "Deepwoken's entry menu cannot trigger a close/rejoin loop");
        check(!BadRegionAutoLog.ShouldLeave(joined, autolog, "current", DateTime.Now, outside, recoveryClient: true),
            "A recovered client remains playable even if matchmaking returns another bad region");
        check(!BadRegionAutoLog.ShouldLeave(joined, autolog, "current", DateTime.Now, new RegionClassification { Quality = RegionQuality.Acceptable }) &&
              !BadRegionAutoLog.ShouldLeave(joined, autolog, "current", DateTime.Now, new RegionClassification { Quality = RegionQuality.Good }),
            "An unmeasured city inside the preferred area cannot trigger autolog just because it is absent from fallbacks");
        autolog.PreferNorthAmericaOnly = false; autolog.PreferEuropeOnly = true;
        check(BadRegionAutoLog.ShouldLeave(joined with { Location = "Singapore" }, autolog, "current", DateTime.Now, outside), "An EU-to-Asia Deepwoken join qualifies for autolog");
        check(!BadRegionAutoLog.ShouldLeave(joined, autolog, "current", DateTime.Now, new RegionClassification { Quality = RegionQuality.Good, IsConfiguredRegion = true }),
            "Primary and fallback regions are retained");
        check(!BadRegionAutoLog.ShouldLeave(joined, autolog, "current", DateTime.Now, new RegionClassification { Quality = RegionQuality.Unknown }) &&
              !BadRegionAutoLog.ShouldLeave(joined with { RegionSource = "Unknown" }, autolog, "current", DateTime.Now, outside), "Unknown locations never trigger autolog");
        check(!BadRegionAutoLog.ShouldLeave(joined, autolog, "new-job", DateTime.Now, outside) &&
              !BadRegionAutoLog.ShouldLeave(joined with { Timestamp = DateTime.Now.AddMinutes(-2) }, autolog, "current", DateTime.Now, outside), "Old jobs and stale diagnostics cannot close the current session");
        DateTime decisionTime = DateTime.Now;
        var delayed = joined with { Timestamp = decisionTime.AddMinutes(-2) };
        check(BadRegionAutoLog.ShouldLeave(delayed, autolog, "current", decisionTime, outside,
                currentJoinStartedAt: decisionTime.AddMinutes(-3)),
            "A slow region lookup can autolog while its confirmed original join is still active");
        check(!BadRegionAutoLog.ShouldLeave(delayed, autolog, "current", decisionTime, outside,
                currentJoinStartedAt: decisionTime.AddMinutes(-1)) &&
              !BadRegionAutoLog.ShouldLeave(delayed, autolog, "current", decisionTime, outside, true,
                decisionTime.AddMinutes(-3)),
            "A delayed result from a previous join or recovery client cannot close the current game");
        check(!BadRegionAutoLog.ShouldLeave(joined with { IsDeepwoken = false, UniverseId = 123 }, autolog, "current", DateTime.Now, outside) &&
              !BadRegionAutoLog.ShouldRejoin(joined with { UniverseId = 123 }, autolog), "Another game's bad server can neither autolog nor launch Deepwoken");
        check(new long[] { 999002, 999003, 999004, 999005 }.All(id =>
                BadRegionAutoLog.ShouldLeave(joined with { PlaceId = id, IsTeleport = true, IsReservedServer = true }, autolog, "current", DateTime.Now, outside) &&
                BadRegionAutoLog.ShouldRejoin(joined with { PlaceId = id }, autolog)), "Reserved Deepwoken subplaces are recognized by universe without maintaining a list of Layer/Chime place IDs");
        autolog.AutoLeaveBadChimeRegion = false;
        check(!BadRegionAutoLog.ShouldRejoin(joined, autolog), "The single autolog toggle also disables Deepwoken rejoin");
        check(!BadRegionAutoLog.ShouldLeave(joined, autolog, "current", DateTime.Now, outside), "Autolog is opt-in and respects the disabled setting");
        var homeLaunch = AutoLogHomeHandoff.Launch(BadRegionAutoLog.HomeUri, installed, Guid.NewGuid().ToString("N"));
        var parsed = new LaunchSettings(homeLaunch.ArgumentList.ToArray());
        check(parsed.RobloxLaunchArgs == "roblox://navigation/home" && parsed.RobloxLaunchMode == Bloxstrap.Enums.LaunchMode.Player && parsed.VersionFlag.Data == installed,
            "Home handoff uses the native Home URI and exact installed Player build, avoiding an update during multi-client handoff");
        var rejoinLaunch = AutoLogHomeHandoff.Launch(BadRegionAutoLog.RejoinUri, installed);
        check(new LaunchSettings(rejoinLaunch.ArgumentList.ToArray()).AutoLogHomeFlag.Active &&
              JsonSerializer.Deserialize<WatcherData>(JsonSerializer.Serialize(new WatcherData { AutoLogRecovery = true }))!.AutoLogRecovery,
            "The recovery marker survives the automatic launcher and watcher process handoff");
        check(RobloxLaunchUri.TryParse(rejoinLaunch.ArgumentList[0])?.PlaceId == 4111023553 && !rejoinLaunch.ArgumentList[0].Contains("gameInstanceId"),
            "Rejoin returns to Deepwoken entry matchmaking rather than a reserved subplace or the same bad server");
        check(BadRegionAutoLog.IsIdleHomeLog("[FLog::Output] Home loaded") &&
              !BadRegionAutoLog.IsIdleHomeLog("[FLog::Output] ! Joining game 'fixture' place 123 at 192.0.2.1") &&
              !BadRegionAutoLog.IsIdleHomeLog("[FLog::GameJoinUtil] GameJoinUtil::initiateTeleportToReservedServer"),
            "A new public or reserved game join on Home cancels the queued Deepwoken rejoin");
        check(!AutoLogHomeHandoff.RetryAllowed(new[] { DateTime.Now }, DateTime.Now) &&
              AutoLogHomeHandoff.RetryAllowed(Enumerable.Repeat(DateTime.Now.AddMinutes(-11), 3), DateTime.Now), "One recovery per ten minutes prevents repeated autolog/rejoin loops");
        var reservations = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(AutoLogHomeHandoff.ReserveRetry))).GetAwaiter().GetResult();
        check(reservations.Count(x => x) == 1 && !AutoLogHomeHandoff.ReserveRetry(),
            "Only one concurrent client can reserve a recovery; persisted cooldown prevents a later launcher from closing Roblox again");
        File.WriteAllText(Path.Combine(Paths.Cache, "AutoLogRetries.json"), JsonSerializer.Serialize(new[] { DateTime.Now.AddMinutes(-11) }));
        check(AutoLogHomeHandoff.ReserveRetry(), "A manual launch can recover again after the shared cooldown expires");
        App.Settings.Prop = new Settings { CompetitivePreferredCity = "Old UI choice", PreferNorthAmericaOnly = true };
        var learnedDisk = new JsonManager<Settings>();
        learnedDisk.Prop = new Settings { CompetitivePreferredCity = "Santiago de Querétaro", CompetitiveFallbackCities = new() { "Dallas" }, PreferNorthAmericaOnly = true };
        learnedDisk.Save();
        AdaptiveRegionService.SaveUserSettings();
        check(App.Settings.Prop.CompetitivePreferredCity == "Santiago de Querétaro" && App.Settings.Prop.CompetitiveFallbackCities.SequenceEqual(new[] { "Dallas" }),
            "Saving a stale settings window preserves newly learned cities from the watcher");
        App.Settings.Prop.AdaptiveRegionPreferencesEnabled = false; App.Settings.Prop.CompetitivePreferredCity = "Manual city";
        AdaptiveRegionService.SaveUserSettings(); learnedDisk.Load(false);
        check(learnedDisk.Prop.CompetitivePreferredCity == "Manual city", "Turning off learning allows a manual preference to be saved");
        App.Settings.Prop = new Settings();
    }
}
