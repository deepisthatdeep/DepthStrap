using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Bloxstrap.UI.ViewModels.Settings;

namespace Bloxstrap.UI.Elements.Settings.Pages
{
    public partial class RegionSelectorPage
    {
        private bool _pageBindingsAttached;
        private readonly RoutedCommand _focusSearchCommand = new();
        private readonly RoutedCommand _focusRegionCommand = new();
        private readonly List<CommandBinding> _windowCommands = new();
        private readonly List<KeyBinding> _windowKeys = new();
        private Window? _boundWindow;
        private int _loadVersion;
        internal Task RegionsLoadTask { get; private set; } = Task.CompletedTask;

        public RegionSelectorPage() : this(new RegionSelectorViewModel()) { }
        internal RegionSelectorPage(RegionSelectorViewModel viewModel)
        {
            DataContext = viewModel;
            InitializeComponent();
            Loaded += RegionSelectorPage_Loaded;
            Unloaded += (_, _) =>
            {
                _loadVersion++;
                viewModel.StopPendingRequests();
                DetachWindowBindings();
            };
            App.FrostRPC?.SetPage("Region Selector");
        }

        private void RegionSelectorPage_Loaded(object? sender, RoutedEventArgs e)
        {
            if (!_pageBindingsAttached)
            {
                _pageBindingsAttached = true;
                CommandBindings.Add(new CommandBinding(_focusSearchCommand, (_, __) => FocusSearch()));
                InputBindings.Add(new KeyBinding(_focusSearchCommand, Key.E, ModifierKeys.Control));
                CommandBindings.Add(new CommandBinding(_focusRegionCommand, (_, __) => FocusRegion()));
                InputBindings.Add(new KeyBinding(_focusRegionCommand, Key.K, ModifierKeys.Control));
                SearchComboBox.PreviewKeyDown += SearchComboBox_PreviewKeyDown;
                RegionComboBox.PreviewKeyDown += ComboBoxOpenOnArrow_PreviewKeyDown;
                SearchComboBox.Loaded += (_, __) => AttachEditableSearchHandler();
            }
            AttachEditableSearchHandler();
            AttachBindingsToWindow();
            RegionsLoadTask = LoadRegionsAsync(++_loadVersion, RegionsLoadTask);
        }

        private async Task LoadRegionsAsync(int version, Task previous)
        {
            await previous;
            if (version != _loadVersion) return;
            var viewModel = (RegionSelectorViewModel)DataContext;
            if (viewModel.Regions.Count <= 1) await viewModel.InitializeRegionsAsync();
        }

        private void AttachEditableSearchHandler()
        {
            if (SearchComboBox.Template?.FindName("PART_EditableTextBox", SearchComboBox) is TextBox textBox)
            {
                textBox.PreviewKeyDown -= SearchEditable_PreviewKeyDown;
                textBox.PreviewKeyDown += SearchEditable_PreviewKeyDown;
            }
        }

        private void AttachBindingsToWindow()
        {
            var window = Window.GetWindow(this);
            if (ReferenceEquals(window, _boundWindow)) return;
            DetachWindowBindings();
            if (window is null) return;
            _boundWindow = window;
            _windowCommands.Add(new CommandBinding(_focusSearchCommand, (_, __) => FocusSearch()));
            _windowCommands.Add(new CommandBinding(_focusRegionCommand, (_, __) => FocusRegion()));
            _windowKeys.Add(new KeyBinding(_focusSearchCommand, Key.E, ModifierKeys.Control));
            _windowKeys.Add(new KeyBinding(_focusRegionCommand, Key.K, ModifierKeys.Control));
            foreach (var binding in _windowCommands) window.CommandBindings.Add(binding);
            foreach (var binding in _windowKeys) window.InputBindings.Add(binding);
        }

        private void DetachWindowBindings()
        {
            if (_boundWindow is not null)
            {
                foreach (var binding in _windowCommands) _boundWindow.CommandBindings.Remove(binding);
                foreach (var binding in _windowKeys) _boundWindow.InputBindings.Remove(binding);
            }
            _windowCommands.Clear(); _windowKeys.Clear(); _boundWindow = null;
        }

