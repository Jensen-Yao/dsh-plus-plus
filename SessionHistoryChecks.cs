using System.IO;
using System.Text.Json;

namespace DshControl;

public static partial class SelfCheck
{
    static async Task RunSessionHistoryChecksAsync(Action<string, bool> check)
    {
        var fixture = Path.Combine(Path.GetTempPath(), "dshpp-sessions-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(fixture, "sessions", "project");
        var cache = Path.Combine(fixture, "storages", "session_projcache", "sessions");
        Directory.CreateDirectory(Path.Combine(sessions, "session-a"));
        Directory.CreateDirectory(Path.Combine(sessions, "session-b"));
        Directory.CreateDirectory(cache);
        var first = Path.Combine(cache, "a.json");
        File.WriteAllText(first, """
            {"version":7,"record":{"identity":{"createdAt":1,"cwd":"F:/Projects/harness"},"rows":{
              "title":{"ver":0,"seq":1,"val":"saved title"},
              "sessionListMetadata":{"ver":0,"seq":1,"val":{"blank":false,"lastPromptAt":1800000000000}},
              "sessionStats":{"ver":0,"seq":1,"val":{"turns":12}}
            }}}
            """);
        File.WriteAllText(Path.Combine(cache, "deleted.json"), File.ReadAllText(first));
        var saved = File.GetLastWriteTimeUtc(first);
        var result = await SessionHistory.ReadAsync(fixture, 500);
        check("session history reads official per-record title, workspace, turns and time", result.Items.Single(item => item.Id == "session-a")
            is { Title: "saved title", Workspace: "F:/Projects/harness", Turns: 12, Cached: true });
        check("session history does not resurrect cache-only deleted sessions", result.Total == 2 && result.Items.All(item => item.Id != "session-deleted"));
        check("sessions without caches remain visible without opening logs", result.Items.Single(item => item.Id == "session-b").Cached == false);
        check("history listing leaves official storage unchanged", File.GetLastWriteTimeUtc(first) == saved);
        File.WriteAllText(Path.Combine(cache, "b.json"), "{incomplete");
        var partial = await SessionHistory.ReadAsync(fixture, 500);
        check("unreadable cache is reported without losing other sessions", partial.Unreadable == 1 && partial.Items.Count == 2);
        using var broken = JsonDocument.Parse("{\"identity\":{},\"rows\":{\"title\":{\"val\":44},\"sessionStats\":{\"val\":{\"turns\":-1}}}}");
        var baseline = result.Items.First();
        var parsed = SessionHistory.ReadRecord(baseline, broken.RootElement);
        check("malformed optional cache values preserve listing metadata", parsed.Title == baseline.Title && parsed.Turns == baseline.Turns);
        using var abort = new CancellationTokenSource();
        abort.Cancel();
        var cancelled = false;
        try { await SessionHistory.ReadAsync(fixture, 500, abort.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        using var handle = File.Open(first, FileMode.Open, FileAccess.Read, FileShare.None);
        check("history read cancels without retaining an open storage handle", cancelled && handle.CanRead);
    }
}
