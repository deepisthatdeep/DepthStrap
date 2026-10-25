using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Bloxstrap.UI.Elements.Controls
{
    /// <summary>
    /// Interaction logic for OptionControl.xaml
    /// </summary
    [ContentProperty(nameof(InnerContent))]
    public partial class OptionControl : UserControl
    {
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(OptionControl));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(OptionControl));

        public static readonly DependencyProperty HelpLinkProperty =
            DependencyProperty.Register(nameof(HelpLink), typeof(string), typeof(OptionControl));

        public static readonly DependencyProperty InnerContentProperty =
            DependencyProperty.Register(nameof(InnerContent), typeof(object), typeof(OptionControl));

        public string Header
        {
            get { return (string)GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        public string Description
        {
            get { return (string)GetValue(DescriptionProperty); }
            set { SetValue(DescriptionProperty, value); }
        }

        public string HelpLink
        {
            get { return (string)GetValue(HelpLinkProperty); }
            set { SetValue(HelpLinkProperty, value); }
        }

        public object InnerContent
        {
            get { return GetValue(InnerContentProperty); }
            set { SetValue(InnerContentProperty, value); }
        }

        public OptionControl()
        {
            InitializeComponent();
        }

        private void Control_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool stacked = e.NewSize.Width < 640;
            Grid.SetColumnSpan(DescriptionPanel, stacked ? 2 : 1);
            Grid.SetRow(Editor, stacked ? 1 : 0);
            Grid.SetColumn(Editor, stacked ? 0 : 1);
            Grid.SetColumnSpan(Editor, stacked ? 2 : 1);
            Editor.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            Editor.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(20, 0, 0, 0);
        }
    }
}
