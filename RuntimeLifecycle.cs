using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshControl;

public partial class DshService
{
    public readonly SemaphoreSlim Maintenance = new(1, 1);
    public bool IsStarting { get; private set; }
    public string LastFailure { get; private set; } = "";
    public string RuntimeStatePath => Path.Combine(AppConfig.ConfigDir, "runtime-state.json");
    public string LogFile => DshLogPath;
    internal FileStream AcquireOperationLock()
    {
        Directory.CreateDirectory(AppConfig.ConfigDir);
        return new FileStream(Path.Combine(AppConfig.ConfigDir, "maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public string ResolveBindHost()
    {
        if (Cfg.Mode == "local") return "127.0.0.1";
        var address = Cfg.BindHost?.Trim();
        if (string.IsNullOrEmpty(address))
            address = Cfg.AccessMode == "tailscale"
                ? TailscaleCli.Status(Cfg).Ips.FirstOrDefault()
                : DetectLanIp();
        if (!IPAddress.TryParse(address, out var parsed) || parsed.Equals(IPAddress.Any)
            || parsed.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException(Cfg.AccessMode == "tailscale"
                ? "Tailscale 没有可用地址。请连接 Tailscale，或切换到本机 / 局域网模式。"
                : "没有可用的网卡地址。请连接网络，或切换到仅本机模式。");
        var local = NetworkInterface.GetAllNetworkInterfaces().SelectMany(network => network.GetIPProperties().UnicastAddresses);
        if (!local.Any(entry => entry.Address.Equals(parsed)))
            throw new InvalidOperationException($"绑定地址 {address} 不在当前网卡上，请重新选择网络地址。");
        return parsed.ToString();
    }

    public static IEnumerable<string> AvailableAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Where(entry => entry.Address.AddressFamily == AddressFamily.InterNetwork)
        .Select(entry => entry.Address.ToString()).Where(address => !address.StartsWith("169.254.")).Distinct();

    public async Task<(bool ok, string message)> StartAsync(CancellationToken cancellation = default)
    {
        if (!await Maintenance.WaitAsync(0, cancellation)) return (false, "有维护任务正在进行，请等待完成。");
        try { using var operationLock = AcquireOperationLock(); return await StartCoreAsync(cancellation); }
        catch (IOException) { return (false, "另一个 dsh++ 进程正在维护服务。"); }
        finally { Maintenance.Release(); }
    }

    internal async Task<(bool ok, string message)> StartCoreAsync(CancellationToken cancellation = default)
    {
        IsStarting = true;
        LastFailure = "";
        int startedPid = 0;
        int startedHostPid = 0;
        try
        {
            if (!File.Exists(BinJs)) throw new FileNotFoundException("找不到 DSH，请检查安装目录。", BinJs);
            if (IsDshRunning()) throw new InvalidOperationException($"端口 {Cfg.Port} 已被占用，请先检查当前服务。");
            var bind = ResolveBindHost();
            EnsureRemotePatch();
            var arguments = new List<string> { BinJs, "--profile", "web", "--patch", RemotePatch,
                "--host", "127.0.0.1", "--port", Cfg.Port.ToString(), "--no-open" };
            var publicHost = DisplayHost();
            if (!IPAddress.TryParse(publicHost, out _))
            {
                if (Uri.CheckHostName(publicHost) != UriHostNameType.Dns)
                    throw new InvalidOperationException("自定义域名格式无效，请填写域名，不包含端口或路径。");
                arguments.AddRange(new[] { "--trusted-host", publicHost });
            }
            if (bind != "127.0.0.1" && !Cfg.RequirePairing)
                arguments.AddRange(new[] { "--trusted-host", bind });
            if (Cfg.BrowserCompatibilityPatch) FrontendPatch.Ensure(Cfg, Log);
            Directory.CreateDirectory(DshLogDir);
            File.WriteAllText(DshLogPath, "");
            logOffset = 0;
            var planPath = Path.Combine(AppConfig.ConfigDir, "runtime-plan.json");
            var plan = new
            {
                arguments, directory = Cfg.DshAppDir, log = DshLogPath, state = RuntimeStatePath, bin = BinJs,
                bind, port = Cfg.Port, sessionRoot = Path.Combine(AppConfig.ConfigDir, "runtime-sessions"),
                restartGraceSeconds = Cfg.StartupTimeoutSeconds,
                environment = new Dictionary<string, string> { ["DSH_HOME"] = HomeBase, ["DSH_AGENTS_HOME"] = AgentsBase },
            };
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
            var hostScript = BundledAssets.Get("scripts/runtime-host.mjs");
            var launch = new ProcessStartInfo("node") { WorkingDirectory = Cfg.DshAppDir, UseShellExecute = false, CreateNoWindow = true };
            launch.ArgumentList.Add(hostScript);
            launch.ArgumentList.Add(planPath);
            if (File.Exists(RuntimeStatePath)) File.Delete(RuntimeStatePath);
            using var host = Process.Start(launch);
            startedHostPid = host.Id;
            var launchDeadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(RuntimeStatePath))
            {
                if (host.HasExited || DateTime.UtcNow > launchDeadline) throw new InvalidOperationException("启动器未能创建服务状态。" + StartupFailure());
                await Task.Delay(100, cancellation);
            }
            using (var state = JsonDocument.Parse(File.ReadAllText(RuntimeStatePath)))
                startedPid = state.RootElement.GetProperty("pid").GetInt32();
            Log($"正在启动 DSH，地址 {bind}:{Cfg.Port}，PID {startedPid}。");
            using var process = Process.GetProcessById(startedPid);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(Cfg.StartupTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException(StartupFailure());
                if (GetPidOnPort(Cfg.Port) == startedPid)
                {
                    try
                    {
                        using var response = await client.GetAsync(AuthenticatedUrl(), cancellation);
                        var relayReady = bind == "127.0.0.1";
                        if (!relayReady)
                        {
                            using var state = JsonDocument.Parse(File.ReadAllText(RuntimeStatePath));
                            relayReady = state.RootElement.TryGetProperty("relayReady", out var ready) && ready.GetBoolean();
                        }
                        if (response.IsSuccessStatusCode && relayReady)
                        {
                            Log("DSH 已就绪：" + LocalUrl());
                            return (true, "服务已就绪");
                        }
                    }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) when (!cancellation.IsCancellationRequested) { }
                }
                await Task.Delay(500, cancellation);
            }
            throw new TimeoutException("服务未在规定时间内就绪。" + Environment.NewLine + StartupFailure());
        }
        catch (Exception exception)
        {
            var failedSession = ReadRuntimeSession();
            if (startedHostPid != 0 && failedSession != null) File.WriteAllText(Path.Combine(failedSession, "stop"), "stop");
            if (startedHostPid != 0)
            {
                try
                {
                    using var failedHost = Process.GetProcessById(startedHostPid);
                    if (!failedHost.HasExited) { failedHost.Kill(true); await failedHost.WaitForExitAsync(); }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (startedPid != 0)
            {
                try
                {
                    using var failed = Process.GetProcessById(startedPid);
                    if (!failed.HasExited) { failed.Kill(true); await failed.WaitForExitAsync(); }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            LastFailure = CommandRunner.Redact(exception.Message);
            Log("启动失败：" + LastFailure);
            return (false, LastFailure);
        }
        finally { IsStarting = false; }
    }

    public async Task<(bool ok, string message)> StopAsync()
    {
        if (!await Maintenance.WaitAsync(0)) return (false, "有维护任务正在进行。");
        try { using var operationLock = AcquireOperationLock(); return await StopCoreAsync(); }
        catch (IOException) { return (false, "另一个 dsh++ 进程正在维护服务。"); }
        finally { Maintenance.Release(); }
    }

    public async Task<(bool ok, string message)> RestartAsync(CancellationToken cancellation = default)
    {
        if (!await Maintenance.WaitAsync(0, cancellation)) return (false, "有维护任务正在进行。");
        try
        {
            using var operationLock = AcquireOperationLock();
            var stop = await StopCoreAsync();
            return stop.ok ? await StartCoreAsync(cancellation) : stop;
        }
        catch (IOException) { return (false, "另一个 dsh++ 进程正在维护服务。"); }
        finally { Maintenance.Release(); }
    }

    internal async Task<(bool ok, string message)> StopCoreAsync()
    {
        var pid = GetPidOnPort(Cfg.Port);
        var session = ReadRuntimeSession();
        if (pid == 0 && session == null) return (true, "服务已停止");
        if (pid != 0 && !await OwnsProcessAsync(pid) && (session == null || !RuntimeHostAlive()))
            return (false, $"端口 {Cfg.Port} 由其他进程占用（PID {pid}），无法作为 DSH 停止。");
        try
        {
            // The launch-scoped stop marker also rejects late detached restart helpers.
            if (session != null)
            {
                var backendPid = RecordedPid();
                var owned = backendPid != 0 && await OwnsProcessAsync(backendPid);
                File.WriteAllText(Path.Combine(session, "stop"), "stop");
                if (owned)
                {
                    try
                    {
                        using var backend = Process.GetProcessById(backendPid);
                        if (!backend.HasExited) backend.Kill(true);
                        await backend.WaitForExitAsync();
                    }
                    catch (ArgumentException) { }
                }
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < deadline && RuntimeHostAlive()) await Task.Delay(100);
                if (RuntimeHostAlive()) throw new TimeoutException("启动器未确认停止，请检查诊断日志。");
            }
            else if (pid != 0)
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(true);
                await process.WaitForExitAsync();
            }
            Log("DSH 已停止。");
            return (true, "服务已停止");
        }
        catch (ArgumentException) { return (true, "服务已退出"); }
        catch (Exception exception) { return (false, exception.Message); }
    }

    string ReadRuntimeSession()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(RuntimeStatePath));
            var root = document.RootElement;
            if (root.GetProperty("bin").GetString() != BinJs || !root.TryGetProperty("session", out var value)) return null;
            var session = Path.GetFullPath(value.GetString());
            var allowed = Path.GetFullPath(Path.Combine(AppConfig.ConfigDir, "runtime-sessions")) + Path.DirectorySeparatorChar;
            return session.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(session) ? session : null;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException) { return null; }
    }

    bool RuntimeHostAlive()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(RuntimeStatePath));
            using var process = Process.GetProcessById(document.RootElement.GetProperty("hostPid").GetInt32());
            return !process.HasExited;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    public string RuntimePhase()
    {
        if (!RuntimeHostAlive()) return "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(RuntimeStatePath));
            return document.RootElement.TryGetProperty("status", out var status) ? status.GetString() : "";
        }
        catch (Exception error) when (error is IOException or JsonException) { return ""; }
    }

    public async Task<bool> OwnsProcessAsync(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(RuntimeStatePath))
            {
                using var state = JsonDocument.Parse(File.ReadAllText(RuntimeStatePath));
                if (state.RootElement.GetProperty("pid").GetInt32() == pid
                    && state.RootElement.GetProperty("bin").GetString() == BinJs
                    && Math.Abs((process.StartTime.ToUniversalTime() - state.RootElement.GetProperty("started").GetDateTime()).TotalSeconds) < 10)
                    return true;
            }
            var probe = CommandRunner.Create("powershell.exe", new[] { "-NoProfile", "-Command",
                $"(Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}').CommandLine" }, Cfg.DshAppDir);
            var result = await CommandRunner.RunAsync(probe, TimeSpan.FromSeconds(10));
            return result.Ok && result.Output.Replace('/', '\\').Contains(BinJs, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or JsonException)
        { return false; }
    }

    public string AuthenticatedUrl()
    {
        var url = LocalUrl();
        if (!File.Exists(DshLogPath)) return url;
        var match = Regex.Matches(ReadSharedLog(true) + ReadSharedLog(), @"[?&]token=([^\s]+)").LastOrDefault();
        return match == null ? url : url + "/?token=" + Uri.EscapeDataString(match.Groups[1].Value);
    }

    string ReadSharedLog(bool beginning = false)
    {
        using var stream = new FileStream(DshLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!beginning && stream.Length > 100000) stream.Seek(-100000, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        if (!beginning) return reader.ReadToEnd();
        var buffer = new char[64000];
        return new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
    }

    public string StartupFailure()
    {
        if (!File.Exists(DshLogPath)) return "服务没有产生日志，请检查 Node 安装。";
        var text = ReadSharedLog();
        if (text.Contains("unspecified (wildcard)")) return "DSH 拒绝全网卡绑定，请在连接设置中选择具体网卡地址。";
        if (text.Contains("EADDRINUSE")) return "端口已被其他进程占用。";
        return CommandRunner.Redact(string.Join(Environment.NewLine, text.Split('\n').TakeLast(24)));
    }
}
