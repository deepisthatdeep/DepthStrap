using Bloxstrap.Integrations;
using Bloxstrap.Models.APIs.RoValra;

namespace Bloxstrap.Competitive
{
    /// <summary>
    /// Region preference quality. Score is a "Region Preference Score", NOT a ping measurement.
    /// Unknown must never be displayed as a confirmed region pull.
    /// </summary>
    public enum RegionQuality
    {
        Ideal,       // the measured preferred city
        Excellent,   // first configured fallback
        Good,        // second+ fallback / nearby dynamic region
        Acceptable,  // within the preferred area
        Poor,        // outside the preferred area
        Bad,         // known poor target for competitive play
        Unknown      // no reliable location data - do NOT call this a region pull
    }

    public sealed class RegionClassification
    {
        public RegionQuality Quality { get; init; }
        public string DisplayName { get; init; } = "";
        public int Score { get; init; }
        public bool IsPreferred { get; init; }
        public bool IsConfiguredRegion { get; init; }
        public bool IsNorthAmerica { get; init; }

        /// <summary>Where the region data came from: "Roblox DataCenterId", "RoValra IP geolocation", ...</summary>
        public string Source { get; init; } = "Unknown";
    }

    /// <summary>
    /// One physical Roblox datacenter location with everything we know about it.
    /// </summary>
    public sealed class DatacenterRecord
    {
        public int LocationId { get; set; }
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public string CountryCode { get; set; } = "";   // ISO ("US") when available
        public string CountryName { get; set; } = "";
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public HashSet<int> DataCenterIds { get; set; } = new();

        /// <summary>"City, Country" style display key (v1.5.1 compatible).</summary>
        public string RegionKey => $"{City}, {CountryCode}".Trim().TrimEnd(',', ' ');

        public double DistanceKm(DatacenterRecord other)
        {
            if (Latitude is null || Longitude is null || other.Latitude is null || other.Longitude is null)
                return double.MaxValue;

            const double R = 6371.0; // km
            double dLat = ToRad(other.Latitude.Value - Latitude.Value);
            double dLon = ToRad(other.Longitude.Value - Longitude.Value);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRad(Latitude.Value)) * Math.Cos(ToRad(other.Latitude.Value)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private static double ToRad(double deg) => deg * Math.PI / 180.0;
    }

    /// <summary>
    /// Central competitive region logic: datacenter registry, city alias matching,
    /// quality classification and preference scoring. Pure classification is unit-testable
    /// (no I/O on the Classify path once the registry is loaded).
    /// </summary>
    public static class CompetitiveRegionService
    {
        private const string LOG_IDENT = "CompetitiveRegionService";

        // Central place for Deepwoken identity. Universe ID, not Place ID:
        // Deepwoken uses many places and Chime destinations change over time.
        public const long DeepwokenUniverseId = 1359573625;

        private static readonly string _registryCachePath = Path.Combine(Paths.Cache, "CompetitiveDatacenters.json");

        // Built-in aliases for known Roblox US datacenter cities (from the RoValra list).
        // Keys are normalized city names; values are extra terms that should resolve to them.
        private static readonly Dictionary<string, string[]> _cityAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dallas"]      = new[] { "dfw", "fort worth", "texas", "tx" },
            ["chicago"]     = new[] { "illinois", "il" },
            ["ashburn"]     = new[] { "virginia", "va", "north virginia", "northern virginia" },
            ["secaucus"]    = new[] { "new jersey", "nj" },
            ["new york city"] = new[] { "new york", "nyc", "ny" },
            ["los angeles"] = new[] { "l.a.", "la" },
            ["san jose"]    = new[] { "santa clara valley" },
            ["seattle"]     = new[] { "washington", "wa" },
            ["boardman"]    = new[] { "oregon", "or", "pnw", "pacific northwest" },
            ["columbus"]    = new[] { "ohio", "oh" },
            ["atlanta"]     = new[] { "georgia", "ga" },
            ["miami"]       = new[] { "florida", "fl" },
        };

        // Note: "Secaucus" (NJ) and "New York City" frequently share Roblox DC ids in RoValra's list.
        private static readonly Dictionary<string, string> _citySynonyms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["secaucus"] = "new york city",
            ["new york city"] = "secaucus",
        };

