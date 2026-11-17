using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DshControl;

public record McpServerEntry(string Id, string ServerName, string Command, List<string> Args, bool Protected);

/// <summary>检查 DSH 全局 patch 中 stdio MCP server 的工具 schema，并在发现 DeepSeek API 不接受的 surrogate 转义 pattern 时提供代理修复。</summary>
public static class McpSchemaCheck
{
    static readonly Regex SurrogatePattern = new(@"\\u[dD][89abAB][0-9a-fA-F]{2}", RegexOptions.Compiled);
    public const string SanitizerAsset = "scripts/mcp-schema-sanitizer.mjs";

    /// <summary>解析 patch 中的 @deepseek-ai/dsh-mcp-client 条目（stdio）。</summary>
    public static List<McpServerEntry> ReadServers(string patchPath)
    {
        var result = new List<McpServerEntry>();
        if (!File.Exists(patchPath)) return result;
        var stream = new YamlStream();
        using (var reader = File.OpenText(patchPath)) stream.Load(reader);
        if (stream.Documents.Count != 1) return result;
        foreach (var node in stream.Documents[0].RootNode.AllNodes)
        {
            if (node is not YamlMappingNode entry) continue;
            if (PluginLock.Child(entry, "name") is not YamlScalarNode name || name.Value != "@deepseek-ai/dsh-mcp-client") continue;
            if (PluginLock.Child(entry, "id") is not YamlScalarNode id) continue;
            if (PluginLock.Child(entry, "config") is not YamlMappingNode config) continue;
            if (PluginLock.Child(config, "transport") is YamlScalarNode transport && transport.Value != "stdio") continue;
            if (PluginLock.Child(config, "command") is not YamlScalarNode command) continue;
            var args = new List<string>();
            if (PluginLock.Child(config, "args") is YamlSequenceNode list)
                foreach (var item in list.Children)
                    if (item is YamlScalarNode arg && arg.Value != null) args.Add(arg.Value);
            var serverName = (PluginLock.Child(config, "serverName") as YamlScalarNode)?.Value ?? id.Value;
            var protectedEntry = args.Count > 0 && args[0].EndsWith("schema-sanitizer.mjs", StringComparison.OrdinalIgnoreCase);
            result.Add(new(id.Value ?? serverName, serverName, command.Value ?? "", args, protectedEntry));
        }
        return result;
    }

