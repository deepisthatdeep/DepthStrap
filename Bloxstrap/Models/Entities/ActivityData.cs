using Bloxstrap.Models.APIs;
using CommunityToolkit.Mvvm.Input;
using System.Web;
using System.Windows;
using System.Windows.Input;

namespace Bloxstrap.Models.Entities
{
    public class ActivityData
    {
        private long _universeId = 0;
        private readonly TaskCompletionSource<long> _universeResolved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [JsonIgnore]
        public Task<long> UniverseResolved => _universeResolved.Task;

        /// <summary>
        /// If the current activity stems from an in-universe teleport, then this will be
        /// set to the activity that corresponds to the initial game join
        /// </summary>
        public ActivityData? RootActivity { get; set; }

        public long UniverseId
        {
            get => _universeId;
            set
            {
                _universeId = value;
                if (value > 0) _universeResolved.TrySetResult(value);
            }
        }

        public long PlaceId { get; set; } = 0;

        public string JobId { get; set; } = string.Empty;

        /// <summary>
        /// This will be empty unless the server joined is a private server
        /// </summary>
        public string AccessCode { get; set; } = string.Empty;

        public long UserId { get; set; } = 0;

        public string MachineAddress { get; set; } = string.Empty;

        /// <summary>
        /// Live UDMUX endpoint the client actually connected to. Preferred measurement target:
        /// Roblox increasingly routes gameplay traffic through UDMUX, so benchmarking a random
        /// Roblox IP is less meaningful than the endpoint this session really used.
        /// Null when the server was not behind UDMUX (MachineAddress is the direct endpoint).
        /// </summary>
        public string? UdmuxAddress { get; set; }

        public int? UdmuxPort { get; set; }

        /// <summary>RCC (real content computer) address reported alongside the UDMUX line.</summary>
        public string? RccAddress { get; set; }

        public int? RccPort { get; set; }

        /// <summary>
        /// Where the last successful location query came from (diagnostics):
        /// "cached", "RoValra IP geolocation" or "ipinfo.io".
        /// </summary>
        public string LastLocationSource { get; private set; } = "";

        public bool MachineAddressValid => !string.IsNullOrEmpty(MachineAddress) && !MachineAddress.StartsWith("10.");

        public bool IsTeleport { get; set; } = false;

        public ServerType ServerType { get; set; } = ServerType.Public;

        public DateTime TimeJoined { get; set; }

        public DateTime? TimeLeft { get; set; }

        // everything below here is optional strictly for bloxstraprpc, discord rich presence, or game history

        /// <summary>
        /// This is intended only for other people to use, i.e. context menu invite link, rich presence joining
        /// </summary>
        public string RPCLaunchData { get; set; } = string.Empty;

        public UniverseDetails? UniverseDetails { get; set; }

        public string? RootJobId { get; set; }

        public event EventHandler<string>? OnDeleteRequested;

        public ICommand RejoinServerCommand => new RelayCommand(() => RejoinServer(true));
        public ICommand CopyDeeplinkCommand => new RelayCommand(CopyDeeplink);
        public ICommand CopyServerIdCommand => new RelayCommand(CopyServerId);
        public ICommand DeleteHistoryCommand => new RelayCommand(DeleteHistory);

        internal HttpClient QueryClient { get; init; } = App.HttpClient;
        internal Action<Exception>? QueryErrorReporter { get; init; }

        private void ReportQueryError(Exception error, string service)
        {
            if (QueryErrorReporter is not null) { QueryErrorReporter(error); return; }
            Frontend.ShowConnectivityDialog(
                string.Format(Strings.Dialog_Connectivity_UnableToConnect, service),
                Strings.ActivityWatcher_LocationQueryFailed, MessageBoxImage.Warning, error);
        }

        private SemaphoreSlim serverQuerySemaphore = new(1, 1);
        private SemaphoreSlim serverTimeSemaphore = new(1, 1);

        public string GetInviteDeeplink(bool launchData = true)
        {
            string deeplink = $"roblox://experiences/start?placeId={PlaceId}";

            if (ServerType == ServerType.Private) // thats not going to work
                deeplink += "&accessCode=" + AccessCode;
            else
                deeplink += "&gameInstanceId=" + JobId;

            if (launchData && !string.IsNullOrEmpty(RPCLaunchData))
                deeplink += "&launchData=" + HttpUtility.UrlEncode(RPCLaunchData);

            return deeplink;
        }

        public async Task<DateTime?> QueryServerTime(CancellationToken cancellationToken = default, bool showErrors = true)
        {
            const string LOG_IDENT = "ActivityData::QueryServerTime";
            if (string.IsNullOrEmpty(JobId)) throw new InvalidOperationException("JobId is null");
            if (PlaceId == 0) throw new InvalidOperationException("PlaceId is null");

            await serverTimeSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (GlobalCache.ServerTime.TryGetValue(JobId, out DateTime? time)) return time;
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(5));
                string raw = await QueryClient.GetStringAsync($"https://apis.rovalra.com/v1/server_details?place_id={PlaceId}&server_ids={JobId}", budget.Token);
                var response = JsonSerializer.Deserialize<RoValraTimeResponse>(raw)
                    ?? throw new InvalidHTTPResponseException("Server time response was empty");

