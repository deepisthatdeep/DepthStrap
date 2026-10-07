using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Bloxstrap;
using Bloxstrap.Utility;
using Bloxstrap.UI.ViewModels.Settings;

internal static class HistoryChecks
{
    private sealed class CountedStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal long BytesRead;
        public override int Read(Span<byte> buffer) { int count = base.Read(buffer); BytesRead += count; return count; }
    }

    internal static void Run(Action<bool, string> check)
    {
        string text = "\ufefffirst\r\nQuerétaro 😃\r\n\r\n東京 🩸\nlast";
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            check(ReverseLineReader.Read(stream, blockSize: 7).SequenceEqual(new[] { "last", "東京 🩸", "", "Querétaro 😃", "first" }),
                "Reverse history reader preserves UTF-8 across tiny blocks, CRLF, blank lines, BOM and an unterminated tail");
        foreach (var pair in new[] { ("", Array.Empty<string>()), ("\n", new[] { "" }), ("a\n\n", new[] { "", "a" }) })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(pair.Item1));
            check(ReverseLineReader.Read(stream, blockSize: 2).SequenceEqual(pair.Item2), "Reverse history reader handles empty files and trailing newline boundaries");
        }
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\n" + new string('x', 100) + "\nlast\n")))
            check(ReverseLineReader.Read(stream, blockSize: 7, maxLineBytes: 32).SequenceEqual(new[] { "last", "first" }),
                "Oversized corrupt history lines are skipped with bounded memory without hiding surrounding valid lines");
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try { ReverseLineReader.Read(stream, cancellation.Token).ToList(); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Cancelled history reads stop before accessing another block");
        }
        byte[] large = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 20000).Select(i => i + ":" + new string('x', 100) + "\n")));
        using (var stream = new CountedStream(large))
        {
            var lines = ReverseLineReader.Read(stream).Take(50).ToList();
            check(lines.Count == 50 && lines[0].StartsWith("19999:") && lines[49].StartsWith("19950:") && stream.BytesRead <= 65536,
                "The last fifty lines of a multi-megabyte history need at most one 64 KB tail read");
        }

        string path = Path.Combine(Paths.Logs, "CompetitiveSessions", $"{DateTime.Today:yyyy-MM-dd}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string? original = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllLines(path, Enumerable.Range(0, 60).Select(i => JsonSerializer.Serialize(new {
                type = "event", timestamp = DateTime.Today.AddMinutes(i), placeId = i,
                location = "Querétaro 😃 " + i, regionQuality = "Good", cloudflareWarp = i % 2 == 0, runLabel = "Fixture" })));
            File.AppendAllText(path, "{broken\n{\"type\":\"event\"");
            var viewModel = new CompetitivePageViewModel();
            Wait(viewModel.HistoryLoadTask);
            check(viewModel.RecentMatches.Count == 50 && viewModel.RecentMatches[0].Place == "59" &&
                viewModel.RecentMatches[49].Place == "10" && viewModel.RecentMatches[0].Region.Contains("😃") &&
                viewModel.RunSummaries.Sum(x => x.Matches) == 50,
                "Background history loads the newest fifty valid matches and matching summaries despite malformed appended lines");
            File.WriteAllText(path, JsonSerializer.Serialize(new { type = "event", timestamp = DateTime.Now, placeId = 999, location = "New" }));
            viewModel.RefreshHistoryCommand.Execute(null);
            Task cancelledLoad = viewModel.HistoryLoadTask;
            viewModel.StopSessionPolling();
            Wait(cancelledLoad);
            check(viewModel.RecentMatches[0].Place == "59", "Unloading the page cancels a queued history result instead of applying stale rows");
            viewModel.StartSessionPolling();
            Wait(viewModel.HistoryLoadTask);
            check(viewModel.RecentMatches.Count == 1 && viewModel.RecentMatches[0].Place == "999",
                "Returning to the history page resumes a cancelled load and displays fresh data");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                viewModel.RefreshHistoryCommand.Execute(null);
                Wait(viewModel.HistoryLoadTask);
                check(viewModel.RecentMatches.Count == 1 && viewModel.RecentMatches[0].Place == "999" &&
                    viewModel.HistoryStatus.Contains("unavailable"),
                    "A temporarily locked history file retains existing rows and reports a recoverable status");
            }
            File.WriteAllText(path, "");
            viewModel.RefreshHistoryCommand.Execute(null);
            Wait(viewModel.HistoryLoadTask);
            check(viewModel.RecentMatches.Count == 0 && viewModel.RunSummaries.Count == 0 && viewModel.HistoryStatus.Contains("No matches"),
                "An empty refreshed history clears old matches and shows an empty-state message");
            viewModel.StopSessionPolling();
        }
        finally { if (original is null) File.Delete(path); else File.WriteAllText(path, original); }
    }

    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background,
            (_, _) => { if (task.IsCompleted || clock.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; }, Application.Current.Dispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!task.IsCompleted) throw new TimeoutException("History loading did not finish.");
        task.GetAwaiter().GetResult();
    }
}
