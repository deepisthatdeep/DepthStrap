using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bloxstrap.UI
{
    /// <summary>The app's packaged artwork and independent color themes.</summary>
    public static class BrandTheme
    {
        public const string ArenaUri = "pack://application:,,,/DepthStrap;component/Resources/Brand/CrimsonContract.png";
        public const string IconUri = "pack://application:,,,/DepthStrap;component/DepthStrap.ico";
        private static readonly Lazy<BitmapImage> ArenaImage = new(() =>
        {
            var image = new BitmapImage(new Uri(ArenaUri));
            image.Freeze();
            return image;
        });
        public static IReadOnlyList<Theme> Choices { get; } = new[]
        {
            Theme.CrimsonContract, Theme.Dark, Theme.Light, Theme.Blue, Theme.Purple,
            Theme.Green, Theme.Orange, Theme.Pink, Theme.Default
        };

        public static void Migrate(Settings settings)
        {
            if (settings.AppearanceVersion >= 1) return;
            settings.Theme = Theme.CrimsonContract;
            settings.BootstrapperIcon = BootstrapperIcon.IconDepthStrap;
            settings.BootstrapperStyle = BootstrapperStyle.DepthStrapDialog;
            settings.SelectedBackdrop = WindowsBackdrops.None;
            settings.BootstrapperTitle = App.DisplayName;
            settings.BackgroundImagePath = null;
            settings.BootstrapperIconCustomLocation = "";
            settings.AppearanceVersion = 1;
        }

        private static void Set(ResourceDictionary resources, string key, string hex)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            resources[key] = color;
            resources[key + "Brush"] = new SolidColorBrush(color);
        }

        public static void Apply(ResourceDictionary resources, Theme theme)
        {
            if (theme == Theme.Custom) theme = Theme.CrimsonContract; // compatibility with old saved themes
            bool light = theme == Theme.Light;
            resources.MergedDictionaries[2] = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/DepthStrap;component/UI/Style/{(light ? "Light" : "Dark")}.xaml")
            };
            string background = theme switch
            {
                Theme.Light => "#F4F4F4", Theme.Blue => "#142737", Theme.Purple => "#291D38",
                Theme.Green => "#183129", Theme.Orange => "#38261B", Theme.Pink => "#362230",
                Theme.CrimsonContract => "#180D10", _ => "#202020"
            };
            string accent = theme switch
            {
                Theme.Light => "#385A80", Theme.Blue => "#93C6F6", Theme.Purple => "#C5A3F4",
                Theme.Green => "#99D9AE", Theme.Orange => "#EFBC86", Theme.Pink => "#EDB3D5",
                Theme.CrimsonContract => "#D99B8C", _ => "#C8C8C8"
            };
            foreach (string key in new[] { "SystemAccentColor", "SystemAccentColorSecondary", "SystemAccentColorTertiary" }) Set(resources, key, accent);
            Set(resources, "TextOnAccentFillColorPrimary", light ? "#FFFFFF" : "#201113");
            Set(resources, "TextFillColorPrimary", light ? "#191919" : "#F5EDE4");
            Set(resources, "TextFillColorSecondary", light ? "#555555" : "#D6C9BE");
            Set(resources, "TextFillColorTertiary", light ? "#666666" : "#B8A9A0");
            Set(resources, "ApplicationBackground", background);
            bool arena = theme == Theme.CrimsonContract;
            Set(resources, "ControlFillColorDefault", light ? "#FDFDFD" : arena ? "#ED211417" : "#DC101010");
            Set(resources, "CardBackgroundFillColorDefault", light ? "#FDFDFD" : arena ? "#EC211417" : "#DC181818");
            Set(resources, "CardBackgroundFillColorSecondary", light ? "#F5F5F5" : arena ? "#DE1A1114" : "#CD141414");
            resources["NewTextEditorBackground"] = new SolidColorBrush((Color)resources["ControlFillColorDefault"]);
            resources["NewTextEditorForeground"] = resources["TextFillColorPrimaryBrush"];
            resources["NewTextEditorLink"] = new SolidColorBrush((Color)resources["SystemAccentColor"]);
            resources["PrimaryBackgroundColor"] = resources["CardBackgroundFillColorSecondaryBrush"];
            resources["NormalDarkAndLightBackground"] = resources["CardBackgroundFillColorDefaultBrush"];
            resources["BrandNavigationBackground"] = resources["CardBackgroundFillColorSecondaryBrush"];
            resources["ApplicationBackground"] = arena
                ? new ImageBrush(ArenaImage.Value) { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Bottom }
                : resources["ApplicationBackgroundBrush"];
            if (arena)
            {
                var bounds = new RectangleGeometry(new Rect(0, 0, ArenaImage.Value.PixelWidth, ArenaImage.Value.PixelHeight));
                var layers = new DrawingGroup();
                layers.Children.Add(new GeometryDrawing(new ImageBrush(ArenaImage.Value), null, bounds));
                layers.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(185, 24, 13, 16)), null, bounds));
                var popup = new DrawingBrush(layers) { Stretch = Stretch.UniformToFill };
                popup.Freeze();
                resources["PopupBackground"] = popup;
            }
            else resources["PopupBackground"] = resources["ApplicationBackgroundBrush"];
        }

        internal static void ApplyPopup(Window window)
        {
            Apply(Application.Current.Resources, App.Settings.Prop.Theme.GetFinal());
            window.SetResourceReference(Window.BackgroundProperty, "PopupBackground");
            window.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
            window.Icon = new BitmapImage(new Uri(IconUri));
            FontManager.ApplySavedFont(window);
        }

        internal static System.Windows.Controls.Border PopupSurface(FrameworkElement content)
        {
            var surface = new System.Windows.Controls.Border { Child = content };
            surface.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "PopupBackground");
            return surface;
        }
    }
}
