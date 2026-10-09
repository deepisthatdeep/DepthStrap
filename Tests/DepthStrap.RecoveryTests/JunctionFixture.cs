using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DepthStrap.Recovery;

/// <summary>Creates an NTFS junction inside one isolated test root. Never used by the UI.</summary>
internal static class JunctionFixture
{
    internal static void Create(string root, string link, string target)
    {
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string path in new[] { link, target })
            if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A junction fixture must stay inside its test directory.");
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] data = new byte[16 + substitute.Length + 2 + display.Length + 2];
        using (var writer = new BinaryWriter(new MemoryStream(data), Encoding.Unicode))
        {
            writer.Write(0xA0000003u); writer.Write(checked((ushort)(data.Length - 8))); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2))); writer.Write(checked((ushort)display.Length));
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(display); writer.Write((ushort)0);
        }
        if (!DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputLength, IntPtr output, int outputLength, out uint returned, IntPtr overlapped);
}
