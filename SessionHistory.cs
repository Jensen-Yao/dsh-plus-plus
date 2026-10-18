using System.IO;
using System.Text.Json;

namespace DshControl;

/// <summary>Listing metadata from local session directories and official projection checkpoints; no session log is opened or migrated.</summary>
public record SessionHistoryItem(string Id, string Title, string Workspace, DateTime Updated, int Turns, string Directory, bool Cached)
{
    public string UpdatedText => Updated.ToLocalTime().ToString("MM-dd HH:mm");
    public string WorkspaceName => Workspace.Length == 0 ? "无工作目录" : Path.GetFileName(Workspace.TrimEnd('\\', '/'));
    public string Status => Cached ? "已保存" : "无标题缓存";
}

public record SessionHistoryResult(List<SessionHistoryItem> Items, int Total, int Unreadable);

public static class SessionHistory
{
    public static async Task<SessionHistoryResult> ReadAsync(string home, int limit, CancellationToken cancellation = default)
    {
        var root = Path.Combine(home, "sessions");
        if (!System.IO.Directory.Exists(root)) return new(new(), 0, 0);
        var directories = new List<DirectoryInfo>();
        var unreadable = 0;
        foreach (var project in new DirectoryInfo(root).EnumerateDirectories())
        {
            cancellation.ThrowIfCancellationRequested();
            if (project.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            try { directories.AddRange(project.EnumerateDirectories().Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))); }
            catch (IOException) { unreadable++; }
            catch (UnauthorizedAccessException) { unreadable++; }
        }
        var ordered = directories.OrderByDescending(directory => directory.LastWriteTimeUtc).ToList();
        var rows = new Dictionary<string, SessionHistoryItem>(StringComparer.Ordinal);
        foreach (var directory in ordered)
        {
            cancellation.ThrowIfCancellationRequested();
            rows[directory.Name] = new(directory.Name, "未命名会话", "", directory.LastWriteTimeUtc, 0, directory.FullName, false);
        }
        var legacy = Path.Combine(home, "storages", "session_projcache.json");
        if (File.Exists(legacy))
        {
            try
            {
                using var document = await ReadDocumentAsync(legacy, cancellation);
                var sessions = Child(Child(document.RootElement, "tables"), "sessions");
                if (sessions.ValueKind == JsonValueKind.Object)
                    foreach (var entry in sessions.EnumerateObject())
                        if (rows.TryGetValue(entry.Name, out var item)) rows[entry.Name] = ReadRecord(item, entry.Value);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { unreadable++; }
        }
        var cache = Path.Combine(home, "storages", "session_projcache", "sessions");
        foreach (var item in rows.Values.ToArray())
        {
            cancellation.ThrowIfCancellationRequested();
            var key = item.Id.StartsWith("session-", StringComparison.Ordinal) ? item.Id[8..] : item.Id;
            var path = Path.Combine(cache, key + ".json");
            if (!File.Exists(path)) path = Path.Combine(cache, item.Id + ".json");
            if (!File.Exists(path)) continue;
            try
            {
                using var document = await ReadDocumentAsync(path, cancellation);
                rows[item.Id] = ReadRecord(rows[item.Id], Child(document.RootElement, "record"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { unreadable++; }
        }
        return new(rows.Values.OrderByDescending(item => item.Updated).Take(Math.Clamp(limit, 50, 2000)).ToList(), directories.Count, unreadable);
    }

    static async Task<JsonDocument> ReadDocumentAsync(string path, CancellationToken cancellation)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > 128L * 1024 * 1024) throw new IOException("会话缓存超过读取上限。");
        return await JsonDocument.ParseAsync(file, cancellationToken: cancellation);
    }

    internal static SessionHistoryItem ReadRecord(SessionHistoryItem item, JsonElement record)
    {
        var identity = Child(record, "identity");
        var rows = Child(record, "rows");
        var title = Child(Child(rows, "title"), "val");
        var workspace = Child(identity, "cwd");
        var turns = Child(Child(Child(rows, "sessionStats"), "val"), "turns");
        var activity = Child(Child(Child(rows, "sessionListMetadata"), "val"), "lastPromptAt");
        var updated = item.Updated;
        if (activity.ValueKind == JsonValueKind.Number && activity.TryGetInt64(out var time)
            && time >= 0 && time <= 253402300799999) updated = DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime;
        return item with
        {
            Title = title.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(title.GetString()) ? title.GetString() : item.Title,
            Workspace = workspace.ValueKind == JsonValueKind.String ? workspace.GetString() : item.Workspace,
            Turns = turns.ValueKind == JsonValueKind.Number && turns.TryGetInt32(out var count) && count >= 0 ? count : item.Turns,
            Updated = updated,
            Cached = record.ValueKind == JsonValueKind.Object && rows.ValueKind == JsonValueKind.Object,
        };
    }

    static JsonElement Child(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var child) ? child : default;
}
