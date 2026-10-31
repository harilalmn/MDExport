using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace MDExport;

public sealed record PaletteCommand(string Title, string? Shortcut, Action Run);

/// <summary>
/// Searchable list of every command. A separate window because WPF cannot draw over the
/// WebView, which would otherwise hide half of a popup panel.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<PaletteCommand> _commands;
    private bool _closing;

    private CommandPaletteWindow(IReadOnlyList<PaletteCommand> commands)
    {
        InitializeComponent();
        _commands = commands;
        ApplyFilter();
        Closing += (_, _) => _closing = true;
        Loaded += (_, _) => QueryBox.Focus();
    }

    public PaletteCommand? Chosen { get; private set; }

    public static void Open(Window owner, IReadOnlyList<PaletteCommand> commands)
    {
        var palette = new CommandPaletteWindow(commands) { Owner = owner };
        palette.Left = owner.Left + (owner.ActualWidth - palette.Width) / 2;
        palette.Top = owner.Top + 72;
        palette.WindowStartupLocation = WindowStartupLocation.Manual;
        palette.ShowDialog();

        // Run after the palette has gone, so a command that opens a dialog owns the focus.
        palette.Chosen?.Run();
    }

    private void ApplyFilter()
    {
        var terms = QueryBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ResultList.ItemsSource = _commands
            .Where(c => terms.All(t => c.Title.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        ResultList.SelectedIndex = 0;
    }

    private void Choose()
    {
        Chosen = ResultList.SelectedItem as PaletteCommand;
        if (Chosen != null) Close();
    }

    private void QueryBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (ResultList != null) ApplyFilter();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Enter:
                Choose();
                break;
            case Key.Down:
                ResultList.SelectedIndex = Math.Min(ResultList.SelectedIndex + 1, ResultList.Items.Count - 1);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                break;
            case Key.Up:
                ResultList.SelectedIndex = Math.Max(ResultList.SelectedIndex - 1, 0);
                ResultList.ScrollIntoView(ResultList.SelectedItem);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ResultList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Choose();

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_closing) Close();
    }
}
