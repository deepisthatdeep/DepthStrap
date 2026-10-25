using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Bloxstrap.UI.Elements.Controls;

internal static class OptionLayoutChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var editor = new TextBox { Width = 280, Text = "Retain unsaved input" };
        var option = new OptionControl
        {
            Header = "A long setting title that must remain readable in a narrow settings panel",
            Description = "This description wraps above the editor at narrow widths and remains beside it at wide widths.",
            InnerContent = editor
        };
        foreach (double width in new[] { 900d, 460d, 900d })
        {
            option.Width = width;
            option.Measure(new Size(width, double.PositiveInfinity));
            option.Arrange(new Rect(0, 0, width, option.DesiredSize.Height));
            option.UpdateLayout();
            option.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            option.Measure(new Size(width, double.PositiveInfinity));
            option.Arrange(new Rect(0, 0, width, option.DesiredSize.Height));
            option.UpdateLayout();
            var label = option.DescriptionPanel.TransformToAncestor(option).TransformBounds(new Rect(option.DescriptionPanel.RenderSize));
            var input = editor.TransformToAncestor(option).TransformBounds(new Rect(editor.RenderSize));
            check(width < 640 ? input.Top >= label.Bottom : input.Left >= label.Right,
                "Setting description and editor do not overlap at width " + width);
            check(input.Right <= width && label.Right <= width,
                "Setting title and editor fit the available width " + width);
            check(editor.Text == "Retain unsaved input" && ReferenceEquals(option.InnerContent, editor),
                "Responsive layout preserves the editor and unsaved input");
        }
    }
}
