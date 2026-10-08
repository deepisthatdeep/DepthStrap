using Bloxstrap.Roblox;
using Bloxstrap.Competitive;
using Bloxstrap.AppData;
using Bloxstrap.Integrations;

namespace Bloxstrap
{
    public class Watcher : IDisposable
    {
        private readonly InterProcessLock _lock;

        private readonly WatcherData? _watcherData;

        private readonly NotifyIconWrapper? _notifyIcon;

        public readonly ActivityWatcher? ActivityWatcher;

        public readonly CompetitiveRegionMonitor? CompetitiveMonitor;

        public readonly CompetitiveNetworkMonitor? NetworkMonitor;

        public readonly IntegrationWatcher? IntegrationWatcher;

        public readonly PlayerDiscordRichPresence? PlayerRichPresence;
        public readonly StudioDiscordRichPresence? StudioRichPresence;

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private bool _isDisposed = false;
        private Task? _returnHomeTask;
        private int _autoLogStarted;

        public Watcher()
        {
            const string LOG_IDENT = "Watcher";

            string? watcherDataArg = App.LaunchSettings.WatcherFlag.Data;

            if (String.IsNullOrEmpty(watcherDataArg))
            {
#if DEBUG
                string path = new RobloxPlayerData().ExecutablePath;
                if (!File.Exists(path))
                    throw new ApplicationException("Roblox player is not been installed");

                using var gameClientProcess = Process.Start(path);

                _watcherData = new() { ProcessId = gameClientProcess.Id };
#else
                throw new Exception("Watcher data not specified");
#endif
            }
            else
            {
                _watcherData = JsonSerializer.Deserialize<WatcherData>(Encoding.UTF8.GetString(Convert.FromBase64String(watcherDataArg)));
            }

            if (_watcherData is null)
                throw new Exception("Watcher data is invalid");

            _lock = new InterProcessLock("Watcher-" + _watcherData.ProcessId);
            if (!_lock.IsAcquired) return;

            // the competitive Chime monitor also needs the activity watcher, even when
            // plain activity tracking is disabled by the user
            bool wantActivityWatcher = App.Settings.Prop.EnableActivityTracking ||
                RegionMonitoringPolicy.NeedsWatcher(App.Settings.Prop);

            if (wantActivityWatcher)
            {
                ActivityWatcher = new(_watcherData.LogFile, _watcherData.LaunchMode, _watcherData.ProcessId);

                if (App.Settings.Prop.UseDisableAppPatch)
                {
                    ActivityWatcher.OnAppClose += delegate
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Received desktop app exit, closing Roblox");
                        using var process = Process.GetProcessById(_watcherData.ProcessId);
                        process.CloseMainWindow();
                    };
                }

                // legacy integrations are dormant unless explicitly re-enabled (feature strip stage 1)
                bool legacyIntegrations = App.SupportsLegacyIntegrations && App.Settings.Prop.LegacyIntegrationsEnabled;

                if ((_watcherData.LaunchMode == LaunchMode.Studio || _watcherData.LaunchMode == LaunchMode.StudioAuth) && legacyIntegrations && App.Settings.Prop.StudioRPC)
                    StudioRichPresence = new(ActivityWatcher);
                else if (_watcherData.LaunchMode == LaunchMode.Player && legacyIntegrations && App.Settings.Prop.UseDiscordRichPresence)
                    PlayerRichPresence = new(ActivityWatcher);

                // competitive network monitor (UDMUX/RCC capture, WARP state, ICMP, JSONL history)
                if (_watcherData.LaunchMode == LaunchMode.Player && RegionMonitoringPolicy.NeedsWatcher(App.Settings.Prop))
                {
                    NetworkMonitor = new(ActivityWatcher);
                    NetworkMonitor.AutoLogHandler = AutoLogBadRegionAsync;
                    if (RegionMonitoringPolicy.NeedsAlerts(App.Settings.Prop))
                        CompetitiveMonitor = new(NetworkMonitor);
                }
            }

            _notifyIcon = new(this);
        }

        public void KillRobloxProcess() => CloseProcess(_watcherData!.ProcessId, true);

