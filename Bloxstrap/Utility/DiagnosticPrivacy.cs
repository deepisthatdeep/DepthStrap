namespace Bloxstrap.Utility;

internal static class DiagnosticPrivacy
{
    private static readonly Regex SecretFields = new(
        """(?<key>\.ROBLOSECURITY|gameinfo|authenticationTicket|access_token|refresh_token)(?<separator>["']?\s*(?::|=|%3A|%3D)\s*)(?:"[^"]*"|'[^']*'|[^\s&+;,}]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Bearer = new(@"\bBearer\s+[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static string Redact(string message) => Bearer.Replace(
        SecretFields.Replace(message, match => match.Groups["key"].Value + match.Groups["separator"].Value + "[redacted]"),
        "Bearer [redacted]");

    internal static string RequestAddress(Uri? address) => address?.IsAbsoluteUri == true
        ? address.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
        : "[address unavailable]";
}