namespace Bloxstrap.Competitive
{
    internal static class BadRegionAutoLog
    {
        internal static bool ShouldLeave(Models.CompetitiveNetworkEvent result, Settings settings,
            string currentJob, DateTime now, RegionClassification classification, bool recoveryClient = false,
            DateTime? currentJoinStartedAt = null)
        {
            if (recoveryClient || result.PlaceId == EntryPlaceId || !settings.AutoLeaveBadChimeRegion || !settings.CompetitiveModeEnabled || !IsDeepwoken(result) ||
                string.IsNullOrEmpty(currentJob) || result.JobId != currentJob || classification.IsConfiguredRegion ||
                classification.Quality is not (RegionQuality.Bad or RegionQuality.Poor) || result.RegionSource == "Unknown" || result.RegionSource.Length == 0 ||
                now < result.Timestamp) return false;
            // A slow location service can finish after a minute while this same join is still active.
            // Use the live join boundary when available; callers without it keep the age safeguard.
            if (currentJoinStartedAt is DateTime joined)
            {
                if (joined == default || joined > now || result.Timestamp < joined) return false;
            }
            else if (now - result.Timestamp > TimeSpan.FromSeconds(60)) return false;
            if (!string.IsNullOrWhiteSpace(settings.CompetitivePreferredCity) || settings.CompetitiveFallbackCities.Any(x => !string.IsNullOrWhiteSpace(x))) return true;
            // Before a city has been measured, only a confirmed outside-area join qualifies.
            return (classification.Quality is RegionQuality.Bad or RegionQuality.Poor) &&
                (settings.PreferNorthAmericaOnly || settings.PreferEuropeOnly);
        }

        internal const string HomeUri = "roblox://navigation/home";
        internal const long EntryPlaceId = 4111023553;
        // Entry place verified against Roblox's public universe API. Subplaces are identified by universe,
        // never by a fixed list of Layer/Chime place IDs; reserved destinations cannot be resumed by URI.
        internal const string RejoinUri = "roblox://experiences/start?placeId=4111023553";
        internal static bool IsDeepwoken(Models.CompetitiveNetworkEvent result) =>
            result.IsDeepwoken && result.UniverseId == CompetitiveRegionService.DeepwokenUniverseId;
        internal static bool ShouldRejoin(Models.CompetitiveNetworkEvent result, Settings settings) =>
            settings.AutoLeaveBadChimeRegion && IsDeepwoken(result);
        internal static bool IsIdleHomeLog(string log) =>
            !log.Contains("[FLog::Output] ! Joining game") && !log.Contains("GameJoinUtil::initiateTeleportToPlace") &&
            !log.Contains("GameJoinUtil::joinGamePostPrivateServer") && !log.Contains("GameJoinUtil::initiateTeleportToReservedServer");
    }
}
