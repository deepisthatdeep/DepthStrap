using System.IO;
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
            var installer = RobloxVersionArchiveViewModel.InstallerStartInfo();
            var installerArgs = new LaunchSettings(installer.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            check(installerArgs.NoLaunchFlag.Active && installerArgs.ForceFlag.Active &&
                installerArgs.RobloxLaunchMode == Bloxstrap.Enums.LaunchMode.Player && !installer.UseShellExecute,
                "Version installation uses install-only Player arguments and cannot implicitly launch Roblox");
            check(RobloxUpdatePolicy.RequestedVersion(true, null, new Settings { PauseRobloxUpdates = true }, target,
                installedExecutableExists: false, installationPending: true) == target,
                "Repairing an interrupted paused installation retains the selected Roblox build even when its executable is missing");
            CompetitiveSettingsBackup.PlayerPresence = () => false;
            App.Settings.Prop = new Settings { RobloxPlayerVersionOverride = previous, PauseRobloxUpdates = false,
                UpdateRoblox = false, StaticDirectory = true };
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
