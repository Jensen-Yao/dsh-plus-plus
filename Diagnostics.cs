using System.Text;
using System.IO;
using System.Text.Json;

namespace DshControl;

public static class Diagnostics
{
    public static async Task<string> ReportAsync(AppConfig cfg, DshService svc, UpdateService updates)
    {
        var report = new StringBuilder();
        report.AppendLine($"诊断 {DateTime.Now:yyyy-MM-dd HH:mm:ss} · dsh++ {UpdateService.AppVersion()}");
        var (version, error) = updates.LocalDshVersion();
        report.AppendLine("DSH: " + (error ?? version));
        var desktop = new DesktopService(cfg, svc);
        var desktopStatus = desktop.Status();
        report.AppendLine("桌面版: " + (desktopStatus.Installed
            ? $"{desktopStatus.Version} · {desktopStatus.State} · {desktopStatus.ProcessCount} 进程"
            : "未安装"));
        report.AppendLine("桌面 Profile: " + desktop.ProfileDirectory);
        report.AppendLine("任务看板单写锁: " + desktop.TaskLedgerOwner() + " · " + desktop.TaskLedgerLock);
        if (desktopStatus.LatestCrashLog.Length > 0)
            report.AppendLine("桌面最近 Host 日志: " + desktopStatus.LatestCrashLog);
        report.AppendLine("安装目录: " + cfg.DshAppDir);
        report.AppendLine("数据目录: " + svc.HomeBase);
        try { report.AppendLine("计划绑定: " + svc.ResolveBindHost()); }
        catch (Exception exception) { report.AppendLine("绑定检查失败: " + exception.Message); }
        var pid = svc.GetPidOnPort(cfg.Port);
        report.AppendLine($"端口 {cfg.Port}: " + (pid == 0 ? "未监听" : $"PID {pid} · " + (await svc.OwnsProcessAsync(pid) ? "DSH" : "其他进程")));
        foreach (var manager in new[] { "npm", "pnpm" })
        {
            try
            {
                var start = CommandRunner.PackageManager(manager, new[] { "--version" }, cfg.DshAppDir);
                var result = await CommandRunner.RunAsync(start, TimeSpan.FromSeconds(10));
                report.AppendLine(manager + ": " + result.Output);
            }
            catch (Exception exception) { report.AppendLine(manager + ": " + exception.Message); }
        }
        foreach (var profile in updates.Profiles())
        {
            report.AppendLine("Profile: " + profile.Name);
            foreach (var plugin in profile.Plugins)
            {
                if (plugin.SourceIssue.Length > 0) report.AppendLine("  来源提示: " + plugin.Name + " · " + plugin.SourceIssue + " · " + plugin.ResolvedSource);
                if (string.IsNullOrEmpty(plugin.Installed)) report.AppendLine("  未安装: " + plugin.Name);
                if (plugin.IsCore && plugin.Installed != version)
                    report.AppendLine($"  内核伴随包版本不同: {plugin.Name} {plugin.Installed} / DSH {version}");
                var packageRoot = Path.Combine(profile.Dir, "node_modules", plugin.Name.Replace('/', Path.DirectorySeparatorChar));
                var manifestPath = Path.Combine(packageRoot, "package.json");
                if (!File.Exists(manifestPath)) continue;
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (manifest.RootElement.TryGetProperty("dsh", out var dsh) && dsh.TryGetProperty("client", out _)
                    && manifest.RootElement.TryGetProperty("exports", out var exports) && exports.ValueKind == JsonValueKind.Object
                    && exports.TryGetProperty("./client", out var client) && client.ValueKind == JsonValueKind.String
                    && !File.Exists(Path.Combine(packageRoot, client.GetString())))
                    report.AppendLine("  缺少客户端产物: " + plugin.Name + " " + client.GetString());
            }
        }
        var bin = Path.Combine(cfg.DshAppDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        var inspection = CommandRunner.Create("node", new[] { bin, "--profile", "web", "--dump-config" }, cfg.DshAppDir);
        inspection.Environment["DSH_HOME"] = svc.HomeBase;
        var composed = await CommandRunner.RunAsync(inspection, TimeSpan.FromSeconds(45));
        report.AppendLine(composed.Ok ? "配置组合: 通过" : "配置组合: 失败\n" + composed.Output);
        report.AppendLine("诊断结束");
        return CommandRunner.Redact(report.ToString());
    }
}
