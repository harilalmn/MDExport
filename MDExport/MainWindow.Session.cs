using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MDExport.Services;

namespace MDExport;

public partial class MainWindow
{
    private const double BaseEditorFontSize = 14;
    private const double ZoomStep = 0.1;
    private const double OutlineWidth = 230;

    private AppSettings _settings = new();
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _infoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _diskChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Action?[] _infoActions = new Action?[2];

    private FileSystemWatcher? _watcher;
    private string? _lastDiskText;
    private bool _focusMode;
    private WindowState _stateBeforeFocus;
    private ViewMode _viewBeforeFocus;

    private void InitializeSession()
    {
        _settings = AppSettings.Load();

        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top && IsOnScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        Editor.WordWrap = _settings.WordWrap;
        MenuWordWrap.IsChecked = _settings.WordWrap;
        MenuDarkPreview.IsChecked = _settings.DarkPreview;
        ReopenLastBox.IsChecked = _settings.ReopenLastDocument;
        Editor.FontSize = BaseEditorFontSize * _settings.Zoom;

        _recoveryTimer.Tick += RecoveryTimer_Tick;
        _infoTimer.Tick += (_, _) => HideInfo();
        _diskChangeTimer.Tick += DiskChangeTimer_Tick;

        ApplyPreviewTheme();

        // Editing is only ever entered on request, so a document never opens ready to change.
        if (_settings.ViewMode == ViewMode.Edit) _settings.ViewMode = ViewMode.Read;
        SetViewMode(_settings.ViewMode);
        SetOutlineVisible(_settings.ShowOutline);
        RebuildRecentMenu();
    }

    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft
        && top >= SystemParameters.VirtualScreenTop
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;

