using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace DshControl;

public partial class UpdateService
{
    static readonly string[] DesktopIgnoredDependencies = { "electron", "playwright-core" };

    internal static DesktopSyncPlan BuildDesktopSyncPlan(string webDirectory, string desktopDirectory,
        Dictionary<string, (string target, string note, bool drift, string webBehind)> targets = null)
    {
        var web = JsonNode.Parse(File.ReadAllText(Path.Combine(webDirectory, "package.json"))).AsObject();
        var desktop = JsonNode.Parse(File.ReadAllText(Path.Combine(desktopDirectory, "package.json"))).AsObject();
        var dependencies = new JsonObject();
        var dependencyChanges = new List<(string, string, string)>();
        foreach (var dependency in web["dependencies"]!.AsObject())
        {
            if (dependency.Key.StartsWith("@deepseek-ai/") || DesktopIgnoredDependencies.Contains(dependency.Key, StringComparer.OrdinalIgnoreCase)) continue;
            var installed = InstalledVersion(webDirectory, dependency.Key);
            var target = dependency.Value!.GetValue<string>();
            if (installed.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(target, @"^(?:[~^>=<*\d]|latest$)"))
                target = installed;
            var desktopInstalled = InstalledVersion(desktopDirectory, dependency.Key);
            if (desktopInstalled.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(desktopInstalled, @"^\d")
                && CompareVersions(desktopInstalled, target) > 0)
                target = desktopInstalled; // 官方桌面端升级过的版本不被同步降级
            var note = "";
            if (targets != null && targets.TryGetValue(dependency.Key, out var resolved)
                && System.Text.RegularExpressions.Regex.IsMatch(resolved.target, @"^\d"))
            {
                target = resolved.target;
                note = resolved.note;
            }
            dependencies[dependency.Key] = target;
            var current = desktop["dependencies"]?[dependency.Key]?.GetValue<string>() ?? "";
            if (!string.Equals(current, target, StringComparison.Ordinal)) dependencyChanges.Add((dependency.Key, target + note, current));
        }
        foreach (var obsolete in desktop["dependencies"]?.AsObject().Select(entry => entry.Key).ToArray() ?? Array.Empty<string>())
            if (!web["dependencies"]!.AsObject().Any(entry => entry.Key == obsolete)
                && !obsolete.StartsWith("@deepseek-ai/") && !DesktopIgnoredDependencies.Contains(obsolete, StringComparer.OrdinalIgnoreCase))
            {
                dependencyChanges.Add((obsolete, "<移除>", desktop["dependencies"]![obsolete]!.GetValue<string>()));
            }

        var webBundles = web["dsh"]!["profile"]!["bundles"]!.AsArray().Select(node => node!.GetValue<string>()).ToList();
        var bundles = new JsonArray { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" };
        var bundleChanges = new List<(string, string, string)>();
        foreach (var bundle in webBundles.Where(name => !bundles.Any(node => node!.GetValue<string>() == name)))
            bundles.Add(bundle);
        var desktopBundles = desktop["dsh"]?["profile"]?["bundles"]?.AsArray().Select(node => node!.GetValue<string>()).ToList() ?? new List<string>();
        if (!desktopBundles.SequenceEqual(bundles.Select(node => node!.GetValue<string>())))
            bundleChanges.Add((string.Join(" → ", bundles.Select(node => node!.GetValue<string>())), string.Join(" → ", desktopBundles), "bundle 顺序或内容不同"));

        desktop["dependencies"] = dependencies;
        desktop["dsh"]!["profile"]!["bundles"] = bundles;
        return new(desktop, SanitizeDesktopPatch(File.ReadAllText(Path.Combine(webDirectory, "cordis.patch.yml"))), dependencyChanges, bundleChanges);
    }

    internal static string SanitizeDesktopPatch(string source)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(source);
        stream.Load(reader);
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlSequenceNode entries) return "[]\n";
        for (var index = entries.Children.Count - 1; index >= 0; index--)
        {
            var entry = entries.Children[index] as YamlMappingNode;
            var id = PluginLock.Child(entry, "id") as YamlScalarNode;
            if (id != null && id.Value == "web-ui-remote-web-ui") entries.Children.RemoveAt(index);
        }
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(writer, false);
        return writer.ToString().TrimEnd() + "\n";
    }

    static string SnapshotPath => Path.Combine(AppConfig.ConfigDir, "desktop-sync-snapshot.yml");

    static List<YamlNode> ParsePatchEntries(string source)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(source ?? "[]\n");
        stream.Load(reader);
        return stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlSequenceNode entries
            ? entries.Children.ToList() : new List<YamlNode>();
    }

    static string EntryId(YamlNode entry) =>
        entry is YamlMappingNode map && PluginLock.Child(map, "id") is YamlScalarNode id ? id.Value ?? "" : "";

    static string Render(IEnumerable<YamlNode> entries)
    {
        var stream = new YamlStream();
        var sequence = new YamlSequenceNode();
        foreach (var entry in entries) sequence.Children.Add(entry);
        stream.Documents.Add(new YamlDocument(sequence));
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(writer, false);
        return writer.ToString().TrimEnd() + "\n";
    }

    static bool SameEntry(YamlNode left, YamlNode right)
    {
        if (left == null || right == null) return left == right;
        var stream = new YamlStream();
        stream.Documents.Add(new YamlDocument(new YamlSequenceNode { left }));
        using var leftWriter = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(leftWriter, false);
        stream = new YamlStream();
        stream.Documents.Add(new YamlDocument(new YamlSequenceNode { right }));
        using var rightWriter = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(rightWriter, false);
        return leftWriter.ToString() == rightWriter.ToString();
    }

    /// <summary>按条目 id 做三方合并：desktop 与快照相同则采用 web（投影改动），desktop 有本地改动则保留并回流 web，双方都改且不同以 desktop 为准。desktop 独有条目追加回流。无快照时退化为以 web 为准（与旧版一致）并首次建立快照。</summary>
    internal static (string Merged, List<string> Notes) MergePatchEntries(string baseText, string webText, string desktopText)
    {
        var notes = new List<string>();
        var webEntries = ParsePatchEntries(webText);
        if (baseText == null) return (Render(webEntries), notes);
        static Dictionary<string, YamlNode> IndexById(IEnumerable<YamlNode> entries)
        {
            var index = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var id = EntryId(entry);
                if (id.Length > 0) index.TryAdd(id, entry);
            }
            return index;
        }
        var baseEntries = IndexById(ParsePatchEntries(baseText));
        var desktopEntries = IndexById(ParsePatchEntries(desktopText));
        var merged = new List<YamlNode>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var webEntry in webEntries)
        {
            var id = EntryId(webEntry);
            consumed.Add(id);
            baseEntries.TryGetValue(id, out var baseEntry);
            desktopEntries.TryGetValue(id, out var desktopEntry);
            if (desktopEntry == null || SameEntry(desktopEntry, baseEntry))
            {
                merged.Add(webEntry); // 桌面未改动：投影 web
                if (desktopEntry == null && baseEntry != null) notes.Add(id + "：桌面端已删除，按 web 恢复");
            }
            else if (SameEntry(webEntry, baseEntry) || !SameEntry(webEntry, desktopEntry))
            {
                merged.Add(desktopEntry); // 桌面本地改动：保留（web 未动时回流；双方都改以桌面为准）
                if (!SameEntry(webEntry, baseEntry)) notes.Add(id + "：两端都改过，以桌面为准");
                else notes.Add(id + "：桌面端改动已回流 web");
            }
            else merged.Add(desktopEntry);
        }
        foreach (var (id, desktopEntry) in desktopEntries)
            if (!consumed.Contains(id) && id.Length > 0)
            {
                merged.Add(desktopEntry);
                notes.Add(id + "：桌面端新增条目已回流 web");
            }
        return (Render(merged), notes);
    }

    public async Task<(bool ok, string msg)> SynchronizeDesktopFromWebAsync(Action<string> progress = null,
        CancellationToken cancellation = default)
    {
        var web = Profiles().FirstOrDefault(profile => profile.Name == "web");
        var desktop = Profiles().FirstOrDefault(profile => profile.Name == "desktop");
        if (web == null || desktop == null) return (false, "需要同时存在 web 与 desktop profile。");
        if (!desktopService.Installed) return (false, "未找到官方桌面版程序。");
        var runtime = desktopService.Status().Version;
        var targets = await ResolveDesktopTargetsAsync(web, desktop, runtime, cancellation);
        foreach (var entry in targets.Where(item => item.Value.note.Length > 0))
            Log($"同步目标 {entry.Key} → {entry.Value.target} {entry.Value.note}");
        var webPatchPath = Path.Combine(web.Dir, "cordis.patch.yml");
        var desktopPatchPath = Path.Combine(desktop.Dir, "cordis.patch.yml");
        var snapshot = File.Exists(SnapshotPath) ? await File.ReadAllTextAsync(SnapshotPath, cancellation) : null;
        var (merged, mergeNotes) = MergePatchEntries(snapshot, await File.ReadAllTextAsync(webPatchPath, cancellation),
            await File.ReadAllTextAsync(desktopPatchPath, cancellation));
        var desktopPatch = SanitizeDesktopPatch(merged);
        var preview = BuildDesktopSyncPlan(web.Dir, desktop.Dir, targets);
        if (!preview.HasChanges && File.ReadAllText(desktopPatchPath) == desktopPatch
            && preview.Manifest["dependencies"]!.AsObject().All(item =>
                (targets.TryGetValue(item.Key, out var target) ? target.target : InstalledVersion(web.Dir, item.Key))
                    == InstalledVersion(desktop.Dir, item.Key)
                && InstalledVersion(desktop.Dir, item.Key).Length > 0))
            return (true, "桌面版已与 web 配置一致（含桌面端本地改动与内核兼容上限），未停止服务或重新安装依赖。");
        var result = await MaintainAsync(new() { desktop.Dir }, "同步 web 配置到桌面版", async () =>
        {
            var plan = BuildDesktopSyncPlan(web.Dir, desktop.Dir, targets);
            if (merged != await File.ReadAllTextAsync(webPatchPath, cancellation))
            {
                var webBackup = webPatchPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(webPatchPath, webBackup, true);
                File.WriteAllText(webPatchPath, merged);
                Log("桌面端 patch 改动已回流 web，备份：" + webBackup);
            }
            File.WriteAllText(Path.Combine(desktop.Dir, "package.json"), plan.Manifest.ToJsonString() + Environment.NewLine);
            File.WriteAllText(desktopPatchPath, desktopPatch);
            var install = await PackageAsync("pnpm", new[] { "install", "--reporter=append-only", "--config.minimumReleaseAge=0" },
                desktop.Dir, progress, cancellation);
            if (!install.Ok) throw new InvalidOperationException(install.Cancelled ? "同步已取消" : install.TimedOut
                ? "安装超时" : "桌面版依赖安装失败：" + CommandRunner.Redact(install.Output));
            foreach (var dependency in plan.Manifest["dependencies"]!.AsObject())
            {
                var expected = targets.TryGetValue(dependency.Key, out var target) ? target.target : InstalledVersion(web.Dir, dependency.Key);
                var actual = InstalledVersion(desktop.Dir, dependency.Key);
                if (expected.Length > 0 && actual != expected)
                    throw new InvalidOperationException($"{dependency.Key} 未同步到目标版本：目标 {expected}，desktop {actual}。");
            }
            await File.WriteAllTextAsync(SnapshotPath, desktopPatch, cancellation);
        }, progress, cancellation);
        if (result.ok)
        {
            var drift = targets.Where(item => item.Value.drift).Select(item => item.Key + "（web " + item.Value.webBehind + "）").ToList();
            var extras = new List<string>();
            if (mergeNotes.Count > 0) extras.Add("patch：" + string.Join("；", mergeNotes));
            if (drift.Count > 0) extras.Add("版本：web 落后于桌面端 → " + string.Join("、", drift) + "；在插件页「升级到最新」后再次同步可锁齐两端");
            if (extras.Count > 0) result = (result.ok, result.msg + "\n" + string.Join("\n", extras));
        }
        return result;
    }

    /// <summary>桌面启动前的依赖自愈：检测 desktop profile 直接依赖的安装产物缺失（官方插件市场更新可能破坏 node_modules），缺失时自动走「从 Web 同步配置」重建。</summary>
    public async Task<(bool ok, string msg)> RepairDesktopDependenciesIfMissingAsync(CancellationToken cancellation = default)
    {
        var desktop = Profiles().FirstOrDefault(profile => profile.Name == "desktop");
        if (desktop == null) return (true, "");
        var missing = desktop.Plugins.Where(plugin => !plugin.IsIndirect && plugin.Installed.Length == 0).Select(plugin => plugin.Name).ToList();
        if (missing.Count == 0) return (true, "");
        Log("桌面端插件安装产物缺失：" + string.Join("、", missing) + "，自动同步重建依赖。");
        var result = await SynchronizeDesktopFromWebAsync(null, cancellation);
        return result.ok
            ? (true, "已自动重建缺失的桌面端依赖（" + string.Join("、", missing) + "）。")
            : (false, "桌面端依赖缺失（" + string.Join("、", missing) + "）且自动重建失败：" + result.msg);
    }

    /// <summary>为每个社区依赖解析同步目标版本：取两端已装中较新者，且必须满足桌面 runtime 的内核要求（不满足时回退兼容上限）。官方桌面端安装器升级过的版本不会被同步降级。</summary>
    internal async Task<Dictionary<string, (string target, string note, bool drift, string webBehind)>> ResolveDesktopTargetsAsync(
        ProfileInfo web, ProfileInfo desktop, string runtime, CancellationToken cancellation)
    {
        var targets = new Dictionary<string, (string, string, bool, string)>();
        foreach (var plugin in web.Plugins)
        {
            cancellation.ThrowIfCancellationRequested();
            if (plugin.IsCore || plugin.IsLocal || plugin.IsGit || plugin.IsIndirect
                || DesktopIgnoredDependencies.Contains(plugin.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var webInstalled = InstalledVersion(web.Dir, plugin.Name);
            if (webInstalled.Length == 0) continue;
            var desktopInstalled = InstalledVersion(desktop.Dir, plugin.Name);
            var desired = desktopInstalled.Length > 0 && CompareVersions(desktopInstalled, webInstalled) > 0 ? desktopInstalled : webInstalled;
            var reference = NpmReference(plugin.Name, plugin.Spec).Name;
            if (!string.IsNullOrWhiteSpace(runtime))
            {
                var requirement = await QueryKernelRequirementAsync(reference, desired, web.Dir, cancellation);
                Log($"内核要求核对 {plugin.Name}@{desired} → 要求「{requirement}」/ 桌面 runtime {runtime}");
                if (SatisfiesRange(runtime, requirement) == false)
                {
                    var fallback = await LatestKernelCompatibleVersionAsync(plugin.Name, desired, runtime, web.Dir, cancellation);
                    Log("不满足，候选兼容版本：" + (fallback.Length > 0 ? fallback : "无"));
                    if (fallback.Length > 0)
                    {
                        targets[plugin.Name] = (fallback, "（桌面内核兼容上限）", desktopInstalled.Length > 0 && CompareVersions(desktopInstalled, webInstalled) > 0, webInstalled);
                        continue;
                    }
                }
            }
            targets[plugin.Name] = (desired, "", desktopInstalled.Length > 0 && CompareVersions(desired, webInstalled) > 0, webInstalled);
        }
        return targets;
    }

    /// <summary>在 preferred 及更低的发布版中，找到内核要求被 runtime 满足的最新版本；查不到返回空（沿用 preferred，由安装阶段暴露问题）。</summary>
    async Task<string> LatestKernelCompatibleVersionAsync(string name, string preferred, string runtime, string directory,
        CancellationToken cancellation)
    {
        var reference = NpmReference(name, "");
        var query = CommandRunner.PackageManager("npm", new[] { "view", reference.Name, "versions", "--json" }, directory);
        var result = await CommandRunner.RunAsync(query, TimeSpan.FromSeconds(30), cancellation: cancellation);
        if (!result.Ok) return "";
        List<string> versions;
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            versions = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(item => item.GetString() ?? "").Where(IsExactVersion).ToList()
                : new List<string>();
        }
        catch (JsonException) { return ""; }
        var candidates = versions.Where(version => !version.Contains('-') && CompareVersions(version, preferred) <= 0)
            .OrderByDescending(version => version, Comparer<string>.Create(CompareVersions)).Take(6).ToList();
        foreach (var candidate in candidates)
        {
            var requirement = await QueryKernelRequirementAsync(reference.Name, candidate, directory, cancellation);
            if (requirement.Length == 0 || SatisfiesRange(runtime, requirement) == true) return candidate;
        }
        return "";
    }
}
