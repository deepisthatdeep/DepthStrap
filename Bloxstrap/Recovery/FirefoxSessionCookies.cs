using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DepthStrap.Recovery;

/// <summary>Removes Roblox cookie records from Firefox session recovery without removing tabs or other sites' cookies.</summary>
internal static class FirefoxSessionCookies
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    internal sealed record Edited(byte[] OriginalHash, byte[] Cleaned, int Cookies);
    internal static bool IsRoblox(string? host) => host is not null && (host.TrimStart('.').Equals("roblox.com", StringComparison.OrdinalIgnoreCase)
        || host.TrimStart('.').EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));
    internal static Edited Read(string path)
    {
        using var parents = BrowserCookieDatabase.GuardParents(path);
        BrowserCookieReset.RejectRedirects(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumBytes) throw new IOException("A Firefox recovery file exceeds the supported size. No browser data was removed.");
        byte[] original = new byte[(int)file.Length]; file.ReadExactly(original);
        bool compressed = original.AsSpan().StartsWith("mozLz40\0"u8);
        byte[] json = compressed ? Decode(original) : original;
        JsonNode root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 256 }) ?? throw new JsonException(); }
        catch (JsonException) { throw new IOException("A Firefox recovery file could not be parsed. It was preserved; reset is incomplete."); }
        int removed = 0;
        Visit(root);
        byte[] cleaned = removed == 0 ? original : JsonSerializer.SerializeToUtf8Bytes(root);
        if (removed > 0 && compressed) cleaned = Encode(cleaned);
        return new(SHA256.HashData(original), cleaned, removed);
        void Visit(JsonNode node)
        {
            if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key == "cookies" && pair.Value is JsonArray cookies)
                        for (int i = cookies.Count - 1; i >= 0; i--)
                            if (cookies[i] is JsonObject cookie && cookie["host"] is JsonValue value && value.TryGetValue<string>(out var host) && IsRoblox(host))
                            { cookies.RemoveAt(i); removed++; }
                    if (pair.Value is not null) Visit(pair.Value);
                }
            else if (node is JsonArray array) foreach (var child in array) if (child is not null) Visit(child);
        }
    }
    internal static void Write(string path, Edited edit)
    {
        using var parents = BrowserCookieDatabase.GuardParents(path);
        var current = Read(path);
        if (!CryptographicOperations.FixedTimeEquals(current.OriginalHash, edit.OriginalHash)) throw new IOException("Firefox recovery data changed during reset. Review a new preview.");
        if (edit.Cookies == 0) return;
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".depthstrap-cookie-reset-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Only sanitized data reaches the temporary file; no credential backup.
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(edit.Cleaned); file.Flush(true); }
            BrowserCookieReset.RejectRedirects(path);
            File.Replace(temporary, path, null);
            if (Read(path).Cookies != 0) throw new IOException("Firefox recovery cookies remain. Reset is incomplete.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void ValidateWritable(string path, Edited edit)
    {
        if (edit.Cookies == 0) return;
        using var parents = BrowserCookieDatabase.GuardParents(path);
        BrowserCookieReset.RejectRedirects(path);
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new IOException("A Firefox recovery file is read-only. No reset was started; make the file writable and create a new preview.");
        // Check write permission and sharing before application files are removed.
        // The actual replacement still rechecks the original content identity.
        using var writable = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
    }
    internal static byte[] Encode(byte[] json)
    {
        if (json.Length > MaximumBytes) throw new IOException("Firefox session data is too large.");
        using var output = new MemoryStream(); output.Write("mozLz40\0"u8);
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, json.Length); output.Write(size);
        // A valid raw LZ4 block can contain one literal-only final sequence.
        output.WriteByte((byte)(Math.Min(json.Length, 15) << 4));
        if (json.Length >= 15)
        { int remaining = json.Length - 15; while (remaining >= 255) { output.WriteByte(255); remaining -= 255; } output.WriteByte((byte)remaining); }
        output.Write(json); return output.ToArray();
    }
    internal static byte[] Decode(byte[] input)
    {
        if (input.Length < 12 || !input.AsSpan(0, 8).SequenceEqual("mozLz40\0"u8)) throw new IOException("Invalid Firefox recovery header.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(8, 4));
        if (length < 0 || length > MaximumBytes) throw new IOException("Firefox recovery data exceeds its supported size.");
        byte[] output = new byte[length]; int source = 12, target = 0;
        while (source < input.Length)
        {
            int token = input[source++]; int literals = Extra(token >> 4);
            if (literals > input.Length - source || literals > output.Length - target) throw new IOException("Invalid Firefox recovery literals.");
            input.AsSpan(source, literals).CopyTo(output.AsSpan(target)); source += literals; target += literals;
            if (source == input.Length) break;
            if (source + 2 > input.Length) throw new IOException("Invalid Firefox recovery offset.");
            int offset = input[source++] | input[source++] << 8; int match = checked(Extra(token & 15) + 4);
            if (offset == 0 || offset > target || match > output.Length - target) throw new IOException("Invalid Firefox recovery match.");
            for (int i = 0; i < match; i++) { output[target] = output[target - offset]; target++; }
        }
        if (target != output.Length) throw new IOException("Firefox recovery length is invalid.");
        return output;
        int Extra(int initial)
        {
            if (initial < 15) return initial;
            int value = initial, next;
            do
            {
                if (source >= input.Length) throw new IOException("Truncated Firefox recovery data.");
                next = input[source++]; value = checked(value + next);
                if (value > MaximumBytes) throw new IOException("Firefox recovery sequence is too large.");
            } while (next == 255);
            return value;
        }
    }
}
