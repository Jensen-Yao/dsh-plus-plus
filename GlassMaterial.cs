using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DshControl;

/// <summary>Uses Windows desktop acrylic when available; opaque materials remain usable on unsupported systems.</summary>
internal static class GlassMaterial
{
    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    internal static string Apply(Window window, AppConfig config)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return "等待窗口";
        var dark = config.Theme == "dark" ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var enabled = config.LiquidGlass && !SystemParameters.HighContrast && TransparencyEnabled();
        var native = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            var material = enabled ? 3 : 1;
            native = DwmSetWindowAttribute(handle, 38, ref material, sizeof(int)) == 0 && enabled;
            var margins = new Margins { Left = native ? -1 : 0, Right = native ? -1 : 0, Top = native ? -1 : 0, Bottom = native ? -1 : 0 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
        }
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = native ? Colors.Transparent : ((SolidColorBrush)window.FindResource("BgAppBrush")).Color;
        var theme = new ResourceDictionary { Source = new Uri($"Themes/{(config.Theme == "light" ? "Light" : "Dark")}.xaml", UriKind.Relative) };
        foreach (var key in new[] { "WindowMaterialBrush", "SidebarGlassBrush", "ToolbarGlassBrush", "GlassStrokeBrush" })
            window.Resources[key] = enabled ? theme[key] : window.FindResource(key == "GlassStrokeBrush" ? "BorderBrush" : "BgAppBrush");
        if (!native) window.Resources["WindowMaterialBrush"] = window.FindResource("BgAppBrush");
        return native ? "Windows 原生亚克力" : enabled ? "半透明材质" : "实色材质";
    }

    static bool TransparencyEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
    }
}
