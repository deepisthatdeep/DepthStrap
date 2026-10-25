using System.IO;
using System.Net.Http;
using System.Net;
using Bloxstrap;
using Bloxstrap.Enums;
using Bloxstrap.Models.APIs.Roblox;
using Bloxstrap.Models.Persistable;
using Bloxstrap.RobloxInterfaces;
using Bloxstrap.UI.ViewModels.Settings;

internal static class SettingsInteractionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var original = App.Settings.Prop;
        try
        {
            App.Settings.Prop = new Settings();
            using var model = new MainWindowViewModel();
            check(!model.HasUnsavedSettingsChanges, "Opening settings starts with no unsaved editable changes");
            App.Settings.Prop.AutomaticDepthStrapUpdates = false;
            int confirmations = 0;
            check(!model.ConfirmDiscardChanges(() => { confirmations++; return false; }) && confirmations == 1,
                "Changing only automatic app updates requests confirmation and Cancel keeps the window open");
            check(model.ConfirmDiscardChanges(() => true), "Explicit discard permits closing with app-only changes");
            App.Settings.Prop.AutomaticDepthStrapUpdates = true;
            check(!model.HasUnsavedSettingsChanges, "Reverting an edit clears unsaved settings without saving");
            App.Settings.Prop.Theme = Theme.Blue;
            check(model.HasUnsavedSettingsChanges, "Changing only theme is an unsaved edit");
            check(App.Settings.TrySave() && !model.HasUnsavedSettingsChanges, "Successful settings save resets the editable baseline");

            App.Settings.Prop.Theme = Theme.Light;
            var attributes = File.GetAttributes(App.Settings.FileLocation);
            try
            {
                File.SetAttributes(App.Settings.FileLocation, attributes | FileAttributes.ReadOnly);
                check(!App.Settings.TrySave() && model.HasUnsavedSettingsChanges,
                    "Failed settings save retains the dirty state and its previous baseline");
            }
            finally { File.SetAttributes(App.Settings.FileLocation, attributes); }
            App.Settings.Prop.Theme = Theme.Blue;
            App.Settings.Prop.CompetitivePreferredCity = "Learned city";
            App.Settings.Prop.CompetitiveFallbackCities.Add("Learned fallback");
            App.Settings.Prop.RegionCalibrationCompleted = true;
            App.Settings.Prop.CompetitiveFpsCap = 144;
            App.Settings.Prop.PreferNorthAmericaOnly = true;
            App.Settings.Prop.NetworkSetupVersion++;
            App.Settings.Prop.IsNavigationSidebarExpanded = false;
            check(!model.HasUnsavedSettingsChanges, "Background learned regions, automatic FPS and layout state do not request discard confirmation");
            App.Settings.Prop.AdaptiveRegionPreferencesEnabled = false;
            App.Settings.Prop.AutomaticRegionalPreference = false;
            App.Settings.Prop.MatchFpsToMonitorRefreshRate = false;
            check(App.Settings.TrySave(), "Manual settings fixture baseline saves");
            App.Settings.Prop.CompetitivePreferredCity = "Manual city";
            check(model.HasUnsavedSettingsChanges, "Manual city edits remain tracked when automatic learning is disabled");
            check(App.Settings.TrySave(), "Manual city saves");
            App.Settings.Prop.CompetitiveFpsCap = 120;
            check(model.HasUnsavedSettingsChanges, "Manual FPS edits remain tracked when monitor matching is disabled");
        }
        finally { App.Settings.Prop = original; }
        Task.Run(() => CheckChannelsAsync(check)).GetAwaiter().GetResult();
    }

    private static async Task CheckChannelsAsync(Action<bool, string> check)
    {
        string originalChannel = App.Settings.Prop.Channel;
        bool ignoredChannel = App.State.Prop.IgnoreOutdatedChannel;
        try
        {
            var first = new TaskCompletionSource<(ClientVersion, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<(ClientVersion, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken firstToken = default;
            var model = new ChannelViewModel((channel, token) =>
            {
                if (channel == "first") { firstToken = token; return first.Task; }
                return second.Task;
            }, TimeSpan.FromSeconds(2));
            model.ViewChannel = "first";
            var firstLookup = model.CurrentLookup;
            model.ViewChannel = "second";
            var secondLookup = model.CurrentLookup;
            check(firstToken.IsCancellationRequested, "A new channel selection cancels its predecessor");
            second.SetResult((new ClientVersion { Version = "2.0.0", VersionGuid = "version-second" }, false));
            await secondLookup.WaitAsync(TimeSpan.FromSeconds(2));
            first.SetResult((new ClientVersion { Version = "1.0.0", VersionGuid = "version-first", IsBehindDefaultChannel = true }, false));
            await firstLookup.WaitAsync(TimeSpan.FromSeconds(2));
            check(model.ChannelDeployInfo?.VersionGuid == "version-second" && !model.ShowChannelWarning && !model.ShowLoadingError,
                "Reverse completion cannot replace the latest channel details or warning state");

            var failure = new ChannelViewModel((_, _) => Task.FromException<(ClientVersion, bool)>(new HttpRequestException("Fixture transport error")), TimeSpan.FromSeconds(2));
            failure.ViewChannel = "offline";
            await failure.CurrentLookup;
            check(failure.ShowLoadingError && failure.ChannelDeployInfo is null && failure.ChannelInfoLoadingText.Contains("retry"),
                "Transport failures end loading with visible retry guidance");
            var privateChannel = new ChannelViewModel((_, _) => Task.FromResult((new ClientVersion
                { Version = "3.0.0", VersionGuid = "private-fixture-secret" }, true)), TimeSpan.FromSeconds(2));
            privateChannel.ViewChannel = "private";
            await privateChannel.CurrentLookup;
            check(privateChannel.ChannelDeployInfo?.VersionGuid == "version-private", "Private channel hashes remain masked");

            var never = new TaskCompletionSource<(ClientVersion, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timeout = new ChannelViewModel((_, _) => never.Task, TimeSpan.FromMilliseconds(40));
            timeout.ViewChannel = "slow";
            await timeout.CurrentLookup.WaitAsync(TimeSpan.FromSeconds(2));
            check(timeout.ShowLoadingError && timeout.ChannelInfoLoadingText.Contains("timed out"),
                "A noncompleting channel request reaches a bounded terminal timeout state");
            never.SetResult((new ClientVersion { Version = "late", VersionGuid = "late" }, false));
            check(timeout.ChannelDeployInfo is null, "A response arriving after timeout cannot publish details");
            timeout.ViewChannel = "  ";
            await timeout.CurrentLookup;
            check(timeout.ShowLoadingError && timeout.ChannelInfoLoadingText == "Enter a channel name.", "An empty channel has an immediate terminal prompt");
            privateChannel.ViewChannel = "LiVe";
            await privateChannel.CurrentLookup;
            check(App.Settings.Prop.Channel == Deployment.DefaultChannel, "Channel aliases still normalize to production");
            bool cancelled = false;
            try { await Deployment.GetInfo("production", token: new CancellationToken(true)); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Deployment lookup observes cancellation before cached or network work");
        }
        finally
        {
            App.Settings.Prop.Channel = originalChannel;
            App.State.Prop.IgnoreOutdatedChannel = ignoredChannel;
        }
    }
}
