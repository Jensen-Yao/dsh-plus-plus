using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DshControl;

/// <summary>Reads pnpm importer sources without executing hooks or rewriting dependency resolutions.</summary>
internal sealed class PluginLock
{
    readonly YamlMappingNode importer;
    public string Error { get; }

    PluginLock(YamlMappingNode importer = null, string error = "") { this.importer = importer; Error = error; }

    internal static YamlNode Child(YamlNode parent, string key) => parent is YamlMappingNode map
        && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    static string Scalar(YamlNode node) => (node as YamlScalarNode)?.Value ?? "";

    public static PluginLock Read(string directory)
    {
        var path = Path.Combine(directory, "pnpm-lock.yaml");
        if (!File.Exists(path)) return new();
        try
        {
            using var reader = File.OpenText(path);
            var yaml = new YamlStream();
            yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
                return new(error: "锁文件格式无效");
            var importer = Child(Child(root, "importers"), ".") as YamlMappingNode;
            return importer == null ? new(error: "锁文件缺少当前 profile 的 importer") : new(importer);
        }
        catch (Exception error) when (error is IOException or YamlDotNet.Core.YamlException)
        { return new(error: "锁文件无法读取：" + error.Message); }
    }

    public void Inspect(PluginItem plugin)
    {
        plugin.SourceIssue = Error;
        var entry = Child(Child(importer, "dependencies"), plugin.Name);
        plugin.ResolvedSource = Scalar(Child(entry, "version"));
        plugin.InstalledCommit = Commit(plugin.ResolvedSource);
        var declaration = Scalar(Child(entry, "specifier"));
        if (declaration.Length > 0 && declaration != plugin.Spec)
            plugin.SourceIssue = "锁文件声明与 package.json 不一致";
        if (!plugin.IsLocal && (plugin.ResolvedSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || plugin.ResolvedSource.StartsWith("link:", StringComparison.OrdinalIgnoreCase)))
            plugin.SourceIssue = "远端声明解析到了本地归档或链接，更新时必须重新核对来源";
    }

    public static string Commit(string source)
    {
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || source.StartsWith("link:", StringComparison.OrdinalIgnoreCase)) return "";
        var match = Regex.Match(source, @"(?:^|[/#])(?<sha>[0-9a-fA-F]{40})(?:$|[.(/?])");
        return match.Success ? match.Groups["sha"].Value.ToLowerInvariant() : "";
    }

    public static bool SameCommit(string expected, string actual) => expected.Length >= 7 && actual.Length >= 7
        && (actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || expected.StartsWith(actual, StringComparison.OrdinalIgnoreCase));

    /// <summary>Keep the user's tracking declaration while retaining the exact resolution produced by pnpm.</summary>
    public static void RestoreTrackingSpec(string directory, string name, string spec)
    {
        var manifestPath = Path.Combine(directory, "package.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("依赖清单为空。");
        manifest["dependencies"]![name] = spec;
        var lockPath = Path.Combine(directory, "pnpm-lock.yaml");
        var yaml = new YamlStream();
        using (var reader = File.OpenText(lockPath)) yaml.Load(reader);
        var entry = Child(Child(Child(Child(yaml.Documents.Single().RootNode, "importers"), "."), "dependencies"), name) as YamlMappingNode
            ?? throw new InvalidDataException("安装后的锁文件缺少目标依赖：" + name);
        entry.Children[new YamlScalarNode("specifier")] = new YamlScalarNode(spec);
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        yaml.Save(writer, false);
        AtomicWrite(manifestPath, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        AtomicWrite(lockPath, writer.ToString().TrimEnd() + "\n");
    }

    static void AtomicWrite(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, true);
    }
}