    private void SaveSession()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (_focusMode)
        {
            // Focus mode is temporary; what is worth keeping is the layout it replaced.
            _settings.ViewMode = _viewBeforeFocus;
        }
        else
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
        }
        _settings.Save();
    }

    // --------------------- Views ---------------------

    private bool IsCodeEditorVisible => _settings.ViewMode is ViewMode.Write or ViewMode.Split;

    private async void SwitchViewMode(ViewMode mode)
    {
        if (mode == _settings.ViewMode)
        {
            // Choosing the current view still has to leave it working.
            SyncEditViewToPage();
            if (mode == ViewMode.Edit) Preview.Focus();
            return;
        }

        // Page edits must be in the document before the page they were made on goes away.
        if (IsEditView) await FlushPageEditsAsync();
        SetViewMode(mode);
    }

    private void SetViewMode(ViewMode mode)
    {
        var leavingEdit = _settings.ViewMode == ViewMode.Edit && mode != ViewMode.Edit;
        _settings.ViewMode = mode;
        var editor = mode is ViewMode.Write or ViewMode.Split;
        var preview = mode != ViewMode.Write;

        EditorColumn.MinWidth = editor ? 120 : 0;
        EditorColumn.Width = editor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitterColumn.Width = editor && preview ? new GridLength(1) : new GridLength(0);
        PreviewColumn.MinWidth = preview ? 120 : 0;
        PreviewColumn.Width = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        // A collapsed pane must not hold on to keyboard focus or keep receiving typing.
        EditorPane.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        PreviewPane.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;

        ViewWrite.IsChecked = MenuViewWrite.IsChecked = mode == ViewMode.Write;
        ViewSplit.IsChecked = MenuViewSplit.IsChecked = mode == ViewMode.Split;
        ViewRead.IsChecked = MenuViewRead.IsChecked = mode == ViewMode.Read;
        ViewEdit.IsChecked = MenuViewEdit.IsChecked = mode == ViewMode.Edit;

        EditBar.Visibility = mode == ViewMode.Edit ? Visibility.Visible : Visibility.Collapsed;
        SyncEditViewToPage();
        if (mode == ViewMode.Edit)
        {
            Preview.Focus();
        }
        else if (leavingEdit)
        {
            RenderPreviewFresh();
            if (editor) Editor.Focus();
        }
    }

    private void EnsureEditorVisible()
    {
        if (!IsCodeEditorVisible) SwitchViewMode(ViewMode.Split);
    }

    private void SetOutlineVisible(bool visible)
    {
        _settings.ShowOutline = visible;
        MenuOutline.IsChecked = visible;
        OutlineColumn.Width = new GridLength(visible ? OutlineWidth : 0);
        OutlinePane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) RefreshOutline();
    }

    private void ToggleFocusMode()
    {
        _focusMode = !_focusMode;
        MenuFocus.IsChecked = _focusMode;
        TopBar.Visibility = BottomBar.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;

        if (_focusMode)
        {
            _stateBeforeFocus = WindowState;
            _viewBeforeFocus = _settings.ViewMode;
            OutlinePane.Visibility = Visibility.Collapsed;
            OutlineColumn.Width = new GridLength(0);
            SetViewMode(ViewMode.Write);
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            Editor.Focus();
            ShowInfo("Focus mode. Press Esc or F11 to leave.");
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFocus;
            SetViewMode(_viewBeforeFocus);
            SetOutlineVisible(_settings.ShowOutline);
        }
    }

    private void ApplyPreviewTheme()
    {
        PreviewPane.Background = _settings.DarkPreview
            ? (System.Windows.Media.Brush)FindResource("EditorBackgroundBrush")
            : System.Windows.Media.Brushes.White;
        if (_webViewReady)
        {
            Preview.DefaultBackgroundColor = _settings.DarkPreview
                ? System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E)
                : System.Drawing.Color.White;
        }
    }

    // --------------------- Outline ---------------------

    private sealed record OutlineEntry(string Title, int Line, Thickness Indent);

    private static readonly Regex Heading = new(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    private void RefreshOutline()
    {
        if (!_settings.ShowOutline) return;

        var entries = new List<OutlineEntry>();
        var inFence = false;
        foreach (var line in Editor.Document.Lines)
        {
            var text = Editor.Document.GetText(line);
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            var match = Heading.Match(text);
            if (match.Success)
            {
                var level = match.Groups[1].Length;
                entries.Add(new OutlineEntry(match.Groups[2].Value, line.LineNumber, new Thickness((level - 1) * 12, 0, 0, 0)));
            }
        }

        OutlineList.ItemsSource = entries;
        OutlineEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OutlineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is not OutlineEntry entry) return;
        OutlineList.SelectedItem = null;

        if (IsCodeEditorVisible)
        {
            Editor.ScrollToLine(entry.Line);
            Editor.TextArea.Caret.Line = entry.Line;
            Editor.TextArea.Caret.Column = 1;
            Editor.Focus();
        }
        if (_previewDocumentReady)
            _ = Preview.CoreWebView2.ExecuteScriptAsync($"window.mdScrollToLine && window.mdScrollToLine({entry.Line});");
    }

    // --------------------- Notifications ---------------------

    private void ShowInfo(string message, params (string Label, Action Run)[] actions)
    {
        InfoText.Text = message;
        SetInfoAction(InfoActionA, 0, actions);
        SetInfoAction(InfoActionB, 1, actions);
        InfoBar.Visibility = Visibility.Visible;

        // Anything with a decision in it stays until it is answered.
        _infoTimer.Stop();
        if (actions.Length == 0) _infoTimer.Start();
    }

    private void SetInfoAction(Button button, int index, (string Label, Action Run)[] actions)
    {
        var present = index < actions.Length;
        button.Visibility = present ? Visibility.Visible : Visibility.Collapsed;
        button.Content = present ? actions[index].Label : null;
        button.Tag = index;
        _infoActions[index] = present ? actions[index].Run : null;
    }

    private void HideInfo()
    {
        _infoTimer.Stop();
        InfoBar.Visibility = Visibility.Collapsed;
    }

    private void InfoAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _infoActions[(int)((Button)sender).Tag];
        HideInfo();
        action?.Invoke();
    }

    private void InfoClose_Click(object sender, RoutedEventArgs e) => HideInfo();

    // --------------------- Recent files ---------------------

    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var path in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = path.Replace("_", "__"), ToolTip = path, Tag = path };
            item.Click += RecentItem_Click;
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = _settings.RecentFiles.Count > 0;
        if (_settings.RecentFiles.Count == 0) return;

        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "_Clear Recent Files" };
        clear.Click += (_, _) =>
        {
            _settings.RecentFiles.Clear();
            RebuildRecentMenu();
        };
        RecentMenu.Items.Add(clear);
    }

    private void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        var path = (string)((MenuItem)sender).Tag;
        if (!File.Exists(path))
        {
            _settings.RecentFiles.Remove(path);
            RebuildRecentMenu();
            ShowInfo("That file no longer exists: " + path);
            return;
        }
        if (ConfirmDiscardChanges()) LoadFile(path);
    }

    private void RememberRecent(string path)
    {
        _settings.AddRecent(path);
        RebuildRecentMenu();
    }

    private void ReopenLastBox_Click(object sender, RoutedEventArgs e) =>
        _settings.ReopenLastDocument = ReopenLastBox.IsChecked == true;

    /// <summary>The document to open when the app was started without one, if any.</summary>
    private string? FindDocumentToReopen()
    {
        if (!_settings.ReopenLastDocument || _settings.RecentFiles.Count == 0) return null;

        var path = _settings.RecentFiles[0];
        if (File.Exists(path)) return path;

        ShowInfo("Could not reopen your last document; it no longer exists: " + path);
        return null;
    }

    // --------------------- Crash recovery ---------------------

    private void RecoveryTimer_Tick(object? sender, EventArgs e)
    {
        _recoveryTimer.Stop();
        if (_isModified) RecoveryStore.Write(_currentFilePath, Editor.Text);
    }

    private void OfferRecovery()
    {
        if (RecoveryStore.FindOrphan() is not { } orphan) return;

        var snapshot = orphan.Snapshot;
        var name = string.IsNullOrEmpty(snapshot.DocumentPath) ? "an untitled document" : Path.GetFileName(snapshot.DocumentPath);
        var answer = ConfirmDialog.Show(this,
            $"Recover unsaved work from {name}?",
            $"MDExport closed unexpectedly. A copy from {snapshot.SavedUtc.ToLocalTime():g} is available.",
            "Recover", "Discard");

        if (answer == ConfirmResult.Cancel) return;
        RecoveryStore.Discard(orphan.File);
        if (answer != ConfirmResult.Primary || !ConfirmDiscardChanges()) return;

        Editor.Text = snapshot.Text;
        _currentFilePath = snapshot.DocumentPath;
        _isModified = true;
        WatchCurrentFile();
        UpdateTitle();
        RecoveryStore.Write(_currentFilePath, Editor.Text);
    }

    // --------------------- External changes ---------------------

    private void WatchCurrentFile()
    {
        _watcher?.Dispose();
        _watcher = null;
        _diskChangeTimer.Stop();
        if (string.IsNullOrEmpty(_currentFilePath)) return;

        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(_currentFilePath)!, Path.GetFileName(_currentFilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += Watcher_Event;
            _watcher.Created += Watcher_Event;
            _watcher.Renamed += Watcher_Event;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _watcher = null; // Some network and virtual folders cannot be watched.
        }
    }

    private void Watcher_Event(object sender, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            // Editors write in bursts; settle before reading.
            _diskChangeTimer.Stop();
            _diskChangeTimer.Start();
        });

    private void DiskChangeTimer_Tick(object? sender, EventArgs e)
    {
        _diskChangeTimer.Stop();
        if (string.IsNullOrEmpty(_currentFilePath)) return;

        string text;
        try
        {
            text = File.ReadAllText(_currentFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (text == _lastDiskText || text == Editor.Text)
        {
            _lastDiskText = text;
            return;
        }

        _lastDiskText = text;
        if (!_isModified)
        {
            ReloadFromDisk(text);
            if (IsEditView) ResetPageFromDocument();
            ShowInfo($"Reloaded {Path.GetFileName(_currentFilePath)}; it was changed outside MDExport.");
            return;
        }

        ShowInfo($"{Path.GetFileName(_currentFilePath)} was changed outside MDExport.",
            ("Reload", () =>
            {
                ReloadFromDisk(text);
                if (IsEditView) ResetPageFromDocument();
            }),
            ("Keep Mine", () => { }));
    }

    private void ReloadFromDisk(string text)
    {
        var caret = Editor.CaretOffset;
        Editor.Text = text;
        Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);
        _isModified = false;
        UpdateTitle();
        RecoveryStore.Clear();
    }
}
