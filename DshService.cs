using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DshControl;

public enum Tone { Gray, Green, Orange }

public class StorageRowInfo
{
    public string Name = "";
    public string Desc = "";
    public string Path = "";
    public string Sub = "";
    public string Tag = "";
    public string Kind = "";         // "dsh" | "agents"（可修改的根目录行）
    public bool IsFile;
    public bool Informational;
    public bool CanEdit;
}

public class TsUiState
{
    public Tone Tone;
    public string CkText = "";
    public string Detail = "";
    public string FixLabel = "";
    public string FixAction = "";    // install | login | up | down
}

/// <summary>
/// dsh++ 的全部控制逻辑（与界面无关）：dsh 启停、端口/绑定检测、
/// 连接方式计算、remote patch 生成、Tailscale 操作、存储位置清单、日志尾随。
/// </summary>
public class DshService : IDisposable
{
    public readonly AppConfig Cfg;
    readonly Action<string> log;
    FileSystemWatcher logWatcher;
    long logOffset;

    string DshLogDir => Path.Combine(AppConfig.ConfigDir, "logs");
    string DshLogPath => Path.Combine(DshLogDir, "dsh-web.log");
    string BinJs => Path.Combine(Cfg.DshAppDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
    string LanPatch => Path.Combine(Cfg.DshAppDir, "lan.patch.yml");
    string RemotePatch => Path.Combine(AppConfig.ConfigDir, "remote-auto.patch.yml");
    string DownloadsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public DshService(AppConfig cfg, Action<string> log = null)
    {
        Cfg = cfg;
        this.log = log;
    }

    void Log(string m)
    {
        try { log?.Invoke(m); } catch { }
    }

    static string ExpandHome(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return p;
        p = p.Trim();
        if (p == "~") return UserProfile;
        if (p.StartsWith("~/") || p.StartsWith("~\\")) return Path.Combine(UserProfile, p.Substring(2));
        return p;
    }

    public string HomeBase => string.IsNullOrWhiteSpace(Cfg.DshHome)
        ? Path.Combine(UserProfile, ".dsh")
        : Path.GetFullPath(ExpandHome(Cfg.DshHome));

    public string AgentsBase => string.IsNullOrWhiteSpace(Cfg.AgentsHome)
        ? Path.Combine(UserProfile, ".agents")
        : Path.GetFullPath(ExpandHome(Cfg.AgentsHome));

    // ---------------------------------------------------------------- 状态检测

    public bool IsDshRunning() => GetPidOnPort(Cfg.Port) > 0;

    public string GetBindAddress(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            string fallback = null;
            foreach (var line in outp.Split('\n'))
            {
                var m = Regex.Match(line, $@"\s*TCP\s+(\[[^\]]+\]|\S+):({port})\s+\S+\s+LISTENING\s+(\d+)");
                if (!m.Success) continue;
                var b = m.Groups[1].Value;
                if (b == "0.0.0.0" || b == "127.0.0.1") return b;
                if (fallback == null && b.StartsWith("[")) fallback = "::1";
            }
            return fallback;
        }
        catch { }
        return null;
    }

