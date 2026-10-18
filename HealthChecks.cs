using System.IO;
using System.Text.Json;

namespace DshControl;

/// <summary>A read-only local finding. Passed checks do not imply API compatibility or a running agent.</summary>
public record HealthFinding(string Level, string Area, string Title, string Detail)
{
    public bool NeedsAttention => Level is "错误" or "注意";
}

public record HealthReport(DateTime CheckedAt, List<HealthFinding> Findings)
{
    public string Summary => $"{Findings.Count(item => item.Level == "错误")} 错误 · {Findings.Count(item => item.Level == "注意")} 提醒 · {Findings.Count(item => item.Level == "通过")} 通过";
    public string Export() => CommandRunner.Redact($"dsh++ {UpdateService.AppVersion()} · {CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{Summary}\n\n"
        + string.Join("\n\n", Findings.Select(item => $"[{item.Level}] {item.Area} / {item.Title}\n{item.Detail}")));
}

/// <summary>Inspects local package managers, artifacts, profiles and host state without installing or stopping services.</summary>
public static class HealthChecks
{
    public static async Task<HealthReport> RunAsync(AppConfig cfg, DshService svc, UpdateService updates,
        CancellationToken cancellation = default)
    {
        var findings = new List<HealthFinding>();
        void Add(string level, string area, string title, string detail) => findings.Add(new(level, area, title, CommandRunner.Redact(detail)));
        var (version, error) = updates.LocalDshVersion();
        Add(error == null ? "通过" : "错误", "Web", "DSH 内核", error ?? version);
        var host = BundledAssets.Get("scripts/runtime-host.mjs");
        Add("通过", "启动", "持久启动资产", host);
        var statePath = Path.Combine(AppConfig.ConfigDir, "runtime-state.json");
        if (File.Exists(statePath))
        {
            try
            {
                using var state = JsonDocument.Parse(File.ReadAllText(statePath));
                if (state.RootElement.TryGetProperty("hostScript", out var script))
                {
                    var path = script.GetString();
                    Add(File.Exists(path) ? "通过" : "错误", "启动", "运行宿主脚本", File.Exists(path) ? path : "运行时引用的脚本已丢失，npm/pnpm 子进程也会失败。请从当前控制台重启 Web 服务。\n" + path);
                }
                else Add("信息", "启动", "运行宿主脚本", "当前宿主尚未记录脚本路径；下次从此控制台启动后可检查。");
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            { Add("注意", "启动", "宿主状态无法读取", exception.Message); }
        }
        var pid = svc.GetPidOnPort(cfg.Port);
        Add(pid == 0 ? "信息" : await svc.OwnsProcessAsync(pid) ? "通过" : "错误", "Web", "监听端口",
            pid == 0 ? $"端口 {cfg.Port} 未监听。" : $"端口 {cfg.Port} · PID {pid}");
        try { Add("通过", "网络", "计划绑定", svc.ResolveBindHost()); }
        catch (Exception exception) { Add("注意", "网络", "绑定地址不可用", exception.Message); }
        foreach (var manager in new[] { "npm", "pnpm" })
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var result = await CommandRunner.RunAsync(CommandRunner.PackageManager(manager, new[] { "--version" },
                    Directory.Exists(cfg.DshAppDir) ? cfg.DshAppDir : AppConfig.ConfigDir), TimeSpan.FromSeconds(8), cancellation: cancellation);
                cancellation.ThrowIfCancellationRequested();
                Add(result.Ok ? "通过" : "错误", "环境", manager, result.Ok ? result.StandardOutput : ExplainManagerFailure(result));
            }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
            { Add("错误", "环境", manager, exception.Message); }
        }
        var desktop = new DesktopService(cfg, svc);
        var desktopState = desktop.Status();
        Add("信息", "桌面版", desktopState.State, desktopState.Installed ? desktopState.Version + " · " + desktopState.ProcessCount + " 进程" : desktopState.Executable);
        if (desktopState.Running && pid != 0 && desktopState.TaskLedgerOwner is "桌面版" or "Web 端")
            Add("注意", "共享数据", "任务看板单写锁", desktopState.TaskLedgerOwner + "持有任务看板锁，另一端的任务看板可能降级。需要完整看板时停止另一端；不要删除活动锁文件。");
        var profilesRoot = Path.Combine(svc.HomeBase, "profiles");
        foreach (var directory in Directory.Exists(profilesRoot) ? Directory.GetDirectories(profilesRoot) : Array.Empty<string>())
        {
            var manifestPath = Path.Combine(directory, "package.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                Add("通过", Path.GetFileName(directory), "依赖清单", manifestPath);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            { Add("错误", Path.GetFileName(directory), "依赖清单无法读取", exception.Message); }
        }
        foreach (var profile in updates.Profiles())
        {
            var lockFile = PluginLock.Read(profile.Dir);
            if (lockFile.Error.Length > 0) Add("错误", profile.Name, "锁文件无法解析", lockFile.Error);
            var artifacts = new List<HealthFinding>();
            foreach (var plugin in profile.Plugins)
            {
                cancellation.ThrowIfCancellationRequested();
                if (plugin.SourceIssue.Length > 0) Add("注意", profile.Name, plugin.Name, plugin.SourceIssue);
                artifacts.AddRange(InspectPlugin(plugin));
                if (plugin.IsCore && profile.Name == "web")
                {
                    var provided = UpdateService.InstalledVersion(cfg.DshAppDir, plugin.Name);
                    if (provided.Length > 0 && plugin.Installed.Length > 0 && provided != plugin.Installed)
                        Add("注意", profile.Name, "伴随包版本不同", $"{plugin.Name} · profile {plugin.Installed} / 内核提供 {provided}。可在版本与备份中同步官方依赖。");
                }
            }
            findings.AddRange(artifacts);
            if (artifacts.Count == 0) Add("通过", profile.Name, "插件安装与客户端产物", $"检查 {profile.Plugins.Count} 个已发现包；未发现缺失文件。未运行插件，不代表 API 兼容性验证。");
        }
        foreach (var record in updates.History().Where(record => record.NeedsRecovery))
            Add("注意", "维护", record.Label, "存在中断或恢复未完成的维护任务，请前往「版本与备份」查看恢复点。服务仍可启动。");
        return new(DateTime.UtcNow, findings.OrderBy(item => item.Level == "错误" ? 0 : item.Level == "注意" ? 1 : item.Level == "信息" ? 3 : 2).ToList());
    }

