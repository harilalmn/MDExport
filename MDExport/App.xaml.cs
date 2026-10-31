using System;
using System.IO;
using System.Windows;
using MDExport.Services;

namespace MDExport;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? initialFile = null;
        if (e.Args.Length > 0)
        {
            var candidate = e.Args[0];
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                initialFile = Path.GetFullPath(candidate);
        }

        var forwarded = false;
        SingleInstance.RunExclusive(() =>
        {
            forwarded = initialFile != null && SingleInstance.TryForward(initialFile);
            if (!forwarded) SingleInstance.Listen(OpenForwardedFile);
        });

        if (forwarded)
        {
            Shutdown();
            return;
        }

        var window = new MainWindow(initialFile);
        window.Show();
    }

    private void OpenForwardedFile(string path) =>
        Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.OpenFromOtherInstance(path));
}