        private static readonly HashSet<string> _naCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            "US", "CA", "MX", "United States", "Canada", "Mexico"
        };

        private static readonly HashSet<string> _transatlanticCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            // Europe + UK (and close neighbors Roblox serves from there)
            "GB", "UK", "DE", "FR", "NL", "SE", "NO", "DK", "FI", "PL", "ES", "IT", "PT",
            "CH", "AT", "BE", "IE", "CZ", "RO", "GR", "TR",
            "United Kingdom", "Germany", "France", "Netherlands", "Sweden", "Norway", "Denmark",
            "Finland", "Poland", "Spain", "Italy", "Portugal", "Switzerland", "Austria",
            "Belgium", "Ireland", "Czechia", "Romania", "Greece", "Turkey"
        };

        private static readonly HashSet<string> _oceaniaCountries = new(StringComparer.OrdinalIgnoreCase)
        {
            "AU", "NZ", "Australia", "New Zealand"
        };

        // registry state (in-memory, loaded once per app run)
        private static List<DatacenterRecord>? _records;
        private static Dictionary<int, DatacenterRecord> _dcIdToRecord = new();
        private static readonly object _registryLock = new();

        #region Registry loading

        /// <summary>
        /// Ensures the datacenter registry is available: memory -> fresh disk cache -> API -> stale disk cache.
        /// Returns false when nothing could be loaded (classification still works with display strings).
        /// </summary>
        public static async Task<bool> EnsureRegistryLoadedAsync(DepthStrapServerBrowser? fetcher = null)
        {
            lock (_registryLock)
            {
                if (_records is not null && _records.Count > 0)
                    return true;
            }

            bool fromCache = false;
            bool cacheFresh = false;

            // 1. disk cache (7 days TTL, same as the existing DataCentersCache.json)
            try
            {
                if (File.Exists(_registryCachePath))
                {
                    using var fs = File.OpenRead(_registryCachePath);
                    var cached = JsonSerializer.Deserialize<RegistryCache>(fs);

                    if (cached?.Entries is not null && cached.Entries.Count > 0)
                    {
                        cacheFresh = cached.LastUpdated > DateTime.UtcNow.AddDays(-7);
                        fromCache = true;
                        if (!cacheFresh)
                            App.Logger.WriteLine(LOG_IDENT, "Using stale competitive datacenter cache");

                        lock (_registryLock)
                        {
                            BuildRegistry(cached.Entries);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException($"{LOG_IDENT}::EnsureRegistryLoadedAsync", ex);
            }

            bool haveData = false;
            lock (_registryLock) haveData = _records is not null && _records.Count > 0;

            // 2. API (when we have no data at all, or the cache copy is stale)
            if (!haveData || (fromCache && !cacheFresh))
            {
                try
                {
                    fetcher ??= new DepthStrapServerBrowser();
                    var apiEntries = await fetcher.GetDatacenterEntriesAsync();

                    if (apiEntries is not null && apiEntries.Count > 0)
                    {
                        lock (_registryLock)
                            BuildRegistry(apiEntries);

                        try
                        {
                            Directory.CreateDirectory(Paths.Cache);
                            File.WriteAllText(_registryCachePath, JsonSerializer.Serialize(new RegistryCache
                            {
                                Entries = apiEntries,
                                LastUpdated = DateTime.UtcNow
                            }));
                        }
                        catch { /* cache write is best effort */ }

                        haveData = true;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException($"{LOG_IDENT}::EnsureRegistryLoadedAsync", ex);
                }
            }

            lock (_registryLock) haveData = _records is not null && _records.Count > 0;

            if (!haveData)
                App.Logger.WriteLine(LOG_IDENT, "Region registry unavailable (API + cache failed); falling back to display-string matching");

            return haveData;
        }

        private sealed class RegistryCache
        {
            public List<DatacenterEntry> Entries { get; set; } = new();
            public DateTime LastUpdated { get; set; }
        }

        private static void BuildRegistry(List<DatacenterEntry> entries)
        {
            var records = new List<DatacenterRecord>();
            var map = new Dictionary<int, DatacenterRecord>();

            foreach (var entry in entries)
            {
                if (entry.Inactive || entry.DataCenterIds.Count == 0)
                    continue;

                var record = new DatacenterRecord
                {
                    LocationId = entry.LocationId,
                    City = Normalize(entry.Location.City),
                    State = Normalize(entry.Location.Region),
                    CountryCode = Normalize(entry.Location.Country),
                    CountryName = Normalize(entry.Location.CountryName),
                    Latitude = entry.Location.Latitude,
                    Longitude = entry.Location.Longitude,
                };

                foreach (var id in entry.DataCenterIds)
                {
                    record.DataCenterIds.Add(id);
                    map[id] = record;
                }

                records.Add(record);
            }

            _records = records;
            _dcIdToRecord = map;

            App.Logger.WriteLine(LOG_IDENT, $"Region registry loaded: {records.Count} locations, {map.Count} datacenter ids");
        }

        internal static List<string> UnprobedLocations(IEnumerable<Networking.RoutingTarget> targets, string playerCountry)
        {
            var published = targets.ToList();
            lock (_registryLock)
                return (_records ?? new List<DatacenterRecord>()).Where(x => !Networking.RoutingTargetDiscovery.CoversLocation(published, x.City, x.CountryCode) &&
                    (Networking.RegionGeography.IsNorthAmerica(playerCountry) ? Networking.RegionGeography.IsNorthAmerica(x.CountryCode) :
                     Networking.RegionGeography.IsEurope(playerCountry) ? Networking.RegionGeography.IsEurope(x.CountryCode) :
                     x.CountryCode.Equals(playerCountry, StringComparison.OrdinalIgnoreCase)))
                    .Select(x => x.RegionKey).Distinct().Order().ToList();
        }
        public static DatacenterRecord? FindByDataCenterId(int datacenterId)
        {
            lock (_registryLock)
                return _dcIdToRecord.TryGetValue(datacenterId, out var record) ? record : null;
        }

        /// <summary>Resolves a user-entered city/alias to the best matching registry record.</summary>
        public static DatacenterRecord? MatchCity(string cityOrAlias)
        {
            if (string.IsNullOrWhiteSpace(cityOrAlias))
                return null;

            var aliases = ResolveAliases(cityOrAlias);

            lock (_registryLock)
            {
                if (_records is null)
                    return null;

                DatacenterRecord? best = null;
                int bestScore = -1;

                foreach (var record in _records)
                {
                    int score = 0;

                    // exact city match beats state/country matches
                    if (aliases.Any(a => a.Equals(record.City, StringComparison.OrdinalIgnoreCase)))
                        score = Math.Max(score, 3);
                    else if (aliases.Any(a => MatchesField(record.City, a)))
                        score = Math.Max(score, 2);

                    if (record.State.Length > 0 && aliases.Any(a => a.Equals(record.State, StringComparison.OrdinalIgnoreCase) || MatchesField(record.State, a)))
                        score = Math.Max(score, 1);

                    if (aliases.Any(a => a.Equals(record.CountryCode, StringComparison.OrdinalIgnoreCase) || a.Equals(record.CountryName, StringComparison.OrdinalIgnoreCase)))
                        score = Math.Max(score, 1);

                    // synonym groups (Secaucus <-> New York City share DC ids)
                    if (_citySynonyms.TryGetValue(record.City, out var syn) && aliases.Contains(Normalize(syn)))
                        score = Math.Max(score, 2);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = record;
                    }
                }

                // a zero score means nothing actually matched - don't hand back an arbitrary record
                return bestScore > 0 ? best : null;
            }
        }

        /// <summary>Expands a user-entered city into its full alias set (case-insensitive).</summary>
        public static List<string> ResolveAliases(string cityOrAlias)
        {
            var result = new List<string>();
            string normalized = Normalize(cityOrAlias).ToLowerInvariant();

            if (normalized.Length > 0)
                result.Add(normalized);

            // direct alias table hit?
            foreach (var (key, values) in _cityAliases)
            {
                bool hit = key == normalized || values.Any(v => v == normalized);
                if (!hit && _citySynonyms.TryGetValue(key, out var syn) && Normalize(syn) == normalized)
                    hit = true;

                if (hit)
                {
                    result.Add(key.ToLowerInvariant());
                    foreach (var v in values)
                        result.Add(v.ToLowerInvariant());
                }
            }

            return result.Distinct().ToList();
        }

        private static bool MatchesField(string field, string alias)
        {
            if (field.Length == 0 || alias.Length == 0)
                return false;

            // short codes (tx, nj, va...) must match exactly to avoid "il" matching random words
            if (alias.Length <= 2)
                return field.Equals(alias, StringComparison.OrdinalIgnoreCase);

            return field.Contains(alias, StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string? value) =>
            (value ?? "").Trim().Replace("\r", " ").Replace("\n", " ").Trim();

        #endregion

        #region Classification

        /// <summary>
        /// Classifies a region. Pure logic once the registry is loaded - safe to unit test.
        /// </summary>
        public static RegionClassification Classify(string? region, int? datacenterId = null, string source = "Unknown")
        {
            var settings = App.Settings.Prop;

            DatacenterRecord? record = null;
            if (datacenterId is > 0)
                record = FindByDataCenterId(datacenterId.Value);

            // parse the display string: "City, Country" or "City, State, Country"
            string city = "", state = "", country = "";
            if (!string.IsNullOrWhiteSpace(region))
            {
                var parts = region.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1) city = Normalize(parts[0]);
                if (parts.Length == 3) state = Normalize(parts[1]);
                if (parts.Length >= 2) country = Normalize(parts[^1]);
                if (country.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) country = "";
            }

            // no registry record yet? try to match by display string
            if (record is null && city.Length > 0)
                record = MatchCity(city);

            // Live joins must classify even before the datacenter API/cache is available.
            // Keep the city specific: Austin, Texas must not be treated as Dallas.
            if (record is null && city.Length > 0 && !city.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                record = new DatacenterRecord { City = city, State = state, CountryCode = country };

            bool isNA = IsNorthAmerica(record, country);

            // --- list matching: preferred first, then ordered fallbacks ---
            int score = -1; // default: unknown
            bool listMatched = false;
            string? matchedEntryName = null;

            if (record is not null)
            {
                var candidates = new List<(string Name, int TierScore)>
                {
                    (settings.CompetitivePreferredCity, 100),
                };
                for (int i = 0; i < settings.CompetitiveFallbackCities.Count; i++)
                {
                    int tier = i == 0 ? 90 : i == 1 ? 80 : 70;
                    candidates.Add((settings.CompetitiveFallbackCities[i], tier));
                }

                foreach (var (name, tier) in candidates)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var aliases = ResolveAliases(name);
                    if (RecordMatches(record, aliases))
                    {
                        score = tier;
                        listMatched = true;
                        matchedEntryName = name;
                        break; // first (highest-tier) match wins - preferred beats fallbacks by order
                    }
                }
            }

            string resolvedCountry = record?.CountryCode.Length > 0 ? record.CountryCode : country;
            if (!listMatched && resolvedCountry.Length > 0)
            {
                bool isEU = Networking.RegionGeography.IsEurope(resolvedCountry);
                var preferred = MatchCity(settings.CompetitivePreferredCity);
                bool sameArea = settings.PreferNorthAmericaOnly ? isNA : settings.PreferEuropeOnly ? isEU :
                    preferred is not null && (
                        (Networking.RegionGeography.IsNorthAmerica(preferred.CountryCode) && isNA) ||
                        (Networking.RegionGeography.IsEurope(preferred.CountryCode) && isEU) ||
                        preferred.CountryCode.Equals(resolvedCountry, StringComparison.OrdinalIgnoreCase));
                bool constrained = settings.PreferNorthAmericaOnly || settings.PreferEuropeOnly || preferred is not null;
                score = !constrained ? -1 : sameArea ? 50 : 10;
                if (sameArea && preferred is not null && record is not null && preferred.DistanceKm(record) <= 1500) score = 70;
            }
            var quality = score switch
            {
                >= 95 => RegionQuality.Ideal,
                >= 85 => RegionQuality.Excellent,
                >= 70 => RegionQuality.Good,
                >= 50 => RegionQuality.Acceptable,
                >= 20 => RegionQuality.Poor,
                >= 0 => RegionQuality.Bad,
                _ => RegionQuality.Unknown
            };

            if (!listMatched && score >= 0 && ((settings.PreferNorthAmericaOnly && !isNA) || (settings.PreferEuropeOnly && !Networking.RegionGeography.IsEurope(resolvedCountry))))
                quality = RegionQuality.Bad;

            string displayName = record is not null
                ? (record.CountryName.Length > 0 && record.CountryCode != record.CountryName
                    ? $"{record.City}, {record.CountryName}"
                    : record.RegionKey)
                : Normalize(region);

            if (string.IsNullOrEmpty(displayName))
                displayName = "Unknown";

            return new RegionClassification
            {
                Quality = quality,
                DisplayName = displayName,
                Score = score,
                IsPreferred = listMatched && matchedEntryName == settings.CompetitivePreferredCity,
                IsConfiguredRegion = listMatched,
                IsNorthAmerica = isNA,
                Source = source
            };
        }

        private static bool RecordMatches(DatacenterRecord record, List<string> aliases)
        {
            foreach (var alias in aliases)
            {
                if (alias.Equals(record.City, StringComparison.OrdinalIgnoreCase) || MatchesField(record.City, alias))
                    return true;

                // Use state aliases only when the city is itself unspecified/state-only.
                if (record.City.Equals(record.State, StringComparison.OrdinalIgnoreCase) &&
                    record.State.Length > 0 && alias.Equals(record.State, StringComparison.OrdinalIgnoreCase))
                    return true;

                // synonym groups
                if (_citySynonyms.TryGetValue(record.City, out var syn) && aliases.Contains(Normalize(syn).ToLowerInvariant()))
                    return true;
            }

            return false;
        }

        private static bool IsNorthAmerica(DatacenterRecord? record, string countryToken)
        {
            if (record is not null)
            {
                if (_naCountries.Contains(record.CountryCode))
                    return true;
                if (_naCountries.Contains(record.CountryName))
                    return true;
            }

            return _naCountries.Contains(countryToken);
        }

        #endregion
    }
}
