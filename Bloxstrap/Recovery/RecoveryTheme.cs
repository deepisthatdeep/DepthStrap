using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FontFamily = System.Windows.Media.FontFamily;

namespace DepthStrap.Recovery;

internal static class RecoveryTheme
{
    internal static void InitializeHelperAppearance(string executable)
    {
        // Read only the appearance selection beside this installed app. Do not
        // enter normal startup or load/reset Roblox state in an elevated helper.
        var theme = Bloxstrap.Enums.Theme.CrimsonContract;
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "Settings.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= 0 || stream.Length > 4 * 1024 * 1024) throw new IOException("Invalid appearance file size.");
            using var data = JsonDocument.Parse(stream);
            if (data.RootElement.TryGetProperty("Theme", out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && Enum.IsDefined(typeof(Bloxstrap.Enums.Theme), number))
                    theme = (Bloxstrap.Enums.Theme)number;
                else if (value.ValueKind == JsonValueKind.String && Enum.TryParse<Bloxstrap.Enums.Theme>(value.GetString(), out var parsed) && Enum.IsDefined(parsed))
                    theme = parsed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        Bloxstrap.App.Settings.Prop.Theme = theme;
        if (Application.Current is not null) Bloxstrap.UI.BrandTheme.Apply(Application.Current.Resources, theme);
    }
    private static readonly Lazy<Brush> Background = new(() =>
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/DepthStrap;component/Resources/Brand/CrimsonContract.png"));
        image.Freeze();
        var bounds = new RectangleGeometry(new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        var layers = new DrawingGroup();
        layers.Children.Add(new GeometryDrawing(new ImageBrush(image), null, bounds));
        layers.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(205, 24, 13, 16)), null, bounds));
        var brush = new DrawingBrush(layers) { Stretch = Stretch.UniformToFill }; brush.Freeze(); return brush;
    });

    internal static void Apply(Window window)
    {
        bool integrated = window.TryFindResource("PopupBackground") is Brush && window.TryFindResource("TextFillColorPrimaryBrush") is Brush;
        if (integrated)
        {
            window.SetResourceReference(Window.BackgroundProperty, "PopupBackground");
            window.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
        else { window.Background = Background.Value; window.Foreground = Brushes.WhiteSmoke; }
        window.FontFamily = window.Owner?.FontFamily ?? Application.Current?.MainWindow?.FontFamily ?? new FontFamily("Segoe UI");
        window.Icon = new BitmapImage(new Uri("pack://application:,,,/DepthStrap;component/DepthStrap.ico"));
        window.MaxWidth = Math.Max(240, SystemParameters.WorkArea.Width - 40);
        window.MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height - 40);
        Brush controlBackground = integrated && window.TryFindResource("ControlFillColorDefaultBrush") is Brush fill ? fill : new SolidColorBrush(Color.FromRgb(49, 28, 33));
        Brush controlForeground = window.Foreground;
        Brush controlBorder = integrated && window.TryFindResource("ControlStrokeColorDefaultBrush") is Brush stroke ? stroke : new SolidColorBrush(Color.FromRgb(159, 102, 112));
        window.Resources[SystemColors.WindowBrushKey] = controlBackground;
        window.Resources[SystemColors.WindowTextBrushKey] = controlForeground;
        window.Resources[SystemColors.ControlBrushKey] = controlBackground;
        window.Resources[SystemColors.ControlTextBrushKey] = controlForeground;
        window.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(159, 102, 112));
        window.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        foreach (Type control in new[] { typeof(Button), typeof(TextBox), typeof(ComboBox) })
        {
            var style = new Style(control);
            style.Setters.Add(new Setter(Control.BackgroundProperty, controlBackground));
            style.Setters.Add(new Setter(Control.ForegroundProperty, controlForeground));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, controlBorder));
            if (control == typeof(TextBox)) style.Setters.Add(new Setter(TextBox.CaretBrushProperty, controlForeground));
            if (control == typeof(ComboBox))
            {
                style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 28d));
                // The native Windows template can retain a light selection
                // surface even when the app sets a dark Background.
                style.Setters.Add(new Setter(Control.TemplateProperty, System.Windows.Markup.XamlReader.Parse("""
                    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ComboBox">
                      <Grid>
                        <ToggleButton Name="PART_Toggle" Focusable="False" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"
                            Width="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" Height="{Binding ActualHeight, RelativeSource={RelativeSource TemplatedParent}}"
                            Background="{TemplateBinding Background}" Foreground="{TemplateBinding Foreground}" BorderBrush="{TemplateBinding BorderBrush}"
                            IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                          <ToggleButton.Template>
                            <ControlTemplate TargetType="ToggleButton">
                              <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" Padding="6,4">
                                <TextBlock Text="⌄" Foreground="{TemplateBinding Foreground}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                              </Border>
                            </ControlTemplate>
                          </ToggleButton.Template>
                        </ToggleButton>
                        <ContentPresenter Margin="7,4,25,4" VerticalAlignment="Center" IsHitTestVisible="False"
                            Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                        <Popup Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" IsOpen="{TemplateBinding IsDropDownOpen}" Focusable="False">
                          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1"
                              MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}">
                            <ScrollViewer MaxHeight="{TemplateBinding MaxDropDownHeight}" CanContentScroll="True" VerticalScrollBarVisibility="Auto">
                              <ItemsPresenter/>
                            </ScrollViewer>
                          </Border>
                        </Popup>
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.55"/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                    """)));
            }
            window.Resources[control] = style;
        }
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, controlForeground));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, controlBackground));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        window.Resources[typeof(ComboBoxItem)] = itemStyle;
    }
}

internal sealed class RecoveryPrompt : Window
{
    internal MessageBoxResult Result { get; private set; }
    internal RecoveryPrompt(string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        Title = title; Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; RecoveryTheme.Apply(this);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = image == MessageBoxImage.Warning ? "Review before continuing" : "DepthStrap Anti Api ( Beta )", Margin = new Thickness(0, 10, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(217, 155, 140)) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 20) });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(actions);
        MessageBoxResult[] choices = buttons switch
        {
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK]
        };
        Result = choices.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel : choices[^1];
        if (!choices.Contains(defaultResult)) defaultResult = choices[0];
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.ToString(), MinWidth = 86, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 4, 0, 0), IsDefault = choice == defaultResult, IsCancel = choice == Result };
            button.Click += (_, _) => { Result = choice; Close(); }; actions.Children.Add(button);
        }
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    internal static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        var dialog = new RecoveryPrompt(message, title, buttons, image, defaultResult);
        if (owner is not null) { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        dialog.ShowDialog(); return dialog.Result;
    }

    internal static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult = MessageBoxResult.None)
        => Show(null, message, title, buttons, image, defaultResult);
}
