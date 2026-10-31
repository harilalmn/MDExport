using System.Windows;

namespace MDExport;

internal enum ConfirmResult { Primary, Secondary, Cancel }

/// <summary>Themed question dialog with buttons named after what they do.</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmResult _result = ConfirmResult.Cancel;

    private ConfirmDialog() => InitializeComponent();

    internal static ConfirmResult Show(Window owner, string heading, string message, string primary, string secondary)
    {
        var dialog = new ConfirmDialog { Owner = owner, Title = "MDExport" };
        dialog.HeadingText.Text = heading;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primary;
        dialog.SecondaryButton.Content = secondary;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        _result = ConfirmResult.Primary;
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        _result = ConfirmResult.Secondary;
        Close();
    }
}
