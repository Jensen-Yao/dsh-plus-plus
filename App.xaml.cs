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
            var syncLogPath = System.IO.Path.Combine(AppConfig.ConfigDir, "desktop-sync-log.txt");
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.Delete(syncLogPath);
            var updates = new UpdateService(config, service, desktop,
                message => System.IO.File.AppendAllText(syncLogPath, message + Environment.NewLine));
            var result = System.Threading.Tasks.Task.Run(async () => e.Args[1] switch
            {
                "start" => await updates.RepairDesktopDependenciesIfMissingAsync() is { ok: false } repair
                    ? repair
                    : await desktop.StartAsync(),
                "stop" => await desktop.StopAsync(),
                "restart" => await desktop.RestartAsync(),
                "sync-web" => await updates.SynchronizeDesktopFromWebAsync(),
                "check-update" => await desktop.CheckUpdateAsync() is { } checkedUpdate
                    ? (checkedUpdate.Installed, checkedUpdate.Error.Length > 0 ? checkedUpdate.Error : checkedUpdate.UpdateAvailable ? checkedUpdate.CurrentVersion + " -> " + checkedUpdate.Latest.Version : checkedUpdate.CurrentVersion)
                    : (false, "未检查"),
                "update" => await desktop.UpdateAsync((await desktop.CheckUpdateAsync()).Latest),
                "status" => desktop.Status() is { } desktopStatus ? (desktopStatus.Installed, desktopStatus.Version + " · " + desktopStatus.State) : (false, "未检查"),
                _ => (false, "Usage: --desktop start|stop|restart|sync-web|check-update|update|status"),
            }).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "desktop-result.txt"), result.Item2);
            Shutdown(result.Item1 ? 0 : 1);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--session-history")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var result = System.Threading.Tasks.Task.Run(() => SessionHistory.ReadAsync(service.HomeBase, config.SessionHistoryLimit)).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "session-history-summary.json"),
                System.Text.Json.JsonSerializer.Serialize(new { result.Total, Displayed = result.Items.Count, Cached = result.Items.Count(item => item.Cached), result.Unreadable }));
            Shutdown();
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--health")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var report = System.Threading.Tasks.Task.Run(() => HealthChecks.RunAsync(config, service, new UpdateService(config, service))).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "health-report.txt"), report.Export());
            Shutdown(report.Findings.Any(item => item.Level == "错误") ? 1 : 0);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--plugin-latest")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var updates = new UpdateService(config, service);
            var profileName = e.Args.Length > 2 ? e.Args[2] : "web";
            var result = System.Threading.Tasks.Task.Run(async () =>
            {
                var profile = updates.Profiles().FirstOrDefault(item => item.Name == profileName);
                if (profile == null) return (ok: false, msg: "profile 不存在：" + profileName);
                var plugin = profile.Plugins.FirstOrDefault(item => item.Name == e.Args[1] && !item.IsIndirect);
                if (plugin == null) return (ok: false, msg: "插件不存在：" + e.Args[1]);
                var check = await updates.CheckPluginsAsync(new[] { plugin });
                if (plugin.CheckError.Length > 0) return (ok: false, msg: "远端检查失败：" + plugin.CheckError);
                if (string.IsNullOrWhiteSpace(plugin.Latest) || !UpdateService.IsExactVersion(plugin.Latest.TrimStart('v', 'V')))
                    return (ok: false, msg: "远端未返回有效最新版本：" + plugin.Latest);
                return await updates.UpdatePluginsAsync(new[] { plugin }, explicitSpec: plugin.Latest);
            }).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "plugin-update-result.txt"),
                result.ok ? "完成：" + result.msg : "失败：" + result.msg);
            Shutdown(result.ok ? 0 : 1);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--recover")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var updates = new UpdateService(config, service);
            var result = System.Threading.Tasks.Task.Run(async () =>
            {
                var pending = updates.History().Where(record => record.NeedsRecovery).ToList();
                if (pending.Count == 0) return (ok: true, msg: "没有待恢复的维护任务。");
                var messages = new List<string>();
                foreach (var record in pending)
                {
                    var recovered = await updates.RecoverMaintenanceAsync(record);
                    messages.Add((recovered.ok ? "已恢复 " : "恢复失败 ") + record.Label + "：" + recovered.msg);
                }
                return (ok: messages.All(line => line.StartsWith("已恢复")), msg: string.Join("\n", messages));
            }).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "recover-result.txt"),
                result.ok ? "完成：" + result.msg : "失败：" + result.msg);
            Shutdown(result.ok ? 0 : 1);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--plugin-update")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var updates = new UpdateService(config, service);
            var profileName = e.Args.Length > 2 ? e.Args[2] : "web";
            var result = System.Threading.Tasks.Task.Run(async () =>
            {
                var profile = updates.Profiles().FirstOrDefault(item => item.Name == profileName);
                if (profile == null) return (ok: false, msg: "profile 不存在：" + profileName);
                var plugin = profile.Plugins.FirstOrDefault(item => item.Name == e.Args[1] && !item.IsIndirect);
                if (plugin == null) return (ok: false, msg: "插件不存在：" + e.Args[1]);
                return await updates.UpdatePluginsAsync(new[] { plugin });  // 按当前声明解析目标（Git 浮动拉最新提交，npm 范围装范围内最高）
            }).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "plugin-update-result.txt"),
                result.ok ? "完成：" + result.msg : "失败：" + result.msg);
            Shutdown(result.ok ? 0 : 1);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--mcp-check")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var patchPath = System.IO.Path.Combine(service.HomeBase, "cordis.patch.yml");
            var lines = new List<string>();
            foreach (var server in McpSchemaCheck.ReadServers(patchPath))
            {
                var tools = 0; var bad = 0; var status = "已防护";
                if (!server.Protected)
                {
                    var probe = System.Threading.Tasks.Task.Run(
                        () => McpSchemaCheck.ProbeAsync(server, TimeSpan.FromSeconds(25))).GetAwaiter().GetResult();
                    tools = probe.tools; bad = probe.badTools;
                    status = probe.error.Length > 0 ? "探测失败：" + probe.error : bad > 0 ? "需要修复" : "通过";
                }
                lines.Add($"{server.ServerName} · tools={tools} · bad={bad} · {status}");
            }
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(AppConfig.ConfigDir, "mcp-check-result.txt"), lines);
            Shutdown(0);
            return;
        }
        if (e.Args.Length > 1 && e.Args[0] == "--runtime-update")
        {
            var config = AppConfig.Load();
            using var service = new DshService(config);
            var updates = new UpdateService(config, service);
            var result = System.Threading.Tasks.Task.Run(() => updates.UpdateRuntimeAsync(e.Args[1])).GetAwaiter().GetResult();
            System.IO.Directory.CreateDirectory(AppConfig.ConfigDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppConfig.ConfigDir, "runtime-update-result.txt"), result.ok ? "完成：" + result.msg : "失败：" + result.msg);
            Shutdown(result.ok ? 0 : 1);
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
                StartPage = Math.Clamp(p, 0, 9);
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
