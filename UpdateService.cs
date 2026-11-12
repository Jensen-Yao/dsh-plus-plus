using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshControl;

public class PluginItem
{
    public string Name { get; set; } = "";
    public string Spec { get; set; } = "";
    public string Installed { get; set; } = "";
    public string Profile { get; set; } = "";
    public string ProfileDir { get; set; } = "";
    public string PackageDirectory { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string ResolvedSource { get; set; } = "";
    public string InstalledCommit { get; set; } = "";
    public string SourceIssue { get; set; } = "";
    public bool Selected { get; set; }
    public string Latest { get; set; } = "未检查";
    public string Detail { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string TargetCommit { get; set; } = "";
    public string CheckError { get; set; } = "";
    public bool RemoteIdentityVerified { get; set; }
    public string KernelVersion { get; set; } = "";
    public string TargetKernelRange { get; set; } = "";
    public bool? TargetKernelSatisfied { get; set; }
    public string LatestKernelRange { get; set; } = "";
    public bool? LatestKernelSatisfied { get; set; }
    public DateTime? CheckedAt { get; set; }
    public bool IsIndirect => !string.IsNullOrEmpty(OwnerName);
    public bool HasUpdate => UpdateService.IsNewer(Latest, Installed)
        || (!string.IsNullOrEmpty(TargetCommit) && !PluginLock.SameCommit(TargetCommit, InstalledCommit));
    public string Source => IsIndirect ? "聚合子包 · " + OwnerName : IsLocal ? "本地链接" : IsGit ? "GitHub / Git" : "npm";
    public bool IsLocal => new[] { "link:", "file:", "workspace:" }.Any(prefix => Spec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    public bool IsGit => Spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase) || Spec.StartsWith("git+", StringComparison.OrdinalIgnoreCase) || Spec.Contains("github.com");
    public bool IsCore => Name.StartsWith("@deepseek-ai/");
    public bool CanUpdate => !IsIndirect && !IsLocal && !IsCore && UpdateService.SpecUpdatable(Spec);
    public bool CanAdvance => !IsIndirect && !IsCore && HasUpdate && (!IsLocal || RemoteIdentityVerified)
        && UpdateService.IsExactVersion((Latest ?? "").TrimStart('v', 'V'));
    public string AdvanceTarget => IsGit ? Spec.Split('#')[0] + "#" + Latest : Latest;
    public bool IsPinned => !IsLocal && !IsGit && !IsIndirect && !UpdateService.SpecUpdatable(Spec);
    public bool DeclarationRestricted => !IsLocal && !IsGit && !IsIndirect && !IsPinned && RemoteChecked
        && UpdateService.IsNewer(Latest, Installed) && !UpdateService.IsNewer(TargetVersion, Installed);
    public bool KernelBlocked => TargetKernelSatisfied == false || LatestKernelSatisfied == false;
    bool RemoteChecked => Latest is not ("未检查" or "检查失败" or "npm 未发布");
    public string Policy => IsIndirect ? "随入口维护" : IsLocal ? "本地维护" : IsCore ? "跟随内核" : CanUpdate ? "跟随声明" : "固定版本";
    public string Status => !string.IsNullOrEmpty(SourceIssue) ? "来源偏差" : !string.IsNullOrEmpty(CheckError) ? "检查失败"
        : !string.IsNullOrEmpty(Detail) ? Detail : string.IsNullOrEmpty(Installed) ? "未安装" : Policy;
    public string Description => $"{Name}\n声明：{Spec}\n实际来源：{ResolvedSource}\n{SourceIssue}\n{CheckError}"
        + (IsIndirect ? "\n更新入口：" + OwnerName : "")
        + (CheckedAt.HasValue ? $"\n检查时间：{CheckedAt.Value.ToLocalTime():MM-dd HH:mm:ss}" : "\n尚未检查远端")
        + (RemoteChecked && UpdateService.IsNewer(Latest, Installed) ? "\n远端最新：" + Latest : "")
        + (!IsLocal && !IsGit && !string.IsNullOrEmpty(TargetVersion) ? "\n声明内最高：" + TargetVersion : "")
        + (TargetKernelSatisfied == false ? $"\n声明内目标 {TargetVersion} 要求内核 {TargetKernelRange}，当前内核 {KernelVersion}；先在「版本与备份」升级 DSH 内核再更新。" : "")
        + (LatestKernelSatisfied == false ? $"\n远端最新 {Latest} 要求内核 {LatestKernelRange}，当前内核 {KernelVersion}；升级到最新前需先升级内核。" : "")
        + (DeclarationRestricted ? "\n远端新版超出声明范围，普通更新只安装声明范围内版本；选中后用「升级到最新」切换（可选固定或浮动声明），或用「指定版本」修改声明。" : "")
        + (IsPinned && RemoteChecked && UpdateService.IsNewer(Latest, Installed) && !IsIndirect ? "\n版本已固定，不随批量更新变化；选中后用「升级到最新」改用远端新版，或用「指定版本」改成范围声明。" : "");

    internal void CopyCheckFrom(PluginItem previous)
    {
        Latest = previous.Latest;
        Detail = previous.Detail;
        TargetVersion = previous.TargetVersion;
        TargetCommit = previous.TargetCommit;
        CheckedAt = previous.CheckedAt;
        CheckError = previous.CheckError;
        RemoteIdentityVerified = previous.RemoteIdentityVerified;
        KernelVersion = previous.KernelVersion;
        TargetKernelRange = previous.TargetKernelRange;
        TargetKernelSatisfied = previous.TargetKernelSatisfied;
        LatestKernelRange = previous.LatestKernelRange;
        LatestKernelSatisfied = previous.LatestKernelSatisfied;
    }
}

public class ProfileInfo
{
    public string Name = "";
    public string Dir = "";
    public List<PluginItem> Plugins = new();
}

public partial class UpdateService
{
    readonly AppConfig cfg;
    readonly DshService svc;
    readonly DesktopService desktopService;
    readonly Action<string> log;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    const string Repo = "Jensen-Yao/dsh-plus-plus";
    string ProfilesRoot => Path.Combine(svc.HomeBase, "profiles");

    public UpdateService(AppConfig cfg, DshService svc, DesktopService desktop = null, Action<string> log = null)
    {
        this.cfg = cfg;
        this.svc = svc;
        desktopService = desktop ?? new(cfg, svc);
        this.log = log;
    }

    void Log(string message) => log?.Invoke(CommandRunner.Redact(message));

    public (string ver, string err) LocalDshVersion()
    {
        try
        {
            var manifest = Path.Combine(cfg.DshAppDir, "node_modules", "@deepseek-ai", "dsh", "package.json");
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return (document.RootElement.GetProperty("version").GetString() ?? "", null);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        { return ("", exception.Message); }
    }

    public (string latest, string alpha, string next, string err) RemoteDshVersions(CancellationToken cancellation = default)
    {
        try
        {
            var start = CommandRunner.PackageManager("npm", new[] { "view", "@deepseek-ai/dsh", "dist-tags", "--json" }, cfg.DshAppDir);
            var result = Task.Run(() => CommandRunner.RunAsync(start, TimeSpan.FromSeconds(40), cancellation: cancellation)).GetAwaiter().GetResult();
            if (!result.Ok) return ("", "", "", result.TimedOut ? "版本检查超时，可稍后重试。" : result.Output);
            using var document = JsonDocument.Parse(result.StandardOutput);
            string Tag(string tag) => document.RootElement.TryGetProperty(tag, out var value) ? value.GetString() : "";
            return (Tag("latest"), Tag("alpha"), Tag("next"), null);
        }
        catch (Exception exception) { return ("", "", "", exception.Message); }
    }

    public static bool IsNewer(string remote, string local)
        => !string.IsNullOrWhiteSpace(remote) && !string.IsNullOrWhiteSpace(local) && CompareVersions(remote, local) > 0;

    public static int CompareVersions(string left, string right)
    {
        static string Clean(string version) => (version ?? "").Trim().TrimStart('v', 'V').Split('+')[0];
        var leftParts = Clean(left).Split('-', 2);
        var rightParts = Clean(right).Split('-', 2);
        if (!Version.TryParse(leftParts[0], out var leftCore) || !Version.TryParse(rightParts[0], out var rightCore)) return 0;
        var coreOrder = leftCore.CompareTo(rightCore);
        if (coreOrder != 0) return coreOrder;
        if (leftParts.Length != rightParts.Length) return leftParts.Length == 1 ? 1 : -1;
        if (leftParts.Length == 1) return 0;
        var leftPre = leftParts[1].Split('.');
        var rightPre = rightParts[1].Split('.');
        for (var index = 0; index < Math.Min(leftPre.Length, rightPre.Length); index++)
        {
            var leftNumeric = long.TryParse(leftPre[index], out var leftNumber);
            var rightNumeric = long.TryParse(rightPre[index], out var rightNumber);
            var order = leftNumeric && rightNumeric ? leftNumber.CompareTo(rightNumber)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1
                : string.CompareOrdinal(leftPre[index], rightPre[index]);
            if (order != 0) return order;
        }
        return leftPre.Length > rightPre.Length ? 1 : 0;
    }

    /// <summary>判断版本是否满足 semver 范围；范围无法解析时返回 null（视为未知，不据此拦截）。</summary>
    public static bool? SatisfiesRange(string version, string range)
    {
        if (string.IsNullOrWhiteSpace(range)) return true;
        if (string.IsNullOrWhiteSpace(version)) return null;
        var unknown = false;
        foreach (var alternative in range.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var result = SatisfiesComparators(version, alternative);
            if (result == true) return true;
            if (result == null) unknown = true;
        }
        return unknown ? null : false;
    }

    static bool? SatisfiesComparators(string version, string comparators)
    {
        var parsed = new List<(string Op, string Ver)>();
        var hyphen = Regex.Match(comparators, @"^(?<low>\S+)\s+-\s+(?<high>\S+)$");
        if (hyphen.Success)
        {
            var low = ParseComparator(">=" + hyphen.Groups["low"].Value);
            var high = ParseComparator("<=" + hyphen.Groups["high"].Value);
            if (low == null || high == null) return null;
            parsed.AddRange(low);
            parsed.AddRange(high);
        }
        else
            foreach (var token in comparators.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = ParseComparator(token);
                if (piece == null) return null;
                parsed.AddRange(piece);
            }
        if (parsed.Count == 0) return true;
        if (version.Contains('-') && !parsed.Any(rule => rule.Ver.Contains('-') && SameCore(version, rule.Ver)))
            return false;
        foreach (var (op, ver) in parsed)
        {
            var order = CompareVersions(version, ver);
            var ok = op switch
            {
                ">=" => order >= 0, ">" => order > 0, "<=" => order <= 0, "<" => order < 0, _ => order == 0,
            };
            if (!ok) return false;
        }
        return true;
    }

    static List<(string Op, string Ver)> ParseComparator(string token)
    {
        token = token.Trim();
        if (token.Length == 0 || token is "*" or "x" or "X") return new();
        var match = Regex.Match(token, @"^(?<op>>=?|<=?|\^|~|=)?v?(?<ver>[\dXx*][\w.+*-]*)$");
        if (!match.Success) return null;
        var op = match.Groups["op"].Value;
        var raw = match.Groups["ver"].Value;
        var dash = raw.IndexOf('-');
        var core = (dash >= 0 ? raw[..dash] : raw).Split('.');
        var pre = dash >= 0 ? raw[dash..] : "";
        var numbers = new List<int>();
        var wildcard = -1;
        for (var index = 0; index < core.Length; index++)
        {
            if (core[index] is "x" or "X" or "*") { wildcard = index; break; }
            if (!int.TryParse(core[index], out var number)) return null;
            numbers.Add(number);
        }
        var significant = wildcard >= 0 ? wildcard : numbers.Count;
        if (significant == 0 || significant > 3 || pre.Length > 0 && significant < 3) return null;
        var full = numbers.Concat(Enumerable.Repeat(0, 3 - numbers.Count)).ToList();
        var lower = string.Join(".", full) + pre;
        string Bump(int index)
        {
            var parts = full.ToList();
            parts[index]++;
            for (var rest = index + 1; rest < 3; rest++) parts[rest] = 0;
            return string.Join(".", parts);
        }
        string CaretUpper() => significant switch
        {
            1 => Bump(0),
            2 => full[0] > 0 ? Bump(0) : Bump(1),
            _ => full[0] > 0 ? Bump(0) : full[1] > 0 ? Bump(1) : Bump(2),
        };
        return op switch
        {
            ">=" => new() { (">=", lower) },
            "<" => new() { ("<", lower) },
            ">" or "<=" when significant < 3 => null,
            ">" => new() { (">", lower) },
            "<=" => new() { ("<=", lower) },
            "^" => new() { (">=", lower), ("<", CaretUpper()) },
            "~" => new() { (">=", lower), ("<", significant == 1 ? Bump(0) : Bump(1)) },
            _ when significant == 3 => new() { ("=", lower) },
            _ => new() { (">=", lower), ("<", significant == 1 ? Bump(0) : Bump(1)) },
        };
    }

    static bool SameCore(string left, string right)
    {
        static string Core(string version) => (version ?? "").Trim().TrimStart('v', 'V').Split('+')[0].Split('-')[0];
        return Core(left) == Core(right);
    }

    public static string AppVersion() => Assembly.GetEntryAssembly()
        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public async Task<(string tag, string zipUrl, string err)> LatestAppReleaseAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.UserAgent.ParseAdd("dsh-plus-plus/" + AppVersion());
            using var response = await http.SendAsync(request, cancellation);
            if (!response.IsSuccessStatusCode) return ("", null,
                CommandRunner.DescribeHttpStatus("读取 dsh++ 最新发布", (int)response.StatusCode, response.ReasonPhrase,
                    response.Content.Headers.ContentLength) + $"\n地址：https://api.github.com/repos/{Repo}/releases/latest");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
            var assets = document.RootElement.GetProperty("assets").EnumerateArray();
            var asset = assets.FirstOrDefault(asset =>
                asset.GetProperty("name").GetString() is string name
                && name.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined) return (tag, null,
                "dsh++ 最新发布没有匹配 win-x64.zip 的资产。可用资产：" + string.Join(", ", assets.Select(item => item.GetProperty("name").GetString() ?? "未命名")));
            return (tag, asset.GetProperty("browser_download_url").GetString(), null);
        }
        catch (Exception exception) { return ("", null, "dsh++ 发布检查失败：" + exception.Message); }
    }

    public async Task<(bool ok, string msg)> ApplyAppUpdateAsync(string zipUrl)
    {
        if (!Uri.TryCreate(zipUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
            || !uri.AbsolutePath.StartsWith("/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            return (false, "dsh++ 更新包地址无效：" + (zipUrl ?? "空"));
        var executable = Environment.ProcessPath;
        var script = BundledAssets.Get("scripts/apply-update.ps1");
        if (executable == null) return (false, "无法定位当前 dsh++ 进程，更新未开始。");
        if (!File.Exists(script)) return (false, "更新脚本缺失，请使用完整发布包：" + script);
        var work = Path.Combine(AppConfig.ConfigDir, "app-updates", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, "release.zip");
            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!response.IsSuccessStatusCode) return (false, CommandRunner.DescribeHttpStatus(
                    "下载 dsh++ 更新包", (int)response.StatusCode, response.ReasonPhrase, response.Content.Headers.ContentLength)
                    + "\n地址：" + uri);
                await using var destination = File.Create(archive);
                await response.Content.CopyToAsync(destination);
            }
            var staged = Path.Combine(work, "staged");
            ZipFile.ExtractToDirectory(archive, staged);
            var required = new[] { "dsh-plus-plus.exe", "compat-polyfill.js", "scripts/runtime-host.mjs", "scripts/apply-update.ps1" };
            if (required.Any(file => !File.Exists(Path.Combine(staged, file))))
                return (false, "dsh++ 更新包缺少必需文件，未替换当前版本。缺失："
                    + string.Join(", ", required.Where(file => !File.Exists(Path.Combine(staged, file))))
                    + $"\n压缩包：{archive}\n展开目录：{staged}");
            var stagedScript = Path.Combine(work, "apply-update.ps1");
            File.Copy(script, stagedScript);
            var planPath = Path.Combine(work, "plan.json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId, executable, staged,
                destination = AppContext.BaseDirectory, backup = Path.Combine(work, "previous"), files = required,
            }));
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", stagedScript, "-Plan", planPath }) start.ArgumentList.Add(argument);
            Process.Start(start);
            return (true, "更新包已验证，控制台将重启；旧版本保留在 " + work);
        }
        catch (Exception exception) { return (false, "dsh++ 更新未完成：" + CommandRunner.Redact(exception.Message)); }
    }

    public List<ProfileInfo> Profiles()
    {
        var profiles = new List<ProfileInfo>();
        if (!Directory.Exists(ProfilesRoot)) return profiles;
        foreach (var directory in Directory.GetDirectories(ProfilesRoot).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var manifest = Path.Combine(directory, "package.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!document.RootElement.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object) continue;
                var profile = new ProfileInfo { Name = Path.GetFileName(directory), Dir = directory };
                var snapshot = PluginLock.Read(directory);
                foreach (var dependency in dependencies.EnumerateObject())
                    if (dependency.Value.ValueKind == JsonValueKind.String)
                        profile.Plugins.Add(ReadPlugin(profile, dependency.Name, dependency.Value.GetString() ?? "", snapshot));
                DiscoverBundlePlugins(profile);
                if (profile.Plugins.Count > 0) profiles.Add(profile);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            { Log("读取 profile 失败：" + directory + " · " + exception.Message); }
        }
        return profiles;
    }

    internal static string InstalledVersion(string profile, string package)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "node_modules",
                package.Replace('/', Path.DirectorySeparatorChar), "package.json")));
            return document.RootElement.GetProperty("version").GetString() ?? "";
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return ""; }
    }

    public string PluginSummary()
    {
        var profiles = Profiles();
        return $"{profiles.Count} 个 profile · {profiles.Sum(profile => profile.Plugins.Count)} 个插件";
    }

    public static bool SpecUpdatable(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var value = spec.Trim();
        if (new[] { "file:", "link:", "workspace:" }.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        if (value.StartsWith("github:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
            || value.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var marker = value.IndexOf('#');
            return marker < 0 || !Regex.IsMatch(value[(marker + 1)..], @"^(?:[0-9a-fA-F]{7,40}|v?\d+(?:\.\d+)+.*)$");
        }
        return !IsExactVersion(value.StartsWith("npm:", StringComparison.OrdinalIgnoreCase) ? NpmReference("", value).Spec : value);
    }

    public static string FloatingSpec(string spec, string version)
    {
        if (spec.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            return "npm:" + NpmReference("", spec).Name + "@^" + version;
        return "^" + version;
    }
}