    public int GetPidOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            foreach (var line in outp.Split('\n'))
            {
                var m = Regex.Match(line, $@"\s*TCP\s+\S+:({port})\s+\S+\s+LISTENING\s+(\d+)");
                if (m.Success && int.TryParse(m.Groups[2].Value, out var pid)) return pid;
            }
        }
        catch { }
        return 0;
    }

    public string DetectLanIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                var ip = props.UnicastAddresses.FirstOrDefault(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !a.Address.ToString().StartsWith("169.254."));
                if (ip != null) return ip.Address.ToString();
            }
        }
        catch { }
        return null;
    }

    bool IsIpLiteral(string s) => IPAddress.TryParse(s, out _);

    public string DisplayHost()
    {
        if (Cfg.Mode == "local") return "127.0.0.1";
        switch (Cfg.AccessMode)
        {
            case "domain":
                var d = Cfg.DisplayHost?.Trim();
                return string.IsNullOrEmpty(d) ? (DetectLanIp() ?? "127.0.0.1") : d;
            case "tailscale":
                var st = TailscaleCli.Status(Cfg);
                var dns = st.DnsName?.Trim().TrimEnd('.');
                if (!string.IsNullOrEmpty(dns) && dns.Contains(".")) return dns;
                if (st.Ips.Count > 0) return st.Ips[0];
                return DetectLanIp() ?? "127.0.0.1";
            default:
                return DetectLanIp() ?? "127.0.0.1";
        }
    }

    public string LanUrl() => $"http://{DisplayHost()}:{Cfg.Port}";
    public string LocalUrl() => $"http://127.0.0.1:{Cfg.Port}";

    // ---------------------------------------------------------------- dsh 控制

    void EnsureRemotePatch()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDir);
            var sb = new StringBuilder();
            sb.AppendLine("# dsh-control generated remote-web-ui overlay (applied last via --patch)");
            sb.AppendLine("- id: remote-web-ui");
            sb.AppendLine("  config:");
            sb.AppendLine($"    autoTunnel: {(Cfg.AutoTunnel ? "true" : "false")}");
            sb.AppendLine($"    requirePairingForLan: {(Cfg.RequirePairing ? "true" : "false")}");
            var host = DisplayHost();
            if (!Cfg.AutoTunnel && !string.IsNullOrEmpty(host) && !IsIpLiteral(host) && Cfg.AccessMode != "ip")
                sb.AppendLine($"    publicBaseUrl: http://{host}:{Cfg.Port}");
            File.WriteAllText(RemotePatch, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log("生成 remote patch 失败: " + ex.Message);
        }
    }

    public string BuildDshCommand()
    {
        var sb = new StringBuilder();
        sb.Append($"node \"{BinJs}\" web");
        if (Cfg.Mode == "lan" && File.Exists(LanPatch)) sb.Append($" --patch \"{LanPatch}\"");
        EnsureRemotePatch();
        sb.Append($" --patch \"{RemotePatch}\"");
        sb.Append($" --port {Cfg.Port}");
        var host = DisplayHost();
        if (!string.IsNullOrEmpty(host) && !IsIpLiteral(host)) sb.Append($" --trusted-host {host}");
        return sb.ToString();
    }

    /// <summary>启动 dsh；返回 (是否已发起, 给用户看的提示信息)。</summary>
    public (bool ok, string message) StartDsh()
    {
        if (!File.Exists(BinJs))
            return (false, $"找不到 dsh 可执行文件：\n{BinJs}\n\n请检查 dsh-app 目录是否正确（config.json 里的 DshAppDir）。");
        if (IsDshRunning())
            return (false, $"端口 {Cfg.Port} 已有 dsh 在运行，你改的配置还没有生效。\n\n" +
                "要应用新配置（端口 / 连接方式 / 域名 / 存储位置），请：\n" +
                "1. 先点「停止服务」\n" +
                "2. 等状态变灰\n" +
                "3. 再点「▶ 启动服务」\n\n" +
                "（停止服务不会丢失数据）");
        try
        {
            FrontendPatch.Ensure(Cfg, Log);
            Directory.CreateDirectory(DshLogDir);
            var cmd = BuildDshCommand();
            Log("启动命令: " + cmd);
            Log("（日志写入 " + DshLogPath + "）");
            var psi = new ProcessStartInfo("cmd.exe", $"/c {cmd} > \"{DshLogPath}\" 2>&1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Cfg.DshAppDir,
            };
            psi.Environment["DSH_HOME"] = HomeBase;
            psi.Environment["DSH_AGENTS_HOME"] = AgentsBase;
            var dshProcess = Process.Start(psi);
            logOffset = File.Exists(DshLogPath) ? new FileInfo(DshLogPath).Length : 0;
            Log($"存储根: {HomeBase}");
            Log("已发起启动 (PID " + dshProcess.Id + ")，几秒后生效…");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, "启动失败: " + ex.Message);
        }
    }

    public void StopDsh()
    {
        var pid = GetPidOnPort(Cfg.Port);
        if (pid > 0)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                Log($"正在停止 dsh (PID {pid})…");
                p.Kill(true);
                Log("已发送停止指令。");
            }
            catch (Exception ex)
            {
                Log("停止失败: " + ex.Message);
            }
        }
        else
        {
            Log($"端口 {Cfg.Port} 上没有在运行的 dsh。");
        }
    }

    public void OpenUi()
    {
        if (!IsDshRunning()) { Log("dsh 尚未运行，先点「▶ 启动服务」。"); return; }
        try { Process.Start(new ProcessStartInfo(LocalUrl()) { UseShellExecute = true }); }
        catch (Exception ex) { Log("打开失败: " + ex.Message); }
    }

    public void OpenDir(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("打开目录失败: " + ex.Message); }
    }

    public void AddFirewallRule()
    {
        RunElevated($"netsh advfirewall firewall add rule name=\"dsh web {Cfg.Port}\" dir=in action=allow protocol=TCP localport={Cfg.Port}");
    }

    public void RunElevated(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                Verb = "RunAs",
                UseShellExecute = true,
            };
            Process.Start(psi);
            Log("已请求管理员执行（若弹出 UAC 请点「是」）。");
        }
        catch (Exception ex)
        {
            Log("未执行（可能取消了 UAC）: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- Tailscale

    public TsUiState GetTsState()
    {
        var u = new TsUiState();
        var st = TailscaleCli.Exists(Cfg) ? TailscaleCli.Status(Cfg) : new TsStatus { Error = "未安装" };
        if (st.Error == "未安装")
        {
            u.Tone = Tone.Orange;
            u.CkText = "✗ Tailscale 未安装";
            u.Detail = "电脑和手机都装 Tailscale 才能在任何网络互连";
            u.FixLabel = "安装 Tailscale";
            u.FixAction = "install";
            return u;
        }
        if (st.BackendState == "NeedsLogin")
        {
            u.Tone = Tone.Orange;
            u.CkText = "✗ Tailscale 需要登录";
            u.Detail = "点「登录」在浏览器里授权（用同一个账号登录手机和电脑）";
            u.FixLabel = "登录";
            u.FixAction = "login";
            return u;
        }
        var running = st.BackendState == "Running";
        if (running && st.Ips.Count > 0)
        {
            u.Tone = Tone.Green;
            u.CkText = "✓ Tailscale 已连接";
            u.Detail = $"IP：{string.Join(", ", st.Ips)}　域名：{st.DnsName?.TrimEnd('.')}";
            u.FixLabel = "断开";
            u.FixAction = "down";
        }
        else
        {
            u.Tone = Tone.Orange;
            u.CkText = "✗ Tailscale 未连接";
            u.Detail = "点「连接」上线";
            u.FixLabel = "连接";
            u.FixAction = "up";
        }
        return u;
    }

    public void StartTsAsync(string args)
    {
        if (!TailscaleCli.Exists(Cfg)) { Log("Tailscale 未安装。"); return; }
        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(Cfg.TailscalePath, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                p.OutputDataReceived += (s, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    Log("[tailscale] " + e.Data);
                    var m = Regex.Match(e.Data, @"https://\S+");
                    if (m.Success && m.Value.Contains("tailscale"))
                    {
                        var url = m.Value.TrimEnd('.', ',', ')');
                        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                        catch { }
                    }
                };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log("[tailscale] " + e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                Log($"[tailscale] 退出码 {p.ExitCode}");
            }
            catch (Exception ex)
            {
                Log("tailscale 执行失败: " + ex.Message);
            }
        });
    }

    public void TsDown()
    {
        var r = TailscaleCli.Run(Cfg, "down");
        Log("[tailscale] down → " + (r.output.Length > 120 ? r.output.Substring(0, 120) : r.output));
    }

    public void TsInstall()
    {
        try
        {
            var msi = Path.Combine(DownloadsDir, "tailscale-setup-1.102.2-amd64.msi");
            if (!File.Exists(msi))
            {
                Log("下载 Tailscale Windows 安装包…");
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var bytes = client.GetByteArrayAsync("https://pkgs.tailscale.com/stable/tailscale-setup-1.102.2-amd64.msi").GetAwaiter().GetResult();
                File.WriteAllBytes(msi, bytes);
                Log("下载完成: " + msi);
            }
            Log("启动安装程序（若弹出 UAC 请点「是」，按提示完成安装）…");
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("安装启动失败: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 存储位置

    public List<StorageRowInfo> StorageRows()
    {
        return new List<StorageRowInfo>
        {
            new() { Name = "DSH 主目录（总根）", Desc = "所有用户数据的总根：设置、会话、技能、配置都在它下面；改这里 = 全部搬走", Path = HomeBase, CanEdit = true, Kind = "dsh" },
            new() { Name = "设置 settings.yaml", Desc = "界面、模型、权限等个人设置", Path = Path.Combine(HomeBase, "settings.yaml"), IsFile = true },
            new() { Name = "凭据 .credentials.yaml", Desc = "API Key 等敏感信息", Path = Path.Combine(HomeBase, ".credentials.yaml"), IsFile = true },
            new() { Name = "会话记录 sessions", Desc = "对话历史（按工作区分目录存放）", Path = Path.Combine(HomeBase, "sessions") },
            new() { Name = "数据存储 storages", Desc = "工作区注册表等内部数据", Path = Path.Combine(HomeBase, "storages") },
            new() { Name = "配置与插件 profiles", Desc = "dsh 配置与插件；MCP 服务器在 cordis.patch.yml 里定义", Path = Path.Combine(HomeBase, "profiles", "web", "cordis.patch.yml"), IsFile = true, Tag = "MCP 在这里" },
            new() { Name = "技能（DSH）skills", Desc = "你自己的技能包（SKILL.md 目录）放这里", Path = Path.Combine(HomeBase, "skills"), Tag = "skill 在这里" },
            new() { Name = "技能（Agents）目录", Desc = "通用技能目录（apple-design 等技能就在这里）；用 DSH_AGENTS_HOME 可改", Path = AgentsBase, CanEdit = true, Kind = "agents", Sub = "skills" },
            new() { Name = "技能（项目级）", Desc = "每个工作区目录下的 .dsh\\skills 与 .agents\\skills（随项目走）", Path = "", Informational = true },
            new() { Name = "Agent 预设", Desc = "自定义 agent 预设", Path = Path.Combine(HomeBase, ".agent-presets") },
        };
    }

    // ---------------------------------------------------------------- 日志尾随

    public void StartLogTail(Action<string> onChunk)
    {
        try
        {
            Directory.CreateDirectory(DshLogDir);
            logWatcher = new FileSystemWatcher(DshLogDir, "dsh-web.log")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            logWatcher.Changed += (s, e) => ReadLogTail(onChunk);
            logWatcher.Created += (s, e) => ReadLogTail(onChunk);
            logWatcher.EnableRaisingEvents = true;
            ReadLogTail(onChunk);
        }
        catch (Exception ex)
        {
            Log("日志监听失败: " + ex.Message);
        }
    }

    void ReadLogTail(Action<string> onChunk)
    {
        try
        {
            if (!File.Exists(DshLogPath)) return;
            var fi = new FileInfo(DshLogPath);
            if (fi.Length <= logOffset) { logOffset = fi.Length; return; }
            using var fs = new FileStream(DshLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(logOffset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            logOffset = fs.Position;
            if (string.IsNullOrEmpty(text)) return;
            onChunk?.Invoke(text);
        }
        catch { }
    }

    public void Dispose()
    {
        try { logWatcher?.Dispose(); } catch { }
    }
}
