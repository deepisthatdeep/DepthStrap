namespace Bloxstrap.Roblox;

internal static class RobloxClientVersion
{
    internal static string? ParseReleaseText(string? value)
    {
        if (value is null) return null;
        var match = Regex.Match(value,
            @"\A\s*(?<a>[0-9]{1,10})\s*(?<separator>[,.])\s*(?<b>[0-9]{1,10})\s*\k<separator>\s*(?<c>[0-9]{1,10})\s*\k<separator>\s*(?<d>[0-9]{1,10})\s*\z",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? string.Join(".", new[] { "a", "b", "c", "d" }.Select(name =>
            ulong.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))) : null;
    }

    internal static string? ReadRelease(string executable)
    {
        try
        {
            var metadata = FileVersionInfo.GetVersionInfo(executable);
            // Roblox's revision exceeds the 16-bit VS_FIXEDFILEINFO fields.
            // FilePrivatePart would silently truncate 7421053 to 15485.
            return ParseReleaseText(metadata.FileVersion) ?? ParseReleaseText(metadata.ProductVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
