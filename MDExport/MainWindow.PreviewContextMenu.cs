using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MDExport.Services;
using Microsoft.Web.WebView2.Core;

namespace MDExport;

public partial class MainWindow
{
    // Native WebView2 menu commands we surface under our own themed menu. Running the native
    // command rather than reimplementing it keeps rich-text copy, and copying an image whose
    // data: URI is too long for ContextMenuTarget.SourceUri to carry.
    private const string NativeCopy = "copy";
    private const string NativeCopyImage = "copyImage";

    private void Preview_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        // The native menu's selected command is only read when the deferral completes, so the
        // event has to stay open for as long as our menu is showing.
        var deferral = e.GetDeferral();
        e.Handled = true;

        var menu = new ContextMenu { PlacementTarget = Preview };

        var copy = BuildNativeMenuItem(e, "_Copy", NativeCopy);
        copy.IsEnabled = e.ContextMenuTarget.HasSelection && copy.IsEnabled;
        menu.Items.Add(copy);

        var selectAll = new MenuItem { Header = "Select _All" };
        selectAll.Click += PreviewSelectAll_Click;
        menu.Items.Add(selectAll);

        if (e.ContextMenuTarget.HasLinkUri && IsExternalLink(e.ContextMenuTarget.LinkUri))
        {
            var url = e.ContextMenuTarget.LinkUri;
            menu.Items.Add(new Separator());

            var open = new MenuItem { Header = "_Open Link in Browser" };
            open.Click += (_, _) => OpenExternalLink(url);
            menu.Items.Add(open);

            var copyLink = new MenuItem { Header = "Copy _Link Address" };
            copyLink.Click += (_, _) => CopyTextToClipboard(url);
            menu.Items.Add(copyLink);
        }

        if (e.ContextMenuTarget.Kind == CoreWebView2ContextMenuTargetKind.Image)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildNativeMenuItem(e, "Copy _Image", NativeCopyImage));
        }

        // Closed can fire ahead of the item's Click, which would complete the deferral before
        // the selected command is set. Queueing lets the Click land first.
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(deferral.Complete, DispatcherPriority.Background);
        menu.IsOpen = true;
    }

    private static MenuItem BuildNativeMenuItem(
        CoreWebView2ContextMenuRequestedEventArgs e, string header, string nativeName)
    {
        var native = e.MenuItems.FirstOrDefault(i => i.Name == nativeName);
        var item = new MenuItem { Header = header, IsEnabled = native is { IsEnabled: true } };
        if (native != null)
            item.Click += (_, _) => e.SelectedCommandId = native.CommandId;
        return item;
    }

    // Done by script because Chromium only offers a native select-all on some targets.
    private void PreviewSelectAll_Click(object sender, RoutedEventArgs e)
    {
        Preview.Focus();
        _ = Preview.CoreWebView2?.ExecuteScriptAsync("document.execCommand('selectAll');");
    }

    // Links relative to the document resolve against the synthetic preview origin, so they
    // have no meaningful address to open or copy.
    private static bool IsExternalLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && !HtmlDocumentHost.IsHostedUri(uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    private void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, "Failed to open link:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyTextToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            // Another process can hold the clipboard open.
            MessageBox.Show(this, "Failed to copy to the clipboard:\n" + ex.Message,
                "MDExport", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
