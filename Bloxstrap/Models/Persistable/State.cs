namespace Bloxstrap.Models.Persistable
{
    public class State : IJsonNormalizable
    {
        public bool TestModeWarningShown { get; set; } = false;

        public bool IgnoreOutdatedChannel { get; set; } = false;

        public bool PromptWebView2Install { get; set; } = true;

        public string? LastPage { get; set; } = null!;

        public bool ForceReinstall { get; set; } = false;

        public WindowState SettingsWindow { get; set; } = new();

        public List<ModConfig> Mods { get; set; } = new();

        void IJsonNormalizable.Normalize()
        {
            SettingsWindow ??= new();
            if (!double.IsFinite(SettingsWindow.Width) || SettingsWindow.Width < 0) SettingsWindow.Width = 0;
            if (!double.IsFinite(SettingsWindow.Height) || SettingsWindow.Height < 0) SettingsWindow.Height = 0;
            if (!double.IsFinite(SettingsWindow.Left)) SettingsWindow.Left = 0;
            if (!double.IsFinite(SettingsWindow.Top)) SettingsWindow.Top = 0;
            Mods = (Mods ?? new()).Where(x => x is not null && !string.IsNullOrWhiteSpace(x.FolderName)).ToList();
            foreach (var mod in Mods) mod.Target ??= "Player";
        }
    }
}
