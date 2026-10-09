using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bloxstrap;
using Bloxstrap.Enums;
using Bloxstrap.UI;
using Bloxstrap.UI.Elements.Base;
using Bloxstrap.UI.Elements.Dialogs;
using Bloxstrap.Models;
using Bloxstrap.Roblox;

internal static class PopupChecks
{
    internal static void Run(Action<bool, string> check, string output, Action<FrameworkElement, string, int, int> render)
    {
        Directory.CreateDirectory(Paths.Cache);
        Theme previous = App.Settings.Prop.Theme;
        try
        {
            BitmapSource? artwork = null;
            foreach (var theme in BrandTheme.Choices)
            {
                App.Settings.Prop.Theme = theme;
                BrandTheme.Apply(Application.Current.Resources, theme);
                ExecutorNoticeChecks.Run(check, render, Path.Combine(output, "Popup-ExecutorCompatibilityNotice-" + theme + ".png"));
                if (theme == Theme.CrimsonContract) artwork = ((ImageBrush)Application.Current.Resources["ApplicationBackground"]).ImageSource as BitmapSource;
                Window[] dialogs = [new RegionCalibrationDialog(true), new SystemPerformanceDialog(),
                    new BadRegionAlertWindow("NON-PREFERRED SERVER", "Toronto, Canada\nYour preferred and fallback regions are elsewhere."),
                    new AutoLogRejoinWindow(() => true),
                    new AddFastFlagDialog(),
                    new AdvancedSettingsDialog(), new LanguageSelectorDialog(), new UninstallerDialog(),
                    new QuickSignCodeDialog(), new ExceptionDialog(new IOException("Synthetic UI audit error")),
                    new FluentMessageBox("Keep the current settings?\n\nYou can cancel and review them before continuing.", MessageBoxImage.None, MessageBoxButton.YesNoCancel, MessageBoxResult.Cancel),
                    new ConnectivityDialog("Server details are unavailable", "Your game can continue. Try again later.", MessageBoxImage.None, new HttpRequestException("Preview: service unavailable"))];
                foreach (Window dialog in dialogs)
                {
                    check(theme == Theme.CrimsonContract ? dialog.Background is DrawingBrush : dialog.Background is SolidColorBrush,
                        "Popup uses a dimmed arena or selected solid theme: " + dialog.GetType().Name + "/" + theme);
                    var root = dialog.Content as FrameworkElement ?? throw new InvalidOperationException("Popup content is missing.");
                    root.DataContext = dialog.DataContext;
                    root.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, dialog.Foreground);
                    root.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, dialog.FontFamily);
                    dialog.Content = null;
                    int width = (int)dialog.Width;
                    int height = dialog is SystemPerformanceDialog ? 820 : dialog is RegionCalibrationDialog ? 720 : dialog is ConnectivityDialog ? 390 : dialog is AddFastFlagDialog ? 420 :
                        dialog is AdvancedSettingsDialog or LanguageSelectorDialog or UninstallerDialog or QuickSignCodeDialog or ExceptionDialog ? 600 : 270;
                    render(new Border { Background = dialog.Background, Child = root },
                        Path.Combine(output, "Popup-" + dialog.GetType().Name + "-" + theme + ".png"), width, height);
                    dialog.Close();
                }
            }

