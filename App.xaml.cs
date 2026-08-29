using System;
using System.Windows;

namespace DshControl;

public partial class App : Application
{
    public static AppConfig Cfg;
    public static int StartPage;
    public static bool UiCheck;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--smoke")
        {
            Smoke.Run();
            Shutdown();
            return;
        }
        for (var i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--page" && int.TryParse(e.Args[i + 1], out var p))
                StartPage = Math.Clamp(p, 0, 5);
        UiCheck = Array.IndexOf(e.Args, "--ui-check") >= 0;
        Cfg = AppConfig.Load();
        var theme = string.Equals(Cfg.Theme, "light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        Resources.MergedDictionaries.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative),
        });
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }
}
