namespace Bloxstrap.Roblox
{
    /// <summary>Restore only values still owned by our preset, preserving later user edits.</summary>
    internal static class CompetitiveSettingsBackup
    {
        private sealed class Change
        {
            public string? Original { get; set; }
            public string? Applied { get; set; }
        }

        private sealed class Backup
        {
            public Dictionary<string, Change> Global { get; set; } = new();
            public Dictionary<string, Change> Flags { get; set; } = new();
            public bool? OriginalReadOnly { get; set; }
        }

        private static string FilePath => Path.Combine(Paths.Cache, "CompetitiveSettingsBackup.json");
        private static Backup Read() => File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Backup>(AtomicFile.ReadText(FilePath)) ?? new() : new();

        private static InterProcessLock Acquire()
        {
            var gate = new InterProcessLock("CompetitiveSettingsBackup", TimeSpan.FromSeconds(10));
            if (gate.IsAcquired) return gate;
            gate.Dispose();
            throw new IOException("Timed out waiting for the Competitive settings backup.");
        }

        private static void Save(Backup backup)
        {
            AtomicFile.WriteText(FilePath, JsonSerializer.Serialize(backup));
        }

        private static void Remember(string key, string? original, string? applied, bool global)
        {
            var backup = Read();
            var changes = global ? backup.Global : backup.Flags;
            if (!changes.TryGetValue(key, out var change))
                changes[key] = change = new() { Original = original };
            change.Applied = applied;
            Save(backup);
        }

        public static void SetGlobal(string key, string? value)
        {
            using var gate = Acquire();
            var original = App.GlobalSettings.GetPreset(key);
            if (original is null || original == value) return;
            Remember(key, original, value, global: true);
            App.GlobalSettings.SetPreset(key, value);
        }

        public static void SetFlag(string key, string? value)
        {
            using var gate = Acquire();
            var original = App.FastFlags.GetPreset(key);
            if (original == value) return;
            Remember(key, original, value, global: false);
            App.FastFlags.SetPreset(key, value);
        }

        public static void Restore()
        {
            try
            {
                using var gate = Acquire();
                if (!File.Exists(FilePath)) return;
                ReleaseQualityLock();
                var backup = Read();
                App.GlobalSettings.Load();
                foreach (var (key, change) in backup.Global)
                    if (App.GlobalSettings.GetPreset(key) == change.Applied && change.Original is not null)
                        App.GlobalSettings.SetPreset(key, change.Original);
                foreach (var (key, change) in backup.Flags)
                    if (App.FastFlags.GetPreset(key) == change.Applied)
                        App.FastFlags.SetPreset(key, change.Original);
                App.GlobalSettings.Save();
                App.FastFlags.Save();
                File.Delete(FilePath);
            }
            catch (Exception ex) { App.Logger.WriteException("CompetitiveSettingsBackup::Restore", ex); }
        }

        public static void LockQuality()
        {
            if (!App.GlobalSettings.Loaded || !File.Exists(App.GlobalSettings.FileLocation)) return;
            try
            {
                using var gate = Acquire();
                var backup = Read();
                backup.OriginalReadOnly ??= App.GlobalSettings.GetReadOnly();
                Save(backup);
                App.GlobalSettings.SetReadOnly(true);
            }
            catch (Exception ex) { App.Logger.WriteException("CompetitiveSettingsBackup::Lock", ex); }
        }

        public static void ReleaseQualityLock()
        {
            try
            {
                using var gate = Acquire();
                if (!File.Exists(FilePath)) return;
                var backup = Read();
                if (backup.OriginalReadOnly is not bool original) return;
                App.GlobalSettings.SetReadOnly(original);
                backup.OriginalReadOnly = null;
                Save(backup);
            }
            catch (Exception ex) { App.Logger.WriteException("CompetitiveSettingsBackup::Unlock", ex); }
        }
    }
}
