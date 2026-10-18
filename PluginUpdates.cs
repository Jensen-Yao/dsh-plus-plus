using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshControl;

public record PluginCheckSummary(int Count, int Failed, int Updates, int SourceIssues);
internal record GitPluginReference(string Url, string Ref, string BaseSpec);
internal record PluginUpdatePlan(PluginItem Plugin, string IntentSpec, string InstallSpec, string Version, string Commit,
    string OriginalSpec, string OriginalVersion, string OriginalSource)
{
    public bool NeedsChange => IntentSpec != OriginalSpec || !string.IsNullOrEmpty(Plugin.SourceIssue)
        || (Commit.Length > 0 ? !PluginLock.SameCommit(Commit, Plugin.InstalledCommit) : Version != OriginalVersion);
}

public partial class UpdateService
{
    internal static bool IsExactVersion(string value) => Regex.IsMatch(value, @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$");

    static (string Name, string Spec) NpmReference(string name, string spec)
    {
        if (!spec.StartsWith("npm:", StringComparison.OrdinalIgnoreCase)) return (name, spec);
        var match = Regex.Match(spec, @"^npm:(?<name>(?:@[^/@]+/)?[^/@]+)(?:@(?<spec>.+))?$");
        if (!match.Success) throw new InvalidDataException("npm 别名无效：" + name);
        return (match.Groups["name"].Value, match.Groups["spec"].Success ? match.Groups["spec"].Value : "latest");
    }

    async Task<string> QueryNpmVersionAsync(string name, string spec, string directory, CancellationToken cancellation)
    {
        var reference = NpmReference(name, spec);
        var start = CommandRunner.PackageManager("npm", new[] { "view", reference.Name + "@" + reference.Spec, "version", "--json" }, directory);
        var result = await CommandRunner.RunAsync(start, TimeSpan.FromSeconds(30), cancellation: cancellation);
        if (result.Cancelled) throw new OperationCanceledException(cancellation);
        if (!result.Ok) throw new InvalidOperationException(result.TimedOut ? "版本查询超时"
            : string.IsNullOrWhiteSpace(result.Output) ? "npm 查询失败，退出码 " + result.ExitCode : result.Output);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var values = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(value => value.GetString()).ToArray()
            : new[] { document.RootElement.GetString() };
        var version = "";
        foreach (var candidate in values.Where(value => value != null && IsExactVersion(value)))
            if (version.Length == 0 || IsNewer(candidate, version)) version = candidate;
        return version.Length > 0 ? version : throw new InvalidDataException("registry 没有返回有效版本：" + name);
    }

    internal static GitPluginReference ParseGitReference(string spec)
    {
        var pieces = spec.Split('#', 2);
        var source = pieces[0];
        string url;
        if (source.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var repository = source[7..];
            if (!Regex.IsMatch(repository, @"^[\w.-]+/[\w.-]+$")) throw new InvalidDataException("GitHub 仓库格式无效。");
            url = "https://github.com/" + repository.TrimEnd('/') + (repository.EndsWith(".git") ? "" : ".git");
        }
        else
        {
            url = source.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ? source[4..] : source;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "ssh"))
                throw new InvalidDataException("暂不支持此 Git 来源，请使用 github:owner/repo 或 git+https / git+ssh。");
        }
        var reference = pieces.Length > 1 && pieces[1].Length > 0 ? pieces[1] : "HEAD";
        if (reference.StartsWith("semver:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Git semver ref 需要明确指定 tag 后更新。");
        return new(url, reference, source);
    }

