using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MDExport.Services;

namespace MDExport;

public partial class PdfOptionsWindow : Window
{
    private PdfOptionsWindow(PdfOptions options)
    {
        InitializeComponent();
        PageSizeBox.SelectedIndex = options.PageSize == "A4" ? 0 : 1;
        OrientationBox.SelectedIndex = options.Landscape ? 1 : 0;
        MarginBox.SelectedItem = MarginBox.Items.Cast<ComboBoxItem>()
            .OrderBy(i => System.Math.Abs(MarginOf(i) - options.MarginInches))
            .First();
        PageNumbersBox.IsChecked = options.PageNumbers;
    }

    private PdfOptions? Result { get; set; }

    /// <summary>Returns the chosen options, or null when the user cancelled.</summary>
    internal static PdfOptions? Ask(Window owner, PdfOptions current)
    {
        var dialog = new PdfOptionsWindow(current) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Result;
    }

    private static double MarginOf(ComboBoxItem item) =>
        double.Parse((string)item.Tag, CultureInfo.InvariantCulture);

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        Result = new PdfOptions(
            PageSizeBox.SelectedIndex == 0 ? "A4" : "Letter",
            OrientationBox.SelectedIndex == 1,
            MarginOf((ComboBoxItem)MarginBox.SelectedItem),
            PageNumbersBox.IsChecked == true);
        Close();
    }
}
