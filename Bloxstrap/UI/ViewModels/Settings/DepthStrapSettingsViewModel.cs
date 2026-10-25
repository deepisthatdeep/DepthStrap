using System.Windows.Controls;

namespace Bloxstrap.UI.ViewModels.Settings
{
    /// <summary>
    /// Composes the app-level "DepthStrap" page:
    /// channel/updates/static directory (ChannelViewModel),
    /// file cleaner (BehaviourViewModel) and bootstrapper dialog customization (AppearanceViewModel).
    /// Each section binds to its sub-view-model through a nested DataContext.
    /// </summary>
    public class DepthStrapSettingsViewModel : NotifyPropertyChangedViewModel
    {
        public ChannelViewModel Channel { get; } = new();

        public BehaviourViewModel Cleaner { get; } = new();

        public AppearanceViewModel Dialog { get; }

        public string AppUpdateStatus => AppUpdater.UpdateStatus(App.Version, App.State.Prop.DeferredAppUpdate);

        public DepthStrapSettingsViewModel(Page page)
        {
            // AppearanceViewModel needs the hosting page for live theme/preview application.
            Dialog = new AppearanceViewModel(page);
        }
    }
}
