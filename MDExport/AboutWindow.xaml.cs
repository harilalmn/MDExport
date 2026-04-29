using System;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using MDExport.Services;

namespace MDExport;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {UpdateChecker.GetCurrentVersion()}";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        SetStatus("Checking for updates…", muted: true);

        try
        {
            var info = await UpdateChecker.FetchLatestReleaseAsync();
            var current = UpdateChecker.GetCurrentVersion();

            if (info.IsNewerThan(current))
            {
                SetStatus(
                    $"A new version is available: {info.TagName} (you have {current}).",
                    muted: false);

                var result = MessageBox.Show(this,
                    $"MDExport {info.TagName} is available.\n\nYou are running {current}.\n\nOpen the release page to download?",
                    "Update available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes && !string.IsNullOrEmpty(info.HtmlUrl))
                {
                    UpdateChecker.OpenReleasePage(info.HtmlUrl);
                }
            }
            else
            {
                SetStatus($"You are up to date (version {current}).", muted: true);
            }
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Could not reach GitHub: {ex.Message}", muted: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Update check failed: {ex.Message}", muted: true);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void SetStatus(string text, bool muted)
    {
        StatusText.Text = text;
        StatusText.Foreground = muted
            ? (Brush)FindResource("TextMutedBrush")
            : (Brush)FindResource("TextBrush");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
