using System.IO;
using System.Text.Json;

namespace DshControl;

public partial class UpdateService
{
    static PluginItem ReadPlugin(ProfileInfo profile, string name, string spec, PluginLock snapshot)
    {
        var directory = Path.Combine(profile.Dir, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
        var plugin = new PluginItem
        {
            Name = name, Spec = spec, Profile = profile.Name, ProfileDir = profile.Dir,
            PackageDirectory = directory, Installed = ReadVersion(directory),
        };
        snapshot.Inspect(plugin);
        return plugin;
    }

    static string ReadVersion(string directory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
            return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : "";
        }
        catch (Exception error) when (error is IOException or JsonException) { return ""; }
    }

    static string FindPackage(string from, string name)
    {
        for (var current = new DirectoryInfo(from); current != null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(Path.Combine(candidate, "package.json"))) return candidate;
        }
        return null;
    }

    void DiscoverBundlePlugins(ProfileInfo profile)
    {
        var seen = new HashSet<string>(profile.Plugins.Select(plugin => plugin.PackageDirectory), StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(PluginItem Plugin, string Owner)>();
        foreach (var plugin in profile.Plugins.Where(plugin => !plugin.IsLocal && !plugin.IsCore)) pending.Enqueue((plugin, plugin.Name));
        while (pending.TryDequeue(out var next))
        {
            try
            {
                var path = Path.Combine(next.Plugin.PackageDirectory, "package.json");
                if (!File.Exists(path)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("dsh", out var metadata) || metadata.ValueKind != JsonValueKind.Object
                    || !metadata.TryGetProperty("bundle", out _) || !root.TryGetProperty("dependencies", out var dependencies)
                    || dependencies.ValueKind != JsonValueKind.Object) continue;
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (dependency.Name.StartsWith("@deepseek-ai/") || dependency.Value.ValueKind != JsonValueKind.String) continue;
                    var directory = FindPackage(next.Plugin.PackageDirectory, dependency.Name);
                    if (directory == null || !seen.Add(directory)) continue;
                    using var child = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
                    if (!child.RootElement.TryGetProperty("dsh", out var dsh) || dsh.ValueKind != JsonValueKind.Object) continue;
                    var plugin = new PluginItem
                    {
                        Name = dependency.Name, Spec = dependency.Value.GetString(), Installed = ReadVersion(directory),
                        Profile = profile.Name, ProfileDir = profile.Dir, PackageDirectory = directory, OwnerName = next.Owner,
                        ResolvedSource = "由 " + next.Owner + " 解析", SourceIssue = next.Plugin.SourceIssue,
                    };
                    profile.Plugins.Add(plugin);
                    pending.Enqueue((plugin, next.Owner));
                }
            }
            catch (Exception error) when (error is IOException or JsonException)
            { next.Plugin.SourceIssue = "聚合清单无法读取：" + error.Message; Log(next.Plugin.Name + "：" + next.Plugin.SourceIssue); }
        }
    }
}