                // Register unknown servers for a later lookup without inventing an uptime.
                _ = RegisterServerAsync(cancellationToken);
                DateTime? firstSeen = response.Servers?.FirstOrDefault()?.FirstSeen;
                if (firstSeen is not null) GlobalCache.ServerTime[JobId] = firstSeen;
                return firstSeen;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Failed to get server time for {PlaceId}/{JobId}");
                App.Logger.WriteException(LOG_IDENT, ex);
                if (showErrors) ReportQueryError(ex, "rovalra.com");
                return null;
            }
            finally { serverTimeSemaphore.Release(); }
        }

        private async Task RegisterServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(TimeSpan.FromSeconds(5));
                var body = new RoValraProcessServerBody { PlaceId = PlaceId, ServerIds = new() { JobId } };
                using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var response = await QueryClient.PostAsync("https://apis.rovalra.com/process_servers", content, budget.Token);
                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) { App.Logger.WriteException("ActivityData::RegisterServerAsync", ex); }
        }

        public async Task<string?> QueryServerLocation(CancellationToken cancellationToken = default, bool showErrors = true)
        {
            const string LOG_IDENT = "ActivityData::QueryServerLocation";

            if (!MachineAddressValid)
                throw new InvalidOperationException($"Machine address is invalid ({MachineAddress})");

            await serverQuerySemaphore.WaitAsync(cancellationToken);

            try
            {
                if (GlobalCache.ServerLocation.TryGetValue(MachineAddress, out string? location))
                {
                    LastLocationSource = "cached";
                    return location;
                }
                // Try RoValra API first
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    budget.CancelAfter(TimeSpan.FromSeconds(5));
                    string raw = await QueryClient.GetStringAsync($"https://apis.rovalra.com/v1/geolocation?ip={MachineAddress}", budget.Token);
                    var response = JsonSerializer.Deserialize<RoValraGeolocation>(raw)!;
                    var geolocation = response.Location;

                    if (!string.IsNullOrWhiteSpace(geolocation?.City) && !string.IsNullOrWhiteSpace(geolocation.Country))
                    {
                        if (geolocation.City == geolocation.Region && geolocation.City == geolocation.Country)
                            location = geolocation.Country;
                        else if (geolocation.City == geolocation.Region)
                            location = $"{geolocation.Region}, {geolocation.Country}";
                        else
                            location = $"{geolocation.City}, {geolocation.Region}, {geolocation.Country}";

                        App.Logger.WriteLine(LOG_IDENT, $"Got location from RoValra: {location}");
                        LastLocationSource = "RoValra IP geolocation";
                        GlobalCache.ServerLocation[MachineAddress] = location;
                        return location;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception rovalraEx)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"RoValra API failed, falling back to ipinfo.io: {rovalraEx.Message}");
                }

                // Fallback to ipinfo.io
                cancellationToken.ThrowIfCancellationRequested();
                using var fallbackBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                fallbackBudget.CancelAfter(TimeSpan.FromSeconds(5));
                string fallbackRaw = await QueryClient.GetStringAsync($"https://ipinfo.io/{MachineAddress}/json", fallbackBudget.Token);
                var ipInfo = JsonSerializer.Deserialize<IPInfoResponse>(fallbackRaw)!;

                if (string.IsNullOrEmpty(ipInfo.City))
                    throw new InvalidHTTPResponseException("Reported city was blank");

                if (ipInfo.City == ipInfo.Region)
                    location = $"{ipInfo.Region}, {ipInfo.Country}";
                else
                    location = $"{ipInfo.City}, {ipInfo.Region}, {ipInfo.Country}";

                App.Logger.WriteLine(LOG_IDENT, $"Got location from ipinfo.io: {location}");
                LastLocationSource = "ipinfo.io";
                GlobalCache.ServerLocation[MachineAddress] = location;
                return location;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Failed to get server location for {MachineAddress}");
                App.Logger.WriteException(LOG_IDENT, ex);


                if (showErrors) ReportQueryError(ex, "rovalra.com/ipinfo.io");
                return null;
            }
            finally { serverQuerySemaphore.Release(); }
        }

        public void RejoinServer(bool CloseRoblox = true)
        {
            try
            {
                App.Logger.WriteLine("ActivityData::RejoinServer", $"Rejoining server: {PlaceId}/{JobId}");

                string robloxUri = GetInviteDeeplink(true);

                Process.Start(new ProcessStartInfo
                {
                    FileName = robloxUri,
                    UseShellExecute = true
                });

                if (CloseRoblox)
                    CloseRobloxProcesses();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ActivityData::RejoinServer", ex);
                Frontend.ShowMessageBox($"Failed to rejoin server: {ex.Message}", MessageBoxImage.Error);
            }
        }

        public void CloseRobloxProcesses()
        {
            const string LOG_IDENT = "ActivityData::CloseProcess";

            try
            {
                var process = Process.GetProcessesByName("RobloxPlayerBeta");

                if (process.Length == 0)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Roblox not found");
                    return;
                }

                foreach (var proc in process)
                {
                    if ((DateTime.Now - proc.StartTime).TotalSeconds < 3)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Skipping new process");
                        continue;
                    }

                    proc.Kill();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Roblox could not be closed");
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        private async void CopyDeeplink()
        {
            string deeplink = GetInviteDeeplink();
            Clipboard.SetText(deeplink);
        }

        private async void CopyServerId() => Clipboard.SetText(JobId);

        private void DeleteHistory()
        {
            string jobIdToDelete = !string.IsNullOrEmpty(RootJobId) ? RootJobId : JobId;

            if (!string.IsNullOrEmpty(jobIdToDelete))
            {
                OnDeleteRequested?.Invoke(this, jobIdToDelete);
            }
        }
    }
}
