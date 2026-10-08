using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Bloxstrap.Roblox;
using Bloxstrap.UI.Elements.Dialogs;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    public partial class FastFlagEditorPage
    {
        private readonly ObservableCollection<FastFlag> _rows = new();
        public FastFlagEditorPage() { InitializeComponent(); FlagGrid.ItemsSource = _rows; }
        private void Page_Loaded(object sender, RoutedEventArgs e) => ReloadList();
        public void ReloadList()
        {
            if (FlagGrid is null) return;
            _rows.Clear();
            string query = SearchBox.Text;
            foreach (var flag in App.FastFlags.Prop.OrderBy(x => x.Key))
                if (flag.Key.Contains(query, StringComparison.OrdinalIgnoreCase) || (flag.Value?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                    _rows.Add(new FastFlag { Name = flag.Key, Value = flag.Value?.ToString() ?? "" });
            CountLabel.Text = $"{_rows.Count} shown · {App.FastFlags.Prop.Count} total. Changes apply after Save and the next Roblox launch.";
        }
        private void Search_Changed(object sender, TextChangedEventArgs e) => ReloadList();
        private void ApplyJson(string json)
        {
            var import = FastFlagImport.ParseDetailed(json); // validate the complete import before mutating any flags
            if (import.Unresolved.Count > 0 && Frontend.ShowMessageBox(
                $"{import.Flags.Count} flags can be imported. These {import.Unresolved.Count} shortened names could not be resolved without guessing a prefix:\n\n" +
                string.Join("\n", import.Unresolved.Take(20)) + (import.Unresolved.Count > 20 ? "\n…" : "") +
                "\n\nImport the resolved flags and leave these entries out? The original file is unchanged.",
                MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var parsed = import.Flags;
            if (parsed.Count == 0) throw new InvalidDataException("No full FastFlag names could be resolved. Add the prefixes before importing.");
            if (import.AliasConflicts > 0 && Frontend.ShowMessageBox(
                $"{import.AliasConflicts} shortened entries conflict with explicit full-name entries in this file. Keep the explicit full-name values?",
                MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            int conflicts = parsed.Keys.Count(App.FastFlags.Prop.ContainsKey);
            if (conflicts > 0 && Frontend.ShowMessageBox($"Replace {conflicts} existing flag values with this import?", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            App.FastFlags.SaveUndoSnapshot();
            App.FastFlags.suspendUndoSnapshot = true;
            try { foreach (var flag in parsed) App.FastFlags.SetValue(flag.Key, flag.Value); }
            finally { App.FastFlags.suspendUndoSnapshot = false; }
            ReloadList();
            CountLabel.Text += $" Imported {parsed.Count}; resolved {import.Resolved} shortened names; omitted {import.Unresolved.Count}.";
        }
        private void Attempt(Action action)
        {
            try { action(); }
            catch (Exception ex) { Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error); }
        }
        private void Add_Click(object sender, RoutedEventArgs e) => Attempt(() =>
        {
            var dialog = new AddFastFlagDialog { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            if (dialog.Result != MessageBoxResult.OK) return;
            if (dialog.Tabs.SelectedIndex == 1) ApplyJson(dialog.JsonTextBox.Text);
            else ApplyJson(JsonSerializer.Serialize(new Dictionary<string, string> { [dialog.FlagNameTextBox.Text.Trim()] = dialog.FlagValueComboBox.Text }));
        });
        private void Import_Click(object sender, RoutedEventArgs e) => Attempt(() =>
        {
            var picker = new OpenFileDialog { Filter = "JSON settings|*.json|Text files|*.txt", CheckFileExists = true };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            if (new FileInfo(picker.FileName).Length > 2_000_000) throw new InvalidDataException("Import is larger than 2 MB.");
            ApplyJson(File.ReadAllText(picker.FileName));
        });
        private string Json() => JsonSerializer.Serialize(App.FastFlags.Prop, new JsonSerializerOptions { WriteIndented = true });
        private void Export_Click(object sender, RoutedEventArgs e) => Attempt(() =>
        {
            FlagGrid.CommitEdit();
            var picker = new SaveFileDialog { Filter = "JSON settings|*.json", FileName = "ClientAppSettings.json" };
            if (picker.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllText(picker.FileName, Json());
        });
        private void Copy_Click(object sender, RoutedEventArgs e) => Attempt(() => { FlagGrid.CommitEdit(); Clipboard.SetText(Json()); });
        private void Cell_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not FastFlag flag || e.EditingElement is not TextBox box) return;
            if (box.Text.Length > 4096) { e.Cancel = true; Frontend.ShowMessageBox("Value is too long.", MessageBoxImage.Error); return; }
            App.FastFlags.SetValue(flag.Name, box.Text);
        }
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var selected = FlagGrid.SelectedItems.Cast<FastFlag>().ToList();
            App.FastFlags.SaveUndoSnapshot(); App.FastFlags.suspendUndoSnapshot = true;
            try { foreach (var row in selected) App.FastFlags.SetValue(row.Name, null); }
            finally { App.FastFlags.suspendUndoSnapshot = false; }
            ReloadList();
        }
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (Frontend.ShowMessageBox("Clear all custom FastFlags? You can undo this before saving.", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            App.FastFlags.SaveUndoSnapshot(); App.FastFlags.Prop.Clear(); ReloadList();
        }
        private void Undo_Click(object sender, RoutedEventArgs e) { App.FastFlags.Undo(); ReloadList(); }
        private void Redo_Click(object sender, RoutedEventArgs e) { App.FastFlags.Redo(); ReloadList(); }
    }
}
