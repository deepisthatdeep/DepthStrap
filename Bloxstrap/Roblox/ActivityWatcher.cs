using Bloxstrap.Integrations;
namespace Bloxstrap.Roblox
{
    public class ActivityWatcher : IDisposable
    {
        private const string GameMessageEntry = "[FLog::Output] [BloxstrapRPC]";
        private const string GameJoiningEntry = "[FLog::Output] ! Joining game";

        // these entries are technically volatile!
        // they only get printed depending on their configured FLog level, which could change at any time
        // while levels being changed is fairly rare, please limit the number of varying number of FLog types you have to use, if possible

        private const string GameTeleportingEntry = "[FLog::GameJoinUtil] GameJoinUtil::initiateTeleportToPlace";
        private const string GameJoiningPrivateServerEntry = "[FLog::GameJoinUtil] GameJoinUtil::joinGamePostPrivateServer";
        private const string GameJoiningReservedServerEntry = "[FLog::GameJoinUtil] GameJoinUtil::initiateTeleportToReservedServer";
        private const string GameJoiningUniverseEntry = "[FLog::GameJoinLoadTime] Report game_join_loadtime:";
        private const string GameJoiningUDMUXEntry = "[FLog::Network] UDMUX Address = ";
        private const string GameJoinedEntry = "[FLog::Network] serverId:";
        private const string GameDisconnectedEntry = "[FLog::Network] Time to disconnect replication data:";
        private const string GameLeavingEntry = "[FLog::SingleSurfaceApp] leaveUGCGameInternal";
        private const string GameDisconnectReasonEntry = "[FLog::Network] Sending disconnect with reason:";

        private const string StudioPlaceOpenEntry = "[FLog::PlaceManager] Start to open place";
        private const string StudioPlaceCloseEntry = "[FLog::PlaceManager] PlaceManager::closeCurrentPlayDoc";

        private const string GameJoiningEntryPattern = @"! Joining game '([0-9a-f\-]{36})' place ([0-9]+) at ([0-9\.]+)";
        private const string GameJoiningPrivateServerPattern = @"""accessCode"":""([0-9a-f\-]{36})""";
        private const string GameJoiningUniversePattern = @"universeid:([0-9]+).*userid:([0-9]+)";
        // groups: 1=udmux ip, 2=udmux port, 3=rcc ip, 4=rcc port
        private const string GameJoiningUDMUXPattern = @"UDMUX Address = ([0-9\.]+), Port = ([0-9]+) \| RCC Server Address = ([0-9\.]+), Port = ([0-9]+)";
        private const string GameJoinedEntryPattern = @"serverId: ([0-9\.]+)\|([0-9]+)";
        private const string GameMessageEntryPattern = @"\[BloxstrapRPC\] (.*)";
        private const string GameDisconnectReasonPattern = @"Sending disconnect with reason: (\d+)";

        private int _logEntriesRead = 0;
        private bool _teleportMarker = false;
        private bool _reservedTeleportMarker = false;
        private bool _shouldAutoRejoin = false;
        private string? _joinAddress;
        private string? _confirmedAddress;
        internal bool SuppressAutoRejoin { get; set; }
        internal TimeSpan AutoRejoinDelay { get; set; } = TimeSpan.FromSeconds(3);
        internal Task AutoRejoinTask { get; private set; } = Task.CompletedTask;
        internal Action<ActivityData>? RejoinRequested { get; set; }

        private static readonly string GameHistoryCachePath = Path.Combine(Paths.Cache, "GameHistory.json");
        public event EventHandler? OnHistoryUpdated;

        public event EventHandler<string>? OnLogEntry;
        public event EventHandler? OnGameJoin;
        public event EventHandler? OnConnectionUpdated;
        public event EventHandler? OnGameLeave;
        public event EventHandler? OnStudioPlaceOpened;
        public event EventHandler? OnStudioPlaceClosed;
        public event EventHandler? OnLogOpen;
        public event EventHandler? OnAppClose;
        public event EventHandler<Message>? OnRPCMessage;
        public event EventHandler<StudioMessage>? OnStudioRPCMessage;

