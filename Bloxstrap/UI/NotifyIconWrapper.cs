using Bloxstrap.Roblox;
using Bloxstrap.Competitive;
using Bloxstrap.Integrations;
using Bloxstrap.UI.Elements.ContextMenu;
using System.Windows;

namespace Bloxstrap.UI
{
    public class NotifyIconWrapper : IDisposable
    {
        // lol who needs properly structured mvvm and xaml when you have the absolute catastrophe that this is

        private bool _disposing = false;

        private readonly System.Windows.Forms.NotifyIcon _notifyIcon;

        private readonly MenuContainer _menuContainer;

        private readonly Watcher _watcher;

        private ActivityWatcher? _activityWatcher => _watcher.ActivityWatcher;

        EventHandler? _alertClickHandler;

        public NotifyIconWrapper(Watcher watcher)
        {
            App.Logger.WriteLine("NotifyIconWrapper::NotifyIconWrapper", "Initializing notification area icon");

            _watcher = watcher;

            _notifyIcon = new(new System.ComponentModel.Container())
            {
                Icon = Properties.Resources.IconDepthStrap,
                Text = App.DisplayName,
                Visible = true
            };

            _notifyIcon.MouseClick += MouseClickEventHandler;

            _notifyIcon.MouseDoubleClick += (s, e) =>
            {
                if (e.Button != System.Windows.Forms.MouseButtons.Left)
                    return;

                switch (App.Settings.Prop.DoubleClickAction)
                {
                    case TrayDoubleClickAction.None:
                        Frontend.ShowMessageBox(
                            "You don’t have the double-click action set to anything.",
                            MessageBoxImage.Information
                        );
                        break;

                    case TrayDoubleClickAction.GameHistory:
                        if (!App.Settings.Prop.ShowGameHistoryMenu)
                        {
                            Frontend.ShowMessageBox(
                                "Enable 'Game History' in settings to use this feature.",
                                MessageBoxImage.Information
                            );
                            return;
                        }

                        new ServerHistory(_activityWatcher!).Show();
                        break;

                    case TrayDoubleClickAction.ServerInfo:
                        if (!App.Settings.Prop.ShowServerDetails)
                        {
                            Frontend.ShowMessageBox(
                                "Enable 'Query Server Location' in settings to use this feature.",
                                MessageBoxImage.Information
                            );
                            return;
                        }

                        if (_activityWatcher is not null && _activityWatcher.InGame)
                        {
                            _menuContainer!.ShowServerInformationWindow();
                        }
                        else
                        {
                            Frontend.ShowMessageBox(
                                "Join a game first to view server information.",
                                MessageBoxImage.Information
                            );
                        }
                        break;
                }
            };

            bool chimeMonitorActive = _watcher.CompetitiveMonitor is not null;

            if (_activityWatcher is not null && (App.Settings.Prop.ShowServerDetails || App.Settings.Prop.ShowServerUptime || chimeMonitorActive))
                _activityWatcher.OnGameJoin += OnGameJoin;

            if (chimeMonitorActive)
                _watcher.CompetitiveMonitor!.OnRegionEvaluated += OnCompetitiveRegionEvaluated;

            _menuContainer = new(_watcher);
            _menuContainer.Show();
        }

        #region Context menu
        public void MouseClickEventHandler(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Right)
                return;

            _menuContainer.Activate();
            _menuContainer.ContextMenu.IsOpen = true;
        }
        #endregion

        #region Activity handlers
        public async void OnGameJoin(object? sender, EventArgs e)
        {
            if (_activityWatcher is null)
                return;

            // competitive Chime monitor: when it owns this join (Deepwoken teleport/reserved),
            // suppress the generic notification to avoid duplicate balloons.
            var chimeMonitor = _watcher.CompetitiveMonitor;
            if (chimeMonitor is not null)
            {
                // give the loadtime line a moment so UniverseId is known before we decide
                DateTime deadline = DateTime.Now.AddSeconds(2);
                while (_activityWatcher.Data.UniverseId == 0 && DateTime.Now < deadline)
                    await Task.Delay(100);

                if (chimeMonitor.IsChimeCandidate(_activityWatcher.Data))
                    return;
            }

            string title = _activityWatcher.Data.ServerType switch
            {
                ServerType.Public => Strings.ContextMenu_ServerInformation_Notification_Title_Public,
                ServerType.Private => Strings.ContextMenu_ServerInformation_Notification_Title_Private,
                ServerType.Reserved => Strings.ContextMenu_ServerInformation_Notification_Title_Reserved,
                _ => ""
            };

            bool locationActive = App.Settings.Prop.ShowServerDetails;
            bool uptimeActive = App.Settings.Prop.ShowServerUptime;

            string? serverLocation = "";
            if (locationActive)
                serverLocation = await _activityWatcher.Data.QueryServerLocation(showErrors: false);

            string? serverUptime = "";
            if (uptimeActive)
            {
                DateTime? serverTime = await _activityWatcher.Data.QueryServerTime(showErrors: false);
                TimeSpan _serverUptime = DateTime.UtcNow - (serverTime ?? DateTime.UtcNow);

                if (_serverUptime.TotalSeconds > 60)
                    serverUptime = Time.FormatTimeSpan(_serverUptime);
                else
                    serverUptime = Strings.ContextMenu_ServerInformation_Notification_ServerNotTracked;
            }

            if (
                string.IsNullOrEmpty(serverLocation) && locationActive ||
                string.IsNullOrEmpty(serverUptime) && uptimeActive
                )
                return;

            string notifContent = Strings.Common_UnknownStatus;

            // since we dont have an actual localization, this is probably the best way of doing that
            if (locationActive && !uptimeActive)
                notifContent = String.Format(Strings.ContextMenu_ServerInformation_Notification_Text, serverLocation);
            else if (!locationActive && uptimeActive)
                notifContent = String.Format(Strings.ContextMenu_ServerInformationUptime_Notification_Text, serverUptime);
            else if (locationActive && uptimeActive)
                notifContent = String.Format(Strings.ContextMenu_ServerInformationUptimeAndLocation_Notification_Text, serverLocation, serverUptime);

            ShowAlert(
                title,
                notifContent,
                10,
                (_, _) => _menuContainer.ShowServerInformationWindow()
            );
        }