    /// <summary>启动 MCP server 并请求 tools/list，统计 inputSchema 中含 surrogate 转义 pattern 的工具数。整体有超时兜底并强杀进程树。</summary>
    public static async Task<(int tools, int badTools, string error)> ProbeAsync(McpServerEntry server,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        string? toolsLine = null;
        try
        {
            var start = new ProcessStartInfo(server.Command)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in server.Args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process == null) return (0, 0, "无法启动：" + server.Command);
            using var registration = cancellation.Register(() => { try { process.Kill(true); } catch { } });
            var exchange = Task.Run(() =>
            {
                var writer = process.StandardInput;
                writer.WriteLine(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = 1, method = "initialize",
                    @params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "dshpp-schema-check", version = "1" } },
                }));
                writer.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
                writer.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }));
                writer.Flush();
                while (!process.HasExited)
                {
                    var line = process.StandardOutput.ReadLine();
                    if (line == null) break;
                    if (!line.Contains("\"id\"")) continue;
                    try
                    {
                        using var probe = JsonDocument.Parse(line);
                        if (probe.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                            && id.GetInt32() == 2 && probe.RootElement.TryGetProperty("result", out _))
                        { toolsLine = line; break; }
                    }
                    catch (JsonException) { }
                }
            }, CancellationToken.None);
            var finished = await Task.WhenAny(exchange, Task.Delay(timeout, CancellationToken.None));
            try { process.Kill(true); } catch { }
            if (finished != exchange || toolsLine == null) return (0, 0, "探测超时或未返回工具列表。");
            using var document = JsonDocument.Parse(toolsLine);
            if (!document.RootElement.TryGetProperty("result", out var result)) return (0, 0, "响应缺少 result。");
            var tools = result.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : new();
            var bad = 0;
            foreach (var tool in tools)
                if (tool.TryGetProperty("inputSchema", out var schema) && SurrogatePattern.IsMatch(schema.GetRawText())) bad++;
            return (tools.Count, bad, "");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return (0, 0, exception.Message); }
    }

    /// <summary>把 patch 中指定 MCP 条目的启动参数改为经由内嵌清洗代理；先备份。仅处理尚未防护的条目。</summary>
    public static (bool ok, string message) Repair(string patchPath, string serverId)
    {
        var sanitizer = BundledAssets.Get(SanitizerAsset);
        var lines = File.ReadAllLines(patchPath);
        var blockStart = -1;
        for (var index = 0; index < lines.Length; index++)
            if (lines[index].TrimStart().StartsWith("- id: ") && lines[index].TrimStart()["- id: ".Length..].Trim() == serverId)
            { blockStart = index; break; }
        if (blockStart < 0) return (false, "未找到 MCP 条目：" + serverId);
        var blockIndent = new string(' ', lines[blockStart].TakeWhile(char.IsWhiteSpace).Count());
        var blockEnd = lines.Length;
        for (var index = blockStart + 1; index < lines.Length; index++)
            if (lines[index].StartsWith(blockIndent + "- id:", StringComparison.Ordinal))
            { blockEnd = index; break; }
        int commandLine = -1, argsLine = -1;
        for (var index = blockStart; index < blockEnd; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("command:")) commandLine = index;
            if (trimmed.StartsWith("args:")) argsLine = index;
        }
        if (commandLine < 0) return (false, "条目缺少 command：" + serverId);
        var command = lines[commandLine].TrimStart()["command:".Length..].Trim();
        var backup = patchPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        List<string> updated = new();
        if (argsLine >= 0)
        {
            // 已有 args 列表：node 场景在最前插入 sanitizer；其他命令改走 --exec。
            var argsIndent = new string(' ', lines[argsLine].TakeWhile(char.IsWhiteSpace).Count() + 2);
            var isNode = Path.GetFileNameWithoutExtension(command).StartsWith("node", StringComparison.OrdinalIgnoreCase);
            updated.AddRange(lines[..argsLine]);
            updated.Add(lines[argsLine]);
            updated.Add(argsIndent + "- " + (isNode ? sanitizer : "--exec"));
            if (!isNode) updated.Add(argsIndent + "- " + command);
            updated.AddRange(lines[(argsLine + 1)..]);
            if (!isNode)
            {
                var rebuilt = updated.ToArray();
                rebuilt[commandLine] = lines[commandLine].Replace(command, "node");
                updated = rebuilt.ToList();
            }
        }
        else
        {
            // 没有 args：直接写入内联列表。
            updated.AddRange(lines);
            var insertAt = commandLine + 1;
            var configIndent = new string(' ', lines[commandLine].TakeWhile(char.IsWhiteSpace).Count());
            var isNode = Path.GetFileNameWithoutExtension(command).StartsWith("node", StringComparison.OrdinalIgnoreCase);
            updated.Insert(insertAt, configIndent + "  args: [ " + (isNode
                ? Quote(sanitizer) + " ]"
                : Quote("node") + ", " + Quote("--exec") + ", " + Quote(command) + " ]"));
        }
        File.Copy(patchPath, backup, true);
        var temporary = patchPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllLines(temporary, updated);
        File.Move(temporary, patchPath, true);
        try
        {
            var verify = new YamlStream();
            using var reader = File.OpenText(patchPath);
            verify.Load(reader);
            if (verify.Documents.Count != 1) throw new InvalidDataException("YAML 结构变化");
        }
        catch (Exception exception)
        {
            File.Copy(backup, patchPath, true);
            return (false, "修复后 YAML 校验失败，已还原原文件：" + exception.Message);
        }
        return (true, "已为 " + serverId + " 插入 MCP schema 清洗代理，重启 DSH Web / 桌面版后生效。\n备份：" + backup);
    }

    static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
