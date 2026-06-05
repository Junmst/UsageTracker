using System;
using System.Windows;
using System.Windows.Input;

namespace UsageTrackerNative;

public partial class DeletionChoiceDialog : Window
{
    public DeletionChoiceDialog()
    {
        InitializeComponent();
    }

    public static bool? Show(Window owner, string title, string message)
    {
        var dialog = new DeletionChoiceDialog
        {
            Owner = owner
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        return dialog.ShowDialog();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithoutChoice();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseWithoutChoice();
    }

    private void CloseWithoutChoice()
    {
        DialogResult = null;
        Close();
    }

    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}

