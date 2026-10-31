using System.Windows;

namespace MDExport;

public partial class LinkDialog : Window
{
    private string? _url;

    private LinkDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.CaretIndex = UrlBox.Text.Length;
        };
    }

    /// <summary>Returns the address, or null when the user cancelled.</summary>
    internal static string? Ask(Window owner)
    {
        var dialog = new LinkDialog { Owner = owner };
        dialog.ShowDialog();
        return dialog._url;
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length > 0 && url != "https://") _url = url;
        Close();
    }
}
