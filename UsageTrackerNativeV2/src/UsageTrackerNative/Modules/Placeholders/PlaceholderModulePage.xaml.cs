namespace UsageTrackerNative.Modules.Placeholders;

public partial class PlaceholderModulePage : System.Windows.Controls.UserControl
{
    public PlaceholderModulePage(string title, string description)
    {
        InitializeComponent();
        TitleText.Text = title;
        DescriptionText.Text = description;
    }
}
