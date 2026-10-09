using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Mvvm.Contracts;
using Wpf.Ui.Mvvm.Services;

namespace Bloxstrap.UI.Elements.Base
{
    public abstract class WpfUiWindow : UiWindow
    {
        private readonly IThemeService _themeService = new ThemeService();

        public WpfUiWindow()
        {
            ApplyTheme();
        }

        public void ApplyTheme()
        {
            var theme = App.Settings.Prop.Theme.GetFinal();
            _themeService.SetTheme(theme == Enums.Theme.Light ? ThemeType.Light : ThemeType.Dark);
            BrandTheme.Apply(Application.Current.Resources, theme);
            SetResourceReference(BackgroundProperty, "ApplicationBackground");
            SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
            Icon = new BitmapImage(new Uri(BrandTheme.IconUri));
            Title = App.DisplayName;
        }
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Hardware Accel
            if (App.Settings.Prop.WPFSoftwareRender || App.LaunchSettings.NoGPUFlag.Active)
            {
                if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
                    hwndSource.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            }

            FontManager.ApplySavedFont(this);
        }
    }
}
