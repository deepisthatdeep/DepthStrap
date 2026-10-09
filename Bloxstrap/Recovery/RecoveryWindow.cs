using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using FontFamily = System.Windows.Media.FontFamily;
namespace DepthStrap.Recovery;

internal sealed class RecoveryWindow : Window
{
    private readonly TextBox _output = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 250,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = new SolidColorBrush(Color.FromRgb(28, 17, 19)),
        Foreground = Brushes.WhiteSmoke, Padding = new Thickness(12), BorderBrush = new SolidColorBrush(Color.FromRgb(81, 53, 57)),
        Text = "Choose an inspection. Device identifiers are masked in the displayed results. No checks run automatically." };
    private readonly StackPanel _actions = new();
    private readonly TextBlock _status = new() { Text = "Ready · Beta", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
    private bool _busy;
    internal string? ManagedInstallationExecutable { get; }
    internal RecoveryWindow(string? installationExecutable = null)
    {
        ManagedInstallationExecutable = installationExecutable;
        Title = "DepthStrap — Anti Api ( Beta )"; Width = 880; Height = 820; MinWidth = 650; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Foreground = Brushes.WhiteSmoke; FontFamily = new FontFamily("Segoe UI");
        var buttons = new Style(typeof(Button));
        buttons.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(65, 35, 41))));
        buttons.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.WhiteSmoke));
        buttons.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(159, 102, 112))));
        Resources.Add(typeof(Button), buttons);
        var panel = new StackPanel { Margin = new Thickness(28) };
        void Text(string text, double size = 14) => panel.Children.Add(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        Text("DepthStrap Anti Api ( Beta )", 26);
        Text("Review the scope before confirming a reset. Resetting Roblox data signs you out and removes local settings. MAC changes require administrator approval and a supported adapter driver.");
        Text("Roblox reset and MAC address", 18);
        AddAction("1. Delete Roblox registry keys and cookies…", () => ResetDialogs.SignInAsync(this, message => _output.Text = message));
        AddAction("2. Delete Roblox data folders…", () => ResetDialogs.AllDataAsync(this, message => _output.Text = message, ManagedInstallationExecutable));
        AddAction("3. Spoof MAC address…", () => MacControls.ShowAsync(this));
        _actions.Children.Add(new TextBlock { Text = "Installation and device diagnostics", FontSize = 18, Margin = new Thickness(0, 12, 0, 12) });
        AddAction("Inspect adapters / MAC", () =>
        {
            var adapters = AdapterInspection.Inspect();
            _output.Text = adapters.Length == 0 ? "No adapter information is available." : string.Join("\n\n", adapters.Select(x =>
                $"{x.Name}\n{x.Kind} · {x.State} · current link {x.LinkBitsPerSecond / 1_000_000:N0} Mbps\nMAC: {x.MaskedMac}"));
            return Task.CompletedTask;
        });
        AddAction("Inspect hardware / displays", async () =>
        {
            var hardware = await HardwareInspection.InspectAsync();
            _output.Text = $"{hardware.Manufacturer} · {hardware.Model}\nUUID: {hardware.MaskedUuid}\nMotherboard serial: {hardware.MaskedBoardSerial}\nDisplays: {string.Join(", ", hardware.Monitors)}\n\nInspection does not modify hardware identifiers, licensing or monitor settings.";
        });
        AddAction("Check installed Roblox build…", () =>
        {
            var picker = new OpenFileDialog { Title = "Select your installed RobloxPlayerBeta.exe", Filter = "Roblox Player|RobloxPlayerBeta.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == true)
            {
                var client = ClientInspection.Inspect(picker.FileName);
                _output.Text = $"Client files: {(client.Complete ? "valid executable and required split DLL" : "incomplete or invalid; use DepthStrap's build repair before launching")}\nVersion: {client.Version}\nArchitecture: {client.Architecture}\nDLL: {client.DllState}\n\nThis check cannot establish which Roblox versions third-party tools support. No external launcher handoff is included.";
            }
            return Task.CompletedTask;
        });
        panel.Children.Add(_actions); panel.Children.Add(_output);
        panel.Children.Add(_status);
        RecoveryTheme.Apply(this);
        if (TryFindResource("ControlFillColorDefaultBrush") is Brush)
        {
            _output.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
            _output.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
            _output.SetResourceReference(Control.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        }
        var root = new Grid();
        var surface = new Border { Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        surface.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Background)) { Source = this });
        root.Children.Add(surface);
        Content = root;
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }
    private void AddAction(string title, Func<Task> execute)
    {
        var button = new Button { Content = title, Padding = new Thickness(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        button.Click += async (_, _) => await RunAsync(execute); _actions.Children.Add(button);
    }
    private async Task RunAsync(Func<Task> execute)
    {
        if (_busy) return;
        _busy = true; IsEnabled = false; _status.Text = "Working…";
        try { await execute(); _status.Text = "Finished · no report was uploaded or saved automatically"; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _status.Text = ex.Message; }
        finally { _busy = false; IsEnabled = true; }
    }
}
