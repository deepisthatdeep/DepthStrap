namespace Bloxstrap.Models.Persistable
{
    public class AppState : IJsonNormalizable
    {
        public string VersionGuid { get; set; } = string.Empty;

        public Dictionary<string, string> PackageHashes { get; set; } = new();

        public int Size { get; set; }

        void IJsonNormalizable.Normalize()
        {
            VersionGuid ??= "";
            PackageHashes = (PackageHashes ?? new()).Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value);
        }
    }
}
