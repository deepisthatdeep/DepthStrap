using Microsoft.Win32;

namespace DepthStrap.Recovery;

internal sealed record RobloxWipeFiles(string[] Locations, string[] Projects, string[] Skipped);
internal sealed record RobloxWipeRegistryItem(RobloxRegistryReset Service, RegistryResetPreview Preview, string Identity);

internal static class RobloxWipeCatalog
{
    internal static readonly string[] ClientExecutables = ["RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe", "RobloxStudioBeta.exe", "RobloxStudioLauncherBeta.exe", "RobloxPlayerInstaller.exe", "RobloxStudioInstaller.exe", "RobloxCrashHandler.exe", "DepthStrap.exe"];
    internal static bool ClientsRunning() => CookieReset.ClientsRunning() || new[] { "Windows10Universal", "Roblox", "RobloxCrashHandler", "RobloxPlayerInstaller", "RobloxStudioInstaller" }.Any(name =>
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Any(process => !process.HasExited); }
        finally { foreach (var process in processes) process.Dispose(); }
    });
    internal static string? CommandExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string value = command.Trim();
        if (value.StartsWith('"')) { int end = value.IndexOf('"', 1); value = end > 1 ? value[1..end] : ""; }
        else { int end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); value = end >= 0 ? value[..(end + 4)] : ""; }
        return Path.IsPathFullyQualified(value) && ClientExecutables.Contains(Path.GetFileName(value), StringComparer.OrdinalIgnoreCase) ? value : null;
    }
    internal static IEnumerable<string> RegistryPaths(RegistryKey root, bool classesOnly)
    {
        if (!classesOnly)
        {
            yield return @"Software\ROBLOX Corporation";
            yield return @"Software\Roblox";
            yield return @"Software\DepthStrap";
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is not null)
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    string display = key?.GetValue("DisplayName") as string ?? "";
                    string publisher = key?.GetValue("Publisher") as string ?? "";
                    if ((System.Text.RegularExpressions.Regex.IsMatch(display, @"\ARoblox(?: Player| Studio)?(?: for [^\r\n]{1,100})?\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                        (publisher.Equals("Roblox Corporation", StringComparison.OrdinalIgnoreCase) || CommandExecutable(key?.GetValue("UninstallString") as string) is not null))
                        || (name.Equals("DepthStrap", StringComparison.OrdinalIgnoreCase) && display.Equals("DepthStrap", StringComparison.OrdinalIgnoreCase)
                            && Path.GetFileName(CommandExecutable(key?.GetValue("UninstallString") as string) ?? "").Equals("DepthStrap.exe", StringComparison.OrdinalIgnoreCase)))
                        yield return @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name;
                }
        }
        string prefix = classesOnly ? "" : @"Software\Classes\";
        foreach (string name in new[] { "roblox", "roblox-player", "roblox-studio", "roblox-studio-auth", "Roblox.Place", "Roblox.Model", "depthstrap" }
            .Concat(ClientExecutables.Select(executable => @"Applications\" + executable)))
        {
            using var command = root.OpenSubKey(prefix + name + @"\shell\open\command");
            if (CommandExecutable(command?.GetValue("") as string) is not null) yield return prefix + name;
        }
        foreach (string extension in new[] { ".rbxl", ".rbxlx", ".rbxm", ".rbxmx" })
        {
            using var key = root.OpenSubKey(prefix + extension);
            string? target = key?.GetValue("") as string;
            if (target is not ("Roblox.Place" or "Roblox.Model")) continue;
            using var command = root.OpenSubKey(prefix + target + @"\shell\open\command");
            if (CommandExecutable(command?.GetValue("") as string) is not null) yield return prefix + extension;
        }
        if (!classesOnly)
            foreach (string executable in ClientExecutables)
            {
                string path = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executable;
                using var key = root.OpenSubKey(path);
                if (CommandExecutable(key?.GetValue("") as string) is not null) yield return path;
            }
    }
    internal static List<RobloxWipeRegistryItem> RegistryItems(IEnumerable<RobloxWipeRegistryScope.Root> roots, Func<bool>? running = null)
    {
        var result = new List<RobloxWipeRegistryItem>(); var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
            foreach (string path in RegistryPaths(root.Key, root.ClassesOnly).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var service = RobloxRegistryReset.ForOpenRoot(root.Key, path, root.Label, running ?? ClientsRunning, root.View);
                string identity = service.NativeIdentity();
                if (identity.Length > 0 && identities.Add(identity)) result.Add(new(service, service.Preview(), identity));
            }
        return result;
    }
    internal static string[] FolderRoots(string profile)
    {
        string local = Path.Combine(profile, "AppData", "Local");
        var roots = new List<string> {
            Path.Combine(local, "Roblox"), Path.Combine(local, "rbx-storage"), Path.Combine(profile, "AppData", "Roaming", "Roblox"),
            Path.Combine(profile, "AppData", "LocalLow", "Roblox"), Path.Combine(local, "Temp", "Roblox"), Path.Combine(local, "Temp", "rbx-storage"),
            Path.Combine(local, "DepthStrap"), Path.Combine(profile, "AppData", "Roaming", "DepthStrap"),
            Path.Combine(profile, "AppData", "LocalLow", "DepthStrap"), Path.Combine(local, "Temp", "DepthStrap")
        };
        string packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            roots.AddRange(Directory.EnumerateDirectories(packages).Where(path => Path.GetFileName(path).Equals("ROBLOXCORPORATION.ROBLOX_55nm5eh3cm0pr", StringComparison.OrdinalIgnoreCase)));
        return roots.ToArray();
    }
    internal static RobloxWipeFiles Files(IEnumerable<RobloxWipeProfile> profiles, IEnumerable<string> sharedRoots, string? installedDepthStrap,
        bool includeProjects, CancellationToken token, IEnumerable<string>? additionalInstallations = null)
    {
        var roots = new List<string>(); var projects = new List<string>(); var skipped = new List<string>();
        foreach (var profile in profiles)
        {
            token.ThrowIfCancellationRequested();
            roots.AddRange(FolderRoots(profile.Directory));
            foreach (string name in new[] { "Desktop", "Downloads" })
            {
                string folder = Path.Combine(profile.Directory, name);
                if (!Directory.Exists(folder) || IsRedirected(folder)) continue;
                roots.AddRange(Directory.EnumerateFiles(folder, "*.exe").Where(file => IsRobloxInstaller(file) || IsDepthStrapExecutable(file)));
            }
            foreach (string folder in new[] { Path.Combine(profile.Directory, "Desktop"), Path.Combine(profile.Directory, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs") })
                roots.AddRange(FindShortcuts(folder, token, skipped));
            if (includeProjects) projects.AddRange(FindProjects(profile.Directory, token, skipped));
        }
        roots.AddRange(sharedRoots);
        if (installedDepthStrap is not null) roots.Add(Path.GetDirectoryName(Path.GetFullPath(installedDepthStrap))!);
        if (additionalInstallations is not null) roots.AddRange(additionalInstallations);
        roots.AddRange(projects);
        string[] paths = roots.Where(path => Path.IsPathFullyQualified(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        paths = paths.Where(path => !paths.Any(parent => !parent.Equals(path, StringComparison.OrdinalIgnoreCase) && path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToArray();
        return new(paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), projects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), skipped.Distinct().ToArray());
    }
    private static bool IsRobloxInstaller(string file)
    {
        if (IsRedirected(file) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"\ARoblox(?:Player|Studio)(?:Installer|Launcher(?:Beta)?)(?: \([0-9]+\))?\.exe\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        var metadata = FileVersionInfo.GetVersionInfo(file);
        return metadata.CompanyName?.Contains("Roblox", StringComparison.OrdinalIgnoreCase) == true &&
            (metadata.ProductName?.Contains("Roblox", StringComparison.OrdinalIgnoreCase) == true || metadata.FileDescription?.Contains("Roblox", StringComparison.OrdinalIgnoreCase) == true);
    }
    private static bool IsDepthStrapExecutable(string file)
    {
        if (IsRedirected(file) || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"\ADepthStrap(?: \([0-9]+\))?\.exe\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        return FileVersionInfo.GetVersionInfo(file).ProductName?.Equals("DepthStrap", StringComparison.OrdinalIgnoreCase) == true;
    }
    internal static bool IsProject(string file) => new[] { ".rbxl", ".rbxlx", ".rbxm", ".rbxmx" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
    internal static string InstallationDirectory(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !Path.GetFileName(executable).Equals("DepthStrap.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The original installed DepthStrap.exe must be identified explicitly.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        ValidateInstallationDirectory(directory);
        return directory;
    }
    internal static void ValidateInstallationDirectory(string directory)
    {
        string value = Path.GetFullPath(directory).TrimEnd('\\');
        var protectedPaths = new[] { Path.GetPathRoot(value), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.GetTempPath() };
        if (protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Any(path => value.Equals(path!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            || Directory.Exists(Path.Combine(value, ".git"))) throw new IOException("A shared, profile, or source-workspace directory cannot be erased as an installation.");
        if (!Path.GetFileName(value).Equals("DepthStrap", StringComparison.OrdinalIgnoreCase)
            && !File.Exists(Path.Combine(value, "RobloxPlayerBeta.exe")) && !File.Exists(Path.Combine(value, "RobloxStudioBeta.exe"))
            && !(File.Exists(Path.Combine(value, "Settings.json")) && File.Exists(Path.Combine(value, "State.json"))))
            throw new IOException("A custom app directory must contain recognizable installed app data. Shared folders cannot be erased.");
        if (Directory.Exists(value) && IsRedirected(value)) throw new IOException("The installation folder is redirected.");
    }
    internal static string[] RegisteredInstallations(IEnumerable<RobloxWipeRegistryScope.Root> roots)
    {
        var result = new List<string>();
        foreach (var root in roots.Where(root => !root.ClassesOnly))
            foreach (string path in RegistryPaths(root.Key, false).Where(path => path.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\", StringComparison.OrdinalIgnoreCase)))
            {
                using var key = root.Key.OpenSubKey(path);
                string? executable = CommandExecutable(key?.GetValue("UninstallString") as string);
                if (executable is null) continue;
                string? directory = Path.GetDirectoryName(executable);
                if (directory is null || !Directory.Exists(directory) || !File.Exists(executable)) continue;
                ValidateInstallationDirectory(directory);
                var metadata = FileVersionInfo.GetVersionInfo(executable);
                bool depthstrap = Path.GetFileName(executable).Equals("DepthStrap.exe", StringComparison.OrdinalIgnoreCase);
                bool ownedFolder = depthstrap ? (Path.GetFileName(directory).Equals("DepthStrap", StringComparison.OrdinalIgnoreCase)
                        || (File.Exists(Path.Combine(directory, "Settings.json")) && File.Exists(Path.Combine(directory, "State.json")) ))
                    : System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory), @"\A(?:Roblox|RobloxStudio|version-[a-f0-9]{16})\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!ownedFolder) throw new IOException("A registered client uses an unrecognized shared folder. Review its location before uninstalling: " + directory);
                if ((depthstrap && metadata.ProductName?.Equals("DepthStrap", StringComparison.OrdinalIgnoreCase) == true)
                    || (!depthstrap && metadata.CompanyName?.Contains("Roblox", StringComparison.OrdinalIgnoreCase) == true)) result.Add(directory);
            }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static IEnumerable<string> FindProjects(string root, CancellationToken token, List<string> skipped)
        => Walk(root, token, skipped, root).Where(IsProject);
    internal static IEnumerable<string> FindShortcuts(string root, CancellationToken token, List<string> skipped)
    {
        foreach (string file in Walk(root, token, skipped).Where(path => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)))
        {
            if (new FileInfo(CookieReset.NativePath(file)).Length > 1024 * 1024) continue;
            ShellLink.Shortcut shortcut;
            try { shortcut = ShellLink.Shortcut.ReadFromFile(CookieReset.NativePath(file)); }
            catch (Exception ex) when (ex is IOException or ArgumentException or IndexOutOfRangeException) { continue; }
            string? target = shortcut.LinkInfo?.LocalBasePathUnicode ?? shortcut.LinkInfo?.LocalBasePath;
            if (CommandExecutable(target is null ? null : '"' + target + '"') is not null) yield return file;
        }
    }
    private static IEnumerable<string> Walk(string root, CancellationToken token, List<string> skipped, string? profile = null)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Stack<string>(); pending.Push(root); int entries = 0;
        while (pending.TryPop(out string? folder))
        {
            token.ThrowIfCancellationRequested();
            bool redirected;
            try { redirected = IsRedirected(folder); }
            catch (UnauthorizedAccessException) { skipped.Add("Inaccessible project/shortcut folder: " + CookieReset.NormalizeFinalPath(folder)); continue; }
            if (redirected) { skipped.Add("Redirected folder: " + folder); continue; }
            // Keep enumerated entries in the literal file namespace. Otherwise
            // File.GetAttributes turns an ordinary on-disk "nul" into \\.\nul.
            foreach (string child in DiscoverChildren(CookieReset.NativePath(folder), skipped))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 2000000) throw new IOException("Project/shortcut discovery exceeded its two-million-entry safety limit. No wipe was started.");
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (UnauthorizedAccessException) { skipped.Add("Inaccessible project/shortcut entry: " + CookieReset.NormalizeFinalPath(child)); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Standard compatibility junctions duplicate locations already
                    // covered by their real profile paths. Never traverse a link.
                    if (profile is not null && IsCompatibilityAlias(CookieReset.NormalizeFinalPath(child), profile)) continue;
                    skipped.Add("Redirected entry: " + child); continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Roblox's AppData is already in the owned data roots. Do not
                    // inspect unrelated applications or traverse their profile data.
                    if (!Path.GetFileName(child).Equals("AppData", StringComparison.OrdinalIgnoreCase)) pending.Push(child);
                }
                else yield return CookieReset.NormalizeFinalPath(child);
            }
        }
    }
    private static IEnumerable<string> DiscoverChildren(string folder, List<string> skipped)
    {
        IEnumerator<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator(); }
        catch (UnauthorizedAccessException) { skipped.Add("Inaccessible project/shortcut folder: " + CookieReset.NormalizeFinalPath(folder)); yield break; }
        using (entries)
        {
            while (true)
            {
                bool moved;
                try { moved = entries.MoveNext(); }
                catch (UnauthorizedAccessException) { skipped.Add("Inaccessible project/shortcut folder: " + CookieReset.NormalizeFinalPath(folder)); yield break; }
                if (!moved) yield break;
                yield return entries.Current;
            }
        }
    }
    internal static bool IsCompatibilityAlias(string path, string profile)
    {
        string relative = Path.GetRelativePath(profile, path);
        string? expected = relative.ToLowerInvariant() switch
        {
            "application data" => @"AppData\Roaming",
            "local settings" => @"AppData\Local",
            "my documents" => "Documents",
            "cookies" => @"AppData\Local\Microsoft\Windows\INetCookies",
            "nethood" => @"AppData\Roaming\Microsoft\Windows\Network Shortcuts",
            "printhood" => @"AppData\Roaming\Microsoft\Windows\Printer Shortcuts",
            "recent" => @"AppData\Roaming\Microsoft\Windows\Recent",
            "sendto" => @"AppData\Roaming\Microsoft\Windows\SendTo",
            "start menu" => @"AppData\Roaming\Microsoft\Windows\Start Menu",
            "templates" => @"AppData\Roaming\Microsoft\Windows\Templates",
            @"documents\my music" => "Music",
            @"documents\my pictures" => "Pictures",
            @"documents\my videos" => "Videos",
            _ => null
        };
        if (expected is null) return false;
        string? target = new DirectoryInfo(path).LinkTarget;
        if (target is null) return false;
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];
        target = CookieReset.NormalizeFinalPath(target);
        return Path.GetFullPath(target, Path.GetDirectoryName(path)!).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(Path.Combine(profile, expected)).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
    internal static bool IsRedirected(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
