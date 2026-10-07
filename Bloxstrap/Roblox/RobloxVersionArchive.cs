namespace Bloxstrap.Roblox
{
    public static class RobloxVersionArchive
    {
        public static bool IsVersionId(string? value) => value is not null &&
            Regex.IsMatch(value, @"\Aversion-[a-fA-F0-9]{16}\z", RegexOptions.CultureInvariant);

        public static IReadOnlyList<string> InstalledPlayerVersions() => !Directory.Exists(Paths.Versions)
            ? Array.Empty<string>() : Directory.GetDirectories(Paths.Versions)
                .Where(x => IsVersionId(Path.GetFileName(x)) && RobloxClientFiles.IsPlayerComplete(Path.Combine(x, App.RobloxPlayerAppName)))
                .OrderByDescending(Directory.GetLastWriteTimeUtc).Select(Path.GetFileName).OfType<string>().ToArray();

        public static HashSet<string> RetainedVersions() => InstalledPlayerVersions()
            .Take(Math.Clamp(App.Settings.Prop.RobloxVersionArchiveLimit, 1, 10))
            .Append(App.Settings.Prop.RobloxPlayerVersionOverride)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
