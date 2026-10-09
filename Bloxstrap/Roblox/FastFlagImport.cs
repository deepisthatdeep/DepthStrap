namespace Bloxstrap.Roblox
{
    internal static class FastFlagImport
    {
        internal const int MaxJsonLength = 16_000_000;
        internal const int MaxValueLength = 1_000_000;
        public static Dictionary<string, string> Parse(string json) => ParseDetailed(json).Flags;

        internal static string ReadFile(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaxJsonLength) throw new InvalidDataException("Import is larger than 16 MB.");
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var result = new StringBuilder();
            char[] buffer = new char[8192];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (result.Length + read > MaxJsonLength) throw new InvalidDataException("Import is larger than 16 MB.");
                result.Append(buffer, 0, read);
            }
            return result.ToString();
        }

        internal sealed record ImportResult(Dictionary<string, string> Flags, List<string> UnprefixedNames, int SkippedNulls);

        public static ImportResult ParseDetailed(string json)
        {
            if (json.Length > MaxJsonLength) throw new InvalidDataException("Import is larger than 16 MB.");
            json = json.Trim().TrimStart('\uFEFF').Trim();
            // Support pasted object members, without truncating malformed trailing content.
            if (json.StartsWith('"')) json = "{" + json;
            if (!json.EndsWith('}')) json += "}";
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object of flag names and values.");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var unprefixed = new List<string>();
            int skippedNulls = 0;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                ValidateName(entry.Name);
                if (!names.Add(entry.Name)) throw new InvalidDataException($"Duplicate flag: {entry.Name}");
                if (names.Count > 50_000) throw new InvalidDataException("Import contains more than 50,000 flags.");
                // Both upstream editors skip null values; they are not deletion requests.
                if (entry.Value.ValueKind == JsonValueKind.Null) { skippedNulls++; continue; }
                if (!IsFullName(entry.Name)) unprefixed.Add(entry.Name);
                string value = entry.Value.ValueKind switch
                {
                    JsonValueKind.String => entry.Value.GetString()!,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => entry.Value.ToString(),
                    _ => throw new InvalidDataException($"{entry.Name}: use a string, number or boolean value.")
                };
                if (value.Length > MaxValueLength) throw new InvalidDataException($"{entry.Name}: value is too long.");
                result.Add(entry.Name, value);
            }
            // Roblox interprets the keys. Preserve shortened and full names separately,
            // even when their suffixes match, rather than guessing aliases or precedence.
            return new(result, unprefixed, skippedNulls);
        }
        private static bool IsFullName(string name) => Regex.IsMatch(name, @"\A(?:D|S)?F(?:Flag|Int|String|Log)[A-Za-z0-9_]{1,180}\z");
        public static void ValidateName(string name)
        {
            if (!Regex.IsMatch(name, @"\A[\p{L}\p{N}_]{1,256}\z"))
                throw new InvalidDataException($"Invalid flag name: {name}. Use letters, numbers and underscores.");
        }
    }
}
