using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MDExport.Services;

/// <summary>
/// Colours the native title bar to match the app. Keeping the native bar, rather than drawing a
/// custom one, retains Snap Layouts and standard window behaviour. Windows 10 ignores the colour
/// attributes and still honours dark mode.
/// </summary>
internal static class WindowChromeHelper
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    private const int SurfaceColorRef = 0x00232323;
    private const int TextColorRef = 0x00E6E6E6;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void RegisterForAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)));

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        Set(hwnd, DwmUseImmersiveDarkMode, 1);
        Set(hwnd, DwmCaptionColor, SurfaceColorRef);
        Set(hwnd, DwmTextColor, TextColorRef);
    }

    private static void Set(IntPtr hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