        /// <summary>
        /// Max two short detail lines for the balloon (full details live in history/logs):
        /// "ICMP RTT: 17 ms" and "WARP: On / DFW ingress". Never longer than that.
        /// </summary>
        private static string BuildNetworkDetailLines(CompetitiveNetworkEvent? evt, bool chime)
        {
            if (evt is null)
                return "";

            var lines = new List<string>();

            if (evt.Latency?.HasMeasurement == true)
                lines.Add($"ICMP RTT: {evt.Latency.AverageMs:0} ms");
            else if (chime && evt.Latency is not null)
                lines.Add("no ICMP measurement"); // supplementary only - never "100% loss"

            if (evt.Cloudflare is not null && !string.IsNullOrEmpty(evt.Cloudflare.Colo))
                lines.Add($"WARP: {(evt.Cloudflare.WarpActive is true ? "On" : evt.Cloudflare.WarpActive is false ? "Off" : "Unknown")} / {evt.Cloudflare.Colo} ingress");

            return string.Join("\n", lines.Take(2));
        }

        /// <summary>
        /// Specialized Deepwoken/Chime region notifications, driven by CompetitiveRegionMonitor.
        /// </summary>
        public void OnCompetitiveRegionEvaluated(object? sender, CompetitiveRegionResult result)
        {
            try
            {
                var monitor = _watcher.CompetitiveMonitor;
                if (monitor is null) return;

                bool badAlert = result.Quality == RegionQuality.Poor || result.Quality == RegionQuality.Bad;

                // WarnOnBadChimeRegion gates the scary ones; positive/fallback alerts always show.
                if (badAlert && !App.Settings.Prop.WarnOnBadChimeRegion)
                    return;
                if (!badAlert && !App.Settings.Prop.ChimeRegionMonitorEnabled)
                    return;

                var notif = CompetitiveRegionMonitor.BuildNotification(result);
                if (notif is null) return;

                // enrich Chime balloons with live network details when the network monitor has them.
                // Stage 1 (region) fires first; ICMP/WARP may still be in flight - show what's ready,
                // never wait for diagnostics to display the region warning.
                string message = notif.Value.Message;
                if (result.IsTeleport || result.IsReservedServer)
                {
                    var netEvt = _watcher.NetworkMonitor?.GetLatestEvent(result.JobId);
                    string details = BuildNetworkDetailLines(netEvt, chime: true);
                    if (details.Length > 0)
                        message += "\n" + details;
                }

                if (badAlert)
                {
                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        if (!_disposing && _activityWatcher?.InGame == true &&
                            _activityWatcher.Data.JobId == result.JobId && App.Settings.Prop.WarnOnBadChimeRegion &&
                            _watcher.NetworkMonitor?.LatestEvent?.Timestamp == result.Timestamp)
                        {
                            string alertKey = $"{result.JobId}|{_activityWatcher.Data.TimeJoined.Ticks}|{result.Location}";
                            if (_lastBadRegionAlert == alertKey) return;
                            _lastBadRegionAlert = alertKey;
                            new Elements.Dialogs.BadRegionAlertWindow(notif.Value.Title, message).Show();
                        }
                    });
                    return;
                }
                ShowAlert(
                    notif.Value.Title,
                    message,
                    10,
                    (_, _) => _menuContainer.ShowServerInformationWindow()
                );

            }
            catch (Exception ex)
            {
                App.Logger.WriteException("NotifyIconWrapper::OnCompetitiveRegionEvaluated", ex);
            }
        }
        #endregion

        // we may need to create our own handler for this, because this sorta sucks
        private string? _lastBadRegionAlert;
        public void ShowAlert(string caption, string message, int duration, EventHandler? clickHandler)
        {
            string id = Guid.NewGuid().ToString()[..8];

            string LOG_IDENT = $"NotifyIconWrapper::ShowAlert.{id}";

            App.Logger.WriteLine(LOG_IDENT, $"Showing alert for {duration} seconds (clickHandler={clickHandler is not null})");
            App.Logger.WriteLine(LOG_IDENT, $"{caption}: {message.Replace("\n", "\\n")}");

            _notifyIcon.BalloonTipTitle = caption;
            _notifyIcon.BalloonTipText = message;

            if (_alertClickHandler is not null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Previous alert still present, erasing click handler");
                _notifyIcon.BalloonTipClicked -= _alertClickHandler;
            }

            _alertClickHandler = clickHandler;
            _notifyIcon.BalloonTipClicked += clickHandler;

            _notifyIcon.ShowBalloonTip(duration);

            Task.Run(async () =>
            {
                await Task.Delay(duration * 1000);

                _notifyIcon.BalloonTipClicked -= clickHandler;

                App.Logger.WriteLine(LOG_IDENT, "Duration over, erasing current click handler");

                if (_alertClickHandler == clickHandler)
                    _alertClickHandler = null;
                else
                    App.Logger.WriteLine(LOG_IDENT, "Click handler has been overridden by another alert");
            });
        }

        public void Dispose()
        {
            if (_disposing)
                return;

            _disposing = true;

            App.Logger.WriteLine("NotifyIconWrapper::Dispose", "Disposing NotifyIcon");

            _menuContainer.Dispatcher.Invoke(_menuContainer.Close);
            _notifyIcon.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
