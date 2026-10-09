using System.IO;
using System.Reflection;
using Bloxstrap;

internal static class LoggerChecks
{
    private sealed class FailingStream(string path, Exception failure) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
    {
        internal int Attempts;
        public override void Write(ReadOnlySpan<byte> buffer) { Attempts++; throw failure; }
    }

    internal static void Run(Action<bool, string> check)
    {
        var privacy = new Logger();
        foreach (string value in new[] { "gameinfo:fixture-ticket+launchmode:play", ".ROBLOSECURITY=fixture-cookie; path=/",
            "authenticationTicket=fixture-auth&placeId=123", "access_token=\"fixture-token with spaces\"", "Authorization: Bearer fixture-bearer", "{\"authenticationTicket\":\"fixture-json-auth\"}",
            "gameinfo%3Afixture-encoded-ticket%2Blaunchmode%3Aplay" })
        {
            privacy.WriteLine("PrivacyFixture", value);
            check(!privacy.AsDocument.Contains("fixture-ticket") && !privacy.AsDocument.Contains("fixture-cookie") &&
                !privacy.AsDocument.Contains("fixture-auth") && !privacy.AsDocument.Contains("fixture-token") &&
                !privacy.AsDocument.Contains("fixture-bearer") && !privacy.AsDocument.Contains("fixture-json-auth") &&
                !privacy.AsDocument.Contains("fixture-encoded-ticket"), "Known credential fields are removed from in-memory diagnostics");
        }
        privacy.Initialize();
        try
        {
            using var sharedRead = new FileStream(privacy.FileLocation!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(sharedRead);
            string disk = reader.ReadToEnd();
            check(disk.Contains("[redacted]") && !disk.Contains("fixture-ticket") && !disk.Contains("fixture-cookie") &&
                !disk.Contains("fixture-auth") && !disk.Contains("fixture-token") && !disk.Contains("fixture-bearer") &&
                !disk.Contains("fixture-json-auth") && !disk.Contains("fixture-encoded-ticket"),
                "Initializing disk logging replays only the redacted diagnostic history");
        }
        finally { ((FileStream)typeof(Logger).GetField("_filestream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(privacy)!).Dispose(); }
        string loggedAddress = Bloxstrap.Utility.DiagnosticPrivacy.RequestAddress(new Uri("https://user:fixture-password@example.com/v1/join?authenticationTicket=fixture-secret#fixture-fragment"));
        check(loggedAddress == "https://example.com/v1/join", "HTTP diagnostics retain the endpoint while excluding userinfo, query parameters and fragments");
        const string ordinary = "placeId=123 jobId=fixture-job status=503 version-0123456789abcdef";
        check(Bloxstrap.Utility.DiagnosticPrivacy.Redact(ordinary) == ordinary, "Credential redaction preserves ordinary troubleshooting fields");
        string originalBase = Paths.Base;
        string retentionRoot = Path.Combine(originalBase, "logger-retention-" + Guid.NewGuid().ToString("N"));
        string retentionLogs = Path.Combine(retentionRoot, "Logs");
        Directory.CreateDirectory(retentionLogs);
        string expired = Path.Combine(retentionLogs, $"{App.ProjectName}_20260101T010101Z_123_{Guid.NewGuid():N}.log");
        string recent = Path.Combine(retentionLogs, $"{App.ProjectName}_20260102T010101Z_123_{Guid.NewGuid():N}.log");
        string locked = Path.Combine(retentionLogs, $"{App.ProjectName}_20260103T010101Z_123_{Guid.NewGuid():N}.log");
        string unrelated = Path.Combine(retentionLogs, "keep-my-notes.txt");
        string malformed = Path.Combine(retentionLogs, $"{App.ProjectName}_personal-notes.log");
        foreach (string path in new[] { expired, recent, locked, unrelated, malformed }) File.WriteAllText(path, "retention fixture");
        foreach (string path in new[] { expired, locked, unrelated, malformed }) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        Logger? retention = null;
        try
        {
            Paths.Initialize(retentionRoot);
            using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
            retention = new Logger();
            retention.Initialize();
            check(!File.Exists(expired), "Retention removes expired app diagnostics with the generated filename format");
            check(File.Exists(recent), "Retention preserves recent diagnostic files");
            check(File.Exists(unrelated) && File.ReadAllText(unrelated) == "retention fixture",
                "Retention preserves expired unrelated files placed in Logs");
            check(File.Exists(malformed), "Retention does not delete arbitrary files merely starting with the app name");
            check(File.Exists(locked) && retention.Initialized,
                "A locked expired log is preserved without preventing logger startup");
        }
        finally
        {
            ((FileStream?)typeof(Logger).GetField("_filestream", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(retention!))?.Dispose();
            Paths.Initialize(originalBase);
        }
        string blockedRoot = Path.Combine(originalBase, "logger-directory-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(blockedRoot);
        File.WriteAllText(Path.Combine(blockedRoot, "Logs"), "unrelated fixture; preserve");
        try
        {
            Paths.Initialize(blockedRoot);
            var blocked = new Logger();
            blocked.Initialize();
            check(!blocked.Initialized && blocked.NoWriteMode,
                "A file occupying the Logs directory falls back to memory without breaking startup");
            blocked.WriteLine("LoggerStorageFixture", "retained after directory creation failure");
            check(blocked.AsDocument.Contains("retained after directory creation failure") &&
                File.ReadAllText(Path.Combine(blockedRoot, "Logs")) == "unrelated fixture; preserve",
                "Unavailable log-directory initialization preserves conflicting files and later diagnostics");
        }
        finally { Paths.Initialize(originalBase); }
        var streamField = typeof(Logger).GetField("_filestream", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (Exception failure in new Exception[] { new IOException("Fixture unavailable volume"),
            new UnauthorizedAccessException("Fixture revoked write permission"), new ObjectDisposedException("fixture") })
        {
            var logger = new Logger();
            logger.Initialize();
            ((FileStream)streamField.GetValue(logger)!).Dispose();
            using var failed = new FailingStream(Path.Combine(Paths.Base, "logger-fault-" + Guid.NewGuid().ToString("N")), failure);
            streamField.SetValue(logger, failed);
            logger.WriteLine("LoggerStorageFixture", "first failed disk write");
            check(logger.NoWriteMode && !logger.Initialized && failed.Attempts == 1,
                "Diagnostic storage failure switches to memory without escaping: " + failure.GetType().Name);
            Parallel.For(0, 64, i =>
            {
                logger.WriteLine("LoggerStorageFixture", $"retained-{i:D2}");
                _ = logger.AsDocument;
            });
            check(failed.Attempts == 1 && logger.AsDocument.Contains("first failed disk write") &&
                Enumerable.Range(0, 64).All(i => logger.History.Count(line => line.EndsWith($"retained-{i:D2}")) == 1),
                "After storage failure, concurrent writes and document copies preserve every diagnostic without retry loops");
            var later = Task.Run(() => logger.WriteLine("LoggerStorageFixture", "after failure"));
            check(later.Wait(TimeSpan.FromSeconds(2)) && logger.AsDocument.Contains("after failure"),
                "A failed disk write releases the logger lock and later callers keep running");
        }
    }
}