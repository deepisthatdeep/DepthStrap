using System.IO;
using System.Text.Json;
using Bloxstrap;
using Bloxstrap.Networking;
using Bloxstrap.UI.Elements.Settings.Pages;
using Bloxstrap.UI.ViewModels.Settings;

internal static class ExportAndNetworkGuardChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var originalFlags = App.FastFlags.Prop;
        string path = Path.Combine(Paths.Cache, "atomic-export-fixture.json");
        Directory.CreateDirectory(Paths.Cache);
        try
        {
            App.FastFlags.Prop = new() { ["FStringFixture"] = "Quotes \" and newline\nremain intact" };
            var page = new FastFlagEditorPage();
            page.ExportTo(path);
            string saved = File.ReadAllText(path);
            check(JsonSerializer.Deserialize<Dictionary<string, string>>(saved)!["FStringFixture"] == App.FastFlags.Prop["FStringFixture"].ToString(),
                "FastFlags export round-trips escaped values through the atomic writer");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            App.FastFlags.Prop["FStringFixture"] = "replacement";
            bool rejected = false;
            try { page.ExportTo(path); } catch (UnauthorizedAccessException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == saved,
                "Failed FastFlags export preserves the previous export bytes and reports failure");
            check(!Directory.EnumerateFiles(Paths.Cache, "atomic-export-fixture.json.*.tmp").Any(),
                "Failed FastFlags export cleans its staged file");
        }
        finally
        {
            App.FastFlags.Prop = originalFlags;
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }

        string sentinel = Path.Combine(Paths.Cache, "RegionCalibration.json");
        string? previous = File.Exists(sentinel) ? File.ReadAllText(sentinel) : null;
        try
        {
            File.WriteAllText(sentinel, "existing calibration fixture");
            int logStart = App.Logger.History.Count;
            bool refused = false;
            try { RegionCalibrationService.RunComparisonAsync(true, true, null, CancellationToken.None, () => true).GetAwaiter().GetResult(); }
            catch (NetworkComparisonUnavailableException ex) { refused = ex.Message == RegionCalibrationService.PlayerPrerequisite; }
            check(refused && File.ReadAllText(sentinel) == "existing calibration fixture",
                "The backend refuses running-player comparisons before clearing history or changing routes");
            var status = App.Logger.History.Skip(logStart).ToArray();
            check(status.Length == 1 && status[0].Contains(RegionCalibrationService.PlayerPrerequisite) && !status[0].Contains("Exception"),
                "Expected comparison refusal logs a status line without an exception stack");
            using var gate = RegionCalibrationService.OpenOperationGate();
            bool acquired = gate.WaitOne(0);
            check(acquired, "A refused comparison releases its operation gate for retry");
            try
            {
                refused = false;
                try { RegionCalibrationService.RunComparisonAsync(false, false, null, CancellationToken.None,
                    () => throw new InvalidOperationException("Player check must not run while another operation holds the gate")).GetAwaiter().GetResult(); }
                catch (NetworkComparisonUnavailableException) { refused = true; }
                check(refused, "An already-running network operation is reported as an expected refusal before any work starts");
            }
            finally { if (acquired) gate.Release(); }
            var model = new CompetitivePageViewModel();
            check(model.NetworkComparisonPrerequisite == RegionCalibrationService.PlayerPrerequisite,
                "The comparison action displays the same prerequisite enforced by its backend");
            model.StopSessionPolling();
            HistoryChecks.Wait(model.HistoryLoadTask);
        }
        finally { if (previous is null) File.Delete(sentinel); else File.WriteAllText(sentinel, previous); }
    }
}
