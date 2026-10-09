using Bloxstrap.Competitive;
/*
 *  Froststrap
 *  Copyright (c) Froststrap Team
 *
 *  This file is part of Froststrap and is distributed under the terms of the
 *  GNU Affero General Public License, version 3 or later.
 *
 *  SPDX-License-Identifier: AGPL-3.0-or-later
 *
 *  Description: Nix flake for shipping for Nix-darwin, Nix, NixOS, and modules
 *               of the Nix ecosystem. 
 */

using Bloxstrap.Integrations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;

namespace Bloxstrap.UI.ViewModels.Settings
{
    public partial class RegionSelectorViewModel : ObservableObject
    {
        private const string LOG_IDENT = "RegionSelectorViewModel";
        private readonly HashSet<string> _displayedServerIds = new();
        private readonly DepthStrapServerBrowser _fetcher;
        private readonly Action<string> _launch;
        private readonly Func<string, CancellationToken, Task<List<OmniSearchContent>>> _searchGames;
        private readonly Func<List<ThumbnailRequest>, CancellationToken, Task<string?[]>> _loadThumbnails;
        private int _gameSearchVersion;
        private CancellationTokenSource? _gameSearchCts;
        private int _queryVersion;
        internal const string AllRegions = "All regions";
        private Dictionary<int, string>? _dcMap;
        private CancellationTokenSource? _searchDebounceCts;
        private CancellationTokenSource? _scanCts;
        public System.Windows.Input.ICommand CancelScanCommand => new RelayCommand(() => _scanCts?.Cancel());