        private DateTime LastRPCRequest;

        private readonly LaunchMode _launchMode;
        private readonly int _robloxPID;
        internal int RobloxProcessId => _robloxPID;

        public string LogLocation = null!;

        public bool InGame = false;
        public bool InStudioPlace = false;
        public bool InRobloxStudio = false;

        private const int HttpPort = 4875;
        private HttpListener? _httpListener;
        private readonly CancellationTokenSource _httpCancellationTokenSource = new();

        public ActivityData Data { get; private set; } = new();

        /// <summary>
        /// Ordered by newest to oldest
        /// </summary>
        public List<ActivityData> History = new();

        public bool IsDisposed = false;

        public void CloseProcess(int pid)
        {
            const string LOG_IDENT = "Watcher::CloseProcess";

            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"PID {pid} has already exited");
                    return;
                }

                process.Kill();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"PID {pid} could not be closed");
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        public ActivityWatcher(string? logFile = null, LaunchMode launchMode = LaunchMode.Player, int RobloxPID = 0)
        {
            if (!String.IsNullOrEmpty(logFile))
                LogLocation = logFile;

            _launchMode = launchMode;
            _robloxPID = RobloxPID;

            if (_launchMode == LaunchMode.Studio || _launchMode == LaunchMode.StudioAuth)
            {
                InRobloxStudio = true;
                StartHTTPServer();
            }

            LoadGameHistory();
        }

        public async void Start()
        {
            try { await RunAsync(); }
            catch (Exception ex) { App.Logger.WriteException("ActivityWatcher::Start", ex); }
        }

        internal async Task RunAsync()
        {
            const string LOG_IDENT = "ActivityWatcher::Start";

            // okay, here's the process:
            //
            // - tail the latest log file from %localappdata%\roblox\logs
            // - check for specific lines to determine player's game activity as shown below:
            //
            // - get the place id, job id and machine address from '! Joining game '{{JOBID}}' place {{PLACEID}} at {{MACHINEADDRESS}}' entry
            // - confirm place join with 'serverId: {{MACHINEADDRESS}}|{{MACHINEPORT}}' entry
            // - check for leaves/disconnects with 'Time to disconnect replication data: {{TIME}}' entry
            //
            // we'll tail the log file continuously, monitoring for any log entries that we need to determine the current game activity

            FileInfo logFileInfo;

            if (String.IsNullOrEmpty(LogLocation))
            {
                string logDirectory = Path.Combine(Paths.Roblox, "logs");

                // Anchor discovery to this client's start, so a slow launch does not age its
                // own log out of a fixed fifteen-second window.
                DateTime sessionStart = DateTime.Now;
                if (_robloxPID > 0)
                {
                    try { using var client = Process.GetProcessById(_robloxPID); sessionStart = client.StartTime; }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    { App.Logger.WriteException(LOG_IDENT, ex); }
                }
                DateTime earliestLog = sessionStart.AddSeconds(-2);

                App.Logger.WriteLine(LOG_IDENT, "Opening Roblox log file...");

                while (true)
                {
                    if (IsDisposed) return;
                    logFileInfo = (Directory.Exists(logDirectory) ? new DirectoryInfo(logDirectory).GetFiles() : Array.Empty<FileInfo>())
                        .Where(x => IsPlayerSessionLog(x.Name) && IsSessionLogTime(x.CreationTime, earliestLog, DateTime.Now))
                        .OrderByDescending(x => x.CreationTime)
                        .FirstOrDefault()!;

                    if (logFileInfo is null) { await Task.Delay(1000); if (IsDisposed) return; continue; }

                    break;
                }

                LogLocation = logFileInfo.FullName;
            }
            else
            {
                logFileInfo = new FileInfo(LogLocation);
            }

            FileStream? logFileStream = null;
            while (!IsDisposed && logFileStream is null)
            {
                try { logFileStream = logFileInfo.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Log not ready; retrying: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
            if (logFileStream is null) return;
            OnLogOpen?.Invoke(this, EventArgs.Empty);

            App.Logger.WriteLine(LOG_IDENT, $"Opened {LogLocation}");

            using (logFileStream)
            {
                var lines = new LogLineBuffer();
                byte[] buffer = new byte[4096];
                while (!IsDisposed)
                {
                    // Roblox normally appends, but recover if a log is truncated in place.
                    if (logFileStream.Length < logFileStream.Position) { logFileStream.Position = 0; lines.Reset(); }
                    int read = await logFileStream.ReadAsync(buffer);
                    if (read == 0) await Task.Delay(1000);
                    else lines.Append(buffer.AsSpan(0, read), ReadLogEntry);
                }
            }
        }

        internal static bool IsPlayerSessionLog(string name) => name.Contains("Player", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

        internal static bool IsSessionLogTime(DateTime created, DateTime earliest, DateTime now) => created >= earliest && created <= now;

        internal void ReadLogEntry(string entry)
        {
            const string LOG_IDENT = "ActivityWatcher::ReadLogEntry";

            OnLogEntry?.Invoke(this, entry);

            _logEntriesRead += 1;

            // debug stats to ensure that the log reader is working correctly
            // if more than 1000 log entries have been read, only log per 100 to save on spam
            if (_logEntriesRead <= 1000 && _logEntriesRead % 50 == 0)
                App.Logger.WriteLine(LOG_IDENT, $"Read {_logEntriesRead} log entries");
            else if (_logEntriesRead % 100 == 0)
                App.Logger.WriteLine(LOG_IDENT, $"Read {_logEntriesRead} log entries");

            // get the log message from the read line
            int logMessageIdx = entry.IndexOf("[FLog::", StringComparison.Ordinal);
            if (logMessageIdx == -1)
            {
                // likely a log message that spanned multiple lines
                return;
            }

            string logMessage = entry[logMessageIdx..];

            if (InRobloxStudio || _launchMode == LaunchMode.Studio || _launchMode == LaunchMode.StudioAuth)
            {
                ProcessStudioLogEntry(logMessage);
            }
            else
            {
                ProcessPlayerLogEntry(logMessage);
            }
        }

        private void ProcessStudioLogEntry(string logMessage)
        {
            const string LOG_IDENT = "ActivityWatcher::ProcessStudioLogEntry";

            // incase this got called and InRobloxStudio is still false
            if (!InRobloxStudio)
            {
                InRobloxStudio = true;
            }

            // i need to find more logs stuff for studio lowkey
            if (!InStudioPlace)
            {
                if (logMessage.StartsWith(StudioPlaceOpenEntry))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Studio place opened");
                    InStudioPlace = true;

                    OnStudioPlaceOpened?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (InStudioPlace)
            {
                if (logMessage.StartsWith(StudioPlaceCloseEntry))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Studio place closed");
                    InStudioPlace = false;

                    OnStudioPlaceClosed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private void ProcessPlayerLogEntry(string logMessage)
        {
            const string LOG_IDENT = "ActivityWatcher::ProcessPlayerLogEntry";

            if (Data.PlaceId != 0 && logMessage.StartsWith(GameJoiningUniverseEntry))
                {
                    var match = Regex.Match(logMessage, GameJoiningUniversePattern);

                    if (!match.Success || !long.TryParse(match.Groups[1].Value, out long universe) || universe <= 0 ||
                        !long.TryParse(match.Groups[2].Value, out long user) || user < 0)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to assert format for game join universe entry");
                        App.Logger.WriteLine(LOG_IDENT, logMessage);
                        return;
                    }

                    Data.UniverseId = universe;
                    Data.UserId = user;

                    if (History.Any())
                    {
                        var lastActivity = History.First();

                        if (Data.UniverseId == lastActivity.UniverseId && Data.IsTeleport)
                            Data.RootActivity = lastActivity.RootActivity ?? lastActivity;
                    }
                    return;
                }

            if (logMessage.StartsWith(GameLeavingEntry))
            {
                App.Logger.WriteLine(LOG_IDENT, "User is back into the desktop app");

                OnAppClose?.Invoke(this, EventArgs.Empty);

                if (InGame)
                {
                    InGame = false;
                    Data.TimeLeft = DateTime.Now;
                    AddToHistory(Data);
                    OnGameLeave?.Invoke(this, EventArgs.Empty);
                }
                if (Data.PlaceId != 0)
                {
                    Data = new();
                }
                _joinAddress = _confirmedAddress = null;
                _teleportMarker = _reservedTeleportMarker = _shouldAutoRejoin = false;

                return;
            }

            if (logMessage.StartsWith(GameDisconnectReasonEntry))
            {
                var match = Regex.Match(logMessage, GameDisconnectReasonPattern);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int reasonCode))
                {

                    if (reasonCode == 1)
                    {
                        _shouldAutoRejoin = true;
                        App.Logger.WriteLine(LOG_IDENT, $"Inactivity timeout detected (reason code: {reasonCode})");
                    }
                    if (reasonCode == 277)
                    {
                        _shouldAutoRejoin = true;
                        App.Logger.WriteLine(LOG_IDENT, $"Internet Disconnection detected (reason code: {reasonCode})");
                    }
                    else
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Disconnect reason code: {reasonCode}");
                    }
                }
            }

            // Teleports and replacement joins do not always have a preceding disconnect line.
            if (logMessage.StartsWith(GameTeleportingEntry) || logMessage.StartsWith(GameJoiningReservedServerEntry))
            {
                _teleportMarker = true;
                if (logMessage.StartsWith(GameJoiningReservedServerEntry)) _reservedTeleportMarker = true;
                return;
            }
            if (logMessage.StartsWith(GameJoiningEntry))
            {
                var match = Regex.Match(logMessage, GameJoiningEntryPattern);
                if (!match.Success || !Guid.TryParse(match.Groups[1].Value, out _) ||
                    !long.TryParse(match.Groups[2].Value, out long place) || place <= 0 ||
                    !IPAddress.TryParse(match.Groups[3].Value, out _)) return;
                string job = match.Groups[1].Value;
                if (Data.PlaceId == place && Data.JobId == job) return;
                if (InGame)
                {
                    InGame = false;
                    Data.TimeLeft = DateTime.Now;
                    AddToHistory(Data);
                    OnGameLeave?.Invoke(this, EventArgs.Empty);
                }
                if (Data.PlaceId != 0) Data = new();
                Data.PlaceId = place;
                Data.JobId = job;
                Data.MachineAddress = _joinAddress = match.Groups[3].Value;
                _confirmedAddress = null;
                _shouldAutoRejoin = false;
                Data.IsTeleport = _teleportMarker;
                if (_reservedTeleportMarker) Data.ServerType = ServerType.Reserved;
                _teleportMarker = _reservedTeleportMarker = false;
                if (App.Settings.Prop.ShowServerDetails && Data.MachineAddressValid) _ = Data.QueryServerLocation(showErrors: false);
                if (App.Settings.Prop.ShowServerUptime) _ = Data.QueryServerTime(showErrors: false);
                App.Logger.WriteLine(LOG_IDENT, $"Joining Game ({Data})");
                return;
            }
            if (Data.PlaceId != 0 && logMessage.StartsWith(GameJoiningUDMUXEntry))
            {
                var match = Regex.Match(logMessage, GameJoiningUDMUXPattern);
                if (!match.Success || match.Groups[3].Value != _joinAddress ||
                    !IPAddress.TryParse(match.Groups[1].Value, out _) ||
                    !int.TryParse(match.Groups[2].Value, out int udmuxPort) || udmuxPort is < 1 or > 65535 ||
                    !int.TryParse(match.Groups[4].Value, out int rccPort) || rccPort is < 1 or > 65535) return;
                bool updateConfirmed = InGame && Data.UdmuxAddress != match.Groups[1].Value;
                Data.UdmuxAddress = match.Groups[1].Value;
                Data.UdmuxPort = udmuxPort;
                Data.RccAddress = match.Groups[3].Value;
                Data.RccPort = rccPort;
                Data.MachineAddress = Data.UdmuxAddress;
                TryConfirmJoin();
                if (updateConfirmed) OnConnectionUpdated?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!InGame && Data.PlaceId != 0 && logMessage.StartsWith(GameJoinedEntry))
            {
                var match = Regex.Match(logMessage, GameJoinedEntryPattern);
                if (!match.Success || !IPAddress.TryParse(match.Groups[1].Value, out _) ||
                    !int.TryParse(match.Groups[2].Value, out int port) || port is < 1 or > 65535) return;
                _confirmedAddress = match.Groups[1].Value;
                TryConfirmJoin();
                return;
            }

            if (!InGame && Data.PlaceId == 0)
            {
                // We are not in a game, nor are in the process of joining one

                if (logMessage.StartsWith(GameJoiningPrivateServerEntry))
                {
                    // we only expect to be joining a private server if we're not already in a game

                    Data.ServerType = ServerType.Private;

                    var match = Regex.Match(logMessage, GameJoiningPrivateServerPattern);

                    if (match.Groups.Count != 2)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to assert format for game join private server entry");
                        App.Logger.WriteLine(LOG_IDENT, logMessage);
                        return;
                    }

                    Data.AccessCode = match.Groups[1].Value;
                }
            }
            else if (InGame && Data.PlaceId != 0)
            {
                // We are confirmed to be in a game

                if (logMessage.StartsWith(GameDisconnectedEntry))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Disconnected from Game ({Data})");

                    InGame = false;
                    Data.TimeLeft = DateTime.Now;
                    AddToHistory(Data);
                    OnGameLeave?.Invoke(this, EventArgs.Empty);

                    var autoRejoinData = Data;
                    Data = new();

                    if (App.Settings.Prop.AutoRejoin && !SuppressAutoRejoin)
                    {
                        AutoRejoinTask = TryAutoRejoinAsync(autoRejoinData, Data);
                    }
                    else _shouldAutoRejoin = false;
                }
                else if (logMessage.StartsWith(GameMessageEntry))
                {
                    var match = Regex.Match(logMessage, GameMessageEntryPattern);

                    if (match.Groups.Count != 2)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to assert format for RPC message entry");
                        App.Logger.WriteLine(LOG_IDENT, logMessage);
                        return;
                    }

                    string messagePlain = match.Groups[1].Value;
                    Message? message;

                    App.Logger.WriteLine(LOG_IDENT, $"Received message: '{messagePlain}'");

                    if ((DateTime.Now - LastRPCRequest).TotalSeconds <= 1)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Dropping message as ratelimit has been hit");
                        return;
                    }

                    try
                    {
                        message = JsonSerializer.Deserialize<Message>(messagePlain);
                    }
                    catch (Exception)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to parse message! (JSON deserialization threw an exception)");
                        return;
                    }

                    if (message is null)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to parse message! (JSON deserialization returned null)");
                        return;
                    }

                    if (string.IsNullOrEmpty(message.Command))
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to parse message! (Command is empty)");
                        return;
                    }

                    if (message.Command == "SetLaunchData")
                    {
                        string? data;

                        try
                        {
                            data = message.Data.Deserialize<string>();
                        }
                        catch (Exception)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Failed to parse message! (JSON deserialization threw an exception)");
                            return;
                        }

                        if (data is null)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Failed to parse message! (JSON deserialization returned null)");
                            return;
                        }

                        if (data.Length > 200)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Data cannot be longer than 200 characters");
                            return;
                        }

                        Data.RPCLaunchData = data;
                    }

                    OnRPCMessage?.Invoke(this, message);

                    LastRPCRequest = DateTime.Now;
                }
            }
        }

        private async Task TryAutoRejoinAsync(ActivityData previous, ActivityData idle)
        {
            try
            {
                await Task.Delay(AutoRejoinDelay);
                // A delayed reconnect cannot close a replacement game, a pending join, or
                // a client whose watcher was disposed while the timer was waiting.
                if (IsDisposed || SuppressAutoRejoin || !App.Settings.Prop.AutoRejoin ||
                    !ReferenceEquals(Data, idle) || InGame || Data.PlaceId != 0 || !_shouldAutoRejoin) return;
                if (RejoinRequested is not null) RejoinRequested(previous);
                else { previous.RejoinServer(false); CloseProcess(_robloxPID); }
            }
            catch (Exception ex) { App.Logger.WriteException("ActivityWatcher::AutoRejoin", ex); }
            finally { if (ReferenceEquals(Data, idle)) _shouldAutoRejoin = false; }
        }

        private void TryConfirmJoin()
        {
            if (InGame || string.IsNullOrEmpty(_confirmedAddress) ||
                (_confirmedAddress != _joinAddress && _confirmedAddress != Data.UdmuxAddress)) return;
            InGame = true;
            Data.TimeJoined = DateTime.Now;
            App.Logger.WriteLine("ActivityWatcher", $"Joined Game ({Data})");
            OnGameJoin?.Invoke(this, EventArgs.Empty);
        }

        private void StartHTTPServer()
        {
            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://localhost:{HttpPort}/");
                _httpListener.Start();

                _ = ListenForHTTPRequests(_httpCancellationTokenSource.Token);

                App.Logger.WriteLine("ActivityWatcher", $"Studio RPC server active on port {HttpPort}");
            }
            catch (Exception ex) { App.Logger.WriteException("ActivityWatcher::Start", ex); }
        }

        public void StopHTTPServer()
        {
            _httpCancellationTokenSource.Cancel();

            if (_httpListener != null)
            {
                try { _httpListener.Close(); }
                catch { }
                _httpListener = null;
            }
        }

        private async Task ListenForHTTPRequests(CancellationToken token)
        {
            while (_httpListener?.IsListening == true && !token.IsCancellationRequested)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync().WaitAsync(token);

                    _ = Task.Run(() => ProcessHTTPRequest(context), token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    App.Logger.WriteException("ActivityWatcher::HTTPListener", ex);
                    await Task.Delay(1000, token);
                }
            }
        }

        private void ProcessHTTPRequest(HttpListenerContext context)
        {
            using var response = context.Response;

            try
            {
                if (context.Request.HttpMethod != "POST" || context.Request.Url?.AbsolutePath != "/rpc")
                {
                    response.StatusCode = 404;
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                string json = reader.ReadToEnd();
                var message = JsonSerializer.Deserialize<StudioMessage>(json);

                if (message != null)
                {
                    if (message.StudioCommand == "SetRichPresence")
                    {
                        var richPresenceData = message.Data.Deserialize<StudioRichPresence>();
                        if (richPresenceData != null)
                            message.Data = JsonSerializer.SerializeToElement(richPresenceData);
                    }

                    OnStudioRPCMessage?.Invoke(this, message);
                    response.StatusCode = 200;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("ActivityWatcher::ProcessHTTP", $"Error: {ex.Message}");
                response.StatusCode = 500;
            }
        }

        public void LoadGameHistory()
        {
            try
            {
                if (!File.Exists(GameHistoryCachePath))
                {
                    App.Logger.WriteLine("ActivityWatcher::LoadGameHistory", "No existing game history cache found");
                    History = new List<ActivityData>();
                    return;
                }

                string json = File.ReadAllText(GameHistoryCachePath);

                var options = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = null
                };

                var gameHistory = JsonSerializer.Deserialize<List<GameHistoryData>>(json, options);

                if (gameHistory != null)
                {
                    History = new List<ActivityData>();

                    foreach (var history in gameHistory)
                    {
                        var serverType = (ServerType)history.ServerType;

                        if (serverType == ServerType.Private || serverType == ServerType.Reserved)
                        {
                            continue;
                        }

                        if (history.UniverseId == 0 || history.PlaceId == 0 || history.TimeJoined == default)
                        {
                            continue;
                        }

                        var activity = new ActivityData
                        {
                            UniverseId = history.UniverseId,
                            PlaceId = history.PlaceId,
                            JobId = history.JobId,
                            UserId = history.UserId,
                            ServerType = serverType,
                            TimeJoined = history.TimeJoined,
                            TimeLeft = history.TimeLeft,
                        };

                        activity.UniverseDetails = UniverseDetails.LoadFromCache(activity.UniverseId);
                        History.Add(activity);
                    }

                    History = History
                        .GroupBy(x => x.UniverseId)
                        .SelectMany(g => g.OrderByDescending(x => x.TimeJoined).Take(3))
                        .OrderByDescending(x => x.TimeJoined)
                        .ToList();

                    App.Logger.WriteLine("ActivityWatcher::LoadGameHistory", $"Loaded {History.Count} game history entries from cache");
                }
                else
                {
                    History = new List<ActivityData>();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ActivityWatcher::LoadGameHistory", ex);
                History = new List<ActivityData>();
            }
        }

        public void SaveGameHistory()
        {
            try
            {
                Directory.CreateDirectory(Paths.Cache);

                var validHistory = History
                    .Where(activity =>
                        activity.ServerType != ServerType.Private &&
                        activity.ServerType != ServerType.Reserved &&
                        activity.UniverseId != 0 &&
                        activity.PlaceId != 0 &&
                        activity.TimeJoined != default)
                    .ToList();

                var limitedHistory = validHistory
                    .GroupBy(x => x.UniverseId)
                    .SelectMany(g => g.OrderByDescending(x => x.TimeJoined).Take(3))
                    .ToList();

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var gameHistory = limitedHistory.Select(activity => new GameHistoryData
                {
                    UniverseId = activity.UniverseId,
                    PlaceId = activity.PlaceId,
                    JobId = activity.JobId,
                    UserId = activity.UserId,
                    ServerType = (int)activity.ServerType,
                    TimeJoined = activity.TimeJoined,
                    TimeLeft = activity.TimeLeft,
                }).ToList();

                string json = JsonSerializer.Serialize(gameHistory, options);
                File.WriteAllText(GameHistoryCachePath, json);

                App.Logger.WriteLine("ActivityWatcher::SaveGameHistory",
                    $"Saved {gameHistory.Count} game history entries to cache ({limitedHistory.Count} after filtering)");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ActivityWatcher::SaveGameHistory", ex);
            }
        }

        private void AddToHistory(ActivityData activity)
        {
            if (activity.ServerType == ServerType.Private || activity.ServerType == ServerType.Reserved)
            {
                App.Logger.WriteLine("ActivityWatcher::AddToHistory",
                    $"Skipping {activity.ServerType} server from history");
                return;
            }

            if (activity.UniverseId == 0 || activity.PlaceId == 0 || activity.TimeJoined == default)
            {
                App.Logger.WriteLine("ActivityWatcher::AddToHistory",
                    "Skipping incomplete activity from history");
                return;
            }

            if (!string.IsNullOrEmpty(activity.JobId))
            {
                History.RemoveAll(x => x.JobId == activity.JobId);
            }

            History.Insert(0, activity);

            History = History
                .GroupBy(x => x.UniverseId)
                .SelectMany(g => g.OrderByDescending(x => x.TimeJoined).Take(3))
                .OrderByDescending(x => x.TimeJoined)
                .ToList();

            if (History.Count > 125)
            {
                History = History.Take(125).ToList();
            }

            SaveGameHistory();
            OnHistoryUpdated?.Invoke(this, EventArgs.Empty);

            App.Logger.WriteLine("ActivityWatcher::AddToHistory",
                $"Added history entry for universe {activity.UniverseId}. Total entries: {History.Count}");
        }

        public void Dispose()
        {
            IsDisposed = true;
            if (InRobloxStudio)
                StopHTTPServer();
            GC.SuppressFinalize(this);
        }
    }
}
