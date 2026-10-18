using System;
using System.Windows;

namespace DshControl;

public partial class App : Application
{
    public static AppConfig Cfg;
    public static int StartPage = -1;
    public static bool UiCheck;
    public static string RenderDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, failure) =>
        {
            var directory = RenderDirectory ?? AppConfig.ConfigDir;
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "crash.log"), failure.Exception.ToString());
            if (RenderDirectory == null)
                MessageBox.Show("控制台遇到错误，诊断已写入 " + System.IO.Path.Combine(directory, "crash.log"), "dsh++");
            failure.Handled = true;
            Shutdown(1);
        };
        if (e.Args.Length > 0 && e.Args[0] == "--smoke")
        {
            Smoke.Run();
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--self-test")
        {
            Environment.ExitCode = SelfCheck.Run();
            Shutdown();
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--service")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var result = System.Threading.Tasks.Task.Run(async () => e.Args[1] switch
            {
                "start" => await service.StartAsync(),
                "stop" => await service.StopAsync(),
                "restart" => await service.RestartAsync(),
                "status" => (service.IsDshRunning(), service.IsDshRunning() ? "running" : "stopped"),
                _ => (false, "Usage: --service start|stop|restart|status"),
            }).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "service-result.txt"), result.Item2);
            Shutdown(result.Item1 ? 0 : 1);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--desktop")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var desktop = new DesktopService(config, service);
            var updates = new UpdateService(config, service, desktop);
            var result = System.Threading.Tasks.Task.Run(async () => e.Args[1] switch
            {
                "start" => await desktop.StartAsync(),
                "stop" => await desktop.StopAsync(),
                "restart" => await desktop.RestartAsync(),
                "sync-web" => await updates.SynchronizeDesktopFromWebAsync(),
                "status" => (desktop.Installed, desktop.Status().State),
                _ => (false, "Usage: --desktop start|stop|restart|sync-web|status"),
            }).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "desktop-result.txt"), result.Item2);
            Shutdown(result.Item1 ? 0 : 1);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--check-plugins")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var updates = new UpdateService(config, service);
            var profileName = e.Args.Length > 1 ? e.Args[1] : "web";
            var plugins = updates.Profiles().Where(profile => profile.Name == profileName).SelectMany(profile => profile.Plugins).ToList();
            var summary = System.Threading.Tasks.Task.Run(() => updates.CheckPluginsAsync(plugins)).GetAwaiter().GetResult();
            var report = System.Text.Json.JsonSerializer.Serialize(new
            {
                version = UpdateService.AppVersion(), profile = profileName, summary,
                plugins = plugins.Select(plugin => new { plugin.Name, plugin.OwnerName, plugin.Installed, plugin.Latest,
                    plugin.Spec, plugin.ResolvedSource, plugin.SourceIssue, plugin.TargetVersion, plugin.TargetCommit, plugin.CheckError, plugin.CheckedAt }),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "plugin-update-check.json"), CommandRunner.Redact(report));
            Shutdown(plugins.Count > 0 && summary.Failed == 0 ? 0 : 1);
            return;
        }
        for (var i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--page" && int.TryParse(e.Args[i + 1], out var p))
                StartPage = Math.Clamp(p, 0, 8);
        for (var i = 0; i < e.Args.Length - 1; i++)
            if (e.Args[i] == "--render-check") RenderDirectory = e.Args[i + 1];
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
