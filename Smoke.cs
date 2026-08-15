using System;
using System.IO;
using System.Text;

namespace DshControl;

/// <summary>
/// 无窗口自检：dsh-plus-plus.exe --smoke 会把这些诊断写到
/// %TEMP%\dsh-control-smoke.txt 后退出，用于验证部署环境。
/// </summary>
public static class Smoke
{
    public static void Run()
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        try
        {
            var cfg = AppConfig.Load();
            W("config.json=" + AppConfig.ConfigPath);
            W("DshAppDir=" + cfg.DshAppDir + "  exists=" + Directory.Exists(cfg.DshAppDir));
            W("Port=" + cfg.Port + "  Mode=" + cfg.Mode + "  AutoTunnel=" + cfg.AutoTunnel + "  DisplayHost=" + (cfg.DisplayHost ?? ""));
            W("TailscalePath=" + cfg.TailscalePath + "  exists=" + File.Exists(cfg.TailscalePath));

            var binJs = Path.Combine(cfg.DshAppDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            W("bin.js exists=" + File.Exists(binJs));
            var lanPatch = Path.Combine(cfg.DshAppDir, "lan.patch.yml");
            W("lan.patch.yml exists=" + File.Exists(lanPatch));

            using var svc = new DshService(cfg);
            W("LAN IP=" + (svc.DetectLanIp() ?? "(none)"));
            W("PortListening=" + svc.IsDshRunning() + "  pid=" + svc.GetPidOnPort(cfg.Port));
            W("DisplayHost=" + svc.DisplayHost());
            W("LanUrl=" + svc.LanUrl());
            W("LocalUrl=" + svc.LocalUrl());
            W("TailscaleInstalled=" + TailscaleCli.Exists(cfg));
            W("BuildDshCommand=" + svc.BuildDshCommand());
            W("Theme=" + cfg.Theme);
            var rows = svc.StorageRows();
            W("StorageRows=" + rows.Count);
            foreach (var r in rows)
                W("  storage: " + r.Name);
            W("SMOKE OK");
        }
        catch (Exception ex)
        {
            W("SMOKE FAIL: " + ex);
        }

        var outPath = Path.Combine(Path.GetTempPath(), "dsh-control-smoke.txt");
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
    }
}
