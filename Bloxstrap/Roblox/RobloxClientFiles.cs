using System.Reflection.PortableExecutable;

namespace Bloxstrap.Roblox;

public static class RobloxClientFiles
{
    // Older clients are monolithic. Only require the split DLL when the executable imports it.
    public static bool IsPlayerComplete(string executablePath)
    {
        try
        {
            using var stream = File.OpenRead(executablePath);
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = pe.PEHeaders;
            if (headers.PEHeader is null) return false;
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            long Offset(int rva, int size)
            {
                foreach (var section in headers.SectionHeaders)
                {
                    long delta = (long)rva - section.VirtualAddress;
                    long offset = section.PointerToRawData + delta;
                    if (delta >= 0 && delta + size <= section.SizeOfRawData && offset >= 0 && offset + size <= stream.Length)
                        return offset;
                }
                throw new BadImageFormatException("Invalid import address.");
            }
            var imports = headers.PEHeader.ImportTableDirectory;
            if (imports.RelativeVirtualAddress == 0) return true;
            int count = Math.Min(imports.Size / 20, 1024);
            bool requiresDll = false;
            bool terminated = false;
            for (int i = 0; i < count; i++)
            {
                stream.Position = Offset(checked(imports.RelativeVirtualAddress + i * 20), 20);
                uint thunk = reader.ReadUInt32(), timestamp = reader.ReadUInt32(), chain = reader.ReadUInt32();
                int nameRva = reader.ReadInt32();
                uint address = reader.ReadUInt32();
                if ((thunk | timestamp | chain | (uint)nameRva | address) == 0) { terminated = true; break; }
                var name = new StringBuilder();
                for (int j = 0; j < 256; j++)
                {
                    stream.Position = Offset(checked(nameRva + j), 1);
                    byte value = reader.ReadByte();
                    if (value == 0) break;
                    name.Append((char)value);
                    if (j == 255) throw new BadImageFormatException("Invalid import name.");
                }
                requiresDll |= name.ToString().Equals("RobloxPlayerBeta.dll", StringComparison.OrdinalIgnoreCase);
            }
            if (!terminated) return false;
            if (!requiresDll) return true;
            string dllPath = Path.Combine(Path.GetDirectoryName(executablePath)!, "RobloxPlayerBeta.dll");
            using var dllStream = File.OpenRead(dllPath);
            using var dll = new PEReader(dllStream);
            return dll.PEHeaders.PEHeader is not null && dll.PEHeaders.CoffHeader.Machine == headers.CoffHeader.Machine;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or OverflowException or ArgumentException)
        {
            return false;
        }
    }
}