        private void FocusSearch()
        {
            if (SearchComboBox.Template?.FindName("PART_EditableTextBox", SearchComboBox) is TextBox tb)
            {
                tb.Focus();
                tb.Select(tb.Text?.Length ?? 0, 0);
            }
            else
            {
                SearchComboBox.Focus();
            }
        }

        private void FocusRegion()
        {
            RegionComboBox.Focus();
        }

        private void SearchComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (!SearchComboBox.IsDropDownOpen)
                {
                    SearchComboBox.IsDropDownOpen = true;
                    if (SearchComboBox.Items.Count > 0)
                    {
                        SearchComboBox.SelectedIndex = e.Key == Key.Down ? 0 : SearchComboBox.Items.Count - 1;
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() =>
                        {
                            if (SearchComboBox.ItemContainerGenerator.ContainerFromIndex(SearchComboBox.SelectedIndex) is ComboBoxItem item)
                                item.Focus();
                        }));
                    }
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                var vm = DataContext as RegionSelectorViewModel;
                if (vm == null)
                {
                    e.Handled = true;
                    return;
                }

                // If dropdown is open with a selected item, accept it first.
                if (SearchComboBox.IsDropDownOpen && SearchComboBox.SelectedItem != null)
                {
                    SearchComboBox.IsDropDownOpen = false;
                    e.Handled = true;
                    return;
                }

                // If we have not performed an initial search yet, invoke the Search button (SearchCommand).
                // If a search has already been performed, invoke Load More (LoadMoreCommand).
                if (!vm.HasSearched)
                {
                    if (vm.SearchCommand?.CanExecute(null) ?? false)
                        vm.SearchCommand.Execute(null);
                }
                else
                {
                    if (vm.LoadMoreCommand?.CanExecute(null) ?? false)
                        vm.LoadMoreCommand.Execute(null);
                }

                e.Handled = true;
            }
        }

        private void SearchEditable_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (!SearchComboBox.IsDropDownOpen)
                {
                    SearchComboBox.IsDropDownOpen = true;
                    if (SearchComboBox.Items.Count > 0)
                    {
                        SearchComboBox.SelectedIndex = e.Key == Key.Down ? 0 : SearchComboBox.Items.Count - 1;
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() =>
                        {
                            if (SearchComboBox.ItemContainerGenerator.ContainerFromIndex(SearchComboBox.SelectedIndex) is ComboBoxItem item)
                                item.Focus();
                        }));
                    }
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                var vm = DataContext as RegionSelectorViewModel;
                if (vm == null)
                {
                    e.Handled = true;
                    return;
                }

                // Accept highlighted dropdown item first
                if (SearchComboBox.IsDropDownOpen && SearchComboBox.SelectedItem != null)
                {
                    SearchComboBox.IsDropDownOpen = false;
                    e.Handled = true;
                    return;
                }

                if (!vm.HasSearched)
                {
                    if (vm.SearchCommand?.CanExecute(null) ?? false)
                        vm.SearchCommand.Execute(null);
                }
                else
                {
                    if (vm.LoadMoreCommand?.CanExecute(null) ?? false)
                        vm.LoadMoreCommand.Execute(null);
                }

                e.Handled = true;
            }
        }

        private void ComboBoxOpenOnArrow_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (sender is ComboBox cb)
                {
                    if (!cb.IsDropDownOpen)
                    {
                        cb.IsDropDownOpen = true;
                        if (cb.Items.Count > 0)
                        {
                            cb.SelectedIndex = e.Key == Key.Down ? 0 : cb.Items.Count - 1;
                            Dispatcher.BeginInvoke(DispatcherPriority.Input, new System.Action(() =>
                            {
                                if (cb.ItemContainerGenerator.ContainerFromIndex(cb.SelectedIndex) is ComboBoxItem item)
                                    item.Focus();
                            }));
                        }
                    }
                }

                e.Handled = true;
            }
        }
    }
}
