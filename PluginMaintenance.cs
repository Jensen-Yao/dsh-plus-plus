using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshControl;

public record MaintenanceBackup(string Path, string Directory, string Label, DateTime Created, List<string> Files)
{
    public string Display => $"{Created.ToLocalTime():MM-dd HH:mm}  {Label}";
}

public partial class UpdateService
{
    static readonly string[] TrackedFiles = { "package.json", "pnpm-lock.yaml", "package-lock.json", "cordis.patch.yml", "pnpm-workspace.yaml", ".npmrc" };
    string BackupRoot => Path.Combine(AppConfig.ConfigDir, "backups");
    public bool Busy { get; private set; }
    MaintenanceRecord activeRecord;

    public MaintenanceBackup CreateBackup(string directory, string label)
    {
        var destination = Path.Combine(BackupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(destination);
        var files = TrackedFiles.Where(file => File.Exists(Path.Combine(directory, file))).ToList();
        foreach (var file in files) File.Copy(Path.Combine(directory, file), Path.Combine(destination, file));
        var backup = new MaintenanceBackup(destination, directory, label, DateTime.UtcNow, files);
        File.WriteAllText(Path.Combine(destination, "backup.json"), JsonSerializer.Serialize(backup));
        Log("已备份：" + backup.Display);
        return backup;
    }

    public List<MaintenanceBackup> Backups()
    {
        if (!Directory.Exists(BackupRoot)) return new();
        var backups = new List<MaintenanceBackup>();
        foreach (var folder in Directory.GetDirectories(BackupRoot))
        {
            try
            {
                var backup = JsonSerializer.Deserialize<MaintenanceBackup>(File.ReadAllText(Path.Combine(folder, "backup.json")));
                if (backup != null) backups.Add(backup with { Path = folder });
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
        }
        return backups.OrderByDescending(backup => backup.Created).ToList();
    }

    async Task<CommandResult> PackageAsync(string manager, IEnumerable<string> arguments, string directory,
        Action<string> progress, CancellationToken cancellation)
    {
        var start = CommandRunner.PackageManager(manager, arguments, directory);
        start.Environment["npm_config_fetch_timeout"] = "120000";
        start.Environment["npm_config_fetch_retries"] = "2";
        var clock = Stopwatch.StartNew();
        void Report(string line) { Log(line); progress?.Invoke(line); }
        Report($"正在执行 {manager} {string.Join(" ", arguments)}");
        var task = CommandRunner.RunAsync(start, TimeSpan.FromMinutes(cfg.UpdateTimeoutMinutes), Report, cancellation, started: process =>
        {
            if (activeRecord == null) return;
            activeRecord.WorkerPid = process.Id;
            activeRecord.WorkerStarted = process.StartTime.ToUniversalTime();
            SaveRecord(activeRecord);
        });
        while (await Task.WhenAny(task, Task.Delay(10000)) != task)
            progress?.Invoke($"维护中 · 已用时 {clock.Elapsed:mm\\:ss} · 可取消");
        var result = await task;
        if (activeRecord != null)
        {
            activeRecord.WorkerPid = 0;
            SaveRecord(activeRecord);
        }
        return result;
    }

    async Task ValidateAsync(CancellationToken cancellation, string profileName = "web")
    {
        if (profileName == "desktop")
        {
            await desktopService.ValidateStartupAsync(progress: null, cancellation);
            return;
        }
        var bin = Path.Combine(cfg.DshAppDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        var start = CommandRunner.Create("node", new[] { bin, "--profile", profileName, "--dump-config" }, cfg.DshAppDir);
        start.Environment["DSH_HOME"] = svc.HomeBase;
        var result = await CommandRunner.RunAsync(start, TimeSpan.FromSeconds(45), cancellation: cancellation);
        var detail = CommandRunner.Redact(result.Output);
        if (!result.Ok) throw new InvalidOperationException("组合检查失败：" + detail[..Math.Min(1600, detail.Length)]);
    }

    async Task RestoreFilesAsync(MaintenanceBackup backup, Action<string> progress)
    {
        foreach (var file in TrackedFiles)
        {
            var target = Path.Combine(backup.Directory, file);
            if (backup.Files.Contains(file)) File.Copy(Path.Combine(backup.Path, file), target, true);
            else if (File.Exists(target)) File.Delete(target);
        }
        var manager = File.Exists(Path.Combine(backup.Directory, "pnpm-lock.yaml")) ? "pnpm" : "npm";
        var hasNpmLock = File.Exists(Path.Combine(backup.Directory, "package-lock.json"));
        if (manager == "npm" && !hasNpmLock)
            progress?.Invoke("此恢复点没有锁文件，将按原声明重新解析，不能保证传递依赖的原版本。");
        var arguments = manager == "pnpm"
            ? new[] { "install", "--frozen-lockfile", "--offline" }
            : new[] { hasNpmLock ? "ci" : "install", "--prefer-offline" };
        var result = await PackageAsync(manager, arguments, backup.Directory, progress, CancellationToken.None);
        if (!result.Ok && manager == "pnpm")
            result = await PackageAsync(manager, new[] { "install", "--frozen-lockfile" }, backup.Directory, progress, CancellationToken.None);
        if (!result.Ok) throw new InvalidOperationException("依赖恢复失败，备份已保留：" + backup.Path + "\n" + CommandRunner.Redact(result.Output));
    }

    public async Task<(bool ok, string msg)> UpdatePluginsAsync(IReadOnlyList<PluginItem> plugins,
        Action<string> progress = null, CancellationToken cancellation = default, string explicitSpec = null)
    {
        if (plugins.Count == 0) return (false, "请先选择要更新的插件。");
        if (plugins.Any(plugin => plugin.IsIndirect || plugin.IsLocal || plugin.IsCore || (explicitSpec == null && !plugin.CanUpdate)))
            return (false, "聚合子包须通过入口维护；固定版本请使用「指定版本」，本地链接与内核包不参与此更新。");
        if (explicitSpec != null && (plugins.Count != 1 || !ValidSpec(explicitSpec)))
            return (false, "目标版本无效，请填写版本、范围或 github:owner/repository#ref。");
        List<PluginUpdatePlan> plans;
        try
        {
            plans = new();
            foreach (var plugin in plugins)
            {
                progress?.Invoke("正在解析更新目标（服务保持运行）：" + plugin.Name);
                plans.Add(await ResolvePlanAsync(plugin, explicitSpec ?? plugin.Spec, cancellation));
            }
        }
        catch (OperationCanceledException) { return (false, "目标解析已取消，未停止服务或修改依赖。"); }
        catch (Exception error) { return (false, "目标解析失败，未修改依赖：" + CommandRunner.Redact(error.Message)); }
        if (plans.All(plan => !plan.NeedsChange)) return (true, "已是声明内目标版本，未修改依赖或停止服务。");
        return await MaintainAsync(plugins.Select(plugin => plugin.ProfileDir).Distinct().ToList(),
            $"更新 {plugins.Count} 个插件", async () =>
            {
                foreach (var plan in plans.Where(plan => plan.NeedsChange))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var plugin = plan.Plugin;
                    var beforeVersion = InstalledVersion(plugin.ProfileDir, plugin.Name);
                    Log("已解析目标：" + plugin.Name + " → " + (plan.Commit.Length > 0 ? plan.Commit : plan.Version));
                    var result = await PackageAsync("pnpm", new[] { "add", plugin.Name + "@" + plan.InstallSpec, "--reporter=append-only" },
                        plugin.ProfileDir, progress, cancellation);
                    if (!result.Ok) throw new InvalidOperationException(result.Cancelled ? "更新已取消" : result.TimedOut
                        ? "更新超时" : "更新失败：" + CommandRunner.Redact(result.Output));
                    VerifyInstalledPlan(plan);
                    PluginLock.RestoreTrackingSpec(plugin.ProfileDir, plugin.Name, plan.IntentSpec);
                    VerifyInstalledPlan(plan);
                    var afterVersion = InstalledVersion(plugin.ProfileDir, plugin.Name);
                    Log(plugin.Name + $"：{beforeVersion} → {afterVersion}，目标" + (plan.Commit.Length > 0 ? "提交" : "版本") + "已核对。");
                }
                foreach (var plan in plans) VerifyInstalledPlan(plan);
            }, progress, cancellation, beforeBackup: () =>
            {
                foreach (var plan in plans) EnsurePlanCurrent(plan);
                return Task.CompletedTask;
            });
    }

    public static bool ValidSpec(string spec) => !string.IsNullOrWhiteSpace(spec) && spec.Length <= 300
        && !spec.StartsWith("-") && !new[] { "file:", "link:", "workspace:" }.Any(prefix => spec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        && Regex.IsMatch(spec, @"^[a-zA-Z0-9@/._:#^~*+<>=| -]+$");

    public async Task<(bool ok, string msg)> UpdateRuntimeAsync(string channel, Action<string> progress = null,
        CancellationToken cancellation = default)
    {
        if (channel is not ("latest" or "alpha")) return (false, "无效更新通道。");
        var query = CommandRunner.PackageManager("npm", new[] { "view", "@deepseek-ai/dsh@" + channel, "version", "--json" }, cfg.DshAppDir);
        var resolved = await CommandRunner.RunAsync(query, TimeSpan.FromSeconds(45), cancellation: cancellation);
        if (!resolved.Ok) return (false, "无法解析更新通道：" + resolved.Output);
        var version = JsonSerializer.Deserialize<string>(resolved.StandardOutput);
        if (!Regex.IsMatch(version ?? "", @"^\d+\.\d+\.\d+(?:-[\w.-]+)?$")) return (false, "更新版本无效。");
        return await MaintainAsync(new[] { cfg.DshAppDir }.Concat(Profiles().Where(profile => profile.Name == "web").Select(profile => profile.Dir)).ToList(), "DSH 内核 · " + channel, async () =>
        {
            var result = await PackageAsync("npm", new[] { "install", "--save-exact", "@deepseek-ai/dsh@" + version }, cfg.DshAppDir, progress, cancellation);
            if (!result.Ok) throw new InvalidOperationException("内核更新未完成：" + result.Output);
            var (installed, error) = LocalDshVersion();
            if (error != null || installed != version) throw new InvalidOperationException("安装版本与目标版本不一致。");
            await SynchronizeCompanionPackagesAsync(progress, cancellation);
        }, progress, cancellation);
    }

    async Task SynchronizeCompanionPackagesAsync(Action<string> progress, CancellationToken cancellation)
    {
        var profile = Profiles().FirstOrDefault(profile => profile.Name == "web");
        if (profile == null) return;
        var targets = new List<string>();
        foreach (var plugin in profile.Plugins.Where(plugin => !plugin.IsLocal && plugin.Name.StartsWith("@deepseek-ai/dsh-")))
        {
            var package = Path.Combine(cfg.DshAppDir, "node_modules", plugin.Name.Replace('/', Path.DirectorySeparatorChar), "package.json");
            if (!File.Exists(package)) { Log("内核未提供对应包，保留声明：" + plugin.Name); continue; }
            using var document = JsonDocument.Parse(File.ReadAllText(package));
            var target = document.RootElement.GetProperty("version").GetString();
            if (plugin.Installed != target) targets.Add(plugin.Name + "@" + target);
        }
        if (targets.Count == 0) { progress?.Invoke("官方伴随包已与当前内核一致。"); return; }
        var result = await PackageAsync("pnpm", new[] { "add", "--save-exact" }.Concat(targets), profile.Dir, progress, cancellation);
        if (!result.Ok) throw new InvalidOperationException("官方伴随包同步失败：" + result.Output);
    }

    public Task<(bool ok, string msg)> SynchronizeCompanionsAsync(Action<string> progress = null, CancellationToken cancellation = default)
    {
        var profile = Profiles().FirstOrDefault(profile => profile.Name == "web");
        if (profile == null) return Task.FromResult((false, "没有找到 web profile。"));
        return MaintainAsync(new() { profile.Dir }, "同步官方伴随包", () => SynchronizeCompanionPackagesAsync(progress, cancellation), progress, cancellation);
    }

    async Task<(bool ok, string msg)> MaintainAsync(List<string> directories, string label, Func<Task> change,
        Action<string> progress, CancellationToken cancellation, bool recovery = false, Func<Task> beforeBackup = null)
    {
        var affected = MaintenanceProfiles(directories, svc.HomeBase, cfg.DshAppDir);
        if (!await svc.Maintenance.WaitAsync(0, cancellation)) return (false, "其他启动或维护任务正在进行。");
        var backups = new List<MaintenanceBackup>();
        var wasRunning = false;
        var stopped = false;
        var changed = false;
        var touchesWeb = affected.Contains("web");
        var touchesDesktop = affected.Contains("desktop");
        var desktopWasRunning = false;
        var desktopStopped = false;
        Busy = true;
        FileStream operationLock = null;
        MaintenanceRecord record = null;
        void Stage(string stage, bool required = true)
        {
            record.Stage = stage;
            try { SaveRecord(record); }
            catch (IOException error) when (!required) { Log("维护记录未能保存：" + error.Message); }
            progress?.Invoke(stage);
        }
        void Complete(string outcome, string message)
        {
            if (record == null) return;
            record.Outcome = outcome;
            record.Stage = outcome;
            record.Message = message;
            record.Completed = DateTime.UtcNow;
            try { SaveRecord(record); }
            catch (IOException error) { Log("维护结果未能保存，请保留恢复点：" + error.Message); }
        }
        try
        {
            operationLock = svc.AcquireOperationLock();
            if (!recovery && History().Any(entry => entry.NeedsRecovery))
                return (false, "存在未完成的维护任务，请先在「版本与备份」中恢复。服务启动不受此限制。");
            if (beforeBackup != null) await beforeBackup();
            record = new MaintenanceRecord { Label = label, Backups = backups };
            activeRecord = record;
            SaveRecord(record);
            wasRunning = touchesWeb && svc.IsDshRunning();
            desktopWasRunning = touchesDesktop && desktopService.Status().Running;
            foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                backups.Add(CreateBackup(directory, label));
                Stage("备份已保存");
            }
            if (wasRunning)
            {
                progress?.Invoke("备份完成，正在停止服务…");
                var stop = await svc.StopCoreAsync();
                if (!stop.ok) throw new InvalidOperationException(stop.message);
                stopped = true;
            }
            if (desktopWasRunning)
            {
                Stage("正在停止桌面版");
                var stop = await desktopService.StopAsync();
                if (!stop.ok) throw new InvalidOperationException(stop.message);
                desktopStopped = true;
            }
            cancellation.ThrowIfCancellationRequested();
            Stage("正在修改依赖");
            changed = true;
            await change();
            Stage("正在检查配置组合");
            foreach (var profile in affected) await ValidateAsync(cancellation, profile);
            if (desktopWasRunning && cfg.RestartAfterUpdate)
            {
                Stage("正在恢复桌面版");
                var start = await desktopService.StartAsync();
                if (!start.ok) throw new InvalidOperationException(start.message);
            }
            if (wasRunning && cfg.RestartAfterUpdate)
            {
                Stage("正在启动并验证服务");
                var start = await svc.StartCoreAsync(cancellation);
                if (!start.ok) throw new InvalidOperationException(start.message);
            }
            var success = label + ((wasRunning || desktopWasRunning) && cfg.RestartAfterUpdate ? "完成，原运行端已恢复。" : "完成，备份已保留。");
            Complete("完成", success);
            return (true, success);
        }
        catch (Exception exception)
        {
            var message = CommandRunner.Redact(exception is OperationCanceledException ? "操作已取消" : exception.Message);
            Log(message);
            if (changed)
            {
                Stage("正在恢复更新前依赖", required: false);
                try
                {
                    if (touchesWeb)
                    {
                        var stop = await svc.StopCoreAsync();
                        if (!stop.ok) throw new InvalidOperationException(stop.message);
                    }
                    if (touchesDesktop)
                    {
                        var stop = await desktopService.StopAsync();
                        if (!stop.ok) throw new InvalidOperationException(stop.message);
                    }
                    foreach (var backup in backups.AsEnumerable().Reverse()) await RestoreFilesAsync(backup, progress);
                    message += "\n已恢复更新前依赖。";
                }
                catch (Exception restoreError)
                {
                    var failure = message + "\n恢复未完成：" + CommandRunner.Redact(restoreError.Message);
                    Complete("恢复未完成", failure);
                    return (false, failure);
                }
            }
            if (wasRunning && stopped)
            {
                var start = await svc.StartCoreAsync();
                message += start.ok ? "\n原服务已恢复。" : "\n原服务未能恢复：" + start.message;
            }
            if (desktopWasRunning && desktopStopped)
            {
                var start = await desktopService.StartAsync();
                message += start.ok ? "\n原桌面版已恢复。" : "\n原桌面版未能恢复：" + start.message;
            }
            Complete(changed ? "已回滚" : "未修改", message);
            return (false, message);
        }
        finally { activeRecord = null; operationLock?.Dispose(); Busy = false; svc.Maintenance.Release(); }
    }

    /// <summary>Maps changed installation/profile directories to the hosts that own their dependencies.</summary>
    internal static List<string> MaintenanceProfiles(IEnumerable<string> directories, string home, string installation)
    {
        var result = new List<string>();
        var profiles = Path.GetFullPath(Path.Combine(home, "profiles"));
        foreach (var directory in directories)
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, Path.GetFullPath(installation).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                result.Add("web");
            else if (string.Equals(Path.GetDirectoryName(full), profiles, StringComparison.OrdinalIgnoreCase))
                result.Add(Path.GetFileName(full));
            else throw new InvalidOperationException("恢复点不属于当前安装或 profile 目录：" + directory);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<(bool ok, string msg)> RestoreBackupAsync(MaintenanceBackup backup, Action<string> progress = null,
        CancellationToken cancellation = default) => MaintainAsync(new() { backup.Directory }, "恢复备份", async () =>
        {
            await RestoreFilesAsync(backup, progress);
        }, progress, cancellation);
}
