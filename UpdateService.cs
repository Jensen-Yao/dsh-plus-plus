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
    public DateTime? CheckedAt { get; set; }
    public bool IsIndirect => !string.IsNullOrEmpty(OwnerName);
    public bool HasUpdate => UpdateService.IsNewer(Latest, Installed)
        || (!string.IsNullOrEmpty(TargetCommit) && !PluginLock.SameCommit(TargetCommit, InstalledCommit));
    public string Source => IsIndirect ? "聚合子包 · " + OwnerName : IsLocal ? "本地链接" : IsGit ? "GitHub / Git" : "npm";
    public bool IsLocal => new[] { "link:", "file:", "workspace:" }.Any(prefix => Spec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    public bool IsGit => Spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase) || Spec.StartsWith("git+", StringComparison.OrdinalIgnoreCase) || Spec.Contains("github.com");
    public bool IsCore => Name.StartsWith("@deepseek-ai/");
    public bool CanUpdate => !IsIndirect && !IsLocal && !IsCore && UpdateService.SpecUpdatable(Spec);
    public string Policy => IsIndirect ? "随入口维护" : IsLocal ? "本地维护" : IsCore ? "跟随内核" : CanUpdate ? "跟随声明" : "固定版本";
    public string Status => !string.IsNullOrEmpty(SourceIssue) ? "来源偏差" : !string.IsNullOrEmpty(CheckError) ? "检查失败"
        : !string.IsNullOrEmpty(Detail) ? Detail : string.IsNullOrEmpty(Installed) ? "未安装" : Policy;
    public string Description => $"{Name}\n声明：{Spec}\n实际来源：{ResolvedSource}\n{SourceIssue}\n{CheckError}"
        + (IsIndirect ? "\n更新入口：" + OwnerName : "")
        + (CheckedAt.HasValue ? $"\n检查时间：{CheckedAt.Value.ToLocalTime():MM-dd HH:mm:ss}" : "\n尚未检查远端");

    internal void CopyCheckFrom(PluginItem previous)
    {
        Latest = previous.Latest;
        Detail = previous.Detail;
        TargetVersion = previous.TargetVersion;
        TargetCommit = previous.TargetCommit;
        CheckedAt = previous.CheckedAt;
        CheckError = previous.CheckError;
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

    public (string latest, string alpha, string err) RemoteDshVersions(CancellationToken cancellation = default)
    {
        try
        {
            var start = CommandRunner.PackageManager("npm", new[] { "view", "@deepseek-ai/dsh", "dist-tags", "--json" }, cfg.DshAppDir);
            var result = Task.Run(() => CommandRunner.RunAsync(start, TimeSpan.FromSeconds(40), cancellation: cancellation)).GetAwaiter().GetResult();
            if (!result.Ok) return ("", "", result.TimedOut ? "版本检查超时，可稍后重试。" : result.Output);
            using var document = JsonDocument.Parse(result.StandardOutput);
            string Tag(string tag) => document.RootElement.TryGetProperty(tag, out var value) ? value.GetString() : "";
            return (Tag("latest"), Tag("alpha") is { Length: > 0 } alpha ? alpha : Tag("next"), null);
        }
        catch (Exception exception) { return ("", "", exception.Message); }
    }

    public static bool IsNewer(string remote, string local)
    {
        if (string.IsNullOrWhiteSpace(remote) || string.IsNullOrWhiteSpace(local)) return false;
        static string Clean(string version) => version.Trim().TrimStart('v', 'V').Split('+')[0];
        var remoteParts = Clean(remote).Split('-', 2);
        var localParts = Clean(local).Split('-', 2);
        if (!Version.TryParse(remoteParts[0], out var remoteCore) || !Version.TryParse(localParts[0], out var localCore)) return false;
        var coreOrder = remoteCore.CompareTo(localCore);
        if (coreOrder != 0) return coreOrder > 0;
        if (remoteParts.Length != localParts.Length) return remoteParts.Length == 1;
        if (remoteParts.Length == 1) return false;
        var remotePre = remoteParts[1].Split('.');
        var localPre = localParts[1].Split('.');
        for (var index = 0; index < Math.Min(remotePre.Length, localPre.Length); index++)
        {
            var remoteNumeric = long.TryParse(remotePre[index], out var remoteNumber);
            var localNumeric = long.TryParse(localPre[index], out var localNumber);
            var order = remoteNumeric && localNumeric ? remoteNumber.CompareTo(localNumber)
                : remoteNumeric != localNumeric ? remoteNumeric ? -1 : 1
                : string.CompareOrdinal(remotePre[index], localPre[index]);
            if (order != 0) return order > 0;
        }
        return remotePre.Length > localPre.Length;
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
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            var tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
            var assets = document.RootElement.GetProperty("assets").EnumerateArray();
            var asset = assets.FirstOrDefault(asset =>
                asset.GetProperty("name").GetString() is string name
                && name.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined) return (tag, null, "没有匹配 Windows x64 的安装包。");
            return (tag, asset.GetProperty("browser_download_url").GetString(), null);
        }
        catch (Exception exception) { return ("", null, exception.Message); }
    }

    public async Task<(bool ok, string msg)> ApplyAppUpdateAsync(string zipUrl)
    {
        if (!Uri.TryCreate(zipUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com"
            || !uri.AbsolutePath.StartsWith("/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            return (false, "更新包地址无效。");
        var executable = Environment.ProcessPath;
        var script = BundledAssets.Get("scripts/apply-update.ps1");
        if (executable == null || !File.Exists(script)) return (false, "更新脚本不完整，请使用完整发布包。");
        var work = Path.Combine(AppConfig.ConfigDir, "app-updates", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, "release.zip");
            using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var destination = File.Create(archive);
                await response.Content.CopyToAsync(destination);
            }
            var staged = Path.Combine(work, "staged");
            ZipFile.ExtractToDirectory(archive, staged);
            var required = new[] { "dsh-plus-plus.exe", "compat-polyfill.js", "scripts/runtime-host.mjs", "scripts/apply-update.ps1" };
            if (required.Any(file => !File.Exists(Path.Combine(staged, file))))
                return (false, "更新包缺少必需文件，未替换当前版本。");
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
        catch (Exception exception) { return (false, "更新未完成：" + exception.Message); }
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

    static string InstalledVersion(string profile, string package)
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
}
