using Bloxstrap.RobloxInterfaces;
using System.ComponentModel;
using System.Windows;
using System.Net;

namespace Bloxstrap.UI.ViewModels.Settings
{
    public class ChannelViewModel : NotifyPropertyChangedViewModel
    {
        private readonly Func<string, CancellationToken, Task<(ClientVersion Info, bool IsPrivate)>> _lookup;
        private readonly TimeSpan _lookupTimeout;
        private CancellationTokenSource? _lookupCancellation;
        internal Task CurrentLookup { get; private set; } = Task.CompletedTask;

        public ChannelViewModel() : this(FetchChannelInfoAsync, TimeSpan.FromSeconds(20))
        {
            CurrentLookup = LoadChannelDeployInfo(App.Settings.Prop.Channel);
        }

        internal ChannelViewModel(Func<string, CancellationToken, Task<(ClientVersion Info, bool IsPrivate)>> lookup, TimeSpan timeout)
        {
            _lookup = lookup;
            _lookupTimeout = timeout;
        }

        public bool AutomaticDepthStrapUpdates
        {
            get => App.Settings.Prop.AutomaticDepthStrapUpdates;
            set { App.Settings.Prop.AutomaticDepthStrapUpdates = value; OnPropertyChanged(nameof(AutomaticDepthStrapUpdates)); }
        }
        public bool IsRobloxInstallationMissing => !App.IsPlayerInstalled && !App.IsStudioInstalled;

        private static async Task<(ClientVersion Info, bool IsPrivate)> FetchChannelInfoAsync(string channel, CancellationToken token)
        {
            bool isPrivate = await Deployment.IsChannelPrivate(channel, token);
            if (App.Cookies.Loaded && isPrivate && string.IsNullOrEmpty(Deployment.ChannelToken))
            {
                UserChannel? userChannel = await Deployment.GetUserChannel("WindowsPlayer").WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (userChannel?.Token is not null) Deployment.ChannelToken = userChannel.Token;
            }
            return (await Deployment.GetInfo(channel, true, true, token), isPrivate);
        }

        private async Task LoadChannelDeployInfo(string channel)
        {
            _lookupCancellation?.Cancel();
            using var cancellation = new CancellationTokenSource(_lookupTimeout);
            _lookupCancellation = cancellation;
            ShowLoadingError = false;
            ShowChannelWarning = false;
            ChannelInfoLoadingText = Strings.Menu_Channel_Switcher_Fetching;
            ChannelDeployInfo = null;
            NotifyLookupState();

            try
            {
                if (string.IsNullOrWhiteSpace(channel))
                {
                    ShowLoadingError = true;
                    ChannelInfoLoadingText = "Enter a channel name.";
                    return;
                }
                var (info, isPrivate) = await _lookup(channel, cancellation.Token).WaitAsync(cancellation.Token);
                if (_lookupCancellation != cancellation) return;
                ShowChannelWarning = info.IsBehindDefaultChannel;
                ChannelDeployInfo = new DeployInfo
                {
                    Version = info.Version,
                    VersionGuid = isPrivate ? "version-private" : info.VersionGuid, // we dont want to return the hash of private channels for obvious reason
                    Timestamp = info.Timestamp?.ToLocalTime().ToString() ?? "?"
                };
                App.State.Prop.IgnoreOutdatedChannel = true;
            }
            catch (Exception ex)
            {
                if (_lookupCancellation != cancellation) return;
                ShowLoadingError = true;
                ChannelInfoLoadingText = ex switch
                {
                    OperationCanceledException => "Channel lookup timed out. Check your connection and retry.",
                    InvalidChannelException { StatusCode: HttpStatusCode.Unauthorized } => Strings.Menu_Channel_Switcher_Unauthorized,
                    InvalidChannelException invalid => $"Could not load this channel (HTTP {(int?)invalid.StatusCode}).",
                    _ => "Could not load channel details. Check your connection and retry."
                };
                if (ex is not OperationCanceledException) App.Logger.WriteException("ChannelViewModel::Load", ex);
            }
            finally
            {
                if (_lookupCancellation == cancellation)
                {
                    _lookupCancellation = null;
                    NotifyLookupState();
                }
            }
        }

        private void NotifyLookupState()
        {
            OnPropertyChanged(nameof(ShowLoadingError));
            OnPropertyChanged(nameof(ShowChannelWarning));
            OnPropertyChanged(nameof(ChannelInfoLoadingText));
            OnPropertyChanged(nameof(ChannelDeployInfo));
        }
        public bool ShowLoadingError { get; set; } = false;
        public bool ShowChannelWarning { get; set; } = false;

        public DeployInfo? ChannelDeployInfo { get; private set; } = null;
        public string ChannelInfoLoadingText { get; private set; } = null!;

        public string ViewChannel
        {
            get => App.Settings.Prop.Channel;
            set
            {
                value = (value ?? "").Trim();


                if (value.Equals("live", StringComparison.OrdinalIgnoreCase) || value.Equals("zlive", StringComparison.OrdinalIgnoreCase))
                {
                    App.Settings.Prop.Channel = Deployment.DefaultChannel;
                }
                else
                {
                    App.Settings.Prop.Channel = value;
                }
                CurrentLookup = LoadChannelDeployInfo(App.Settings.Prop.Channel);
                OnPropertyChanged(nameof(ViewChannel));
            }
        }

        public bool UpdateRoblox
        {
            get => App.Settings.Prop.UpdateRoblox && !IsRobloxInstallationMissing;
            set => App.Settings.Prop.UpdateRoblox = value;
        }

        public bool StaticDirectory
        {
            get => App.Settings.Prop.StaticDirectory;
            set => App.Settings.Prop.StaticDirectory = value;
        }

        public bool SaveAndLaunchToPlayer
        {
            get => App.Settings.Prop.SaveAndLaunchToPlayer;
            set => App.Settings.Prop.SaveAndLaunchToPlayer = value;
        }

        public IReadOnlyDictionary<string, ChannelChangeMode> ChannelChangeModes => new Dictionary<string, ChannelChangeMode>
        {
            { Strings.Menu_Channel_ChangeAction_Automatic, ChannelChangeMode.Automatic },
            { Strings.Menu_Channel_ChangeAction_Prompt, ChannelChangeMode.Prompt },
            { Strings.Menu_Channel_ChangeAction_Ignore, ChannelChangeMode.Ignore },
        };

        public string SelectedChannelChangeMode
        {
            get => ChannelChangeModes.FirstOrDefault(x => x.Value == App.Settings.Prop.ChannelChangeMode).Key;
            set => App.Settings.Prop.ChannelChangeMode = ChannelChangeModes[value];
        }

        public bool ForceRobloxReinstallation
        {
            get => App.State.Prop.ForceReinstall || IsRobloxInstallationMissing;
            set => App.State.Prop.ForceReinstall = value;
        }
    }
}