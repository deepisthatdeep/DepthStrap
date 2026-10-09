using System.Windows;

namespace Bloxstrap.Models
{
    public static class FontManager
    {
        public static bool IsCustomFontApplied { get; private set; }

        internal static bool ApplySavedFont(Window window)
        {
            string? path = App.Settings.Prop.CustomFontPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            try
            {
                var font = LoadFontFromFile(path);
                if (font is null) return false;
                window.FontFamily = font;
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { App.Logger.WriteException("FontManager::ApplySavedFont", ex); return false; }
        }

        public static System.Windows.Media.FontFamily? LoadFontFromFile(string fontFilePath)
        {
            if (!File.Exists(fontFilePath))
                return null;

            string path = Path.GetFullPath(fontFilePath);
            var face = Roblox.AppearanceFont.LoadFace(path);
            var directory = new Uri(Path.GetDirectoryName(path)! + Path.DirectorySeparatorChar);
            string familyName = face.FamilyNames.Values.First();
            return new System.Windows.Media.FontFamily(directory,
                "./" + Uri.EscapeDataString(Path.GetFileName(path)) + "#" + familyName);
        }

        public static bool ApplySavedCustomFont()
        {
            string? savedFontPath = App.Settings.Prop.CustomFontPath;

            if (!string.IsNullOrWhiteSpace(savedFontPath) && File.Exists(savedFontPath))
            {
                try
                {
                    var font = LoadFontFromFile(savedFontPath);
                    if (font != null)
                    {
                        ApplyFontGlobally(font);
                        IsCustomFontApplied = true;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("FontManager", $"Failed to load saved font: {ex}");
                }
            }

            return false;
        }

        public static void ApplyFontGlobally(System.Windows.Media.FontFamily fontFamily)
        {
            Application.Current.Resources[SystemFonts.MessageFontFamilyKey] = fontFamily;

            foreach (Window window in Application.Current.Windows)
                window.FontFamily = fontFamily;

            IsCustomFontApplied = fontFamily.Source != "Segoe UI";
        }

        internal static void SetCustomFont(string? path)
        {
            var font = path is null ? new System.Windows.Media.FontFamily("Segoe UI") :
                LoadFontFromFile(path) ?? throw new InvalidDataException("The selected font is unavailable.");
            string? previous = App.Settings.Prop.CustomFontPath;
            App.Settings.Prop.CustomFontPath = path;
            try { Networking.AdaptiveRegionService.SaveUserSettings(); }
            catch { App.Settings.Prop.CustomFontPath = previous; throw; }
            ApplyFontGlobally(font);
        }

        public static void RemoveCustomFont() => SetCustomFont(null);
    }
}
