using System.Text.Json.Nodes;
using System.Windows.Media;

namespace Bloxstrap.Roblox
{
    internal static class AppearanceFont
    {
        internal static string Store(string source)
        {
            string extension = Path.GetExtension(source).ToLowerInvariant();
            byte[] bytes;
            using (var stream = File.OpenRead(source))
            {
                if (stream.Length is < 12 or > 64 * 1024 * 1024)
                    throw new InvalidDataException("Choose a complete font file smaller than 64 MB.");
                bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
            }
            ValidateHeader(bytes.AsSpan(0, 4), extension);
            byte[] digest = System.Security.Cryptography.SHA256.HashData(bytes);
            string hash = Convert.ToHexString(digest);
            string directory = Path.Combine(Paths.Base, "Fonts");
            Directory.CreateDirectory(directory);
            string stored = Path.Combine(directory, hash + extension);
            // A source can disappear/change after selection. Store exactly the validated snapshot.
            // A tampered retained copy gets a new URI, avoiding stale native font caches.
            if (File.Exists(stored))
            {
                using var existing = File.OpenRead(stored);
                if (existing.Length != bytes.Length || !System.Security.Cryptography.SHA256.HashData(existing).AsSpan().SequenceEqual(digest))
                    stored = Path.Combine(directory, hash + "-" + Guid.NewGuid().ToString("N") + extension);
            }
            bool created = !File.Exists(stored);
            if (created) File.WriteAllBytes(stored, bytes);
            try { LoadFace(stored); }
            catch
            {
                if (created)
                {
                    try { File.Delete(stored); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Logger.WriteException("AppearanceFont::Cleanup", ex); }
                }
                throw;
            }
            return stored;
        }

        private static void ValidateHeader(ReadOnlySpan<byte> header, string extension)
        {
            if (!((extension == ".ttf" && header.SequenceEqual(new byte[] { 0, 1, 0, 0 })) ||
                (extension == ".otf" && header.SequenceEqual("OTTO"u8))))
                throw new InvalidDataException("Choose a valid .ttf or .otf font file.");
        }

        internal static GlyphTypeface LoadFace(string source)
        {
            string extension = Path.GetExtension(source).ToLowerInvariant();
            using (var stream = File.OpenRead(source))
            {
                if (stream.Length is < 12 or > 64 * 1024 * 1024) throw new InvalidDataException("The font is incomplete or too large.");
                Span<byte> header = stackalloc byte[4]; stream.ReadExactly(header);
                ValidateHeader(header, extension);
            }
            try
            {
                var face = new GlyphTypeface(new Uri(Path.GetFullPath(source), UriKind.Absolute));
                if (face.GlyphCount == 0 || face.CharacterToGlyphMap.Count == 0 || face.FamilyNames.Count == 0)
                    throw new InvalidDataException("The file does not contain a usable font face.");
                return face;
            }
            catch (Exception ex) when (ex is FileFormatException or ArgumentException or NotSupportedException)
            { throw new InvalidDataException("The font is damaged or unsupported. Choose another .ttf or .otf file.", ex); }
        }

        // Feed these files through the normal mod manifest. Reset and build changes then
        // restore Roblox package files through the existing verified package pipeline.
        internal static Dictionary<string, string> CreateFiles(string versionDirectory, string? font,
            IReadOnlyDictionary<string, string> otherMods)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(font)) return result;
            LoadFace(font);
            string extension = Path.GetExtension(font).ToLowerInvariant();
            string families = Path.Combine(versionDirectory, "content", "fonts", "families");
            if (!Directory.Exists(families)) return result;
            string staging = Path.Combine(Paths.Cache, "AppearanceFont");
            Directory.CreateDirectory(staging);
            string asset = "DepthStrapCustomFont" + extension;
            foreach (string installed in Directory.EnumerateFiles(families, "*.json"))
            {
                string relative = Path.GetRelativePath(versionDirectory, installed);
                string input = otherMods.TryGetValue(relative, out string? mod) ? mod : installed;
                try
                {
                    var family = JsonNode.Parse(File.ReadAllText(input));
                    if (family is not JsonObject obj || obj["faces"] is not JsonArray faces || faces.Count == 0 || faces.Any(x => x is not JsonObject)) continue;
                    foreach (var face in faces.OfType<JsonObject>()) face["assetId"] = "rbxasset://fonts/" + asset;
                    string output = Path.Combine(staging, Path.GetFileName(installed));
                    File.WriteAllText(output, family.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    result[relative] = output;
                }
                catch (JsonException ex) { App.Logger.WriteException("AppearanceFont::Family", ex); }
            }
            if (result.Count > 0) result[Path.Combine("content", "fonts", asset)] = font;
            return result;
        }
    }
}