            App.Settings.Prop.Theme = Theme.CrimsonContract;
            ExecutorNoticeChecks.CheckRealTimer(check);
            BrandTheme.Apply(Application.Current.Resources, Theme.CrimsonContract);
            check(ReferenceEquals(artwork, ((ImageBrush)Application.Current.Resources["ApplicationBackground"]).ImageSource),
                "Opening more dialogs reuses the frozen packaged arena image");
            var background = (DrawingBrush)Application.Current.Resources["PopupBackground"];
            check(background.IsFrozen, "Layered popup backgrounds are immutable and safe to share");
            var bitmap = new RenderTargetBitmap(40, 40, 96, 96, PixelFormats.Pbgra32);
            var sample = new Border { Background = background, Width = 40, Height = 40 };
            sample.Measure(new Size(40, 40)); sample.Arrange(new Rect(0, 0, 40, 40)); bitmap.Render(sample);
            byte[] pixels = new byte[40 * 40 * 4]; bitmap.CopyPixels(pixels, 160, 0);
            check(Enumerable.Range(0, 1600).All(i => pixels[i * 4 + 3] == 255) &&
                Enumerable.Range(0, 1600).All(i => pixels[i * 4] < 100 && pixels[i * 4 + 1] < 100 && pixels[i * 4 + 2] < 100) &&
                Enumerable.Range(0, 1600).Select(i => pixels[i * 4 + 2]).Distinct().Count() > 2,
                "Popup artwork remains visible under an opaque dark scrim for readable light text");

            foreach (var buttons in new[] { MessageBoxButton.OKCancel, MessageBoxButton.YesNoCancel, MessageBoxButton.YesNo })
            {
                MessageBoxResult negative = buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
                var dialog = new FluentMessageBox("Confirmation fixture", MessageBoxImage.None, buttons, negative);
                Button[] choices = [dialog.ButtonOne, dialog.ButtonTwo, dialog.ButtonThree];
                check(choices.Count(button => button.IsDefault) == 1 && choices.Single(button => button.IsDefault).IsCancel,
                    "Confirmation honors the caller's negative default choice: " + buttons);
                dialog.Close();
                check(dialog.Result == negative, "Closing a confirmation preserves the negative/cancel result: " + buttons);
            }
            var yes = new FluentMessageBox("Confirmation fixture", MessageBoxImage.None, MessageBoxButton.YesNo, MessageBoxResult.No);
            yes.ButtonOne.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(yes.Result == MessageBoxResult.Yes, "An explicit affirmative button click still returns Yes");

            var existing = new FluentMessageBox("Backdrop fixture", MessageBoxImage.None, MessageBoxButton.OK);
            new System.Windows.Interop.WindowInteropHelper(existing).EnsureHandle();
            App.ApplyWindowBackdrop(existing, WindowsBackdrops.Aero);
            check(!existing.AllowsTransparency && existing.WindowBackdropType == Wpf.Ui.Appearance.BackgroundType.None,
                "An existing native window defers incompatible transparency changes instead of crashing");
            check(existing.Content is Panel { Background: DrawingBrush }, "Popup content keeps its artwork after native backdrop initialization");
            existing.Close();
            var fresh = new FluentMessageBox("Backdrop fixture", MessageBoxImage.None, MessageBoxButton.OK);
            App.ApplyWindowBackdrop(fresh, WindowsBackdrops.Aero);
            check(fresh.AllowsTransparency && fresh.WindowStyle == WindowStyle.None,
                "A new window configures transparent composition in the valid order before handle creation");
            fresh.Close();
            var nativeTitle = new RegionCalibrationDialog();
            App.ApplyWindowBackdrop(nativeTitle, WindowsBackdrops.Aero);
            check(!nativeTitle.AllowsTransparency && nativeTitle.WindowBackdropType == Wpf.Ui.Appearance.BackgroundType.None && nativeTitle.Content is Border { Background: DrawingBrush },
                "Native-title-bar dialogs retain their artwork without an unsupported backdrop effect");
            nativeTitle.Close();

            string? savedFont = App.Settings.Prop.CustomFontPath;
            string corrupt = Path.Combine(Paths.Cache, "corrupt-popup-font.ttf"); File.WriteAllText(corrupt, "invalid font fixture");
            try
            {
                App.Settings.Prop.CustomFontPath = corrupt;
                var dialog = new RegionCalibrationDialog();
                typeof(WpfUiWindow).GetMethod("OnSourceInitialized", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [EventArgs.Empty]);
                check(dialog.FontFamily is not null, "A corrupt saved custom font cannot prevent a dialog opening");
                dialog.Close();
            }
            finally { App.Settings.Prop.CustomFontPath = savedFont; }

