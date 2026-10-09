using DepthStrap.Recovery;

internal static class RecoveryTestProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--ui") { ToolkitUiChecks.Run(args[1]); return; }
        if (args.SequenceEqual(new[] { "--socket" })) { SocketPinSmoke.Run(); return; }
        if (args.SequenceEqual(new[] { "--inspect" }))
        {
            var adapters = AdapterInspection.Inspect();
            var hardware = HardwareInspection.InspectAsync().GetAwaiter().GetResult();
            if (!HardwareInspection.IsMasked(hardware.MaskedUuid) || !HardwareInspection.IsMasked(hardware.MaskedBoardSerial))
                throw new InvalidOperationException("Unmasked inspection result.");
            using var inventory = JsonDocument.Parse(WindowsMacBackend.RunAsync("Inventory", Guid.Empty, null, CancellationToken.None).GetAwaiter().GetResult());
            Console.WriteLine($"PASS: read-only production inspection; {adapters.Length} adapters, {hardware.Monitors.Length} displays, {inventory.RootElement.GetArrayLength()} driver entries. No identifiers printed or settings changed.");
            return;
        }
        if (args.Length == 2 && args[0] == "--package")
        {
            string executable = Path.GetFullPath(args[1]);
            var version = FileVersionInfo.GetVersionInfo(executable);
            if (version.ProductName != "DepthStrap" || version.FileVersion != Bloxstrap.App.Version) throw new InvalidOperationException("Unexpected app package.");
            Guid adapter = Guid.NewGuid();
            var apply = MacControls.HelperStartInfo(false, adapter, "02:11:22:33:44:55", executable);
            var restore = MacControls.HelperStartInfo(true, adapter, null, executable);
            if (apply.FileName != executable || restore.FileName != executable || apply.Verb != "runas" || restore.Verb != "runas" ||
                !apply.ArgumentList.SequenceEqual(new[] { "--mac-apply", adapter.ToString("D"), "021122334455" }) ||
                !restore.ArgumentList.SequenceEqual(new[] { "--mac-restore", adapter.ToString("D") }))
                throw new InvalidOperationException("Unexpected package helper handoff.");
            Console.WriteLine("PASS: packaged app identity and elevated apply/restore handoff. No helper was launched."); return;
        }
        if (!args.SequenceEqual(new[] { "--self-test" })) throw new ArgumentException("Choose --self-test, --socket, or --ui <report-folder>.");
        SelfTests.Run();
        int checks = 0;
        MacChangeTests.Run((value, description) => { if (!value) throw new InvalidOperationException(description); checks++; }).GetAwaiter().GetResult();
        MacChangeTests.RunHelperDispatchChecks((value, description) => { if (!value) throw new InvalidOperationException(description); checks++; });
        Console.WriteLine($"PASS: {checks} production MAC transaction checks with simulated adapters.");
    }
}