        [ObservableProperty][NotifyPropertyChangedFor(nameof(ServerListMessage))] private bool _hasSearched;
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(SearchCommand))] private string _placeId = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ServerListMessage), nameof(IsServerListEmptyAndNotLoading), nameof(ShowLoadingIndicator))]
        [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(LoadMoreCommand), nameof(SearchGamesCommand))] private bool _isLoading;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowLoadingIndicator))]
        [NotifyCanExecuteChangedFor(nameof(SearchGamesCommand))] private bool _isGameSearchLoading;
        [ObservableProperty] private string _loadingMessage = "";
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))] private string _nextCursor = "";
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(SearchGamesCommand))] private string _searchQuery = "";
        [ObservableProperty] private OmniSearchContent? _selectedSearchResult;
        [ObservableProperty] private int _selectedSortOrder = 2;
        [ObservableProperty][NotifyPropertyChangedFor(nameof(ServerListMessage))] private int _lastFetchProcessedCount;
        [ObservableProperty] private string? _thumbnailUrl;

        // Preferred-region mode (competitive)
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SearchCommand))] private bool _usePreferredRegionMode =
            App.Settings.Prop.CompetitiveModeEnabled && App.Settings.Prop.PreferredRegionEnabled;
        [ObservableProperty] private string _preferredCity = App.Settings.Prop.CompetitivePreferredCity;

        public ObservableCollection<string> Regions { get; } = new();
        public ObservableCollection<ServerEntry> Servers { get; } = new();
        public ObservableCollection<OmniSearchContent> SearchResults { get; } = new();

        public sealed record ServerSortOption
        {
            public string Content { get; init; } = "";
            public int Tag { get; init; }
        }
        public List<ServerSortOption> SortOrderOptions { get; } = new()
        {
            new() { Content = "Large Servers", Tag = 2 },
            new() { Content = "Small Servers", Tag = 1 }
        };

        public bool IsServerListEmpty => Servers.Count == 0;
        public bool IsServerListEmptyAndNotLoading => IsServerListEmpty && !IsLoading;
        public bool ShowLoadingIndicator => IsLoading && !IsGameSearchLoading;

        public string ServerListMessage => IsLoading ? "" :
            !HasSearched ? "Enter a Place ID and click Search to view servers." :
            IsServerListEmpty ? (LastFetchProcessedCount == 0 ? "No public servers found." : "No verified matches for this region. Choose All regions to view servers whose location is unknown.") : "";

        public IAsyncRelayCommand SearchCommand { get; }
        public IAsyncRelayCommand LoadMoreCommand { get; }
        public IAsyncRelayCommand SearchGamesCommand { get; }

        public RegionSelectorViewModel() : this(new DepthStrapServerBrowser()) { }
        internal RegionSelectorViewModel(DepthStrapServerBrowser fetcher, Action<string>? launch = null,
            Func<string, CancellationToken, Task<List<OmniSearchContent>>>? searchGames = null,
            Func<List<ThumbnailRequest>, CancellationToken, Task<string?[]>>? loadThumbnails = null)
        {
            _fetcher = fetcher;
            _launch = launch ?? (uri => Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true }));
            _searchGames = searchGames ?? GameSearching.GetGameSearchResultsAsync;
            _loadThumbnails = loadThumbnails ?? Thumbnails.GetThumbnailUrlsAsync;
            Regions.Add(AllRegions);
            if (string.IsNullOrWhiteSpace(App.Settings.Prop.SelectedRegion)) App.Settings.Prop.SelectedRegion = AllRegions;
            Servers.CollectionChanged += (_, _) => {
                OnPropertyChanged(nameof(IsServerListEmpty));
                OnPropertyChanged(nameof(IsServerListEmptyAndNotLoading));
            };

            SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsLoading && long.TryParse(PlaceId, out var id) && id > 0);
            SearchGamesCommand = new AsyncRelayCommand(SearchGamesAsync, () => !IsLoading && !IsGameSearchLoading && !string.IsNullOrWhiteSpace(SearchQuery));
            LoadMoreCommand = new AsyncRelayCommand(LoadMoreServersAsync, () => !IsLoading && !string.IsNullOrWhiteSpace(NextCursor));

        }

        partial void OnSearchQueryChanged(string value)
        {
            CancelGameSearch();
            SearchResults.Clear();
            PlaceId = long.TryParse(value, out _) ? value : "";

            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
            _searchDebounceCts = new CancellationTokenSource();
            _ = DebouncedSearchTriggerAsync(_searchDebounceCts.Token);
        }

        partial void OnPlaceIdChanged(string value) => InvalidateResults();
        partial void OnSelectedSortOrderChanged(int value) => InvalidateResults();
        partial void OnUsePreferredRegionModeChanged(bool value) => InvalidateResults();
        private void InvalidateResults()
        {
            _queryVersion++;
            _scanCts?.Cancel();
            Servers.Clear(); _displayedServerIds.Clear(); NextCursor = "";
            HasSearched = false; LastFetchProcessedCount = 0; LoadingMessage = "";
        }

        partial void OnSelectedSearchResultChanged(OmniSearchContent? value)
        {
            if (value == null) return;
            PlaceId = value.RootPlaceId.ToString();
            SearchQuery = value.RootPlaceId.ToString();
        }

        public string? SelectedRegion
        {
            get => App.Settings.Prop.SelectedRegion;
            set
            {
                if (App.Settings.Prop.SelectedRegion == value) return;
                App.Settings.Prop.SelectedRegion = value!;
                InvalidateResults();
                OnPropertyChanged();
                SearchCommand.NotifyCanExecuteChanged();
                App.Settings.Save();
            }
        }

        partial void OnPreferredCityChanged(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            App.Settings.Prop.CompetitivePreferredCity = value.Trim();
            InvalidateResults();
            App.Settings.Save();
        }

        private async Task DebouncedSearchTriggerAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(600, token);
                if (!token.IsCancellationRequested && !IsLoading && !string.IsNullOrWhiteSpace(SearchQuery))
                {
                    await SearchGamesAsync(token);
                }
            }
            catch (OperationCanceledException) { }
        }

        internal void StopPendingRequests()
        {
            _scanCts?.Cancel(); _searchDebounceCts?.Cancel();
            CancelGameSearch();
        }
        private void CancelGameSearch()
        {
            _gameSearchVersion++;
            _gameSearchCts?.Cancel();
            _gameSearchCts = null;
            IsGameSearchLoading = false;
        }
        internal async Task InitializeRegionsAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
            LoadingMessage = "Loading datacenters...";
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _scanCts = budget;
            try
            {
                var result = await _fetcher.GetDatacentersAsync(budget.Token) ?? await LoadDatacentersFromCacheAsync();
                if (result is null) { LoadingMessage = "Region lookup unavailable. Public server browsing remains available."; return; }
                Regions.Clear(); Regions.Add(AllRegions);
                foreach (var region in result.Value.regions) Regions.Add(region);
                _dcMap = result.Value.datacenterMap;
                await SaveDatacentersToCacheAsync(result.Value);
                if (!Regions.Contains(SelectedRegion ?? "")) SelectedRegion = AllRegions;
                LoadingMessage = "";
            }
            catch (OperationCanceledException) { LoadingMessage = "Region lookup timed out or stopped. Public server browsing remains available."; }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                LoadingMessage = "Region lookup unavailable. Public server browsing remains available.";
            }
            finally { _scanCts = null; IsLoading = false; }
        }

        private async Task SearchAsync()
        {
            HasSearched = true;
            IsLoading = true;
            Servers.Clear();
            _displayedServerIds.Clear();
            NextCursor = "";
            LastFetchProcessedCount = 0;

            try
            {
                if (UsePreferredRegionMode)
                    await SearchPreferredRegionAsync();
                else
                    await SearchManualRegionAsync();
            }
            catch (OperationCanceledException) { LoadingMessage = "Search stopped. Available results are retained."; }
            catch (Exception ex)
            {
                App.Logger.WriteException("RegionSelector::Search", ex);
                LoadingMessage = "Server search unavailable. Try again later.";
            }
            finally
            {
                _scanCts = null;
                IsLoading = false;
            }
        }

        /// <summary>
        /// Legacy behavior: exact match against the manually selected region, up to 3 pages.
        /// </summary>
        private async Task SearchManualRegionAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedRegion))
            {
                Frontend.ShowMessageBox("Please select a region first.", MessageBoxImage.Warning);
                return;
            }

            LoadingMessage = "Searching servers...";
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _scanCts = budget;

            int pagesChecked = 0;
            while (pagesChecked < 3)
            {
                await LoadServersAsync(pagesChecked == 0);
                pagesChecked++;
                if (string.IsNullOrWhiteSpace(NextCursor)) break;
            }
            LoadingMessage = $"Loaded {Servers.Count} server(s) across {pagesChecked} page(s).";
        }

        /// <summary>
        /// Preferred-region scan: bounded by page count AND wall-clock time, ranks every server
        /// with the competitive classifier, stops early once an ideal (preferred-city) match is found.
        /// Never hangs indefinitely; best fallback candidate always wins if no ideal exists.
        /// </summary>
        private async Task SearchPreferredRegionAsync()
        {
            int maxPages = Math.Clamp(App.Settings.Prop.PreferredRegionMaxPages, 1, 50);
            var deadline = DateTime.Now.AddSeconds(Math.Clamp(App.Settings.Prop.PreferredRegionSearchTimeoutSeconds, 3, 120));

            LoadingMessage = "Loading region registry...";

            // DC-id registry makes classification much stronger; best effort with a cap so the UI never stalls
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(App.Settings.Prop.PreferredRegionSearchTimeoutSeconds, 3, 120)));
            _scanCts = budget;
            try { await CompetitiveRegionService.EnsureRegistryLoadedAsync(_fetcher).WaitAsync(budget.Token); }
            catch (OperationCanceledException) { throw; }

            int pagesChecked = 0;

            int timeoutSeconds = Math.Clamp(App.Settings.Prop.PreferredRegionSearchTimeoutSeconds, 3, 120);
            LoadingMessage = $"Scanning servers for {App.Settings.Prop.CompetitivePreferredCity} (max {maxPages} pages / {timeoutSeconds}s)...";

            while (pagesChecked < maxPages && DateTime.Now < deadline)
            {
                await LoadServersAsync();
                pagesChecked++;

                // ideal match found -> stop scanning (spec: don't burn hundreds of pages)
                bool idealFound = Servers.Any(c => c.Score >= 95);
                if (idealFound || string.IsNullOrWhiteSpace(NextCursor))
                    break;
            }

            RankDisplayedServers();
            LoadingMessage = $"Checked {pagesChecked} page(s): {Servers.Count} server(s) ranked by region preference.";
            _scanCts = null;
        }

        private async Task LoadServersAsync(bool resetCursor = false)
        {
            if (string.IsNullOrWhiteSpace(PlaceId) || string.IsNullOrWhiteSpace(SelectedRegion)) return;

            if (resetCursor) NextCursor = "";
            if (!long.TryParse(PlaceId, out var placeIdLong)) return;
            int version = _queryVersion;
            var result = await _fetcher.FetchServerInstancesAsync(placeIdLong, NextCursor, SelectedSortOrder, _scanCts?.Token ?? default);
            _scanCts?.Token.ThrowIfCancellationRequested();
            if (version != _queryVersion) throw new OperationCanceledException();
            if (result == null) return;

            int number = Servers.Count + 1;
            foreach (var s in result.Servers)
            {
                string mapped = s.DataCenterId is int dc && _dcMap?.TryGetValue(dc, out var region) == true ? region : s.Region;
                if ((UsePreferredRegionMode || SelectedRegion == AllRegions || string.Equals(mapped, SelectedRegion, StringComparison.OrdinalIgnoreCase) ||
                    (mapped != "Unknown" && mapped.Split(',')[0].Trim().Equals(SelectedRegion.Split(',')[0].Trim(), StringComparison.OrdinalIgnoreCase))) && _displayedServerIds.Add(s.Id))
                {
                    var cls = UsePreferredRegionMode ? CompetitiveRegionService.Classify(s.Region, s.DataCenterId, source: s.RegionSource) : null;
                    Servers.Add(new ServerEntry
                    {
                        Number = number++,
                        ServerId = s.Id,
                        Players = $"{s.Playing}/{s.MaxPlayers}",
                        Region = cls?.DisplayName ?? (mapped != "Unknown" ? mapped : s.Region),
                        DataCenterId = s.DataCenterId,
                        Uptime = s.UptimeDisplay,
                        Score = cls?.Score ?? 0,
                        Quality = cls?.Quality.ToString() ?? "Unknown",
                        JoinCommand = new RelayCommand(() => JoinServer(placeIdLong, s.Id))
                    });
                }
            }

            LastFetchProcessedCount += result.Servers.Count;
            NextCursor = result.NextCursor;
        }

        private void RankDisplayedServers()
        {
            var ranked = Servers.OrderByDescending(x => x.Score).ThenBy(x => int.TryParse(x.Players.Split('/')[0], out int count) ? count : int.MaxValue).ToList();
            Servers.Clear();
            for (int i = 0; i < ranked.Count; i++) { ranked[i].Number = i + 1; Servers.Add(ranked[i]); }
        }
        private void JoinServer(long placeId, string serverId)
        {
            try
            {
                _launch($"roblox://experiences/start?placeId={placeId}&gameInstanceId={serverId}");
            }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT, ex); }
        }

        private string GetCachePath() => Path.Combine(Paths.Cache, "DataCentersCache.json");

        private async Task SaveDatacentersToCacheAsync((List<string> regions, Dictionary<int, string> datacenterMap) data)
        {
            try
            {
                Directory.CreateDirectory(Paths.Cache);
                var json = JsonSerializer.Serialize(new DatacentersCache { Regions = data.regions, DatacenterMap = data.datacenterMap, LastUpdated = DateTime.UtcNow });
                await File.WriteAllTextAsync(GetCachePath(), json);
            }
            catch { /* Ignore cache save errors */ }
        }

        private async Task<(List<string> regions, Dictionary<int, string> datacenterMap)?> LoadDatacentersFromCacheAsync()
        {
            try
            {
                if (!File.Exists(GetCachePath())) return null;
                var json = await File.ReadAllTextAsync(GetCachePath());
                var cache = JsonSerializer.Deserialize<DatacentersCache>(json);
                return (cache != null && cache.LastUpdated > DateTime.UtcNow.AddDays(-7)) ? (cache.Regions, cache.DatacenterMap) : null;
            }
            catch { return null; }
        }

        internal async Task SearchGamesAsync(CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(SearchQuery) || long.TryParse(SearchQuery, out _)) return;

            _gameSearchCts?.Cancel();
            int version = ++_gameSearchVersion;
            string query = SearchQuery;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            _gameSearchCts = budget;
            IsGameSearchLoading = true;
            try
            {
                var results = await _searchGames(query, budget.Token);

                if (budget.IsCancellationRequested || version != _gameSearchVersion) return;

                if (results.Any())
                {
                    var thumbRequests = results.Select(r => new ThumbnailRequest
                    {
                        Type = ThumbnailType.GameIcon,
                        TargetId = r.UniverseId,
                        Size = "128x128"
                    }).ToList();

                    try
                    {
                        var urls = await _loadThumbnails(thumbRequests, budget.Token);
                        for (int i = 0; i < results.Count && i < urls.Length; i++) results[i].ThumbnailUrl = urls[i];
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { App.Logger.WriteException(LOG_IDENT, ex); }
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (budget.IsCancellationRequested || version != _gameSearchVersion) return;
                    SearchResults.Clear();
                    foreach (var r in results)
                        SearchResults.Add(r);
                });
            }
            catch (OperationCanceledException)
            {
                if (version == _gameSearchVersion) LoadingMessage = "Game search stopped or timed out. Try again.";
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Game search failed: {ex.Message}");
                if (version == _gameSearchVersion) LoadingMessage = "Game search unavailable. Try again later, or enter a Place ID.";
            }
            finally
            {
                if (version == _gameSearchVersion) { _gameSearchCts = null; IsGameSearchLoading = false; }
            }
        }

        private async Task LoadMoreServersAsync()
        {
            IsLoading = true;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _scanCts = budget;
            try
            {
                for (int i = 0; i < 5 && !string.IsNullOrWhiteSpace(NextCursor); i++) await LoadServersAsync();
                if (UsePreferredRegionMode) RankDisplayedServers();
                LoadingMessage = UsePreferredRegionMode ? $"Loaded {Servers.Count} server(s), ranked by region preference." : $"Loaded {Servers.Count} server(s). Unknown regions are shown only under All regions.";
            }
            catch (OperationCanceledException) { LoadingMessage = "Search stopped. Available results are retained."; }
            catch (Exception ex) { App.Logger.WriteException(LOG_IDENT, ex); LoadingMessage = "More servers could not be loaded. Try again."; }
            finally { _scanCts = null; IsLoading = false; }
        }
    }
}
