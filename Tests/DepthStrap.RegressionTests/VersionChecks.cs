using System.IO;
using System.Net.Http;
using Bloxstrap;
using Bloxstrap.Models.Persistable;
using Bloxstrap.Roblox;
using Bloxstrap.UI.ViewModels.Settings;
using CommunityToolkit.Mvvm.Input;

internal static class VersionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var originalSettings = App.Settings.Prop;
        var originalPlayer = App.PlayerState.Prop;
        string playerFile = App.PlayerState.FileLocation;
        string? savedPlayer = File.Exists(playerFile) ? File.ReadAllText(playerFile) : null;
        string saved = File.ReadAllText(App.Settings.FileLocation);
        var attributes = File.GetAttributes(App.Settings.FileLocation);
        var presence = CompetitiveSettingsBackup.PlayerPresence;
        const string previous = "version-1111111111111111", target = "version-0123456789abcdef";
        int starts = 0, changes = 0;
        try
        {
            const string release = "0.741.0.7411058";
            check(RobloxClientVersion.ParseReleaseText("0, 742, 0, 7421053") == "0.742.0.7421053" &&
                RobloxClientVersion.ParseReleaseText("0.741.0.7411058") == release,
                "Installed Player version parsing preserves full revisions larger than 16-bit metadata fields");
            check(RobloxClientVersion.ParseReleaseText("0,742.0,7421053") is null &&
                RobloxClientVersion.ParseReleaseText("0.742.0.7421053-beta") is null && RobloxClientVersion.ParseReleaseText(null) is null,
                "Malformed or ambiguous file version text never becomes an installed release number");
            check(RobloxReleaseLookup.IsReleaseNumber(release) && !RobloxReleaseLookup.IsReleaseNumber("741") &&
                !RobloxReleaseLookup.IsReleaseNumber(release + "\n"), "Release lookup accepts only an exact four-part version");
            string catalog = "{\"Windows\":\"" + target + "\",\"WindowsResponse\":{\"version\":\"" + release + "\",\"clientVersionUpload\":\"" + target + "\"}}";
            check(RobloxReleaseLookup.ParseCatalog(catalog, release, true).Single() == target,
                "WEAO numeric lookup verifies the Windows response and its matching build hash");
            check(RobloxReleaseLookup.ReadCatalogRelease(catalog, true) == release &&
                RobloxReleaseLookup.ReadCatalogRelease("{\"MacResponse\":{\"version\":\"0.741.0.7411056\"}}", true) is null,
                "Current and previous release labels require a verified Windows Player catalog record");
            check(!RobloxReleaseLookup.ParseCatalog(catalog, "0.741.19.7411056", true).Any(), "Release lookup never chooses a nearby release");
            check(!RobloxReleaseLookup.ParseCatalog("[]", release, false).Any() &&
                !RobloxReleaseLookup.ParseCatalog("{\"WindowsResponse\":null}", release, true).Any(),
                "Unexpected catalog shapes cannot resolve a release or crash other catalog lookups");
            check(!RobloxReleaseLookup.ParseCatalog(catalog.Replace("\"Windows\":\"" + target, "\"Windows\":\"" + previous), release, true).Any(),
                "Inconsistent catalog hashes are rejected");
            check(WeaoDowngradeSource.ParsePrevious(catalog) == target, "Previous-build download verifies consistent release metadata");
            bool inconsistentPrevious = false;
            try { WeaoDowngradeSource.ParsePrevious(catalog.Replace("\"Windows\":\"" + target, "\"Windows\":\"" + previous)); }
            catch (InvalidDataException) { inconsistentPrevious = true; }
            check(inconsistentPrevious, "Previous-build download rejects conflicting hashes instead of installing an unintended release");
            check(!RobloxReleaseLookup.ParseCatalog("{\"MacResponse\":{\"version\":\"" + release + "\",\"clientVersionUpload\":\"" + target + "\"}}", release, true).Any(),
                "A Mac release cannot resolve a Windows Player input");
            string history = "New Studio64 " + previous + " at fixture, file version: 0, 741, 0, 7411058, git hash: fixture ...\n" +
                "New WindowsPlayer " + target + " at fixture, file version: 0, 741, 0, 7411058, git hash: fixture ...\n" +
                "New WindowsPlayer version-hidden at fixture, file version: 0, 741, 0, 7411058, git hash: fixture ...";
            check(RobloxReleaseLookup.ParseHistory(history, release).SequenceEqual(new[] { target }),
                "History lookup excludes Studio and hidden hashes while matching the entire Player version");
            var releaseCatalog = RobloxReleaseLookup.ParseReleaseHistory(history + "\nNew Client " + previous +
                " at fixture, file version: 0, 742, 0, 7420001, git hash: fixture ...");
            check(releaseCatalog.SequenceEqual(new[] { release, "0.742.0.7420001" }),
                "Autocomplete catalog contains Windows Player and legacy Client releases only");
            check(RobloxReleaseLookup.ParseReleaseHistory("New WindowsPlayer version-hidden at fixture, file version: 0, 742, 0, 7421053, git hash: fixture ...\n" +
                "New Studio64 version-hidden at fixture, file version: 0, 742, 19, 7429999, git hash: fixture ...")
                .SequenceEqual(new[] { "0.742.0.7421053" }),
                "Published Player release numbers remain discoverable when Roblox hides hashes; Studio releases stay excluded");
            check(RobloxReleaseLookup.MatchReleases(releaseCatalog, "741").SequenceEqual(new[] { release }) &&
                RobloxReleaseLookup.MatchReleases(releaseCatalog, "742").SequenceEqual(new[] { "0.742.0.7420001" }),
                "Short release input offers only matching release families");
            check(RobloxReleaseLookup.MatchReleases(new[] { release, "0.741.0.7411059", release }, "0.741.")
                    .SequenceEqual(new[] { "0.741.0.7411059", release }),
                "Autocomplete handles dotted prefixes, removes duplicates and sorts newest first");
            check(!RobloxReleaseLookup.MatchReleases(releaseCatalog, release).Any() &&
                !RobloxReleaseLookup.MatchReleases(releaseCatalog, "version-").Any() &&
                !RobloxReleaseLookup.MatchReleases(releaseCatalog, "7").Any(),
                "Complete releases, build hashes and broad one-digit input do not reopen suggestions");
            check(RobloxReleaseLookup.SelectExact(new[] { target, target.ToUpperInvariant() }) == target,
                "Duplicate reports of the same build do not create an ambiguous match");
            bool ambiguous = false, missing = false;
            try { RobloxReleaseLookup.SelectExact(new[] { target, previous }); } catch (InvalidDataException) { ambiguous = true; }
            try { RobloxReleaseLookup.SelectExact(Array.Empty<string>()); } catch (InvalidDataException) { missing = true; }
            check(ambiguous && missing, "Missing and ambiguous releases require a specific build rather than silently substituting one");
            var installer = RobloxVersionArchiveViewModel.InstallerStartInfo();
            var installerArgs = new LaunchSettings(installer.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            check(installerArgs.NoLaunchFlag.Active && installerArgs.ForceFlag.Active &&
                installerArgs.RobloxLaunchMode == Bloxstrap.Enums.LaunchMode.Player && !installer.UseShellExecute,
                "Version installation uses install-only Player arguments and cannot implicitly launch Roblox");
            check(RobloxUpdatePolicy.RequestedVersion(true, null, new Settings { PauseRobloxUpdates = true }, target,
                installedExecutableExists: false, installationPending: true) == target,
                "Repairing an interrupted paused installation retains the selected Roblox build even when its executable is missing");
            CompetitiveSettingsBackup.PlayerPresence = () => false;
            var suggestionVm = new RobloxVersionArchiveViewModel { RunInstaller = () => { starts++; return Task.FromResult(0); } };
            suggestionVm.ReleaseLabels = () => Task.FromResult<(string?, string?)>(("0.742.0.7421053", release));
            HistoryChecks.Wait(suggestionVm.LoadReleaseLabelsAsync());
            check(suggestionVm.ReleaseSummary.Contains("Current Roblox: 742 (0.742.0.7421053)") &&
                suggestionVm.DowngradeLabel == "Downgrade to 741 (0.741.0.7411058)",
                "The version section labels the live current release and exact previous downgrade release");
            suggestionVm.ReleaseLabels = () => Task.FromException<(string?, string?)>(new IOException("Fixture offline"));
            HistoryChecks.Wait(suggestionVm.LoadReleaseLabelsAsync());
            check(suggestionVm.ReleaseSummary.Contains("unavailable") && suggestionVm.DowngradeLabel == "Downgrade from WEAO RDD",
                "Unavailable release labels never display a guessed or hard-coded version");
            int installsBeforeSuggestions = starts;
            string settingsBeforeSuggestions = File.ReadAllText(App.Settings.FileLocation);
            suggestionVm.ReleaseCatalog = () => Task.FromResult(releaseCatalog);
            suggestionVm.VersionId = "741";
            HistoryChecks.Wait(suggestionVm.UpdateSuggestionsAsync());
            check(suggestionVm.SuggestionsOpen && suggestionVm.ReleaseSuggestions.SequenceEqual(new[] { release }),
                "Typing a release family opens its dropdown after lookup");
            suggestionVm.VersionId = release;
            check(!suggestionVm.SuggestionsOpen && starts == installsBeforeSuggestions &&
                File.ReadAllText(App.Settings.FileLocation) == settingsBeforeSuggestions,
                "Selecting a suggested release closes the dropdown without installing or saving a pin");
            var slowCatalog = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            suggestionVm.ReleaseCatalog = () => slowCatalog.Task;
            suggestionVm.VersionId = "741";
            Task staleSuggestions = suggestionVm.UpdateSuggestionsAsync();
            HistoryChecks.Wait(Task.Delay(300));
            suggestionVm.ReleaseCatalog = () => Task.FromResult(releaseCatalog);
            suggestionVm.VersionId = "742";
            HistoryChecks.Wait(suggestionVm.UpdateSuggestionsAsync());
            slowCatalog.SetResult(new[] { release });
            HistoryChecks.Wait(staleSuggestions);
            check(suggestionVm.VersionId == "742" && suggestionVm.ReleaseSuggestions.SequenceEqual(new[] { "0.742.0.7420001" }),
                "A delayed autocomplete response cannot replace results for newer input");
            suggestionVm.ReleaseCatalog = () => Task.FromException<string[]>(new HttpRequestException("Fixture offline"));
            suggestionVm.VersionId = "743";
            HistoryChecks.Wait(suggestionVm.UpdateSuggestionsAsync());
            check(!suggestionVm.SuggestionsOpen && suggestionVm.CanEditVersion && suggestionVm.SuggestionStatus.Contains("Could not load") &&
                starts == installsBeforeSuggestions, "Offline autocomplete reports failure while manual entry remains available");
            suggestionVm.VersionId = "";
            App.Settings.Prop = new Settings { RobloxPlayerVersionOverride = previous, PauseRobloxUpdates = false,
                UpdateRoblox = false, StaticDirectory = true };
            check(RobloxVersionArchiveViewModel.InstalledPlayerExecutable(target) == Path.Combine(Paths.Versions, "WindowsPlayer", App.RobloxPlayerAppName),
                "Installed release metadata uses the active static Player directory when that layout is selected");
            App.Settings.TrySave();
            string previousFile = File.ReadAllText(App.Settings.FileLocation);
            var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var vm = new RobloxVersionArchiveViewModel { RunInstaller = () => { starts++; return finished.Task; }, UpdatePolicyChanged = () => changes++ };
            check(ReferenceEquals(vm.DowngradePreviousCommand, vm.DowngradePreviousCommand),
                "Rollback reuses its async command so concurrent executions share one running state");
            vm.VersionId = target;
            File.SetAttributes(App.Settings.FileLocation, attributes | FileAttributes.ReadOnly);
            vm.InstallVersionCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!);
            check(starts == 0 && changes == 0 && App.Settings.Prop.RobloxPlayerVersionOverride == previous &&
                !App.Settings.Prop.PauseRobloxUpdates && App.Settings.Prop.StaticDirectory && File.ReadAllText(App.Settings.FileLocation) == previousFile,
                "Failed version selection save restores every owned setting and cannot launch an installer");
            vm.UseLatestCommand.Execute(null);
            check(App.Settings.Prop.RobloxPlayerVersionOverride == previous && !App.Settings.Prop.UpdateRoblox &&
                !App.Settings.Prop.PauseRobloxUpdates && changes == 0,
                "Use latest cannot report or apply a successful policy change when saving fails");
            File.SetAttributes(App.Settings.FileLocation, attributes);
            vm.InstallVersionCommand.Execute(null);
            Task running = ((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!;
            check(starts == 1 && changes == 1 && !vm.CanEditVersion && !vm.InstallVersionCommand.CanExecute(null) &&
                !vm.UseLatestCommand.CanExecute(null) && !vm.DowngradePreviousCommand.CanExecute(null) &&
                App.Settings.Prop.RobloxPlayerVersionOverride == target && App.Settings.Prop.PauseRobloxUpdates && !App.Settings.Prop.StaticDirectory,
                "A saved rollback launches one installer and blocks conflicting policy changes until that helper finishes");
            vm.VersionId = previous;
            check(vm.VersionId == target, "The selected build cannot change underneath an in-flight installation");
            App.PlayerState.Prop = new DistributionState { VersionGuid = target };
            App.PlayerState.TrySave();
            finished.TrySetResult(0); HistoryChecks.Wait(running);
            check(vm.CanEditVersion && vm.Status.Contains("Installer finished") && vm.InstalledBuild == target && vm.InstallVersionCommand.CanExecute(null),
                "Installer completion reloads the actual saved build, confirms its executable and releases rollback controls");
            var installedReader = vm.InstalledReleaseReader;
            vm.InstalledReleaseReader = _ => release;
            check(vm.InstalledBuild == "741 (0.741.0.7411058)\n" + target,
                "Installed-build display shows the actual Player release family and full number alongside its hash");
            vm.InstalledReleaseReader = installedReader;
            vm.UseLatestCommand.Execute(null);
            check(vm.VersionId == "" && App.Settings.Prop.RobloxPlayerVersionOverride == "" && !App.Settings.Prop.PauseRobloxUpdates &&
                App.Settings.Prop.UpdateRoblox && changes == 2,
                "Use latest persists and reports the unpinned update policy only after save success");

            var lookup = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken lookupToken = default;
            vm.PreviousLookup = token => { lookupToken = token; return lookup.Task; };
            vm.VersionId = previous;
            vm.DowngradePreviousCommand.Execute(null);
            Task lookupTask = ((IAsyncRelayCommand)vm.DowngradePreviousCommand).ExecutionTask!;
            vm.CancelLookupCommand.Execute(null);
            lookup.TrySetResult(target); HistoryChecks.Wait(lookupTask);
            check(lookupToken.IsCancellationRequested && vm.CanEditVersion && vm.VersionId == previous &&
                App.Settings.Prop.RobloxPlayerVersionOverride == "" && starts == 1 && vm.Status.Contains("cancelled"),
                "Cancelling a delayed WEAO response preserves the current selection and cannot install its eventual result");
            int lookups = 0;
            CompetitiveSettingsBackup.PlayerPresence = () => true;
            vm.PreviousLookup = _ => { lookups++; return Task.FromResult(target); };
            vm.DowngradePreviousCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.DowngradePreviousCommand).ExecutionTask!);
            check(lookups == 0 && starts == 1 && vm.Status.Contains("Close all"),
                "An active Roblox client blocks downgrade before contacting WEAO or saving a new pin");
            CompetitiveSettingsBackup.PlayerPresence = () => false;
            vm.VersionId = "invalid";
            vm.InstallVersionCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!);
            check(starts == 1 && App.Settings.Prop.RobloxPlayerVersionOverride == "", "Malformed version IDs cannot save or launch installation");
            string beforeNumericInput = File.ReadAllText(App.Settings.FileLocation);
            vm.ReleaseLookup = (_, _) => Task.FromException<string>(new InvalidDataException("No exact Windows Player build hash was found."));
            vm.VersionId = " 0.741.19.7411056 ";
            vm.InstallVersionCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!);
            check(starts == 1 && vm.CanEditVersion && vm.Status.Contains("No exact Windows Player") &&
                File.ReadAllText(App.Settings.FileLocation) == beforeNumericInput,
                "An unresolved numeric release does not start installation or change saved settings");
            vm.PreviousLookup = _ => Task.FromException<string>(new IOException("Fixture lookup failure"));
            vm.DowngradePreviousCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.DowngradePreviousCommand).ExecutionTask!);
            check(vm.CanEditVersion && App.Settings.Prop.RobloxPlayerVersionOverride == "" && starts == 1 && vm.Status.Contains("failed"),
                "Lookup failure releases controls and preserves the selected Roblox update policy");
            vm.PreviousLookup = _ => Task.FromResult(target);
            vm.RunInstaller = () => { starts++; return Task.FromResult(23); };
            vm.DowngradePreviousCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.DowngradePreviousCommand).ExecutionTask!);
            check(starts == 2 && vm.CanEditVersion && vm.Status.Contains("code 23") && App.Settings.Prop.RobloxPlayerVersionOverride == target,
                "Installer failure is reported without losing the user's saved retryable version selection");
            vm.RunInstaller = () => Task.FromResult(0);
            vm.VersionId = previous;
            vm.InstallVersionCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!);
            check(vm.Status.Contains("could not be confirmed") && vm.InstalledBuild == target,
                "A helper's zero exit code cannot claim an installation completed when a different build remains on disk");
            App.PlayerState.Prop = new DistributionState { VersionGuid = target, InstallationPending = true };
            App.PlayerState.TrySave();
            vm.VersionId = target;
            vm.InstallVersionCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!);
            check(vm.Status.Contains("could not be confirmed") && vm.InstalledBuild.Contains("incomplete"),
                "An incomplete install is not reported as successfully installed merely because its executable exists");
            var choices = vm.CachedVersions.ToArray();
            File.WriteAllText(playerFile, "{incomplete");
            vm.RefreshVersionsCommand.Execute(null);
            check(vm.InstalledBuild == "Unavailable" && vm.CachedVersions.SequenceEqual(choices) && vm.Status.Contains("Could not refresh"),
                "A corrupt install-state refresh preserves build choices and reports failure without crashing settings");
            App.PlayerState.Prop = new DistributionState { VersionGuid = target }; App.PlayerState.TrySave();
            vm.RefreshVersionsCommand.Execute(null);
            check(vm.InstalledBuild == target, "Refresh builds recovers the current build after its state file is repaired");
            int beforeReleaseInstall = starts;
            vm.RunInstaller = () => { starts++; return Task.FromResult(0); };
            vm.ReleaseLookup = (number, _) => { check(number == release, "Numeric input is trimmed before lookup"); return Task.FromResult(target); };
            vm.VersionId = " " + release + " ";
            vm.InstallVersionCommand.Execute(null);
            HistoryChecks.Wait(((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!);
            check(starts == beforeReleaseInstall + 1 && vm.VersionId == target && App.Settings.Prop.RobloxPlayerVersionOverride == target,
                "A resolved numeric release installs and persists its exact hash through the normal installer path");
            var delayedRelease = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken releaseToken = default;
            vm.ReleaseLookup = (_, token) => { releaseToken = token; return delayedRelease.Task; };
            vm.VersionId = release;
            vm.InstallVersionCommand.Execute(null);
            var releaseTask = ((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!;
            check(!vm.CanEditVersion && vm.CancelLookupCommand.CanExecute(null), "Delayed numeric lookup disables edits and offers cancellation");
            vm.CancelLookupCommand.Execute(null);
            delayedRelease.SetResult(previous); HistoryChecks.Wait(releaseTask);
            check(releaseToken.IsCancellationRequested && vm.CanEditVersion && vm.VersionId == release && starts == beforeReleaseInstall + 1 &&
                App.Settings.Prop.RobloxPlayerVersionOverride == target, "Cancelled numeric lookup cannot install a late response or change the saved pin");
            vm.ReleaseLookup = (_, _) => Task.FromResult("invalid");
            vm.InstallVersionCommand.Execute(null); HistoryChecks.Wait(((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!);
            check(starts == beforeReleaseInstall + 1 && vm.Status.Contains("invalid build hash"), "Invalid numeric lookup output never reaches installation");
            vm.ReleaseLookup = (_, _) => { CompetitiveSettingsBackup.PlayerPresence = () => true; return Task.FromResult(previous); };
            vm.InstallVersionCommand.Execute(null); HistoryChecks.Wait(((IAsyncRelayCommand)vm.InstallVersionCommand).ExecutionTask!);
            check(starts == beforeReleaseInstall + 1 && vm.Status.Contains("Close all") && App.Settings.Prop.RobloxPlayerVersionOverride == target,
                "Roblox opening during numeric lookup blocks installation before any new build selection is saved");
            CompetitiveSettingsBackup.PlayerPresence = () => false;
        }
        finally
        {
            File.SetAttributes(App.Settings.FileLocation, FileAttributes.Normal);
            File.WriteAllText(App.Settings.FileLocation, saved);
            File.SetAttributes(App.Settings.FileLocation, attributes);
            CompetitiveSettingsBackup.PlayerPresence = presence;
            App.Settings.Prop = originalSettings;
            if (savedPlayer is null) File.Delete(playerFile); else File.WriteAllText(playerFile, savedPlayer);
            App.PlayerState.Prop = originalPlayer;
        }
    }
}