            try
            {
                App.Settings.Prop.CustomFontPath = Path.GetFullPath("Bloxstrap/Resources/Fonts/Rubik-VariableFont_wght.ttf");
                var alert = new BadRegionAlertWindow("Font fixture", "Preview only");
                check(alert.FontFamily.Source == FontManager.LoadFontFromFile(App.Settings.Prop.CustomFontPath)!.Source,
                    "New code-built region alerts honor the saved app font");
                alert.Close();
            }
            finally { App.Settings.Prop.CustomFontPath = savedFont; }

            var add = new AddFastFlagDialog();
            check(add.FlagValueComboBox.Text.Length == 0 && !add.ConfirmButton.IsEnabled,
                "FastFlag placeholder is display-only and cannot become an imported flag value");
            add.FlagNameTextBox.Text = "FFlagFixture";
            add.FlagValueComboBox.ApplyTemplate();
            var valueEditor = add.FlagValueComboBox.Template.FindName("PART_EditableTextBox", add.FlagValueComboBox) as TextBox;
            check(valueEditor is not null, "FastFlag value exposes a working editable textbox");
            valueEditor!.Text = "True";
            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            check(add.ConfirmButton.IsEnabled && add.FlagValueComboBox.Text == "True", $"Typing a FastFlag value enables confirmation (control={add.FlagValueComboBox.Text}, editor={valueEditor.Text}, enabled={add.ConfirmButton.IsEnabled}, tab={add.Tabs.SelectedIndex})");
            valueEditor.Text = "";
            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            check(!add.ConfirmButton.IsEnabled, "A flag name without a value cannot be confirmed");
            add.Tabs.SelectedIndex = 1; add.JsonTextBox.Text = "{\"FFlagFixture\":true}";
            check(add.ConfirmButton.IsEnabled, "The JSON import tab enables confirmation for entered text");
            add.JsonTextBox.Text = "   ";
            check(!add.ConfirmButton.IsEnabled, "Whitespace-only JSON cannot be confirmed");
            add.JsonTextBox.Text = "{broken";
            add.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(add.Result == MessageBoxResult.Cancel && add.JsonTextBox.Text == "{broken" && add.ValidationMessage.Text.Length > 0,
                "Invalid FastFlag JSON stays in the dialog with an inline error instead of discarding input");
            add.JsonTextBox.Text = "{\"FFlagNull\":null}";
            check(!add.ValidateInput(), "An import containing only null values remains open for correction");
            add.Tabs.SelectedIndex = 0; add.FlagNameTextBox.Text = "bad flag"; add.FlagValueComboBox.Text = "True";
            check(!add.ValidateInput() && add.ValidationMessage.Text.Length > 0, "Invalid single flag names are rejected before the dialog closes");
            add.FlagNameTextBox.Text = "FFlagFixture"; add.FlagValueComboBox.Text = "  preserve string whitespace  ";
            check(add.ValidateInput() && add.FormattedValue == "  preserve string whitespace  ", "Single flag validation preserves the exact value accepted by JSON import");
            add.Close();

            string file = Path.Combine(Paths.Cache, "bounded-flag-import.json");
            File.WriteAllText(file, "\uFEFF{\"FFlagFixture\":true}");
            check(FastFlagImport.Parse(FastFlagImport.ReadFile(file))["FFlagFixture"] == "True",
                "Bounded file import preserves supported BOM and JSON flag values");
            using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write)) stream.SetLength(FastFlagImport.MaxJsonLength + 1L);
            bool oversized = false;
            try { FastFlagImport.ReadFile(file); } catch (InvalidDataException) { oversized = true; }
            check(oversized, "Oversized import files are rejected through the opened handle before allocation");
            File.WriteAllText(file, "{}");
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool refused = false;
                try { FastFlagImport.ReadFile(file); } catch (IOException) { refused = true; }
                check(refused, "A locked import fails recoverably without changing any flags");
            }
        }
        finally { App.Settings.Prop.Theme = previous; BrandTheme.Apply(Application.Current.Resources, previous); }
    }
}
