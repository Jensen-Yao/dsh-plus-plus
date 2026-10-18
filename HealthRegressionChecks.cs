using System.IO;
using System.Net;
using System.Net.Sockets;

namespace DshControl;

public static partial class SelfCheck
{
    static async Task RunHealthRegressionsAsync(Action<string, bool> check, string fixture, string home,
        UpdateService updates, AppConfig config)
    {
        var asset = BundledAssets.Get("scripts/runtime-host.mjs");
        var preload = await CommandRunner.RunAsync(CommandRunner.Create("node", new[] { "--import", new Uri(asset).AbsoluteUri, "-e", "process.stdout.write('ready')" }, fixture), TimeSpan.FromSeconds(5));
        check("cached runtime preload allows unrelated node package-manager commands", preload.Ok && preload.StandardOutput == "ready");
        var explanation = HealthChecks.ExplainManagerFailure(new(1, "ERR_MODULE_NOT_FOUND runtime-host.mjs token=secret"));
        check("missing preload is diagnosed separately from missing pnpm", explanation.Contains("启动脚本已丢失") && !explanation.Contains("token=secret"));
        var client = Path.Combine(fixture, "client-plugin");
        Directory.CreateDirectory(client);
        File.WriteAllText(Path.Combine(client, "package.json"), """
            {"version":"1.0.0","dsh":{"client":{}},"exports":{"./client":{"types":"./lib/client.d.ts","default":"./lib/client.js"}}}
            """);
        var plugin = new PluginItem { Name = "bridge", Spec = "link:../source", Installed = "1.0.0", Profile = "web", PackageDirectory = client };
        var missing = HealthChecks.InspectPlugin(plugin);
        check("missing linked client bundle is detected with build guidance", missing.Count == 1 && missing[0].Level == "错误" && missing[0].Detail.Contains("build"));
        Directory.CreateDirectory(Path.Combine(client, "lib"));
        File.WriteAllText(Path.Combine(client, "lib", "client.js"), "export {};");
        check("built client removes the missing-artifact finding", HealthChecks.InspectPlugin(plugin).Count == 0);
        var comparison = ProfileDifference.Compare(new[]
        {
            new PluginItem { Name = "same", Profile = "web", Spec = "^1.0.0", Installed = "1.0.0" },
            new PluginItem { Name = "same", Profile = "desktop", Spec = "1.0.0", Installed = "1.0.0" },
            new PluginItem { Name = "only-web", Profile = "web", Spec = "^2.0.0", Installed = "2.0.0" },
            new PluginItem { Name = "child", Profile = "web", OwnerName = "same", Installed = "1.0.0" },
        });
        check("profile comparison distinguishes declaration drift and single-host packages", comparison.Count == 2
            && comparison.Single(item => item.Name == "same").State == "声明不同" && comparison.Single(item => item.Name == "only-web").State == "仅 Web");
        var desktop = Path.Combine(home, "profiles", "desktop");
        Directory.CreateDirectory(desktop);
        File.WriteAllText(Path.Combine(desktop, "package.json"), "{\"dependencies\":{\"example-plugin\":\"^1.0.0\"}}");
        var manifestBefore = File.ReadAllText(Path.Combine(desktop, "package.json"));
        var scope = UpdateService.MaintenanceProfiles(new[] { desktop }, home, config.DshAppDir);
        check("desktop-only maintenance targets no web service", scope.SequenceEqual(new[] { "desktop" }));
        check("mixed profile maintenance includes both hosts", UpdateService.MaintenanceProfiles(new[] { desktop, config.DshAppDir }, home, config.DshAppDir)
            .Order().SequenceEqual(new[] { "desktop", "web" }));
        var listeners = " TCP 100.72.0.1:3080 0.0.0.0:0 LISTENING 50\n TCP 127.0.0.1:3080 0.0.0.0:0 LISTENING 60\n";
        check("loopback backend is preferred to remote relay without saved state", DshService.SelectListener(listeners, 3080, 0).Pid == 60);
        check("recorded backend is preferred when several addresses listen", DshService.SelectListener(listeners, 3080, 50).Pid == 50);
        var impact = MainWindow.MaintenanceImpact(new[] { "desktop" }, true, true, true);
        check("desktop maintenance preview identifies the affected host", impact.Contains("桌面版") && !impact.Contains("Web 服务") && impact.Contains("验证"));
        using var web = new TcpListener(IPAddress.Loopback, config.Port);
        web.Start();
        var desktopPlugin = updates.Profiles().Single(item => item.Name == "desktop").Plugins.Single();
        var failed = await updates.UpdatePluginsAsync(new[] { desktopPlugin }, explicitSpec: "^2.0.0");
        check("desktop validation failure restores dependencies without trying to stop the web listener", !failed.ok
            && failed.msg.Contains("已恢复更新前依赖") && !failed.msg.Contains("恢复未完成")
            && File.ReadAllText(Path.Combine(desktop, "package.json")) == manifestBefore);
        using var connection = new TcpClient();
        await connection.ConnectAsync(IPAddress.Loopback, config.Port);
        check("web listener survives desktop rollback", connection.Connected);
        using (var maintenance = new DshService(config))
        using (maintenance.AcquireOperationLock())
        {
            var rejected = await updates.UpdatePluginsAsync(new[] { desktopPlugin }, explicitSpec: "^2.0.0");
            check("maintenance conflict leaves desktop declarations untouched", !rejected.ok && File.ReadAllText(Path.Combine(desktop, "package.json")) == manifestBefore);
        }
    }
}
