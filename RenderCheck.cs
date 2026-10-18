using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DshControl;

public static class RenderCheck
{
    public static async Task CaptureAsync(MainWindow window, string directory)
    {
        Directory.CreateDirectory(directory);
        var reports = new List<object>();
        window.UseSessionRenderFixture();
        window.WindowState = WindowState.Normal;
        foreach (var theme in new[] { "light", "dark" })
        {
            window.ChangeTheme(theme);
            foreach (var size in new[] { (Width: 1220, Height: 820), (Width: 920, Height: 640) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                for (var page = 0; page < 10; page++)
                {
                    window.SelectPage(page);
                    await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                    var name = $"{theme}-{size.Width}-page{page}.png";
                    reports.Add(Capture((FrameworkElement)window.Content, directory, name));
                }
                foreach (var variant in new[] { "health-issues", "logs", "desktop-differences", "plugins-empty", "session-search", "sessions-empty" })
                {
                    window.SelectPage(variant == "desktop-differences" ? 1 : variant == "plugins-empty" ? 2 : variant.StartsWith("session") ? 9 : 8);
                    if (variant == "health-issues") ((CheckBox)window.FindName("HealthOnlyIssues")).IsChecked = true;
                    if (variant == "logs") ((TabControl)window.FindName("DiagnosticTabs")).SelectedIndex = 1;
                    if (variant == "desktop-differences") ((CheckBox)window.FindName("ComparisonOnlyChanges")).IsChecked = true;
                    if (variant == "plugins-empty") ((TextBox)window.FindName("PluginSearch")).Text = "dshpp-nonexistent-render-fixture";
                    if (variant == "session-search") ((TextBox)window.FindName("SessionSearch")).Text = "浏览器";
                    if (variant == "sessions-empty") ((TextBox)window.FindName("SessionSearch")).Text = "no-matching-session";
                    await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                    var name = $"{theme}-{size.Width}-{variant}.png";
                    reports.Add(Capture((FrameworkElement)window.Content, directory, name));
                    ((CheckBox)window.FindName("HealthOnlyIssues")).IsChecked = false;
                    ((TabControl)window.FindName("DiagnosticTabs")).SelectedIndex = 0;
                    ((CheckBox)window.FindName("ComparisonOnlyChanges")).IsChecked = false;
                    ((TextBox)window.FindName("PluginSearch")).Text = "";
                    ((TextBox)window.FindName("SessionSearch")).Text = "";
                }
            }
            foreach (var width in new[] { 700, 520 })
            {
                var preview = new UpdatePreviewWindow("更新所选插件", new[]
                {
                    new PlannedChange("web / dsh-model-palette", "0.16.3 → github:Jensen-Yao/dsh-model-palette#v0.17.0"),
                    new PlannedChange("web / @yuxianglin/example-with-a-long-name", "1.0.0 → ^1.1.0"),
                }, true, true) { Owner = window, Width = width };
                preview.Show();
                await preview.Dispatcher.InvokeAsync(() => preview.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                var name = $"{theme}-{width}-preview.png";
                reports.Add(Capture((FrameworkElement)preview.Content, directory, name));
                preview.Close();
            }
        }
        var brand = (RainbowBrand)window.FindName("BrandMark");
        brand.SetMotion(true);
        await Task.Delay(100);
        var firstPixels = BrandPixels(brand, directory, "brand-frame-a.png");
        await Task.Delay(800);
        var secondPixels = BrandPixels(brand, directory, "brand-frame-b.png");
        var motionExpected = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        var motionChanged = !firstPixels.AsSpan().SequenceEqual(secondPixels);
        ((CheckBox)window.FindName("HealthOnlyIssues")).IsChecked = false;
        ((TextBox)window.FindName("SessionSearch")).Text = "浏览器";
        var searchCount = ((DataGrid)window.FindName("SessionGrid")).Items.Count;
        ((TextBox)window.FindName("SessionSearch")).Text = "";
        ((ComboBox)window.FindName("SessionWorkspaceFilter")).SelectedIndex = 1;
        var workspaceCount = ((DataGrid)window.FindName("SessionGrid")).Items.Count;
        ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("TglGlass")).IsChecked = false;
        var solid = ((TextBlock)window.FindName("MaterialStateText")).Text;
        ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("TglGlass")).IsChecked = true;
        var material = ((TextBlock)window.FindName("MaterialStateText")).Text;
        File.WriteAllText(Path.Combine(directory, "interaction-report.json"), JsonSerializer.Serialize(new
        {
            sessionSearch = searchCount == 1, workspaceFilter = workspaceCount == 2,
            motionExpected, motionChanged, material, solid, nativeAcrylic = material == "Windows 原生亚克力",
        }));
        if (searchCount != 1 || workspaceCount != 2 || (motionExpected && !motionChanged) || solid != "实色材质")
            throw new InvalidOperationException("会话筛选、徽标动效或玻璃材质切换检查失败。");
        File.WriteAllText(Path.Combine(directory, "render-report.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
    }

    static object Capture(FrameworkElement visual, string directory, string name)
    {
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var draw = background.RenderOpen()) draw.DrawRectangle((Brush)visual.FindResource("BgAppBrush"), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, name))) encoder.Save(file);
        var clipped = new List<string>();
        foreach (var button in Descendants(visual).OfType<Button>().Where(button => button.IsVisible && button.ActualWidth > 0))
        {
            var available = button.ActualWidth - button.Padding.Left - button.Padding.Right;
            if (button.Content is string text)
            {
                var measure = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
                    button.FontSize, Brushes.Black, 1);
                if (measure.Width > available + 2) clipped.Add(text);
            }
            else if (button.Content is FrameworkElement element && element.DesiredSize.Width > available + 2)
                clipped.Add(button.Name.Length > 0 ? button.Name : button.ToolTip?.ToString() ?? "unnamed button");
        }
        return new { name, width = visual.ActualWidth, height = visual.ActualHeight, clippedButtons = clipped };
    }

    static byte[] BrandPixels(RainbowBrand brand, string directory, string name)
    {
        var bitmap = new RenderTargetBitmap((int)brand.ActualWidth, (int)brand.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(brand);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, name))) encoder.Save(file);
        return pixels;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
