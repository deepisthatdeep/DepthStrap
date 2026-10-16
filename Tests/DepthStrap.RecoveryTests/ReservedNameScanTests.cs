using System.Security.AccessControl;
using System.Security.Principal;

namespace DepthStrap.Recovery;

internal static class ReservedNameScanTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        string profile = Path.Combine(root, "reserved-name-profile");
        string documents = Path.Combine(profile, "Documents"); Directory.CreateDirectory(documents);
        string ignored = Path.Combine(profile, "nul"), project = Path.Combine(documents, "Synthetic.rbxlx");
        File.WriteAllText(@"\\?\" + ignored, "synthetic unrelated reserved-name file");
        File.WriteAllText(project, "synthetic Studio project");
        string reservedProject = Path.Combine(documents, "NUL.rbxlx"), reservedDirectory = Path.Combine(documents, "CON");
        Directory.CreateDirectory(@"\\?\" + reservedDirectory);
        string nestedProject = Path.Combine(reservedDirectory, "Nested.rbxl");
        File.WriteAllText(@"\\?\" + reservedProject, "synthetic reserved-name project");
        File.WriteAllText(@"\\?\" + nestedProject, "synthetic project in reserved-name directory");
        var catalog = RobloxWipeCatalog.Files([new("S-1-5-21-1-2-3-1001", profile)], [], null, true, CancellationToken.None);
        check(catalog.Projects.Order().SequenceEqual(new[] { project, reservedProject, nestedProject }.Order()) && catalog.Skipped.Length == 0,
            "An unrelated literal nul file cannot redirect project discovery to the NUL device or abort the wipe");
        var reset = new RobloxDataReset(catalog.Projects, () => false, allowRootFiles: true);
        var result = reset.Execute(reset.Preview(), true);
        check(result.Complete && !File.Exists(project) && !File.Exists(@"\\?\" + reservedProject) && !File.Exists(@"\\?\" + nestedProject)
            && File.ReadAllText(@"\\?\" + ignored) == "synthetic unrelated reserved-name file",
            "A project wipe completes while preserving an unrelated reserved-name fixture file");
        string deniedFolder = Path.Combine(documents, "ProtectedFixture"); Directory.CreateDirectory(deniedFolder);
        string deniedProject = Path.Combine(deniedFolder, "Keep.rbxlx"); File.WriteAllText(deniedProject, "unreadable discovery fixture");
        var directory = new DirectoryInfo(deniedFolder);
        byte[] original = directory.GetAccessControl().GetSecurityDescriptorBinaryForm();
        using var identity = WindowsIdentity.GetCurrent();
        var denied = directory.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            RobloxWipeFiles Capture() => RobloxWipeCatalog.Files([new("S-1-5-21-1-2-3-1001", profile)], [], null, true, CancellationToken.None);
            var unresolved = Capture();
            check(unresolved.Skipped.Any(item => item.Contains(deniedFolder)) && !unresolved.Projects.Contains(deniedProject),
                "An unreadable unrelated folder is reported unresolved without aborting the remaining project discovery");
            using var combined = new RobloxCompleteUninstall(() => [new("S-1-5-21-1-2-3-1001", profile)], () => [], Capture,
                new CompleteUninstallTests.Store { State = new([], []) }, () => false, null);
            var outcome = combined.ExecuteAsync(combined.PreviewAsync().GetAwaiter().GetResult(), true).GetAwaiter().GetResult();
            check(!outcome.Complete && outcome.Message.Contains(deniedFolder) && File.Exists(deniedProject),
                "A protected project location prevents a complete result and is never silently deleted or ignored");
        }
        finally
        {
            var restored = new DirectorySecurity(); restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            directory.SetAccessControl(restored);
        }
    }
}
