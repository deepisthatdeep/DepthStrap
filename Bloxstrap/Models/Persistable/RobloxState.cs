namespace Bloxstrap.Models.Persistable
{
    public class RobloxState : IJsonNormalizable
    {
        public AppState Player { get; set; } = new();

        public AppState Studio { get; set; } = new();

        public List<string> ModManifest { get; set; } = new();

        void IJsonNormalizable.Normalize()
        {
            Player ??= new();
            Studio ??= new();
            ((IJsonNormalizable)Player).Normalize();
            ((IJsonNormalizable)Studio).Normalize();
            ModManifest = (ModManifest ?? new()).Where(DistributionState.IsSafeModPath)
                .Select(x => x.Replace('/', Path.DirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
