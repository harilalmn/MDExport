using System;
using System.IO;
using System.Windows;

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

        var window = new MainWindow(initialFile);
        window.Show();
    }
}
