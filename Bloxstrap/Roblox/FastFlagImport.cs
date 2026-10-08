namespace Bloxstrap.Roblox
{
    internal static class FastFlagImport
    {
        public static Dictionary<string, string> Parse(string json)
        {
            var import = ParseDetailed(json);
            if (import.Unresolved.Count > 0)
                throw new InvalidDataException("These shortened names need their full FastFlag prefix: " + string.Join(", ", import.Unresolved));
            return import.Flags;
        }

        internal sealed record ImportResult(Dictionary<string, string> Flags, List<string> Unresolved, int Resolved, int AliasConflicts);

        public static ImportResult ParseDetailed(string json)
        {
            if (json.Length > 2_000_000) throw new InvalidDataException("Import is larger than 2 MB.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a JSON object of flag names and values.");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!Regex.IsMatch(entry.Name, @"\A[A-Za-z][A-Za-z0-9_]{0,190}\z"))
                    throw new InvalidDataException($"Invalid flag name: {entry.Name}");
                string value = entry.Value.ValueKind switch
                {
                    JsonValueKind.String => entry.Value.GetString()!,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => entry.Value.ToString(),
                    _ => throw new InvalidDataException($"{entry.Name}: use a string, number or boolean value.")
                };
                if (value.Length > 4096) throw new InvalidDataException($"{entry.Name}: value is too long.");
                if (!result.TryAdd(entry.Name, value)) throw new InvalidDataException($"Duplicate flag: {entry.Name}");
                if (result.Count > 10_000) throw new InvalidDataException("Import contains more than 10,000 flags.");
            }
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in result.Where(x => IsFullName(x.Key))) flags.Add(entry.Key, entry.Value);
            var candidates = flags.Keys.Concat(FastFlagManager.PresetFlags.Values).Distinct(StringComparer.Ordinal)
                .GroupBy(x => Regex.Replace(x, @"\A(?:D|S)?F(?:Flag|Int|String|Log)", ""), StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
            var unresolved = new List<string>();
            int resolved = 0, conflicts = 0;
            foreach (var entry in result.Where(x => !IsFullName(x.Key)))
            {
                if (!candidates.TryGetValue(entry.Key, out var names) || names.Length != 1)
                { unresolved.Add(entry.Key); continue; }
                resolved++;
                if (flags.TryGetValue(names[0], out var explicitValue))
                { if (explicitValue != entry.Value) conflicts++; }
                else flags.Add(names[0], entry.Value);
            }
            return new(flags, unresolved, resolved, conflicts);
        }
        private static bool IsFullName(string name) => Regex.IsMatch(name, @"\A(?:D|S)?F(?:Flag|Int|String|Log)[A-Za-z0-9_]{1,180}\z");
        public static void ValidateName(string name)
        {
            if (!IsFullName(name))
                throw new InvalidDataException("Use a full FastFlag name, for example FFlagDebugSkyGray.");
        }
    }
}
