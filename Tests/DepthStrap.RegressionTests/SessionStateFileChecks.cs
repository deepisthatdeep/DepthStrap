using System.IO;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Models;

internal static class SessionStateFileChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string original = Paths.Base;
        string root = Path.Combine(original, "session-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            Paths.Initialize(root);
            Directory.CreateDirectory(Paths.Cache);
            check(CompetitiveNetworkState.TryRead() is null, "Missing session file is an ordinary no-data state");
            File.WriteAllText(CompetitiveNetworkState.FilePath, JsonSerializer.Serialize(new { location = new string('x', 65536) }));
            check(CompetitiveNetworkState.TryRead() is null,
                "Oversized but valid session JSON is rejected rather than read into the polling UI");
            string unicode = JsonSerializer.Serialize(new { location = new string('界', 30000) },
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (unicode.Length >= 65536 || System.Text.Encoding.UTF8.GetByteCount(unicode) <= 65536)
                throw new InvalidOperationException("The raw Unicode fixture must distinguish encoded byte and character limits.");
            File.WriteAllText(CompetitiveNetworkState.FilePath, unicode);
            check(CompetitiveNetworkState.TryRead() is null,
                "Session size limits count encoded bytes, including raw Unicode, rather than character count");
            File.WriteAllText(CompetitiveNetworkState.FilePath, "{\"location\":\"London\",\"warp\":\"off\"}");
            var recovered = CompetitiveNetworkState.TryRead();
            check(recovered?.Location == "London" && recovered.Warp == "off",
                "A normal watcher state loads immediately after rejected oversized data");
            check(File.ReadAllText(CompetitiveNetworkState.FilePath).Contains("London"),
                "Session loading preserves the watcher-owned file and never deletes it to recover");
        }
        finally { Paths.Initialize(original); }
    }
}