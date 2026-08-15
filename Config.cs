using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DshControl;

public class AppConfig
{
    public string DshAppDir { get; set; } = @"F:\DeepSeek harness\dsh-app";
    public int Port { get; set; } = 3080;
    public string Mode { get; set; } = "lan";        // lan | local
    public string AccessMode { get; set; } = "ip";   // ip | domain | tailscale
    public string DisplayHost { get; set; } = "";     // 自定义域名（AccessMode=domain 时使用）
    public bool AutoTunnel { get; set; } = true;
    public bool RequirePairing { get; set; } = false; // false = 免扫码直连（关闭配对门禁）
    public string Theme { get; set; } = "dark";       // dark | light（dsh++ 双主题）
    public string DshHome { get; set; } = "";         // DSH_HOME 总根（空 = 默认 ~/.dsh）
    public string AgentsHome { get; set; } = "";      // DSH_AGENTS_HOME（空 = 默认 ~/.agents）
    public string TailscalePath { get; set; } = @"C:\Program Files\Tailscale\tailscale.exe";

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dsh-control");
    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (cfg != null)
                {
                    // 旧版本迁移：之前填过域名则切到「自定义域名」模式
                    if (string.IsNullOrEmpty(cfg.AccessMode))
                        cfg.AccessMode = string.IsNullOrEmpty(cfg.DisplayHost) ? "ip" : "domain";
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
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
        }
        catch { }
    }
}

