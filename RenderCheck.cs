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
        window.WindowState = WindowState.Normal;
        foreach (var theme in new[] { "light", "dark" })
        {
            window.ChangeTheme(theme);
            foreach (var size in new[] { (Width: 1220, Height: 820), (Width: 960, Height: 700) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                for (var page = 0; page < 9; page++)
                {
                    window.SelectPage(page);
                    await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                    var visual = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    var name = $"{theme}-{size.Width}-page{page}.png";
                    using (var file = File.Create(Path.Combine(directory, name))) encoder.Save(file);
                    var clipped = new List<string>();
                    foreach (var button in Descendants(visual).OfType<Button>().Where(button => button.IsVisible))
                    {
                        if (button.Content is string text && button.ActualWidth > 0)
                        {
                            var measure = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight, new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
                                button.FontSize, Brushes.Black, 1);
                            if (measure.Width > button.ActualWidth - button.Padding.Left - button.Padding.Right + 2)
                                clipped.Add(text);
                        }
                    }
                    reports.Add(new { name, width = visual.ActualWidth, height = visual.ActualHeight, clippedButtons = clipped });
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
                var visual = (FrameworkElement)preview.Content;
                var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var name = $"{theme}-{width}-preview.png";
                using (var file = File.Create(Path.Combine(directory, name))) encoder.Save(file);
                reports.Add(new { name, width = visual.ActualWidth, height = visual.ActualHeight, clippedButtons = new List<string>() });
                preview.Close();
            }
        }
        File.WriteAllText(Path.Combine(directory, "render-report.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
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