    internal static Dictionary<string, string> ParseGitRefs(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && Regex.IsMatch(parts[0], "^[0-9a-fA-F]{40}$")) result[parts[1]] = parts[0].ToLowerInvariant();
        }
        return result;
    }

    internal static string ResolveGitCommit(GitPluginReference source, Dictionary<string, string> references)
    {
        if (Regex.IsMatch(source.Ref, "^[0-9a-fA-F]{7,40}$")) return source.Ref.ToLowerInvariant();
        foreach (var name in source.Ref == "HEAD" ? new[] { "HEAD" }
            : new[] { "refs/heads/" + source.Ref, "refs/tags/" + source.Ref + "^{}", "refs/tags/" + source.Ref })
            if (references.TryGetValue(name, out var commit)) return commit;
        throw new InvalidDataException("远端不存在指定分支或 tag：" + source.Ref);
    }

    async Task<Dictionary<string, string>> QueryGitRefsAsync(GitPluginReference source, string directory, bool tags, CancellationToken cancellation)
    {
        var arguments = new List<string> { "ls-remote" };
        if (tags) arguments.Add("--tags");
        arguments.Add(source.Url);
        if (!tags) arguments.AddRange(source.Ref == "HEAD" ? new[] { "HEAD" }
            : new[] { "refs/heads/" + source.Ref, "refs/tags/" + source.Ref, "refs/tags/" + source.Ref + "^{}" });
        var start = CommandRunner.Create("git", arguments, directory);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        var result = await CommandRunner.RunAsync(start, TimeSpan.FromSeconds(30), cancellation: cancellation);
        if (result.Cancelled) throw new OperationCanceledException(cancellation);
        if (!result.Ok) throw new InvalidOperationException(result.TimedOut ? "Git 查询超时"
            : string.IsNullOrWhiteSpace(result.Output) ? "Git 查询失败，退出码 " + result.ExitCode : result.Output);
        return ParseGitRefs(result.StandardOutput);
    }

    public async Task<PluginCheckSummary> CheckPluginsAsync(IReadOnlyList<PluginItem> plugins,
        CancellationToken cancellation = default, Action<string> progress = null)
    {
        var completed = 0;
        await Parallel.ForEachAsync(plugins, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation }, async (plugin, token) =>
        {
            plugin.CheckError = "";
            plugin.TargetVersion = plugin.TargetCommit = "";
            plugin.Latest = "未检查";
            try
            {
                if (plugin.IsLocal || plugin.IsCore) plugin.Detail = plugin.Policy;
                else if (plugin.IsGit)
                {
                    var source = ParseGitReference(plugin.Spec);
                    if (SpecUpdatable(plugin.Spec))
                    {
                        plugin.TargetCommit = ResolveGitCommit(source, await QueryGitRefsAsync(source, plugin.ProfileDir, false, token));
                        plugin.Latest = source.Ref + "@" + plugin.TargetCommit[..8];
                        plugin.Detail = PluginLock.SameCommit(plugin.TargetCommit, plugin.InstalledCommit) ? "当前提交已同步" : "目标提交待更新";
                    }
                    else
                    {
                        var tags = await QueryGitRefsAsync(source, plugin.ProfileDir, true, token);
                        plugin.Latest = tags.Keys.Where(name => name.StartsWith("refs/tags/") && !name.EndsWith("^{}"))
                            .Select(name => name[10..]).Where(name => IsExactVersion(name.TrimStart('v', 'V')))
                            .Aggregate(plugin.Installed, (latest, tag) => IsNewer(tag, latest) ? tag : latest);
                        plugin.Detail = IsNewer(plugin.Latest, plugin.Installed) ? "有新版（固定 ref）" : "固定 ref；未发现新版本";
                    }
                }
                else
                {
                    var reference = NpmReference(plugin.Name, plugin.Spec);
                    plugin.Latest = await QueryNpmVersionAsync(reference.Name, "latest", plugin.ProfileDir, token);
                    plugin.TargetVersion = reference.Spec == "latest" ? plugin.Latest
                        : await QueryNpmVersionAsync(plugin.Name, plugin.Spec, plugin.ProfileDir, token);
                    plugin.Detail = IsNewer(plugin.TargetVersion, plugin.Installed)
                        ? plugin.IsIndirect ? "有新版；由入口维护" : "声明内可更新"
                        : IsNewer(plugin.Latest, plugin.Installed) ? "有新版（声明限制）"
                        : plugin.Latest == plugin.Installed ? "当前已是最新" : "当前版本高于 latest";
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                plugin.Latest = "检查失败";
                plugin.TargetVersion = plugin.TargetCommit = "";
                plugin.CheckError = CommandRunner.Redact(error.Message);
                plugin.Detail = "检查失败，可重试";
            }
            finally
            {
                plugin.CheckedAt = DateTime.UtcNow;
                progress?.Invoke($"检查 {Interlocked.Increment(ref completed)}/{plugins.Count} · {plugin.Name}");
            }
        });
        return new(plugins.Count, plugins.Count(plugin => plugin.CheckError.Length > 0), plugins.Count(plugin => plugin.HasUpdate), plugins.Count(plugin => plugin.SourceIssue.Length > 0));
    }

    async Task<PluginUpdatePlan> ResolvePlanAsync(PluginItem plugin, string intent, CancellationToken cancellation)
    {
        if (!Regex.IsMatch(plugin.Name, @"^(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9_][a-zA-Z0-9._-]*$"))
            throw new InvalidDataException("插件名称格式无效。");
        var current = Profiles().Single(profile => profile.Dir == plugin.ProfileDir).Plugins.Single(item => !item.IsIndirect && item.Name == plugin.Name);
        var lockError = PluginLock.Read(current.ProfileDir).Error;
        if (lockError.Length > 0) throw new InvalidDataException(lockError + "；请先修复格式，未停止服务。");
        if (current.Spec != plugin.Spec || current.Installed != plugin.Installed || current.ResolvedSource != plugin.ResolvedSource)
            throw new InvalidOperationException("插件列表已过期，请刷新后重试：" + plugin.Name);
        var requested = new PluginItem { Name = plugin.Name, Spec = intent };
        if (requested.IsGit)
        {
            var source = ParseGitReference(intent);
            var commit = Regex.IsMatch(source.Ref, "^[0-9a-fA-F]{7,40}$") ? source.Ref
                : ResolveGitCommit(source, await QueryGitRefsAsync(source, plugin.ProfileDir, false, cancellation));
            return new(current, intent, source.BaseSpec + "#" + commit, "", commit,
                current.Spec, current.Installed, current.ResolvedSource);
        }
        var version = await QueryNpmVersionAsync(plugin.Name, intent, plugin.ProfileDir, cancellation);
        var npm = NpmReference(plugin.Name, intent);
        var install = intent.StartsWith("npm:", StringComparison.OrdinalIgnoreCase) ? "npm:" + npm.Name + "@" + version : version;
        return new(current, intent, install, version, "", current.Spec, current.Installed, current.ResolvedSource);
    }

    void EnsurePlanCurrent(PluginUpdatePlan plan)
    {
        var current = Profiles().Single(profile => profile.Dir == plan.Plugin.ProfileDir).Plugins.Single(plugin => !plugin.IsIndirect && plugin.Name == plan.Plugin.Name);
        if (current.Spec != plan.OriginalSpec || current.Installed != plan.OriginalVersion || current.ResolvedSource != plan.OriginalSource)
            throw new InvalidOperationException("目标解析后依赖被其他程序修改，请刷新再重试：" + plan.Plugin.Name);
    }

    static void VerifyInstalledPlan(PluginUpdatePlan plan)
    {
        var version = InstalledVersion(plan.Plugin.ProfileDir, plan.Plugin.Name);
        if (string.IsNullOrEmpty(version)) throw new InvalidOperationException(plan.Plugin.Name + " 更新后缺少安装产物。");
        if (plan.Version.Length > 0 && version != plan.Version)
            throw new InvalidOperationException($"{plan.Plugin.Name} 目标 {plan.Version}，实际 {version}；命令完成但目标未安装。");
        var installed = new PluginItem { Name = plan.Plugin.Name, Spec = plan.InstallSpec };
        var snapshot = PluginLock.Read(plan.Plugin.ProfileDir);
        snapshot.Inspect(installed);
        if (snapshot.Error.Length > 0 || installed.ResolvedSource.Length == 0)
            throw new InvalidOperationException(plan.Plugin.Name + " 安装后的锁文件来源无法核对。");
        if (plan.Commit.Length > 0 && !PluginLock.SameCommit(plan.Commit, installed.InstalledCommit))
            throw new InvalidOperationException(plan.Plugin.Name + " 目标提交未安装，实际来源：" + installed.ResolvedSource);
        if (installed.ResolvedSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || installed.ResolvedSource.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(plan.Plugin.Name + " 仍解析到本地归档，不能报告远端更新成功。");
    }
}
