using System.Diagnostics;
using System.IO;
using System.Text;
using Bloxstrap.Networking;

internal static class SystemTuningChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var restart = SystemPerformanceTuning.RestartStartInfo();
        check(Path.IsPathFullyQualified(restart.FileName) && restart.FileName.EndsWith("System32\\shutdown.exe", StringComparison.OrdinalIgnoreCase) &&
            restart.ArgumentList.SequenceEqual(new[] { "/r", "/t", "0" }), "Restart uses the Windows executable without forcing applications closed or scheduling during setup");
        foreach (var mode in Enum.GetValues<SystemTuningMode>())
        {
            var start = SystemPerformanceTuning.StartInfo(mode);
            check(start.UseShellExecute == (mode != SystemTuningMode.Preview) && (mode == SystemTuningMode.Preview || start.Verb == "runas") &&
                start.WindowStyle == ProcessWindowStyle.Hidden && !start.ArgumentList.Contains("-ExecutionPolicy") &&
                start.ArgumentList.Sum(x => x.Length) < 30000,
                "Tuning uses bounded embedded commands and requests elevation only for selected changes: " + mode);
            string command = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList.Last()));
            check(command.Contains("-Mode '" + mode + "'") && command.Contains("FromBase64String") && !command.Contains(Bloxstrap.Paths.Base),
                "Elevated tuning does not read a user-writable script path: " + mode);
        }
        bool rejected = false;
        try { SystemPerformanceTuning.StartInfo((SystemTuningMode)99); } catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected, "Unknown tuning modes cannot enter the PowerShell command");
        string source = Path.Combine(Bloxstrap.Paths.Base, "tuning-script.ps1");
        File.WriteAllText(source, SystemPerformanceTuning.Script);
        string fixture = Path.GetFullPath("Tests/DepthStrap.RegressionTests/SystemTuningFixture.ps1");
        var test = new ProcessStartInfo(SystemPerformanceTuning.StartInfo(SystemTuningMode.Preview).FileName)
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        string fixtureCommand = "& {\n" + File.ReadAllText(fixture) + "\n} -Source ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(source)) + "')))";
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(fixtureCommand)) }) test.ArgumentList.Add(argument);
        using var process = Process.Start(test)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new TimeoutException("Tuning fixture exceeded its test budget."); }
        string result = output.GetAwaiter().GetResult();
        check(process.ExitCode == 0 && result.Contains("PASS: 18 PowerShell fixture checks"), "PowerShell profile apply/restore fixtures: " + result + error.GetAwaiter().GetResult());
    }
}
