using Bloxstrap.Roblox;
using System.Windows.Input;
using Bloxstrap.Integrations;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Threading;

namespace Bloxstrap.UI.ViewModels.ContextMenu
{
    internal class ServerHistoryViewModel : NotifyPropertyChangedViewModel, IDisposable
    {
        private readonly ActivityWatcher _activityWatcher;
        private readonly Func<string, Task> _fetchDetails;
        private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
        private bool _disposed;
        private int _loadVersion;
        internal Task HistoryLoadTask { get; private set; } = Task.CompletedTask;

        public List<ActivityData>? GameHistory { get; private set; }

        public GenericTriState LoadState { get; private set; } = GenericTriState.Unknown;

        public string Error { get; private set; } = String.Empty;

        public ICommand CloseWindowCommand => new RelayCommand(RequestClose);
        public ICommand CleanOldHistoryCommand => new RelayCommand(CleanOldHistory);

        public EventHandler? RequestCloseEvent;

        public ServerHistoryViewModel(ActivityWatcher activityWatcher) : this(activityWatcher, UniverseDetails.FetchBulk) { }

        internal ServerHistoryViewModel(ActivityWatcher activityWatcher, Func<string, Task> fetchDetails)
        {
            _activityWatcher = activityWatcher;
            _fetchDetails = fetchDetails;

            _activityWatcher.OnGameLeave += OnHistoryChanged;
            _activityWatcher.OnHistoryUpdated += OnHistoryChanged;

            LoadData();
        }

        private void OnHistoryChanged(object? sender, EventArgs e)
        {
            if (_disposed) return;
            if (_dispatcher.CheckAccess()) LoadData();
            else _dispatcher.BeginInvoke(LoadData);
        }

        private void LoadData()
        {
            if (!_disposed) HistoryLoadTask = LoadDataAsync(++_loadVersion);
        }

        private bool IsCurrent(int version) => !_disposed && version == _loadVersion;

        private async Task LoadDataAsync(int version)
        {
            LoadState = GenericTriState.Unknown;
            OnPropertyChanged(nameof(LoadState));

            try
            {
                await Task.Run(CleanOldEntries);
                if (!IsCurrent(version)) return;

                var entriesNeedingDetails = _activityWatcher.History
                    .Where(x => x.UniverseId != 0 && x.UniverseDetails is null &&
                           x.TimeJoined > DateTime.Now.AddDays(-30))
                    .ToList();

                if (entriesNeedingDetails.Any())
                {
                    var universeIds = entriesNeedingDetails
                        .Select(x => x.UniverseId)
                        .Distinct()
                        .ToList();

                    if (universeIds.Any())
                    {
                        string universeIdsString = String.Join(',', universeIds);
                        await _fetchDetails(universeIdsString);
                        if (!IsCurrent(version)) return;

                        foreach (var entry in entriesNeedingDetails)
                        {
                            entry.UniverseDetails = UniverseDetails.LoadFromCache(entry.UniverseId);
                        }
                    }
                }

                var history = ProcessAndConsolidateHistory(_activityWatcher.History);
                DetachDeleteEvents();
                GameHistory = history;

                // Hook up delete events
                if (GameHistory != null)
                {
                    foreach (var entry in GameHistory)
                    {
                        entry.OnDeleteRequested += OnActivityDeleteRequested;
                    }
                }

                OnPropertyChanged(nameof(GameHistory));

                LoadState = GenericTriState.Successful;
                OnPropertyChanged(nameof(LoadState));
            }
            catch (Exception ex)
            {
                if (!IsCurrent(version)) return;
                App.Logger.WriteException("ServerHistoryViewModel::LoadData", ex);

                Error = $"Failed to load game history: {ex.Message}";
                OnPropertyChanged(nameof(Error));

                LoadState = GenericTriState.Failed;
                OnPropertyChanged(nameof(LoadState));
            }
        }

