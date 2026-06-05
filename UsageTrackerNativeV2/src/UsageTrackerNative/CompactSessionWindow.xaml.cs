using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;
using WpfBrush = System.Windows.Media.Brush;

namespace UsageTrackerNative;

public partial class CompactSessionWindow : Window
{
    public event EventHandler? BackToMainRequested;
    public event EventHandler? ManualIdleRequested;

    public CompactSessionWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += CompactSessionWindow_MouseLeftButtonDown;
        MouseRightButtonDown += CompactSessionWindow_MouseRightButtonDown;
        UpdatePinButtonState();
    }

    public void UpdateSession(string title, string duration)
    {
        SessionTitleText.Text = string.IsNullOrWhiteSpace(title) ? "空闲" : title;
        SessionDurationText.Text = duration;
    }

    public void PositionCompactWindow()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top;
    }

    private void BackToMainButton_Click(object sender, RoutedEventArgs e)
    {
        BackToMainRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdatePinButtonState();
        if (Topmost)
        {
            Show();
            Activate();
        }
    }

    private void UpdatePinButtonState()
    {
        if (PinButton is null)
        {
            return;
        }

        PinButton.Background = (WpfBrush)WpfApplication.Current.Resources[Topmost ? "MenuSelectedBrush" : "ButtonBackgroundBrush"];
        PinButton.BorderBrush = (WpfBrush)WpfApplication.Current.Resources[Topmost ? "InputFocusBorderBrush" : "InputBorderBrush"];
        PinButton.Foreground = (WpfBrush)WpfApplication.Current.Resources[Topmost ? "AccentTextBrush" : "SecondaryTextBrush"];
        PinButton.Opacity = Topmost ? 1.0 : 0.85;
        PinButton.ToolTip = Topmost ? "已固定在最前端" : "点击固定在最前端";
    }

    private void CompactSessionWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            BackToMainRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        DragMove();
    }

    private void CompactSessionWindow_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        ManualIdleRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}

