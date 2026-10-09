using Bloxstrap.Resources;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Bloxstrap.UI.Elements.Dialogs
{
    public partial class AddFastFlagDialog
    {
        public string? FormattedName { get; private set; }
        public string? FormattedValue { get; private set; }

        public MessageBoxResult Result = MessageBoxResult.Cancel;

        public ObservableCollection<CommonValueItem> BooleanValues { get; } = new ObservableCollection<CommonValueItem>()
        {
            new CommonValueItem { Value = "True", Group = "Boolean" },
            new CommonValueItem { Value = "False", Group = "Boolean" },
        };

        public ObservableCollection<CommonValueItem> NumericValues { get; } = new ObservableCollection<CommonValueItem>()
        {
            new CommonValueItem { Value = "64", Group = "Intergers" },
            new CommonValueItem { Value = "100", Group = "Intergers" },
            new CommonValueItem { Value = "128", Group = "Intergers" },
            new CommonValueItem { Value = "256", Group = "Intergers" },
            new CommonValueItem { Value = "512", Group = "Intergers" },
            new CommonValueItem { Value = "1024", Group = "Intergers" },
            new CommonValueItem { Value = "2048", Group = "Intergers" },
            new CommonValueItem { Value = "4096", Group = "Intergers" },
            new CommonValueItem { Value = "8192", Group = "Intergers" },
            new CommonValueItem { Value = "10000", Group = "Intergers" },
            new CommonValueItem { Value = "16384", Group = "Intergers" },
            new CommonValueItem { Value = "2147483647", Group = "Intergers" },
            new CommonValueItem { Value = "-2147483648", Group = "Intergers" },
        };

        public ObservableCollection<CommonValueItem> SpecialValues { get; } = new ObservableCollection<CommonValueItem>()
        {
            new CommonValueItem { Value = "null", Group = "Special" },
        };

        public CollectionViewSource CommonValuesView { get; }

        public AddFastFlagDialog()
        {
            InitializeComponent();


            var allValues = new ObservableCollection<CommonValueItem>();
            foreach (var item in BooleanValues) allValues.Add(item);
            foreach (var item in NumericValues) allValues.Add(item);
            foreach (var item in SpecialValues) allValues.Add(item);

            CommonValuesView = new CollectionViewSource { Source = allValues };
            CommonValuesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CommonValueItem.Group)));

            DataContext = this;
            Tabs.SelectedIndex = 0;
            FlagNameTextBox.TextChanged += (_, _) => UpdateConfirmation();
            JsonTextBox.TextChanged += (_, _) => UpdateConfirmation();
            // Keep confirmation current even if a style consumes the routed edit event.
            FlagValueComboBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new RoutedEventHandler((_, _) => UpdateConfirmation()), handledEventsToo: true);
            Tabs.SelectionChanged += (_, _) => UpdateConfirmation();
            UpdateConfirmation();
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = $"JSON and Text Files|*.json;*.txt"
            };

            if (dialog.ShowDialog() != true)
                return;

            try { JsonTextBox.Text = Roblox.FastFlagImport.ReadFile(dialog.FileName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error); }
        }

        internal bool ValidateInput()
        {
            ValidationMessage.Text = "";
            try
            {
                if (Tabs.SelectedIndex == 0)
                {
                    string name = FlagNameTextBox.Text.Trim();
                    string value = FlagValueComboBox.Text;
                    Roblox.FastFlagImport.ValidateName(name);
                    if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Enter a value.");
                    if (value.Length > Roblox.FastFlagImport.MaxValueLength) throw new InvalidDataException("Value is too long.");
                    FormattedName = name; FormattedValue = value;
                }
                else
                {
                    if (Roblox.FastFlagImport.ParseDetailed(JsonTextBox.Text).Flags.Count == 0)
                        throw new InvalidDataException("No non-null flag values were found.");
                    FormattedName = null; FormattedValue = null;
                }
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                ValidationMessage.Text = ex.Message;
                return false;
            }
        }

        private void OKButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateInput()) return;
            Result = MessageBoxResult.OK;
            DialogResult = true;
            Close();
        }

        private void UpdateConfirmation()
        {
            if (ConfirmButton is null) return;
            ValidationMessage.Text = "";
            ConfirmButton.IsEnabled = Tabs.SelectedIndex == 0
                ? !string.IsNullOrWhiteSpace(FlagNameTextBox.Text) && !string.IsNullOrWhiteSpace(FlagValueComboBox.Text)
                : !string.IsNullOrWhiteSpace(JsonTextBox.Text);
        }
    }

    public class CommonValueItem
    {
        public string Value { get; set; } = "";
        public string Group { get; set; } = "";

        public override string ToString() => Value;
    }
}
