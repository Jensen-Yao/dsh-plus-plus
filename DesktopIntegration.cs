using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace DshControl;

public sealed record DesktopStatus(bool Installed, bool Running, string Version, string Executable, string ProfileDirectory,
    string DataDirectory, int ProcessCount, string LatestCrashLog, string TaskLedgerOwner)
{
    public string State => !Installed ? "未安装" : Running ? "运行中" : "已停止";
}

public sealed class DesktopService
{
    readonly AppConfig cfg;
    readonly DshService svc;
    readonly Action<string> log;

    public DesktopService(AppConfig cfg, DshService svc, Action<string> log = null)
    {
        this.cfg = cfg;
        this.svc = svc;
        this.log = log;
    }

    public string Executable => Path.Combine(cfg.DshDesktopDir, "DeepSeek Harness.exe");
    public string ProfileDirectory => Path.Combine(svc.HomeBase, "profiles", "desktop");
    public string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "@deepseek-ai", "dsh-desktop");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public bool Installed => File.Exists(Executable);
    public string TaskLedgerLock => Path.Combine(svc.HomeBase, "task-board", "ledger-v2.lock");

    public DesktopStatus Status()
    {
        var processes = OwnedProcesses().ToList();
        var version = Installed ? FileVersionInfo.GetVersionInfo(Executable).FileVersion : "";
        var crash = Directory.Exists(LogDirectory)
            ? Directory.GetFiles(LogDirectory, "crash-*-host.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return new(Installed, processes.Count > 0, version, Executable, ProfileDirectory, DataDirectory,
            processes.Count, crash ?? "", TaskLedgerOwner());
    }

    public string TaskLedgerOwner()
    {
        try
        {
            if (!File.Exists(TaskLedgerLock)) return "空闲";
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(TaskLedgerLock));
            var pid = document.RootElement.TryGetProperty("pid", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
                ? value.GetInt32() : 0;
            if (pid <= 0) return "未知";
            if (OwnedProcesses().Any(process => process.Id == pid)) return "桌面版";
            var webPid = svc.GetPidOnPort(cfg.Port);
            if (webPid == pid) return "Web 端";
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.ToLowerInvariant() == "node" || process.ProcessName.Equals("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)
                ? "其他 DSH 宿主" : "PID " + pid;
        }
        catch
        {
            return "未知";
        }
    }

    internal IEnumerable<Process> OwnedProcesses() => Process.GetProcessesByName("DeepSeek Harness")
        .Where(process => StringsEqual(ProcessPath(process), Executable));

    static string ProcessPath(Process process)
    {
        try { return process.MainModule?.FileName ?? ""; }
        catch { return ""; }
    }

    static bool StringsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool ok, string message)> StartAsync()
    {
        if (!Installed) return (false, "找不到桌面版程序，请先安装或检查路径。");
        if (OwnedProcesses().Any()) return (true, "桌面版已在运行。");
        try
        {
            var process = Process.Start(new ProcessStartInfo(Executable)
            {
                WorkingDirectory = cfg.DshDesktopDir, UseShellExecute = true,
            });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var startedUtc = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited) return (false, "桌面版进程已退出。");
                process.Refresh();
                if (!string.IsNullOrEmpty(process.MainWindowTitle)) break;
                await Task.Delay(200);
            }
            process.Refresh();
            var title = process.MainWindowTitle;
            if (process.HasExited || string.IsNullOrEmpty(title) || title.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                return (false, "桌面版启动后未进入可用界面。");
            await Task.Delay(TimeSpan.FromSeconds(2));
            var latestCrash = Status().LatestCrashLog;
            if (latestCrash.Length > 0 && File.GetLastWriteTimeUtc(latestCrash) >= startedUtc)
                return (false, "桌面版 Host 启动失败，最新崩溃日志：" + Path.GetFileName(latestCrash));
            log?.Invoke("桌面版已启动：" + Executable);
            return (true, "桌面版已启动。");
        }
        catch (Exception error) { return (false, "桌面版启动失败：" + error.Message); }
    }

    public async Task<(bool ok, string message)> StopAsync()
    {
        var processes = OwnedProcesses().ToList();
        if (processes.Count == 0) return (true, "桌面版已停止。");
        try
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) process.Kill(true);
            }
            foreach (var process in processes) await process.WaitForExitAsync();
            log?.Invoke($"桌面版已停止，共结束 {processes.Count} 个进程。");
            return (true, "桌面版已停止。");
        }
        catch (Exception error) { return (false, "桌面版停止失败：" + error.Message); }
    }

    public async Task<(bool ok, string message)> RestartAsync()
    {
        var stop = await StopAsync();
        return stop.ok ? await StartAsync() : stop;
    }

    public void OpenDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public async Task ValidateStartupAsync(Action<string> progress = null, CancellationToken cancellation = default)
    {
        progress?.Invoke("正在启动官方桌面版做启动验证…");
        var start = await StartAsync();
        if (!start.ok) throw new InvalidOperationException(start.message);
        cancellation.ThrowIfCancellationRequested();
        var stop = await StopAsync();
        if (!stop.ok) throw new InvalidOperationException(stop.message);
        progress?.Invoke("官方桌面版启动验证通过。");
    }

    public void OpenLatestCrashLog()
    {
        var latest = Status().LatestCrashLog;
        if (latest.Length == 0) return;
        Process.Start(new ProcessStartInfo(latest) { UseShellExecute = true });
    }
}

internal sealed record DesktopSyncPlan(JsonObject Manifest, string Patch, List<(string Name, string Web, string Desktop)> DependencyChanges,
    List<(string Name, string Web, string Desktop)> BundleChanges)
{
    internal bool HasChanges => DependencyChanges.Count > 0 || BundleChanges.Count > 0;
}