    internal static string ExplainManagerFailure(CommandResult result) => result.Output.Contains("ERR_MODULE_NOT_FOUND", StringComparison.Ordinal)
        && result.Output.Contains("runtime-host", StringComparison.OrdinalIgnoreCase)
        ? "继承的 Node 启动脚本已丢失。请从当前控制台重启 Web 服务，再检查插件市场。\n" + CommandRunner.Redact(result.Output)
        : result.TimedOut ? "本地版本命令超时，请检查进程或环境。" : CommandRunner.Redact(result.Output);

    internal static List<HealthFinding> InspectPlugin(PluginItem plugin)
    {
        var result = new List<HealthFinding>();
        if (plugin.Installed.Length == 0)
        {
            result.Add(new("错误", plugin.Profile, plugin.Name, "未找到安装清单；请检查依赖安装或本地链接目标。\n" + plugin.PackageDirectory));
            return result;
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin.PackageDirectory, "package.json")));
            var root = document.RootElement;
            if (root.TryGetProperty("dsh", out var dsh) && dsh.ValueKind == JsonValueKind.Object && dsh.TryGetProperty("client", out _)
                && root.TryGetProperty("exports", out var exports) && exports.ValueKind == JsonValueKind.Object
                && exports.TryGetProperty("./client", out var client))
            {
                var entry = ClientEntry(client);
                if (entry.Length > 0 && !File.Exists(Path.Combine(plugin.PackageDirectory, entry)))
                    result.Add(new("错误", plugin.Profile, plugin.Name, "缺少客户端构建产物：" + entry
                        + (plugin.IsLocal ? "\n此插件为本地链接，请在源码目录执行其 build 脚本。" : "\n请重新安装此插件或检查发布包内容。")));
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        { result.Add(new("错误", plugin.Profile, plugin.Name, CommandRunner.Redact(exception.Message))); }
        return result;
    }

    static string ClientEntry(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String) return entry.GetString() ?? "";
        if (entry.ValueKind == JsonValueKind.Object)
            foreach (var condition in new[] { "browser", "import", "default" })
                if (entry.TryGetProperty(condition, out var nested) && ClientEntry(nested) is { Length: > 0 } path) return path;
        return "";
    }
}
