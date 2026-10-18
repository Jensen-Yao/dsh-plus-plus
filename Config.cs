using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshControl;

public class AppConfig
{
    public bool LiquidGlass { get; set; } = true;
    public bool AnimateBrand { get; set; } = true;
    public int SessionHistoryLimit { get; set; } = 500;
    public string DshAppDir { get; set; } = @"F:\DeepSeek harness\dsh-app";
    public string DshDesktopDir { get; set; } = @"F:\dsh-desktop";
    public int Port { get; set; } = 3080;
    public string Mode { get; set; } = "local";
    public string AccessMode { get; set; } = "ip";   // ip | domain | tailscale
    public string DisplayHost { get; set; } = "";     // 自定义域名（AccessMode=domain 时使用）
    public bool AutoTunnel { get; set; } = false;
    public bool RequirePairing { get; set; } = true;
    public string Theme { get; set; } = "dark";       // dark | light（dsh++ 双主题）
    public string DshHome { get; set; } = "";         // DSH_HOME 总根（空 = 默认 ~/.dsh）
    public string AgentsHome { get; set; } = "";      // DSH_AGENTS_HOME（空 = 默认 ~/.agents）
    public string TailscalePath { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";
    public bool AutoUpdatePlugins { get; set; } = false;
    public bool CheckUpdatesOnLaunch { get; set; } = true;
    public bool RestartAfterUpdate { get; set; } = true;
    public string BindHost { get; set; } = "";
    public int StartupTimeoutSeconds { get; set; } = 60;
    public int UpdateTimeoutMinutes { get; set; } = 15;
    public bool BrowserCompatibilityPatch { get; set; } = true;

    // 窗口状态记忆（NaN = 未保存过，用默认位置）
    public double WinLeft { get; set; } = double.NaN;
    public double WinTop { get; set; } = double.NaN;
    public double WinWidth { get; set; } = double.NaN;
    public double WinHeight { get; set; } = double.NaN;
    public bool WinMaximized { get; set; }
    public int LastPage { get; set; } = 0;

    // Freebuff 组件路径（可按机器调整）
    public string FreebuffDir { get; set; } = @"F:\freebuffapi";
    public string FreebuffDocker { get; set; } = @"E:\DockerDesktop\Docker Desktop.exe";

    public static string ConfigDir => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSHPP_STATE_DIR"))
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dsh-control")
        : Path.GetFullPath(Environment.GetEnvironmentVariable("DSHPP_STATE_DIR"));
    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (cfg != null)
                {
                    // 旧版本迁移：之前填过域名则切到「自定义域名」模式
                    if (string.IsNullOrEmpty(cfg.AccessMode))
                        cfg.AccessMode = string.IsNullOrEmpty(cfg.DisplayHost) ? "ip" : "domain";
                    cfg.AutoUpdatePlugins = false;
                    cfg.Port = Math.Clamp(cfg.Port, 1024, 65535);
                    cfg.StartupTimeoutSeconds = Math.Clamp(cfg.StartupTimeoutSeconds, 10, 180);
                    cfg.UpdateTimeoutMinutes = Math.Clamp(cfg.UpdateTimeoutMinutes, 1, 60);
                    return cfg;
                }
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            var temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, ConfigPath, true);
        }
        catch { }
    }
}

