using System.Security.AccessControl;
using System.Security.Principal;

namespace DepthStrap.Recovery;

internal static class BackupAclFixtureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // A fresh fixture beneath this test binary; never the production store.
        string root = Path.Combine(AppContext.BaseDirectory, "backup-acl-fixture-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Backup ACL fixture already exists.");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "synthetic-backup.json");
        FileSecurity? original = null;
        try
        {
            File.WriteAllText(path, "{\"fixture\":\"synthetic identifiers only\"}");
            var file = new FileInfo(path);
            original = file.GetAccessControl();
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new IOException("Fixture identity unavailable.");
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var protectedAcl = new FileSecurity();
            protectedAcl.SetOwner(owner);
            protectedAcl.SetAccessRuleProtection(true, false);
            foreach (var trustee in new[] { admins, system })
                protectedAcl.AddAccessRule(new FileSystemAccessRule(trustee, FileSystemRights.FullControl, AccessControlType.Allow));
            foreach (var trustee in new[] { owner, users })
                protectedAcl.AddAccessRule(new FileSystemAccessRule(trustee, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            WriteAcl(file, protectedAcl);
            WindowsMacBackend.ValidateBackupAccess(file.GetAccessControl(), owner);
            check(true, "Native Windows file ACL read-back supports protected synthetic backup permissions");
            var permissive = file.GetAccessControl();
            permissive.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
            WriteAcl(file, permissive);
            bool rejected = false;
            try { WindowsMacBackend.ValidateBackupAccess(file.GetAccessControl(), owner); }
            catch (IOException) { rejected = true; }
            check(rejected, "Native Windows backup-file ACL read-back refuses ordinary-user write access");
            check(File.ReadAllText(path) == "{\"fixture\":\"synthetic identifiers only\"}", "Permission rejection preserves the synthetic backup bytes");
            WriteAcl(file, protectedAcl);
            WindowsMacBackend.ValidateBackupAccess(file.GetAccessControl(), owner);
            check(true, "Repairing the synthetic backup file permissions allows a verified retry");
            var approvedWriter = file.GetAccessControl();
            approvedWriter.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            WriteAcl(file, approvedWriter);
            WindowsMacBackend.ValidateBackupAccess(file.GetAccessControl(), owner);
            check(true, "Native individual write grants remain supported for the caller-approved administrative identity");
        }
        finally
        {
            if (File.Exists(path))
            {
                if (original is not null) WriteAcl(new FileInfo(path), original);
                File.Delete(path);
            }
            Directory.Delete(root, false);
        }
    }

    private static void WriteAcl(FileInfo file, FileSecurity saved)
    {
        // A FileSecurity already persisted once has no dirty sections. Rebuild
        // its descriptor so restoring a saved ACL actually writes those sections.
        var replacement = new FileSecurity();
        // This fixture only changes the DACL; do not request SACL/owner/group
        // writes or the Windows security-audit privilege during restoration.
        replacement.SetSecurityDescriptorBinaryForm(saved.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        file.SetAccessControl(replacement);
    }
}
