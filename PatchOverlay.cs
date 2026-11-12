using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DshControl;

/// <summary>profile 的 cordis.patch.yml（overlay patch）只读体检与保守修复：仅移除孤立的文档分隔符，不合并、不去重条目。</summary>
public static class PatchOverlay
{
    public static string PathOf(string profileDirectory) => System.IO.Path.Combine(profileDirectory, "cordis.patch.yml");

    /// <summary>解析 profile 的 overlay patch：不存在视为未配置（信息），多文档或语法损坏为错误，重复条目 id 为注意。</summary>
    public static (string Level, string Title, string Detail) ReadStatus(string profileDirectory)
    {
        var path = PathOf(profileDirectory);
        if (!File.Exists(path)) return ("信息", "overlay patch", "未配置 cordis.patch.yml：" + path);
        try
        {
            var stream = new YamlStream();
            using (var reader = File.OpenText(path)) stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlSequenceNode)
                return ("错误", "overlay patch 无法解析", $"发现 {stream.Documents.Count} 个 YAML 文档（应为单个列表）。Web 或桌面版启动时会因 overlay 解析失败退出。"
                    + "可使用诊断页「修复 overlay patch」移除孤立的文档分隔符（先自动备份）。\n" + path);
            var ids = new List<string>();
            foreach (var node in (YamlSequenceNode)stream.Documents[0].RootNode)
                if (node is YamlMappingNode entry && PluginLock.Child(entry, "id") is YamlScalarNode id) ids.Add(id.Value ?? "");
            var duplicates = ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
            return duplicates.Count > 0
                ? ("注意", "overlay patch 条目重复", "重复 id：" + string.Join(", ", duplicates) + "；后写条目可能覆盖先写条目的配置。"
                    + "桌面端建议用「从 Web 同步配置」以 web 为准重建；也可手动编辑保留需要的一条。\n" + path)
                : ("通过", "overlay patch", $"{ids.Count} 个条目，单文档解析正常。");
        }
        catch (Exception exception)
        {
            return ("错误", "overlay patch 无法解析", exception.Message
                + "\nWeb 或桌面版启动时会因 overlay 解析失败退出。孤立分隔符可尝试诊断页「修复 overlay patch」（先自动备份）；其他语法问题请手动编辑。\n" + path);
        }
    }

    /// <summary>移除顶层孤立的 --- / ... 分隔符行并合并为单文档；修复前备份，修复后仍无法解析则还原并返回失败。</summary>
    public static (bool ok, string message) Repair(string profileDirectory)
    {
        var path = PathOf(profileDirectory);
        if (!File.Exists(path)) return (false, "文件不存在：" + path);
        string original;
        try { original = File.ReadAllText(path); }
        catch (Exception exception) { return (false, "读取失败：" + exception.Message); }
        if (!Regex.IsMatch(original, @"(?m)^(?:---|\.\.\.)[ \t]*$")) return (false, "没有孤立的文档分隔符，无需此修复：" + path);
        var backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var repaired = string.Join("\n", original.Split('\n').Where(line => !Regex.IsMatch(line, @"^(?:---|\.\.\.)[ \t]*\r?$")));
        try
        {
            var verify = new YamlStream();
            verify.Load(new StringReader(repaired));
            if (verify.Documents.Count != 1 || verify.Documents[0].RootNode is not YamlSequenceNode)
                return (false, "移除分隔符后仍不是单文档列表，已保持原文件不动：" + path);
            File.Copy(path, backup, true);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, repaired);
            File.Move(temporary, path, true);
            return (true, "已修复 " + path + "\n备份：" + backup);
        }
        catch (Exception exception) { return (false, "修复未完成，原文件保持不动：" + exception.Message); }
    }
}
