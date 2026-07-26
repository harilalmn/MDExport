using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MDExport.Services;
using Microsoft.Web.WebView2.Core;

namespace MDExport;

public partial class SyntaxReferenceWindow : Window
{
    public SyntaxReferenceWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private async Task InitAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MDExport", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder).ConfigureAwait(true);
            await Browser.EnsureCoreWebView2Async(env).ConfigureAwait(true);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;

            var markdown = MarkdownRenderer.LoadReference();
            var html = MarkdownRenderer.RenderFullPage(markdown, "Markdown Syntax Reference");
            Services.HtmlDocumentHost.NavigateToHtml(Browser.CoreWebView2, html);
            StatusText.Text = "Ready";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to load reference: " + ex.Message;
        }
    }
}
