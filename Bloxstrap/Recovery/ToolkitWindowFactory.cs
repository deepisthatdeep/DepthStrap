using System.Windows;

namespace DepthStrap.Recovery;

/// <summary>Recovery window composition for the public app.</summary>
public static class ToolkitWindowFactory
{
    public static Window Create() => new RecoveryWindow();
    public static Window CreateForInstallation(string executable) => new RecoveryWindow(executable);
}
