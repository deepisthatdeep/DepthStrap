using System.IO;
using System.Text;
using Bloxstrap;
using Bloxstrap.Roblox;

internal static class ClientFilesChecks
{
    internal static void WriteExecutable(string path, bool split = false)
    {
        byte[] data = new byte[1024];
        void Word(int at, ushort value) => BitConverter.GetBytes(value).CopyTo(data, at);
        void Dword(int at, int value) => BitConverter.GetBytes(value).CopyTo(data, at);
        Word(0, 0x5a4d); Dword(0x3c, 0x80); Dword(0x80, 0x4550);
        Word(0x84, 0x8664); Word(0x86, 1); Word(0x94, 240); Word(0x96, 0x22);
        int optional = 0x98;
        Word(optional, 0x20b); Dword(optional + 32, 0x1000); Dword(optional + 36, 0x200);
        Dword(optional + 56, 0x2000); Dword(optional + 60, 0x200); Word(optional + 68, 2);
        Dword(optional + 108, 16); Dword(optional + 120, 0x1000); Dword(optional + 124, 40);
        int section = optional + 240;
        Encoding.ASCII.GetBytes(".text").CopyTo(data, section);
        Dword(section + 8, 0x200); Dword(section + 12, 0x1000);
        Dword(section + 16, 0x200); Dword(section + 20, 0x200);
        Dword(0x200, 0x1080); Dword(0x20c, 0x1060); Dword(0x210, 0x1080);
        Encoding.ASCII.GetBytes(split ? "RobloxPlayerBeta.dll" : "KERNEL32.dll").CopyTo(data, 0x260);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    internal static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Paths.Base, "core-file-checks");
        string exe = Path.Combine(folder, App.RobloxPlayerAppName);
        string dll = Path.Combine(folder, "RobloxPlayerBeta.dll");
        WriteExecutable(exe);
        check(RobloxClientFiles.IsPlayerComplete(exe), "Older monolithic Player does not require a split Roblox DLL");
        WriteExecutable(exe, split: true);
        check(!RobloxClientFiles.IsPlayerComplete(exe), "Split Player with a missing Roblox DLL cannot be accepted");
        File.WriteAllBytes(dll, Array.Empty<byte>());
        check(!RobloxClientFiles.IsPlayerComplete(exe), "Empty required Roblox DLL cannot be accepted");
        WriteExecutable(dll);
        check(RobloxClientFiles.IsPlayerComplete(exe), "Player accepts its required valid matching-architecture DLL");
        var bytes = File.ReadAllBytes(dll); bytes[0x84] = 0x4c; bytes[0x85] = 1; File.WriteAllBytes(dll, bytes);
        check(!RobloxClientFiles.IsPlayerComplete(exe), "Player rejects a required DLL with a different architecture");
        File.WriteAllText(exe, "truncated");
        check(!RobloxClientFiles.IsPlayerComplete(exe), "Truncated Player executable is rejected without a launch");
        check(!RobloxClientFiles.IsPlayerComplete(Path.Combine(folder, "missing.exe")), "Missing Player executable is rejected");
        WriteExecutable(exe, split: true);
        bytes = File.ReadAllBytes(exe); BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 0x20c); File.WriteAllBytes(exe, bytes);
        check(!RobloxClientFiles.IsPlayerComplete(exe), "Invalid import address is rejected without reading outside the executable");
        const string brokenVersion = "version-aaaaaaaaaaaaaaaa";
        string brokenExe = Path.Combine(Paths.Versions, brokenVersion, App.RobloxPlayerAppName);
        WriteExecutable(brokenExe, split: true);
        check(!RobloxVersionArchive.InstalledPlayerVersions().Contains(brokenVersion), "Build catalog excludes an executable missing its required Roblox DLL");
    }
}
