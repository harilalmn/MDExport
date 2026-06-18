using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MDExport;

public partial class MainWindow
{
    // Matches for the current search, recomputed lazily when the pattern,
    // options, or document text change.
    private readonly List<Match> _searchMatches = new();
    private bool _searchDirty = true;

    private static readonly Brush FindErrorBrush = CreateFrozenBrush(0xE0, 0x6C, 0x75);

    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // --------------------- Shortcuts / panel visibility ---------------------

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (ctrl && e.Key == Key.F) { ShowFindPanel(false); e.Handled = true; }
        else if (ctrl && e.Key == Key.H) { ShowFindPanel(true); e.Handled = true; }
        else if (FindPanel.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.F3) { FindNext(shift); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideFindPanel(); e.Handled = true; }
        }
    }

    private void FindMenu_Click(object sender, RoutedEventArgs e) => ShowFindPanel(false);

    private void ReplaceMenu_Click(object sender, RoutedEventArgs e) => ShowFindPanel(true);

    private void ShowFindPanel(bool replaceMode)
    {
        // Find/Replace operates on the editor — make sure it is visible.
        if (MenuToggleEditor.IsChecked != true)
        {
            MenuToggleEditor.IsChecked = true;
            UpdatePaneLayout();
        }

        FindPanel.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = replaceMode ? Visibility.Visible : Visibility.Collapsed;

        // Seed the find box from a single-line selection, if any.
        var sel = Editor.SelectedText;
        if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n'))
            FindTextBox.Text = sel;

        InvalidateSearch();
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void HideFindPanel()
    {
        FindPanel.Visibility = Visibility.Collapsed;
        Editor.Focus();
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e) => HideFindPanel();

    // --------------------- Input handlers ---------------------

    private void FindText_Changed(object sender, RoutedEventArgs e) => InvalidateSearch();

    private void Option_Changed(object sender, RoutedEventArgs e) => InvalidateSearch();

    private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift);
            e.Handled = true;
        }
    }

    private void ReplaceTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ReplaceCurrent();
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext(false);

    private void FindPrev_Click(object sender, RoutedEventArgs e) => FindNext(true);

    private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceCurrent();

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => ReplaceAll();

    // --------------------- Search engine ---------------------

    private Regex? BuildSearchRegex(out string? error)
    {
        error = null;
        var pattern = FindTextBox.Text;
        if (string.IsNullOrEmpty(pattern)) return null;

        var options = RegexOptions.CultureInvariant;
        if (OptMatchCase.IsChecked != true) options |= RegexOptions.IgnoreCase;

        var body = OptRegex.IsChecked == true ? pattern : Regex.Escape(pattern);
        if (OptWholeWord.IsChecked == true) body = $@"\b(?:{body})\b";

        try
        {
            return new Regex(body, options);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void InvalidateSearch()
    {
        _searchDirty = true;
        if (FindPanel.Visibility == Visibility.Visible)
            UpdateSearchMatches();
    }

    private void UpdateSearchMatches()
    {
        _searchMatches.Clear();
        _searchDirty = false;

        var regex = BuildSearchRegex(out var error);
        if (error != null)
        {
            FindStatusText.Text = "Invalid pattern";
            FindStatusText.Foreground = FindErrorBrush;
            return;
        }
        if (regex == null)
        {
            FindStatusText.Text = string.Empty;
            FindStatusText.Foreground = MutedBrush();
            return;
        }

        foreach (Match m in regex.Matches(Editor.Text))
        {
            if (m.Length == 0) continue; // ignore zero-width matches
            _searchMatches.Add(m);
        }

        UpdateSearchStatusText();
    }

    private void UpdateSearchStatusText()
    {
        FindStatusText.Foreground = MutedBrush();
        if (_searchMatches.Count == 0)
        {
            FindStatusText.Text = string.IsNullOrEmpty(FindTextBox.Text) ? string.Empty : "No results";
        }
        else
        {
            FindStatusText.Text = _searchMatches.Count == 1
                ? "1 match"
                : $"{_searchMatches.Count} matches";
        }
    }

    private void FindNext(bool backwards)
    {
        if (_searchDirty) UpdateSearchMatches();
        if (_searchMatches.Count == 0) { UpdateSearchStatusText(); return; }

        var selStart = Editor.SelectionStart;
        var selEnd = selStart + Editor.SelectionLength;

        var index = -1;
        if (!backwards)
        {
            for (var i = 0; i < _searchMatches.Count; i++)
            {
                if (_searchMatches[i].Index >= selEnd) { index = i; break; }
            }
            if (index < 0) index = 0; // wrap to first
        }
        else
        {
            for (var i = _searchMatches.Count - 1; i >= 0; i--)
            {
                if (_searchMatches[i].Index + _searchMatches[i].Length <= selStart) { index = i; break; }
            }
            if (index < 0) index = _searchMatches.Count - 1; // wrap to last
        }

        SelectMatch(_searchMatches[index]);
        FindStatusText.Foreground = MutedBrush();
        FindStatusText.Text = $"{index + 1} of {_searchMatches.Count}";
    }

    private void SelectMatch(Match m)
    {
        Editor.Select(m.Index, m.Length);
        var line = Editor.Document.GetLineByOffset(m.Index).LineNumber;
        Editor.ScrollToLine(line);
        Editor.TextArea.Caret.BringCaretToView();
    }

    private void ReplaceCurrent()
    {
        var regex = BuildSearchRegex(out var error);
        if (regex == null || error != null) { if (error != null) UpdateSearchMatches(); return; }

        // Replace only when the current selection is exactly a match at its position;
        // otherwise just advance to the next match.
        if (Editor.SelectionLength > 0)
        {
            var m = regex.Match(Editor.Text, Editor.SelectionStart);
            if (m.Success && m.Index == Editor.SelectionStart && m.Length == Editor.SelectionLength)
            {
                string replacement;
                try
                {
                    replacement = OptRegex.IsChecked == true ? m.Result(ReplaceTextBox.Text) : ReplaceTextBox.Text;
                }
                catch (Exception)
                {
                    FindStatusText.Text = "Invalid replacement";
                    FindStatusText.Foreground = FindErrorBrush;
                    return;
                }

                Editor.Document.Replace(m.Index, m.Length, replacement);
                Editor.CaretOffset = m.Index + replacement.Length;
                InvalidateSearch();
            }
        }

        FindNext(false);
    }

    private void ReplaceAll()
    {
        var regex = BuildSearchRegex(out var error);
        if (regex == null || error != null) { if (error != null) UpdateSearchMatches(); return; }

        var text = Editor.Text;
        var count = 0;
        string result;
        try
        {
            result = OptRegex.IsChecked == true
                ? regex.Replace(text, m => { count++; return m.Result(ReplaceTextBox.Text); })
                : regex.Replace(text, m => { count++; return ReplaceTextBox.Text; });
        }
        catch (Exception)
        {
            FindStatusText.Text = "Invalid replacement";
            FindStatusText.Foreground = FindErrorBrush;
            return;
        }

        if (count == 0) { UpdateSearchStatusText(); return; }

        var caret = Editor.CaretOffset;
        // Single document operation keeps this as one undo unit.
        Editor.Document.Replace(0, Editor.Document.TextLength, result);
        Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);

        InvalidateSearch();
        FindStatusText.Foreground = MutedBrush();
        FindStatusText.Text = count == 1 ? "Replaced 1 match" : $"Replaced {count} matches";
    }

    private Brush MutedBrush() => (Brush)FindResource("TextMutedBrush");
}