        private Task<bool> AutoLogBadRegionAsync(CompetitiveNetworkEvent result)
        {
            if (_isDisposed || ActivityWatcher is null || _watcherData is null) return Task.FromResult(false);
            var classification = CompetitiveRegionService.Classify(result.Location, source: result.RegionSource);
            if (!BadRegionAutoLog.ShouldLeave(result, App.Settings.Prop, ActivityWatcher.Data.JobId, DateTime.Now, classification,
                _watcherData.AutoLogRecovery, ActivityWatcher.Data.TimeJoined)) return Task.FromResult(false);
            bool left = false;
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_isDisposed || !ActivityWatcher.InGame || ActivityWatcher.Data.UniverseId != CompetitiveRegionService.DeepwokenUniverseId ||
                    !BadRegionAutoLog.ShouldLeave(result, App.Settings.Prop, ActivityWatcher.Data.JobId, DateTime.Now,
                        CompetitiveRegionService.Classify(result.Location, source: result.RegionSource), _watcherData.AutoLogRecovery,
                        ActivityWatcher.Data.TimeJoined)) return;
                Process? player = null;
                TaskCompletionSource? completed = null;
                try
                {
                    player = Process.GetProcessById(_watcherData.ProcessId);
                    if (player.ProcessName != "RobloxPlayerBeta" || player.HasExited) { player.Dispose(); return; }
                    string version = Path.GetFileName(Path.GetDirectoryName(player.MainModule?.FileName)) ?? "";
                    if (!RobloxVersionArchive.IsVersionId(version)) throw new InvalidDataException("Could not identify the running Roblox build.");
                    if (Interlocked.Exchange(ref _autoLogStarted, 1) != 0) { player.Dispose(); return; }
                    // Reserve before closing anything. A recovery client never autologs again;
                    // manual relaunches and other clients share this conservative cooldown.
                    if (!AutoLogHomeHandoff.ReserveRetry())
                    {
                        Interlocked.Exchange(ref _autoLogStarted, 0);
                        player.Dispose();
                        CompetitiveSessionLogger.Write("AUTOLOG: recovery cooldown active; leaving this client running.");
                        return;
                    }
                    completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref _returnHomeTask, completed.Task);
                    bool rejoin = BadRegionAutoLog.ShouldRejoin(result, App.Settings.Prop);
                    ActivityWatcher.SuppressAutoRejoin = true;
                    if (!player.CloseMainWindow())
                    {
                        completed.TrySetResult(); player.Dispose();
                        Interlocked.Exchange(ref _autoLogStarted, 0);
                        ActivityWatcher.SuppressAutoRejoin = false;
                        new UI.Elements.Dialogs.BadRegionAlertWindow("Autolog could not leave", "Roblox did not accept a normal window close. Leave the game manually.").Show();
                        return;
                    }
                    left = true;
                    CompetitiveSessionLogger.Write($"AUTOLOG: region={result.Location}; job={result.JobId}; returning Player pid={_watcherData.ProcessId} to Home; Deepwoken rejoin={rejoin}");
                    var ownedPlayer = player;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // The watcher remains alive for this task after the client exits.
                            await CompetitiveSessionLogger.WriteNetworkEventAsync(result, CancellationToken.None);
                            await ReturnHomeAsync(ownedPlayer, version, rejoin);
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteException("Watcher::AutoLogHome", ex);
                            System.Windows.Application.Current.Dispatcher.Invoke(() => new UI.Elements.Dialogs.BadRegionAlertWindow(
                                "Autolog stopped", "Roblox Home or the rejoin could not be opened. Launch Roblox manually.").ShowDialog());
                        }
                        finally { ownedPlayer.Dispose(); completed.TrySetResult(); }
                    });
                }
                catch (Exception ex)
                {
                    completed?.TrySetResult(); Interlocked.Exchange(ref _autoLogStarted, 0);
                    player?.Dispose(); App.Logger.WriteException("Watcher::AutoLog", ex);
                }
            });
            // Cooldown, changed settings or a rejected close must not suppress diagnostics.
            return Task.FromResult(left);
        }

        private async Task ReturnHomeAsync(Process player, string version, bool rejoin)
        {
            using (var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await player.WaitForExitAsync(closeTimeout.Token);
            string nonce = Guid.NewGuid().ToString("N");
            using var launcher = Process.Start(AutoLogHomeHandoff.Launch(BadRegionAutoLog.HomeUri, version, nonce));
            var homeData = await AutoLogHomeHandoff.WaitAsync(nonce, _cancellationTokenSource.Token);
            using var home = Process.GetProcessById(homeData.Watcher.ProcessId);
            if (home.HasExited || home.ProcessName != "RobloxPlayerBeta" || home.StartTime != homeData.StartedAt) return;
            if (!rejoin)
            {
                CompetitiveSessionLogger.Write("AUTOLOG: staying on Home (rejoin disabled).");
                return;
            }
            if (!await Task.Run(() => home.WaitForInputIdle(15000))) return;
            bool StillOnHome()
            {
                try
                {
                    if (home.HasExited || home.StartTime != homeData.StartedAt || string.IsNullOrEmpty(homeData.Watcher.LogFile)) return false;
                    using var stream = new FileStream(homeData.Watcher.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > 4 * 1024 * 1024) return false;
                    using var reader = new StreamReader(stream);
                    string log = reader.ReadToEnd();
                    return BadRegionAutoLog.IsIdleHomeLog(log);
                }
                catch { return false; }
            }
            if (!StillOnHome()) return;
            var countdown = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new UI.Elements.Dialogs.AutoLogRejoinWindow(StillOnHome); dialog.Show(); return dialog;
            });
            if (!await countdown.Decision || !StillOnHome()) return;
            // Only the Home client created by this handoff is closed; other clients are untouched.
            if (!home.CloseMainWindow()) return;
            using (var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                await home.WaitForExitAsync(closeTimeout.Token);
            using var rejoinLauncher = Process.Start(AutoLogHomeHandoff.Launch(BadRegionAutoLog.RejoinUri, version));
            CompetitiveSessionLogger.Write("AUTOLOG: reopening Deepwoken entry place through normal preferred-server selection.");
        }

        public void CloseProcess(int pid, bool force = false)
        {
            const string LOG_IDENT = "Watcher::CloseProcess";

            try
            {
                using var process = Process.GetProcessById(pid);

                App.Logger.WriteLine(LOG_IDENT, $"Killing process '{process.ProcessName}' (pid={pid}, force={force})");

                if (process.HasExited)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"PID {pid} has already exited");
                    return;
                }

                if (force)
                    process.Kill();
                else
                    process.CloseMainWindow();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"PID {pid} could not be closed");
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        public async Task Run()
        {
            if (!_lock.IsAcquired || _watcherData is null)
                return;

            ActivityWatcher?.Start();

            try
            {
                // Watch this client directly. A failed system-wide process enumeration
                // must not be mistaken for its exit and disable the live monitor.
                using var player = Process.GetProcessById(_watcherData.ProcessId);
                await player.WaitForExitAsync(_cancellationTokenSource.Token);
            }
            catch (ArgumentException)
            {
                App.Logger.WriteLine("Watcher::Run", "Player exited before its process handle could be opened");
            }
            catch (OperationCanceledException)
            {
                App.Logger.WriteLine("Watcher::Run", "Watcher was cancelled");
                return;
            }

            if (_cancellationTokenSource.Token.IsCancellationRequested)
                return;

            // Keep this watcher alive until the Home/rejoin handoff completes after its Player exits.
            var homeTask = Volatile.Read(ref _returnHomeTask);
            if (homeTask is not null) await homeTask;

            if (_watcherData.AutoclosePids is not null)
            {
                foreach (int pid in _watcherData.AutoclosePids)
                    CloseProcess(pid);
            }

            if (App.LaunchSettings.TestModeFlag.Active)
                Process.Start(Paths.Process, "-settings -testmode");
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            App.Logger.WriteLine("Watcher::Dispose", "Disposing Watcher");

            _cancellationTokenSource.Cancel();

            CompetitiveMonitor?.Dispose();
            Roblox.CompetitiveSettingsBackup.ReleaseQualityLock();
            NetworkMonitor?.Dispose();
            ActivityWatcher?.Dispose();
            IntegrationWatcher?.Dispose();
            _notifyIcon?.Dispose();
            PlayerRichPresence?.Dispose();
            StudioRichPresence?.Dispose();
            _cancellationTokenSource.Dispose();

            _lock.Dispose();
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
