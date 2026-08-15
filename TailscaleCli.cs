using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DshControl;

public class TsStatus
{
    public string BackendState = "";
    public bool Online;
    public string HostName = "";
    public string DnsName = "";
    public List<string> Ips = new();
    public string Raw = "";
    public string Error = "";
}

public static class TailscaleCli
{
    public static bool Exists(AppConfig cfg) => File.Exists(cfg.TailscalePath);

    public static (int code, string output) Run(AppConfig cfg, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cfg.TailscalePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            var outp = p.StandardOutput.ReadToEnd();
            var errp = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000))
            {
                try { p.Kill(true); } catch { }
                return (-1, "命令超时");
            }
            return (p.ExitCode, (outp + "\n" + errp).Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    public static TsStatus Status(AppConfig cfg)
    {
        var st = new TsStatus();
        if (!Exists(cfg))
        {
            st.Error = "未安装";
            return st;
        }
        var r = Run(cfg, "status --json");
        if (r.code != 0)
        {
            st.Error = "tailscale 未运行或未登录";
            st.Raw = r.output;
            return st;
        }
        try
        {
            using var doc = JsonDocument.Parse(r.output);
            var root = doc.RootElement;
            if (root.TryGetProperty("BackendState", out var bs)) st.BackendState = bs.GetString() ?? "";
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                if (self.TryGetProperty("Online", out var on) && (on.ValueKind == JsonValueKind.True || on.ValueKind == JsonValueKind.False))
                    st.Online = on.GetBoolean();
                if (self.TryGetProperty("HostName", out var hn) && hn.ValueKind == JsonValueKind.String)
                    st.HostName = hn.GetString() ?? "";
                if (self.TryGetProperty("DNSName", out var dn) && dn.ValueKind == JsonValueKind.String)
                    st.DnsName = dn.GetString() ?? "";
                if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                    foreach (var ip in ips.EnumerateArray())
                        if (ip.ValueKind == JsonValueKind.String && ip.GetString() is string s && !s.Contains(':'))
                            st.Ips.Add(s);
            }
        }
        catch (Exception ex)
        {
            st.Error = "状态解析失败: " + ex.Message;
        }
        return st;
    }
}
