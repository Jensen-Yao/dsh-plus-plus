using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace DshControl;

public sealed record DesktopUpdateInfo(string Version, string Url, long Size, string Sha512, DateTime ReleaseDate);
public sealed record DesktopUpdateStatus(bool Installed, string CurrentVersion, DesktopUpdateInfo Latest,
    bool UpdateAvailable, string Error = "");

public sealed record DesktopStatus(bool Installed, bool Running, string Version, string Executable, string ProfileDirectory,
    string DataDirectory, int ProcessCount, string LatestCrashLog, string TaskLedgerOwner)
{
    public string State => !Installed ? "未安装" : Running ? "运行中" : "已停止";
}

public sealed class DesktopService
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
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
        var count = processes.Count;
        foreach (var process in processes) process.Dispose();
        var version = Installed ? ReadAsarPackageVersion(Path.Combine(cfg.DshDesktopDir, "resources", "app.asar")) : "";
        var crash = Directory.Exists(LogDirectory)
            ? Directory.GetFiles(LogDirectory, "crash-*-host.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        return new(Installed, count > 0, version, Executable, ProfileDirectory, DataDirectory,
            count, crash ?? "", TaskLedgerOwner());
    }

    internal static string ReadAsarPackageVersion(string asarPath)
    {
        using var stream = File.OpenRead(asarPath);
        Span<byte> prefix = stackalloc byte[16];
        if (stream.Read(prefix) != prefix.Length || BitConverter.ToUInt32(prefix) != 4)
            throw new InvalidDataException("桌面版程序包格式无效。");
        var jsonLength = BitConverter.ToInt32(prefix[12..]);
        if (jsonLength <= 0 || jsonLength > 16 * 1024 * 1024) throw new InvalidDataException("桌面版程序包头部无效。");
        var header = new byte[jsonLength];
        if (stream.Read(header) != jsonLength) throw new InvalidDataException("桌面版程序包头部不完整。");
        using var headerDocument = JsonDocument.Parse(Encoding.UTF8.GetString(header).TrimEnd('\0'));
        var json = headerDocument.RootElement;
        var package = json.GetProperty("files").GetProperty("dsh").GetProperty("files").GetProperty("package.json");
        var offset = long.Parse(package.GetProperty("offset").GetString() ?? "-1");
        var size = package.GetProperty("size").GetInt64();
        var contentStart = 16 + jsonLength + offset;
        if (offset < 0 || size <= 0 || contentStart < 0 || contentStart + size > stream.Length)
            throw new InvalidDataException("桌面版程序包条目无效。");
        stream.Seek(contentStart, SeekOrigin.Begin);
        var content = new byte[size];
        if (stream.Read(content) != content.Length) throw new InvalidDataException("桌面版程序包条目不完整。");
        using var manifest = JsonDocument.Parse(Encoding.UTF8.GetString(content));
        return manifest.RootElement.GetProperty("version").GetString() ?? "";
    }

    internal static (string Url, string Channel) ReadDesktopUpdateConfig(string source)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(source));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("桌面版更新配置无效。");
        static string Scalar(YamlNode parent, string key) => parent is YamlMappingNode map
            && map.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value ?? "" : "";
        var url = Scalar(root, "url");
        var channel = Scalar(root, "channel");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || string.IsNullOrWhiteSpace(channel))
            throw new InvalidDataException("桌面版更新源无效。");
        return (url, channel);
    }

    internal static DesktopUpdateInfo ReadDesktopUpdateFeed(string source)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(source));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("桌面版更新信息无效。");
        static string Scalar(YamlNode parent, string key) => parent is YamlMappingNode map
            && map.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value ?? "" : "";
        var version = Scalar(root, "version");
        var file = (root.Children[new YamlScalarNode("files")] as YamlSequenceNode)?.Children.FirstOrDefault() as YamlMappingNode;
        var url = file == null ? "" : Scalar(file, "url");
        var sizeText = file == null ? "" : Scalar(file, "size");
        var sha512 = file == null ? "" : Scalar(file, "sha512");
        var dateText = Scalar(root, "releaseDate");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !long.TryParse(sizeText, out var size) || !DateTime.TryParse(dateText, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var released))
            throw new InvalidDataException("桌面版更新条目无效。");
        return new(version, url, size, sha512, released);
    }

    public async Task<DesktopUpdateStatus> CheckUpdateAsync(CancellationToken cancellation = default)
    {
        try
        {
            if (!Installed) return new(false, "", null, false, "未找到桌面版安装：" + Executable);
            var asar = Path.Combine(cfg.DshDesktopDir, "resources", "app.asar");
            if (!File.Exists(asar)) return new(false, "", null, false, "未找到桌面版 runtime 清单：" + asar);
            string current;
            try { current = ReadAsarPackageVersion(asar); }
            catch (Exception error) { return new(true, "", null, false, "读取桌面版 runtime 版本失败：" + asar + "\n" + error.Message); }

            var configPath = Path.Combine(cfg.DshDesktopDir, "resources", "app-update.yml");
            if (!File.Exists(configPath)) return new(true, current, null, false, "未找到桌面版更新配置：" + configPath);
            var config = ReadDesktopUpdateConfig(await File.ReadAllTextAsync(configPath, cancellation));
            var feedUrl = config.Url.TrimEnd('/') + "/" + Uri.EscapeDataString(config.Channel) + ".yml";
            using var response = await http.GetAsync(feedUrl, cancellation);
            if (!response.IsSuccessStatusCode)
                return new(true, current, null, false,
                    CommandRunner.DescribeHttpStatus("读取官方桌面版更新信息", (int)response.StatusCode, response.ReasonPhrase,
                        response.Content.Headers.ContentLength) + "\n地址：" + feedUrl);
            var body = await response.Content.ReadAsStringAsync(cancellation);
            DesktopUpdateInfo latest;
            try { latest = ReadDesktopUpdateFeed(body); }
            catch (Exception error)
            {
                return new(true, current, null, false,
                    "解析官方桌面版更新信息失败：" + error.Message + "\n地址：" + feedUrl + "\n返回内容：" + CommandRunner.Brief(body));
            }
            return new(true, current, latest, UpdateService.IsNewer(latest.Version, current));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(Installed, "", null, false, "官方桌面版更新检查失败：" + CommandRunner.Redact(error.Message));
        }
    }

    public async Task<(bool ok, string message)> UpdateAsync(DesktopUpdateInfo update, Action<string> progress = null,
        CancellationToken cancellation = default)
    {
        if (update == null || !Uri.TryCreate(update.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "download.deepseek.com" || !uri.AbsolutePath.StartsWith("/dsh-desk/", StringComparison.OrdinalIgnoreCase))
            return (false, "官方桌面版更新地址无效：" + (update?.Url ?? "空"));
        if (!Installed) return (false, "未找到当前桌面版安装：" + Executable);
        var asar = Path.Combine(cfg.DshDesktopDir, "resources", "app.asar");
        var stage = "准备更新";
        long copied = 0;
        var wasRunning = IsRunning();
        var stop = await StopAsync();
        if (!stop.ok) return (false, "更新前停止桌面版失败：" + stop.message);

        var directory = Path.Combine(AppConfig.ConfigDir, "desktop-updates");
        Directory.CreateDirectory(directory);
        var installer = Path.Combine(directory, "deepseek-harness-" + update.Version + "-win-x64.exe");
        CommandResult installerResult = null;
        try
        {
            if (File.Exists(installer) && new FileInfo(installer).Length == update.Size)
            {
                copied = update.Size;
                progress?.Invoke($"发现已完整下载的官方安装包：{installer}");
            }
            else
            {
                stage = "下载官方安装包";
                progress?.Invoke($"正在下载官方桌面版 {update.Version} · {update.Size / 1024 / 1024} MB…");
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(
                    CommandRunner.DescribeHttpStatus(stage, (int)response.StatusCode, response.ReasonPhrase,
                        response.Content.Headers.ContentLength) + "\n地址：" + uri);
                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value != update.Size)
                    throw new InvalidOperationException($"官方 feed 与下载响应大小不一致：feed {update.Size} 字节，HTTP {declaredLength.Value} 字节。\n地址：{uri}");
                await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
                await using (var destination = File.Create(installer))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellation);
                        copied += read;
                        if (copied % (16 * 1024 * 1024) < buffer.Length)
                            progress?.Invoke($"下载中 {copied / 1024 / 1024} MB / {update.Size / 1024 / 1024} MB");
                    }
                    await destination.FlushAsync(cancellation);
                }
                if (copied != update.Size) throw new InvalidOperationException(
                    CommandRunner.DescribeSizeFailure(stage, update.Size, copied) + $"\n地址：{uri}\n文件：{installer}");
            }
            stage = "校验官方安装包";
            progress?.Invoke("下载完成，正在核对 SHA-512…");
            string expected;
            try { expected = Convert.ToHexString(Convert.FromBase64String(update.Sha512)); }
            catch (Exception error) { throw new InvalidOperationException($"解析官方 SHA-512 失败：feed 值 {update.Sha512}。\n{error.Message}"); }
            using var hashStream = File.OpenRead(installer);
            var actual = Convert.ToHexString(SHA512.HashData(hashStream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(CommandRunner.DescribeHashFailure(stage, "SHA-512", expected, actual)
                    + $"\n地址：{uri}\n文件：{installer}");

            stage = "运行官方安装器";
            progress?.Invoke("校验通过，正在安装官方桌面版…");
            var arguments = new[] { "/currentuser", "/S", "/D=" + cfg.DshDesktopDir };
            installerResult = await CommandRunner.RunAsync(CommandRunner.Create(installer, arguments, Path.GetTempPath()),
                TimeSpan.FromMinutes(20), progress, cancellation);
            if (!installerResult.Ok) throw new InvalidOperationException(CommandRunner.DescribeFailure(
                stage, installer + " " + string.Join(' ', arguments), installerResult));

            stage = "核对官方 runtime 版本";
            var started = DateTime.UtcNow;
            var deadline = started.AddMinutes(2);
            string installed = "";
            string readError = "";
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation);
                try { installed = ReadAsarPackageVersion(asar); readError = ""; }
                catch (Exception error) { readError = error.Message; }
                if (string.Equals(installed, update.Version, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (!string.Equals(installed, update.Version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"官方桌面版安装后版本核对失败：目标 {update.Version}，实际 {installed}，等待 {DateTime.UtcNow - started:mm\\:ss}。\n"
                    + $"runtime 清单：{asar}\n最后读取错误：{(readError.Length > 0 ? readError : "无")}\n安装器退出码 {installerResult.ExitCode}。");

            if (wasRunning && cfg.RestartAfterUpdate)
            {
                stage = "重新启动桌面版";
                var restarted = await StartAsync();
                if (!restarted.ok) return (false, "桌面版已更新到 " + update.Version + "，但自动启动失败：" + restarted.message);
            }
            return (true, "官方桌面版已更新到 " + update.Version + "。");
        }
        catch (Exception error)
        {
            var detail = error is OperationCanceledException
                ? $"官方桌面版更新已取消：阶段 {stage}，目标 {update.Version}，已下载 {copied} 字节。"
                : $"{stage}失败：目标 {update.Version}，已下载 {copied} 字节。\n" + CommandRunner.Redact(error.Message)
                  + (installerResult != null ? $"\n安装器退出码 {installerResult.ExitCode}。" : "");
            if (wasRunning)
            {
                var restart = await StartAsync();
                detail += restart.ok ? "\n已尝试恢复原桌面版运行。" : "\n原桌面版未能恢复：" + restart.message;
            }
            return (false, detail);
        }
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
            var owned = OwnedProcesses().ToList();
            try { if (owned.Any(process => process.Id == pid)) return "桌面版"; }
            finally { foreach (var candidate in owned) candidate.Dispose(); }
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

    internal IEnumerable<Process> OwnedProcesses()
    {
        foreach (var process in Process.GetProcessesByName("DeepSeek Harness"))
        {
            if (StringsEqual(ProcessPath(process), Executable)) yield return process;
            else process.Dispose();
        }
    }

    bool IsRunning()
    {
        var owned = OwnedProcesses().ToList();
        try { return owned.Count > 0; }
        finally { foreach (var process in owned) process.Dispose(); }
    }

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
        if (IsRunning()) return (true, "桌面版已在运行。");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Executable)
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
                try { if (!process.HasExited) process.Kill(true); }
                catch (InvalidOperationException) { /* A parent may already have ended this child. */ }
            }
            foreach (var process in processes) await process.WaitForExitAsync();
            log?.Invoke($"桌面版已停止，共结束 {processes.Count} 个进程。");
            return (true, "桌面版已停止。");
        }
        catch (Exception error) { return (false, "桌面版停止失败：" + error.Message); }
        finally { foreach (var process in processes) process.Dispose(); }
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
        try
        {
            if (!start.ok) throw new InvalidOperationException(start.message);
            cancellation.ThrowIfCancellationRequested();
        }
        finally
        {
            var stop = await StopAsync();
            if (!stop.ok) throw new InvalidOperationException(stop.message);
        }
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