        private List<ActivityData> ProcessAndConsolidateHistory(List<ActivityData> history)
        {
            var cutoffDate = DateTime.Now.AddDays(-30);
            var consolidatedHistory = new List<ActivityData>();
            var processedRoots = new HashSet<string>();

            foreach (var entry in history
                .Where(x => x.TimeJoined > cutoffDate)
                .OrderByDescending(x => x.TimeJoined))
            {
                if (entry.ServerType == ServerType.Private || entry.ServerType == ServerType.Reserved)
                    continue;

                if (entry.RootActivity != null && !string.IsNullOrEmpty(entry.RootActivity.JobId))
                {
                    var rootId = entry.RootActivity.JobId;

                    if (!processedRoots.Contains(rootId))
                    {
                        var relatedSessions = history.Where(x =>
                            (x.RootActivity?.JobId == rootId || x.JobId == rootId) &&
                            x.TimeJoined > cutoffDate)
                            .OrderByDescending(x => x.TimeJoined)
                            .ToList();

                        if (relatedSessions.Any())
                        {
                            var latestSession = relatedSessions.First();
                            var earliestSession = relatedSessions.Last();

                            var consolidatedEntry = new ActivityData
                            {
                                UniverseId = latestSession.UniverseId,
                                PlaceId = latestSession.PlaceId,
                                JobId = latestSession.JobId,
                                UserId = latestSession.UserId,
                                ServerType = latestSession.ServerType,
                                TimeJoined = earliestSession.RootActivity?.TimeJoined ?? earliestSession.TimeJoined,
                                TimeLeft = latestSession.TimeLeft,
                                UniverseDetails = latestSession.UniverseDetails,
                                IsTeleport = true,
                                RootJobId = rootId
                            };

                            consolidatedHistory.Add(consolidatedEntry);
                            processedRoots.Add(rootId);
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(entry.JobId) && !processedRoots.Contains(entry.JobId))
                {
                    consolidatedHistory.Add(entry);
                    processedRoots.Add(entry.JobId);
                }
            }

            return consolidatedHistory
                .GroupBy(x => x.UniverseId)
                .SelectMany(g => g.OrderByDescending(x => x.TimeJoined).Take(3))
                .OrderByDescending(x => x.TimeJoined)
                .ToList();
        }

        private void CleanOldEntries()
        {
            try
            {
                var cutoffDate = DateTime.Now.AddDays(-30);
                int oldCount = _activityWatcher.History.Count;

                _activityWatcher.History.RemoveAll(x => x.TimeJoined < cutoffDate);

                int removedCount = oldCount - _activityWatcher.History.Count;

                if (removedCount > 0)
                {
                    App.Logger.WriteLine("ServerHistoryViewModel::CleanOldEntries",
                        $"Removed {removedCount} old history entries (older than {30} days)");

                    _activityWatcher.SaveGameHistory();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ServerHistoryViewModel::CleanOldEntries", ex);
            }
        }

        private void CleanOldHistory()
        {
            if (_disposed) return;
            CleanOldEntries();
            LoadData();
        }

        private void OnActivityDeleteRequested(object? sender, string jobId)
        {
            DeleteHistoryEntry(jobId);
        }

        public void DeleteHistoryEntry(string jobId)
        {
            if (_disposed) return;
            try
            {
                int removedCount = _activityWatcher.History.RemoveAll(x => x.JobId == jobId);

                if (!string.IsNullOrEmpty(jobId))
                {
                    removedCount += _activityWatcher.History.RemoveAll(x =>
                        x.RootActivity?.JobId == jobId);
                }

                if (removedCount > 0)
                {
                    App.Logger.WriteLine("ServerHistoryViewModel::DeleteHistoryEntry",
                        $"Removed {removedCount} history entries for job {jobId}");

                    _activityWatcher.SaveGameHistory();

                    LoadData();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ServerHistoryViewModel::DeleteHistoryEntry", ex);
            }
        }

        private void DetachDeleteEvents()
        {
            if (GameHistory is null) return;
            foreach (var entry in GameHistory) entry.OnDeleteRequested -= OnActivityDeleteRequested;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _loadVersion++;
            _activityWatcher.OnGameLeave -= OnHistoryChanged;
            _activityWatcher.OnHistoryUpdated -= OnHistoryChanged;
            DetachDeleteEvents();
            RequestCloseEvent = null;
        }

        private void RequestClose() => RequestCloseEvent?.Invoke(this, EventArgs.Empty);
    }
}
