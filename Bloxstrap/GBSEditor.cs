using Bloxstrap.Enums.GBSPresets;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Bloxstrap
{
    public class GBSEditor
    {
        public XDocument? Document { get; set; } = null!;

        public Dictionary<string, string> PresetPaths = new()
        {
            // Graphics Settings
            { "Rendering.FramerateCap", "{UserSettings}/int[@name='FramerateCap']" },
            { "Rendering.SavedQualityLevel", "{UserSettings}/token[@name='SavedQualityLevel']" },
            { "Rendering.Fullscreen", "{UserSettings}/bool[@name='Fullscreen']" },
            { "Rendering.MaxQualityEnabled", "{UserSettings}/bool[@name='MaxQualityEnabled']" },
            { "Rendering.VignetteEnabled", "{UserSettings}/bool[@name='VignetteEnabled']" },
            { "Rendering.VignetteEnableOption", "{UserSettings}/bool[@name='VignetteEnabledCustomOption']" },

            // Audio Settings
            { "Audio.MasterVolume", "{UserSettings}/float[@name='MasterVolume']" },
            { "Audio.MasterVolumeStudio", "{UserSettings}/float[@name='MasterVolumeStudio']" },
            { "Audio.PartyVoiceVolume", "{UserSettings}/float[@name='PartyVoiceVolume']" },

            // Input Settings
            { "User.MouseSensitivity", "{UserSettings}/float[@name='MouseSensitivity']" },
            { "User.ShiftLock", "{UserSettings}/token[@name='ControlMode']" },
            { "User.MouseSensitivityFirstPerson", "{UserSettings}/Vector2[@name='MouseSensitivityFirstPerson']" },
            { "User.MouseSensitivityThirdPerson", "{UserSettings}/Vector2[@name='MouseSensitivityThirdPerson']" },
            { "User.CameraYInverted", "{UserSettings}/bool[@name='CameraYInverted']" },
            { "User.HapticStrength", "{UserSettings}/float[@name='HapticStrength']" },

            // Accessibility
            { "UI.Transparency", "{UserSettings}/float[@name='PreferredTransparency']" },
            { "UI.ReducedMotion", "{UserSettings}/bool[@name='ReducedMotion']" },
            { "UI.FontSize", "{UserSettings}/token[@name='PreferredTextSize']" },

            // Miscellaneous Settings
            { "Misc.PerformanceStatsVisible", "{UserSettings}/bool[@name='PerformanceStatsVisible']" },
            { "Misc.ChatTranslationEnabled", "{UserSettings}/bool[@name='ChatTranslationEnabled']" },
            { "Misc.ChatTranslationFTUXShown", "{UserSettings}/bool[@name='ChatTranslationFTUXShown']" },
            { "User.VREnabled", "{UserSettings}/bool[@name='VREnabled']" }
        };

        // we are making it easier for ourselves
        // basically replacing {...} with a path
        // might expand in the future (studio support)
        public Dictionary<string, string> RootPaths = new()
        {
            { "UserSettings", "//Item[@class='UserGameSettings']/Properties" },
        };

        public static IReadOnlyDictionary<FontSize, string?> FontSizes => new Dictionary<FontSize, string?>
        {
            { FontSize.x1, "1" },
            { FontSize.x2, "2" },
            { FontSize.x3, "3" },
            { FontSize.x4, "4" }
        };

        public bool Loaded { get; set; } = false;
        public bool LastLoadFailed { get; private set; }
        public bool LastSaveSucceeded { get; private set; }

        private static InterProcessLock Acquire()
        {
            var gate = new InterProcessLock("GlobalBasicSettings", TimeSpan.FromSeconds(2));
            if (gate.IsAcquired) return gate;
            gate.Dispose();
            throw new IOException("Roblox settings are busy. Try saving again.");
        }

        public string FileLocation => Path.Combine(Paths.Roblox, "GlobalBasicSettings_13.xml");

        public void SetPreset(string prefix, object? value)
        {
            foreach (var pair in PresetPaths.Where(x => x.Key.StartsWith(prefix)))
                SetValue(pair.Value, value);
        }

        public string? GetPreset(string prefix)
        {
            if (!PresetPaths.ContainsKey(prefix))
                return null;

            return GetValue(PresetPaths[prefix]);
        }

        public void SetValue(string path, object? value)
        {
            path = ResolvePath(path);

            XElement? element = Document?.XPathSelectElement(path);
            if (element is null)
                return;

            element.Value = value?.ToString()!;
        }

        public string? GetValue(string path)
        {
            path = ResolvePath(path);

            return Document?.XPathSelectElement(path)?.Value;
        }

        public void SetReadOnly(bool readOnly) => TrySetReadOnly(readOnly);

        internal bool TrySetReadOnly(bool readOnly)
        {
            const string LOG_IDENT = "GBSEditor::SetReadOnly";

            if (!File.Exists(FileLocation))
                return false;

            try
            {
                using var presetGate = Roblox.CompetitiveSettingsBackup.Acquire();
                if (!readOnly && Roblox.CompetitiveSettingsBackup.IsQualityLockInUse()) return false;
                using var gate = Acquire();
                FileAttributes attributes = File.GetAttributes(FileLocation);

                if (readOnly)
                    attributes |= FileAttributes.ReadOnly;
                else
                    attributes &= ~FileAttributes.ReadOnly;

                File.SetAttributes(FileLocation, attributes);

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Failed to set read-only on {FileLocation}");
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        public bool GetReadOnly()
        {
            if (!File.Exists(FileLocation))
                return false;

            return File.GetAttributes(FileLocation).HasFlag(FileAttributes.ReadOnly);
        }

        public void Load()
        {
            string LOG_IDENT = "GBSEditor::Load";

            App.Logger.WriteLine(LOG_IDENT, $"Loading from {FileLocation}...");

            try
            {
                using var gate = Acquire();
                if (!File.Exists(FileLocation))
                {
                    Document = null;
                    Loaded = false;
                    LastLoadFailed = false;
                    return;
                }
                var document = ParseDocument(AtomicFile.ReadText(FileLocation));
                Document = document;
                Loaded = true;
                LastLoadFailed = false;
            }
            catch (Exception ex)
            {
                LastLoadFailed = true;
                App.Logger.WriteLine(LOG_IDENT, "Failed to load!");
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        public virtual void Save() => TrySave();

        public bool TrySave()
        {
            string LOG_IDENT = "GBSEditor::Save";

            App.Logger.WriteLine(LOG_IDENT, $"Saving to {FileLocation}...");

            LastSaveSucceeded = false;
            if (LastLoadFailed) return false;
            if (Document is null) return LastSaveSucceeded = true;
            try
            {
                WriteDocument(Document);
                LastSaveSucceeded = true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Failed to save");
                App.Logger.WriteException(LOG_IDENT, ex);

                return false;
            }

            App.Logger.WriteLine(LOG_IDENT, "Save complete!");
            return true;
        }

        private static XDocument ParseDocument(string xml)
        {
            var document = XDocument.Parse(xml);
            if (document.Root?.Name != "roblox" || document.XPathSelectElement("//Item[@class='UserGameSettings']/Properties") is null)
                throw new InvalidDataException("The file does not contain Roblox user settings.");
            return document;
        }

        private void WriteDocument(XDocument document)
        {
            string xml = document.ToString();
            ParseDocument(xml);
            // Keep one lock order: preset ownership, then XML, then atomic file access.
            using var presetGate = Roblox.CompetitiveSettingsBackup.Acquire();
            if (Roblox.CompetitiveSettingsBackup.IsQualityLockInUse())
            {
                var current = ParseDocument(AtomicFile.ReadText(FileLocation));
                if (XNode.DeepEquals(document, current)) return;
                throw new IOException("Shared Roblox graphics settings are locked for active clients. Close all Roblox clients before changing them.");
            }
            using var gate = Acquire();
            FileAttributes? original = File.Exists(FileLocation) ? File.GetAttributes(FileLocation) : null;
            try
            {
                if (original is FileAttributes attributes && attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(FileLocation, attributes & ~FileAttributes.ReadOnly);
                AtomicFile.WriteText(FileLocation, xml);
            }
            finally
            {
                if (original is FileAttributes attributes && File.Exists(FileLocation)) File.SetAttributes(FileLocation, attributes);
            }
        }

        private string ResolvePath(string rawPath)
        {
            return Regex.Replace(rawPath, @"\{(.+?)\}", match =>
            {
                string key = match.Groups[1].Value;
                return RootPaths.TryGetValue(key, out var value) ? value : match.Value; ;
            });
        }

        public string GetVectorValue(string vectorName, string axis)
        {
            string basePath = ResolvePath(PresetPaths[vectorName]);
            XElement? vectorElement = Document?.XPathSelectElement(basePath);
            return vectorElement?.Element(axis)?.Value ?? "0";
        }

        public void SetVectorValue(string vectorName, string axis, string value)
        {
            string basePath = ResolvePath(PresetPaths[vectorName]);
            XElement? vectorElement = Document?.XPathSelectElement(basePath);

            XElement? axisElement = vectorElement?.Element(axis);
            if (axisElement != null)
            {
                axisElement.Value = value;
            }
        }

        public bool ExportSettings(string exportPath)
        {
            try
            {
                if (!File.Exists(FileLocation)) return false;
                string? dir = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(FileLocation, exportPath, true);
                return true;
            }
            catch { return false; }
        }

        public bool ImportSettings(string importPath)
        {
            try
            {
                if (!File.Exists(importPath)) return false;
                // Validate the exact snapshot before replacing any current settings or lock state.
                var imported = ParseDocument(AtomicFile.ReadText(importPath));
                WriteDocument(imported);
                Document = imported;
                Loaded = true;
                LastLoadFailed = false;
                LastSaveSucceeded = true;
                return true;
            }
            catch { return false; }
        }
    }
}
