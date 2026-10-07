namespace Bloxstrap.Roblox
{
    internal static class RobloxUpdatePolicy
    {
        public static string? RequestedVersion(bool isPlayer, string? explicitVersion, Settings settings, string installedVersion, bool installedExecutableExists, bool installationPending = false)
        {
            if (!string.IsNullOrWhiteSpace(explicitVersion)) return explicitVersion;
            if (!isPlayer) return null;
            if (!string.IsNullOrWhiteSpace(settings.RobloxPlayerVersionOverride)) return settings.RobloxPlayerVersionOverride;
            return settings.PauseRobloxUpdates && (installedExecutableExists || installationPending) && RobloxVersionArchive.IsVersionId(installedVersion) ? installedVersion : null;
        }
    }
}
