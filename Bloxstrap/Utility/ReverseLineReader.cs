namespace Bloxstrap.Utility
{
    internal static class ReverseLineReader
    {
        internal static IEnumerable<string> Read(string path, CancellationToken token = default)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (string line in Read(stream, token)) yield return line;
        }

        // Leaves the caller's stream open. Byte boundaries are decoded only after a full line
        // has been assembled, so UTF-8 characters split between blocks remain intact.
        internal static IEnumerable<string> Read(Stream stream, CancellationToken token = default,
            int blockSize = 65536, int maxLineBytes = 1048576)
        {
            if (!stream.CanRead || !stream.CanSeek || blockSize <= 0 || maxLineBytes <= 0)
                throw new ArgumentException("A readable seekable stream and positive limits are required.");
            long position = stream.Length;
            var buffer = new byte[blockSize];
            var line = new List<byte>();
            bool firstByte = true, oversized = false;
            while (position > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(position, buffer.Length);
                position -= count;
                stream.Seek(position, SeekOrigin.Begin);
                stream.ReadExactly(buffer.AsSpan(0, count));
                for (int i = count - 1; i >= 0; i--)
                {
                    byte value = buffer[i];
                    if (firstByte) { firstByte = false; if (value == (byte)'\n') continue; }
                    if (value == (byte)'\n')
                    {
                        token.ThrowIfCancellationRequested();
                        if (!oversized) yield return Decode(line, false);
                        line.Clear(); oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Count == maxLineBytes) { line.Clear(); oversized = true; }
                        else line.Add(value);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            if (!firstByte && !oversized) yield return Decode(line, true);
        }

        private static string Decode(List<byte> bytes, bool firstLine)
        {
            bytes.Reverse();
            string line = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            return firstLine ? line.TrimStart('\ufeff') : line;
        }
    }
}
