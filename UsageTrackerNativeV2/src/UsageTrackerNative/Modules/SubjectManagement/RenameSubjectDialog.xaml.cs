using System.Windows;
using System.Windows.Input;

namespace UsageTrackerNative.Modules.SubjectManagement;

public partial class RenameSubjectDialog : Window
{
    private RenameSubjectDialog(string currentName)
    {
        InitializeComponent();
        NameInput.Text = currentName;
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    public string? ResultText { get; private set; }

    public static string? Show(Window? owner, string currentName)
    {
        var dialog = new RenameSubjectDialog(currentName)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true ? dialog.ResultText : null;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ResultText = NameInput.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void NameInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            e.Handled = true;
            ConfirmButton_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelButton_Click(sender, e);
        }
    }
}
