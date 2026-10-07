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
        private static Backup Read()
        {
            if (!File.Exists(FilePath)) return new();
            var backup = JsonSerializer.Deserialize<Backup>(AtomicFile.ReadText(FilePath));
            if (backup is null || backup.Global is null || backup.Flags is null ||
                backup.Global.Values.Any(x => x is null) || backup.Flags.Values.Any(x => x is null))
                throw new InvalidDataException("The Competitive settings backup is incomplete; it was preserved for recovery.");
            return backup;
        }

        internal static Func<bool> PlayerPresence { get; set; } = HasPlayers;
        private static bool HasPlayers()
        {
            Process[] players;
            try { players = Process.GetProcessesByName("RobloxPlayerBeta"); }
            catch { return true; } // Unavailable process information is not evidence that every client exited.
            try { return players.Any(x => !x.HasExited); }
            catch { return true; }
            finally { foreach (var player in players) player.Dispose(); }
        }

        internal static bool IsQualityLockInUse()
        {
            using var gate = Acquire();
            return Read().OriginalReadOnly is not null && PlayerPresence();
        }

        internal static InterProcessLock Acquire()
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

        public static bool Restore()
        {
            try
            {
                using var gate = Acquire();
                if (!File.Exists(FilePath)) return true;
                var backup = Read();
                bool deferGlobal = backup.OriginalReadOnly is not null && PlayerPresence();
                if (!deferGlobal)
                {
                    if (!ReleaseQualityLock()) return false;
                    backup = Read();
                    App.GlobalSettings.Load();
                    if (App.GlobalSettings.LastLoadFailed) return false;
                    foreach (var (key, change) in backup.Global)
                        if (App.GlobalSettings.GetPreset(key) == change.Applied && change.Original is not null)
                            App.GlobalSettings.SetPreset(key, change.Original);
                    App.GlobalSettings.Save();
                    if (!App.GlobalSettings.LastSaveSucceeded) return false;
                }
                foreach (var (key, change) in backup.Flags)
                    if (App.FastFlags.GetPreset(key) == change.Applied)
                        App.FastFlags.SetPreset(key, change.Original);
                App.FastFlags.Save();
                if (!App.FastFlags.LastSaveSucceeded) return false;
                if (deferGlobal)
                {
                    backup.Flags.Clear();
                    Save(backup);
                    App.Logger.WriteLine("CompetitiveSettingsBackup", "Shared graphics restoration deferred while Roblox clients are active.");
                }
                else File.Delete(FilePath);
                return true;
            }
            catch (Exception ex) { App.Logger.WriteException("CompetitiveSettingsBackup::Restore", ex); return false; }
        }

        public static void LockQuality()
        {
            if (!App.GlobalSettings.Loaded || App.GlobalSettings.LastLoadFailed || !File.Exists(App.GlobalSettings.FileLocation)) return;
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

        public static bool ReleaseQualityLock()
        {
            try
            {
                using var gate = Acquire();
                if (!File.Exists(FilePath)) return true;
                var backup = Read();
                if (backup.OriginalReadOnly is not bool original) return true;
                if (PlayerPresence()) return false;
                if (!App.GlobalSettings.TrySetReadOnly(original)) return false;
                backup.OriginalReadOnly = null;
                Save(backup);
                return true;
            }
            catch (Exception ex) { App.Logger.WriteException("CompetitiveSettingsBackup::Unlock", ex); return false; }
        }
    }
}
