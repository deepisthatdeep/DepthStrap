namespace Bloxstrap.Utility
{
    internal static class LegacyRobloxStateMigration
    {
        internal static void Migrate(JsonManager<RobloxState> legacy, JsonManager<DistributionState> player,
            JsonManager<DistributionState> studio)
        {
            if (!legacy.IsSaved) return;
            if (!legacy.Load(false))
                throw new IOException("Could not read legacy Roblox installation state. The original file was preserved; repair it before retrying migration.");
            player.Prop = new DistributionState
            {
                VersionGuid = legacy.Prop.Player.VersionGuid,
                PackageHashes = legacy.Prop.Player.PackageHashes,
                Size = legacy.Prop.Player.Size,
                InstallationPending = player.Prop.InstallationPending,
                ModManifest = legacy.Prop.ModManifest.ToDictionary(x => x, _ => new ModFileEntry())
            };
            studio.Prop = new DistributionState
            {
                VersionGuid = legacy.Prop.Studio.VersionGuid,
                PackageHashes = legacy.Prop.Studio.PackageHashes,
                Size = legacy.Prop.Studio.Size,
                InstallationPending = studio.Prop.InstallationPending,
                ModManifest = studio.Prop.ModManifest
            };
            if (!player.TrySave() || !studio.TrySave())
                throw new IOException("Could not save migrated Roblox installation state. The original legacy file was preserved; retry migration after correcting the save problem.");
            legacy.Delete();
        }
    }
}
