namespace Bloxstrap.Models.Persistable
{
    public class DistributionState : IJsonNormalizable
    {
        public string VersionGuid { get; set; } = string.Empty;

        public Dictionary<string, string> PackageHashes { get; set; } = new();

        public int Size { get; set; }

        public bool InstallationPending { get; set; }

        public Dictionary<string, ModFileEntry> ModManifest { get; set; } = new();

        internal static bool IsSafeModPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
            !Path.IsPathRooted(path) && path.Split(new[] { '/', '\\' }).All(part =>
                part.Length > 0 && part != "." && part != ".." && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

        void IJsonNormalizable.Normalize()
        {
            bool damaged = false;
            if (!Roblox.RobloxVersionArchive.IsVersionId(VersionGuid))
            {
                damaged = !string.IsNullOrEmpty(VersionGuid);
                VersionGuid = "";
            }
            else VersionGuid = VersionGuid.ToLowerInvariant();
            if (Size < 0) { Size = 0; damaged = true; }
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (PackageHashes is null) damaged = true;
            else foreach (var pair in PackageHashes)
            {
                if (pair.Value is null || !Regex.IsMatch(pair.Key, @"\A[A-Za-z0-9][A-Za-z0-9_.-]*\z") ||
                    !Regex.IsMatch(pair.Value, @"\A[a-fA-F0-9]{32}\z") || !hashes.TryAdd(pair.Key, pair.Value.ToLowerInvariant()))
                    damaged = true;
            }
            PackageHashes = hashes;
            var mods = new Dictionary<string, ModFileEntry>(StringComparer.OrdinalIgnoreCase);
            if (ModManifest is null) damaged = true;
            else foreach (var pair in ModManifest)
            {
                if (pair.Value is null || pair.Value.Size < 0 || !IsSafeModPath(pair.Key) ||
                    !mods.TryAdd(pair.Key.Replace('/', Path.DirectorySeparatorChar), pair.Value)) damaged = true;
            }
            ModManifest = mods;
            InstallationPending |= damaged;
        }
    }
}
