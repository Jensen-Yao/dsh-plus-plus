using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DshControl;

public class PluginItem
{
    public string Name = "";
    public string Spec = "";
    public string Installed = "";
}

public class ProfileInfo
{
    public string Name = "";
    public string Dir = "";
    public List<PluginItem> Plugins = new();
}

/// <summary>
/// 更新中心逻辑（与界面无关）：
/// 1. dsh（@deepseek-ai/dsh npm 包）版本检查与更新；
/// 2. dsh++ 自身从 GitHub Release 检查与一键自更新；
/// 3. dsh profiles 里已安装插件的批量更新（pnpm）。
/// </summary>
public class UpdateService
{
    readonly AppConfig cfg;
    readonly DshService svc;
    readonly Action<string> log;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    const string Repo = "Jensen-Yao/dsh-plus-plus";

    public UpdateService(AppConfig cfg, DshService svc, Action<string> log = null)
    {
        this.cfg = cfg;
        this.svc = svc;
        this.log = log;
    }

    void Log(string m)
    {
        try { log?.Invoke(m); } catch { }
    }

    static (int code, string output) RunCapture(string file, string args, string cwd, int timeoutMs)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Path.GetTempPath(),
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "无法启动进程");
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "命令超时");
            }
            Task.WaitAll(new Task[] { so, se }, 3000);
            var text = (so.Result + Environment.NewLine + se.Result).Trim();
            return (p.ExitCode, text);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    // ·························· dsh（npm 包） ··························

    string DshPkgJson => Path.Combine(cfg.DshAppDir, "node_modules", "@deepseek-ai", "dsh", "package.json");

    /// <summary>读本地已安装的 dsh 版本（纯文件读取，离线可用）。</summary>
    public (string ver, string err) LocalDshVersion()
    {
        try
        {
            if (!File.Exists(DshPkgJson)) return ("", "找不到 " + DshPkgJson);
            using var doc = JsonDocument.Parse(File.ReadAllText(DshPkgJson));
            return (doc.RootElement.GetProperty("version").GetString() ?? "", null);
        }
        catch (Exception ex) { return ("", ex.Message); }
    }

    /// <summary>查 npm dist-tags：返回 (latest 正式版, alpha 抢先体验版, 错误)。跟随用户 npm 镜像配置。</summary>
    public (string latest, string alpha, string err) RemoteDshVersions()
    {
        var (code, output) = RunCapture("cmd.exe", "/c npm view @deepseek-ai/dsh dist-tags --json", cfg.DshAppDir, 60000);
        if (code != 0) return ("", "", string.IsNullOrWhiteSpace(output) ? "npm view 失败" : output.Split('\n').FirstOrDefault()?.Trim());
        try
        {
            // npm 可能把警告写到 stderr，混在输出里——只截取 JSON 子串解析
            var start = output.IndexOf('{');
            var end = output.LastIndexOf('}');
            if (start < 0 || end <= start)
                return ("", "", "npm 输出中没有 JSON：" + output.Split('\n').FirstOrDefault()?.Trim());
            using var doc = JsonDocument.Parse(output.Substring(start, end - start + 1));
            string Get(string key) =>
                doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("latest") ?? "", Get("alpha") ?? Get("next") ?? "", null);
        }
        catch (Exception ex)
        {
            return ("", "", "解析 dist-tags 失败：" + ex.Message);
        }
    }

    public static bool IsNewer(string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(local)) return false;
        return string.CompareOrdinal(remote.Trim(), local.Trim()) != 0
            && CompareSemver(remote, local) > 0;
    }

    static int CompareSemver(string a, string b)
    {
        var pa = ParseSemver(a);
        var pb = ParseSemver(b);
        for (var i = 0; i < 3; i++)
            if (pa[i] != pb[i]) return pa[i].CompareTo(pb[i]);
        // 主次补丁相同：预发布版（如 0.1.2-rc.1）小于正式版
        var preA = HasPrerelease(a);
        var preB = HasPrerelease(b);
        if (preA == preB) return string.CompareOrdinal(a, b);
        return preA ? -1 : 1;
    }

    static int[] ParseSemver(string v)
    {
        var r = new int[3];
        var core = v.Trim().TrimStart('v', 'V').Split('-')[0].Split('.');
        for (var i = 0; i < 3 && i < core.Length; i++)
            _ = int.TryParse(new string(core[i].TakeWhile(char.IsDigit).ToArray()), out r[i]);
        return r;
    }

    static bool HasPrerelease(string v) => v.Trim().TrimStart('v', 'V').Contains('-');

    /// <summary>更新 dsh：spec = "latest"（正式版）或 "alpha"（抢先体验版）。要求 dsh 已停止。</summary>
    public (bool ok, string msg) UpdateDsh(string spec)
    {
        if (svc.IsDshRunning())
            return (false, "dsh 正在运行——请先在「① 服务开关」停止服务，再更新。");
        var label = spec == "alpha" ? "抢先体验版（alpha）" : "正式版（latest）";
        Log($"[更新] npm install @deepseek-ai/dsh@{spec}（{label}，可能需要几分钟）…");
        var (code, output) = RunCapture("cmd.exe", $"/c npm install @deepseek-ai/dsh@{spec}", cfg.DshAppDir, 900000);
        if (output.Length > 1500) output = output.Substring(output.Length - 1500);
        foreach (var line in output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(12))
            Log("    " + line.TrimEnd());
        if (code != 0)
            return (false, "npm install 失败（详见运行日志）。检查网络后重试。");
        var (ver, err) = LocalDshVersion();
        if (err != null) return (false, "更新后读取版本失败：" + err);
        Log("[更新] dsh 已更新到 " + ver);
        return (true, $"dsh 已更新到 {ver}（{label}）。兼容补丁会自动重新注入；点「启动服务」即可。");
    }

    // ·························· dsh++ 自身 ··························

    public static string AppVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            var attr = asm?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>();
            if (!string.IsNullOrWhiteSpace(attr?.InformationalVersion))
                return attr.InformationalVersion.Split('+')[0];
        }
        catch { }
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe))
                return FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        }
        catch { }
        return "?";
    }

    /// <summary>查 GitHub 最新 Release：返回 (tag, zip 下载地址, 错误)。</summary>
    public async Task<(string tag, string zipUrl, string err)> LatestAppReleaseAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("dsh-plus-plus");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req);
            res.EnsureSuccessStatusCode();
            await using var s = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(s);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string zip = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name != null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zip = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            return (tag, zip, zip == null ? "最新 Release 没有 zip 附件" : null);
        }
        catch (Exception ex)
        {
            return ("", null, ex.Message);
        }
    }

    /// <summary>下载新版本 zip，写自替换脚本，返回后由界面关闭进程、脚本完成替换并重启。</summary>
    public async Task<(bool ok, string msg)> ApplyAppUpdateAsync(string zipUrl)
    {
        var exePath = Environment.ProcessPath;
        if (exePath == null || !Path.GetFileName(exePath).Equals("dsh-plus-plus.exe", StringComparison.OrdinalIgnoreCase))
            return (false, "仅发布版（dsh-plus-plus.exe）支持一键更新；开发目录请用源码构建。");
        var appDir = Path.GetDirectoryName(exePath);
        var work = Path.Combine(Path.GetTempPath(), "dshpp-update-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, "update.zip");
            Log("[自更新] 正在下载 " + zipUrl);
            using (var res = await http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await res.Content.CopyToAsync(fs);
            }
            ZipFile.ExtractToDirectory(zipPath, work);
            if (!File.Exists(Path.Combine(work, "dsh-plus-plus.exe")))
                return (false, "更新包内容不符合预期，已取消。");
            var pid = Environment.ProcessId;
            var bat = Path.Combine(Path.GetTempPath(), "dshpp-update-" + Guid.NewGuid().ToString("N")[..6] + ".bat");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine(":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)");
            sb.AppendLine($"copy /y \"{Path.Combine(work, "dsh-plus-plus.exe")}\" \"{exePath}\" >nul");
            var poly = Path.Combine(work, "compat-polyfill.js");
            if (File.Exists(poly))
                sb.AppendLine($"copy /y \"{poly}\" \"{Path.Combine(appDir ?? ".", "compat-polyfill.js")}\" >nul");
            sb.AppendLine($"start \"\" \"{exePath}\"");
            sb.AppendLine($"rd /s /q \"{work}\"");
            sb.AppendLine("del \"%~f0\"");
            File.WriteAllText(bat, sb.ToString(), new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Log("[自更新] 替换脚本已启动，dsh++ 将自动重启。");
            return (true, "更新包已就绪，dsh++ 即将自动重启完成更新。");
        }
        catch (Exception ex)
        {
            try { Directory.Delete(work, true); } catch { }
            return (false, "下载或解压失败：" + ex.Message);
        }
    }

    // ·························· dsh 插件（profiles） ··························

    string ProfilesRoot => Path.Combine(svc.HomeBase, "profiles");

    /// <summary>列出 profiles 里装了插件（dependencies 非空）的 profile 及每个插件的已装版本。</summary>
    public List<ProfileInfo> Profiles()
    {
        var list = new List<ProfileInfo>();
        var root = ProfilesRoot;
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var pj = Path.Combine(dir, "package.json");
            if (!File.Exists(pj)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pj));
                if (!doc.RootElement.TryGetProperty("dependencies", out var deps)
                    || deps.ValueKind != JsonValueKind.Object) continue;
                var info = new ProfileInfo { Name = Path.GetFileName(dir), Dir = dir };
                foreach (var d in deps.EnumerateObject())
                {
                    if (d.Value.ValueKind != JsonValueKind.String) continue;
                    info.Plugins.Add(new PluginItem
                    {
                        Name = d.Name,
                        Spec = d.Value.GetString() ?? "",
                        Installed = InstalledVersion(dir, d.Name),
                    });
                }
                if (info.Plugins.Count > 0) list.Add(info);
            }
            catch { }
        }
        return list;
    }

    static string InstalledVersion(string profileDir, string pkg)
    {
        var rel = pkg.StartsWith('@') ? pkg.Replace('/', '\\') : pkg;
        var pj = Path.Combine(profileDir, "node_modules", rel, "package.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pj));
            return doc.RootElement.GetProperty("version").GetString() ?? "";
        }
        catch { return ""; }
    }

    public string PluginSummary()
    {
        var profiles = Profiles();
        if (profiles.Count == 0) return "未发现已安装插件的 profile";
        var total = profiles.Sum(p => p.Plugins.Count);
        return $"{profiles.Count} 个 profile，共 {total} 个插件：" +
               string.Join("；", profiles.Select(p => $"{p.Name}({p.Plugins.Count})"));
    }

    /// <summary>生成插件版本明细（每个 profile 下逐个插件：已装版本 + 依赖声明）。</summary>
    public string PluginVersionsReport()
    {
        var profiles = Profiles();
        if (profiles.Count == 0) return "没有在 profiles 下发现已安装的插件。";
        var sb = new StringBuilder();
        foreach (var p in profiles)
        {
            sb.AppendLine($"【{p.Name}】（{p.Plugins.Count} 个插件）");
            foreach (var it in p.Plugins)
            {
                var installed = string.IsNullOrEmpty(it.Installed) ? "未安装" : it.Installed;
                sb.AppendLine($"    {it.Name}");
                sb.AppendLine($"        已装版本：{installed}    依赖声明：{it.Spec}");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>逐 profile 执行 pnpm update（含 git 插件重新拉取）。要求 dsh 已停止。</summary>
    public (bool ok, string msg) UpdateAllPlugins()
    {
        if (svc.IsDshRunning())
            return (false, "dsh 正在运行——插件更新需要先停止服务，更新完再启动。");
        var profiles = Profiles();
        if (profiles.Count == 0) return (true, "没有找到已安装插件的 profile，无需更新。");
        var (pcode, pout) = RunCapture("cmd.exe", "/c pnpm --version", profiles[0].Dir, 30000);
        if (pcode != 0)
            return (false, "找不到可用的 pnpm（pnpm --version 失败）。请先安装 pnpm：npm i -g pnpm");

        var okCount = 0;
        var fail = new List<string>();
        foreach (var p in profiles)
        {
            Log($"[插件] 更新 profile「{p.Name}」（{p.Plugins.Count} 个插件）…");
            var names = string.Join(" ", p.Plugins.Select(x => Quote(x.Name)));
            var (code, output) = RunCapture("cmd.exe", "/c pnpm update", p.Dir, 600000);
            if (output.Length > 1200) output = output.Substring(output.Length - 1200);
            foreach (var line in output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(10))
                Log("    " + line.TrimEnd());
            if (code != 0) { fail.Add(p.Name); continue; }
            if (!string.IsNullOrWhiteSpace(names))
            {
                Log($"[插件] 重新解析「{p.Name}」的 git / 最新插件…");
                var (code2, output2) = RunCapture("cmd.exe", $"/c pnpm update {names}", p.Dir, 600000);
                if (output2.Length > 800) output2 = output2.Substring(output2.Length - 800);
                foreach (var line in output2.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(8))
                    Log("    " + line.TrimEnd());
                if (code2 != 0) { fail.Add(p.Name + "(重解析)"); continue; }
            }
            okCount++;
        }
        return (fail.Count == 0,
            fail.Count == 0
                ? $"插件更新完成：{okCount} 个 profile 全部成功。重启 dsh 后生效。"
                : $"插件更新完成 {okCount}/{profiles.Count}，失败：{string.Join("、", fail)}（详见运行日志）。");
    }

    static string Quote(string s) =>
        s.Any(c => char.IsWhiteSpace(c)) ? "\"" + s + "\"" : s;
}
