public class GithubReleaseAsset
{
    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = null!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;
}