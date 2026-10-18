using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DshControl;

/// <summary>A durable record of dependency changes and their recovery points, not a user-data backup.</summary>
public class MaintenanceRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? Completed { get; set; }
    public int OwnerPid { get; set; } = Environment.ProcessId;
    public DateTime OwnerStarted { get; set; } = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    public string Stage { get; set; } = "准备中";
    public string Outcome { get; set; } = "进行中";
    public string Message { get; set; } = "";
    public int WorkerPid { get; set; }
    public DateTime WorkerStarted { get; set; }
    public List<MaintenanceBackup> Backups { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OwnerAlive
    {
        get
        {
            try
            {
                using var process = Process.GetProcessById(OwnerPid);
                return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - OwnerStarted).TotalSeconds) < 1;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsRecovery => Backups.Count > 0 && (Outcome == "恢复未完成" || (Completed == null && !OwnerAlive));
    [System.Text.Json.Serialization.JsonIgnore]
    public string Status => NeedsRecovery ? "待恢复" : Outcome;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => $"{Created.ToLocalTime():MM-dd HH:mm} · {Label} · {Status}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Detail => $"{Stage}\n{Message}\n" + string.Join("\n", Backups.Select(backup => backup.Directory));

    internal async Task StopWorkerAsync()
    {
        if (WorkerPid <= 0) return;
        try
        {
            using var worker = Process.GetProcessById(WorkerPid);
            if (worker.HasExited || Math.Abs((worker.StartTime.ToUniversalTime() - WorkerStarted).TotalSeconds) >= 1
                || !worker.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase)) return;
            worker.Kill(true);
            await worker.WaitForExitAsync();
        }
        catch (ArgumentException) { }
    }
}

public partial class UpdateService
{
    string HistoryRoot => Path.Combine(AppConfig.ConfigDir, "history");

    internal void SaveRecord(MaintenanceRecord record)
    {
        Directory.CreateDirectory(HistoryRoot);
        var path = Path.Combine(HistoryRoot, record.Id + ".json");
        var temporary = path + ".tmp";
        record.Message = CommandRunner.Redact(record.Message);
        File.WriteAllText(temporary, JsonSerializer.Serialize(record));
        File.Move(temporary, path, true);
    }

    public List<MaintenanceRecord> History()
    {
        if (!Directory.Exists(HistoryRoot)) return new();
        var result = new List<MaintenanceRecord>();
        foreach (var path in Directory.EnumerateFiles(HistoryRoot, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<MaintenanceRecord>(File.ReadAllText(path));
                if (record != null) result.Add(record);
            }
            catch (Exception error) when (error is IOException or JsonException)
            { Log("维护记录无法读取：" + Path.GetFileName(path)); }
        }
        return result.OrderByDescending(record => record.Created).ToList();
    }

    public async Task<(bool ok, string msg)> RecoverMaintenanceAsync(MaintenanceRecord record,
        Action<string> progress = null, CancellationToken cancellation = default)
    {
        if (!record.NeedsRecovery) return (false, "该维护记录不需要恢复。");
        var result = await MaintainAsync(record.Backups.Select(backup => backup.Directory).Distinct().ToList(),
            "恢复中断任务 · " + record.Label, async () =>
            {
                foreach (var backup in record.Backups.AsEnumerable().Reverse()) await RestoreFilesAsync(backup, progress);
            }, progress, cancellation, recovery: true, beforeBackup: record.StopWorkerAsync);
        if (result.ok)
        {
            record.Outcome = "已恢复";
            record.Completed = DateTime.UtcNow;
            record.Stage = "依赖已恢复";
            record.Message = result.msg;
            SaveRecord(record);
        }
        return result;
    }
}
