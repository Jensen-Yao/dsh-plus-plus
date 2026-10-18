using System.IO;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace DshControl;

public partial class UpdateService
{
    static readonly string[] DesktopIgnoredDependencies = { "electron", "playwright-core" };

    internal static DesktopSyncPlan BuildDesktopSyncPlan(string webDirectory, string desktopDirectory)
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
            if (installed.Length > 0 && target[0] is '^' or '~' or '>' or '=' || System.Text.RegularExpressions.Regex.IsMatch(target, @"^\d+\.\d+\.\d+"))
                target = installed;
            dependencies[dependency.Key] = target;
            var current = desktop["dependencies"]?[dependency.Key]?.GetValue<string>() ?? "";
            if (!string.Equals(current, target, StringComparison.Ordinal)) dependencyChanges.Add((dependency.Key, target, current));
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

    public async Task<(bool ok, string msg)> SynchronizeDesktopFromWebAsync(Action<string> progress = null,
        CancellationToken cancellation = default)
    {
        var web = Profiles().FirstOrDefault(profile => profile.Name == "web");
        var desktop = Profiles().FirstOrDefault(profile => profile.Name == "desktop");
        if (web == null || desktop == null) return (false, "需要同时存在 web 与 desktop profile。");
        if (!desktopService.Installed) return (false, "未找到官方桌面版程序。");
        var desktopWasRunning = desktopService.Status().Running;
        if (desktopWasRunning)
        {
            progress?.Invoke("正在停止桌面版…");
            var stop = await desktopService.StopAsync();
            if (!stop.ok) return stop;
        }
        var result = await MaintainAsync(new() { desktop.Dir }, "同步 web 配置到桌面版", async () =>
        {
            var plan = BuildDesktopSyncPlan(web.Dir, desktop.Dir);
            if (!plan.HasChanges && File.ReadAllText(Path.Combine(desktop.Dir, "cordis.patch.yml")) == plan.Patch)
            {
                progress?.Invoke("桌面版已与 web 配置一致。");
                return;
            }
            File.WriteAllText(Path.Combine(desktop.Dir, "package.json"), plan.Manifest.ToJsonString() + Environment.NewLine);
            File.WriteAllText(Path.Combine(desktop.Dir, "cordis.patch.yml"), plan.Patch);
            var install = await PackageAsync("pnpm", new[] { "install", "--reporter=append-only", "--config.minimumReleaseAge=0" },
                desktop.Dir, progress, cancellation);
            if (!install.Ok) throw new InvalidOperationException(install.Cancelled ? "同步已取消" : install.TimedOut
                ? "安装超时" : "桌面版依赖安装失败：" + CommandRunner.Redact(install.Output));
            foreach (var change in plan.DependencyChanges.Where(change => change.Web != "<移除>"))
            {
                var expected = InstalledVersion(web.Dir, change.Name);
                var actual = InstalledVersion(desktop.Dir, change.Name);
                if (expected.Length > 0 && actual != expected)
                    throw new InvalidOperationException($"{change.Name} 未与 web 同步：web {expected}，desktop {actual}。");
            }
        }, progress, cancellation, includeDshService: false, profileName: "desktop");
        if (desktopWasRunning)
        {
            var start = await desktopService.StartAsync();
            result = result.ok ? start : (false, result.msg + (start.ok ? "\n桌面版已恢复运行。" : "\n桌面版恢复启动失败：" + start.message));
        }
        return result;
    }
}
