using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DshControl;

public static partial class SelfCheck
{
    /// <summary>Uses a loopback-only registry to exercise real npm/pnpm resolution and frozen restoration.</summary>
    static async Task RunRealPackageCheckAsync(Action<string, bool> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dshpp-pnpm-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process registry = null;
        var originalState = Environment.GetEnvironmentVariable("DSHPP_STATE_DIR");
        try
        {
            var name = "dshpp-isolated-test-plugin";
            foreach (var version in new[] { "1.0.0", "1.1.0" })
            {
                var source = Path.Combine(directory, "source-" + version);
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "package.json"), JsonSerializer.Serialize(new { name, version }));
                var packed = await CommandRunner.RunAsync(CommandRunner.PackageManager("npm", new[] { "pack", "--json", "--ignore-scripts", "--pack-destination", directory }, source), TimeSpan.FromSeconds(30));
                if (!packed.Ok) throw new InvalidOperationException("fixture 打包失败：" + packed.Output);
            }
            var script = Path.Combine(directory, "registry.mjs");
            File.WriteAllText(script, """
                import http from 'node:http';
                import fs from 'node:fs';
                import path from 'node:path';
                const directory = process.argv[2];
                const name = 'dshpp-isolated-test-plugin';
                const server = http.createServer((request,response)=>{
                  const origin = 'http://127.0.0.1:'+server.address().port;
                  if (request.url === '/'+name) {
                    const versions = Object.fromEntries(['1.0.0','1.1.0'].map(version=>[version,{name,version,dist:{tarball:origin+'/'+name+'-'+version+'.tgz'}}]));
                    response.setHeader('content-type','application/json');
                    response.end(JSON.stringify({name,'dist-tags':{latest:'1.1.0'},versions,time:{'1.0.0':'2020-01-01T00:00:00.000Z','1.1.0':'2020-01-02T00:00:00.000Z'}}));
                  } else if (/^\/dshpp-isolated-test-plugin-1\.(0|1)\.0\.tgz$/.test(request.url)) {
                    response.end(fs.readFileSync(path.join(directory,path.basename(request.url))));
                  } else {response.writeHead(404);response.end('{}');}
                });
                server.listen(0,'127.0.0.1',()=>fs.writeFileSync(path.join(directory,'port.txt'),String(server.address().port)));
                """);
            registry = Process.Start(CommandRunner.Create("node", new[] { script, directory }, directory));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var portPath = Path.Combine(directory, "port.txt");
            while (!File.Exists(portPath) && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (!File.Exists(portPath)) throw new InvalidOperationException("fixture registry 未启动。");
            var home = Path.Combine(directory, "home");
            var profile = Path.Combine(home, "profiles", "web");
            var app = Path.Combine(directory, "app");
            var bin = Path.Combine(app, "node_modules", "@deepseek-ai", "dsh", "lib");
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "bin.js"), "process.stdout.write('[]')");
            File.WriteAllText(Path.Combine(profile, "package.json"), JsonSerializer.Serialize(new { @private = true, dependencies = new Dictionary<string, string> { [name] = "^1.0.0" } }));
            File.WriteAllText(Path.Combine(profile, ".npmrc"), "registry=http://127.0.0.1:" + File.ReadAllText(portPath) + "\n");
            File.WriteAllText(Path.Combine(profile, "pnpm-workspace.yaml"), "packages:\n  - .\nminimumReleaseAge: 0\nautoInstallPeers: false\n");
            Environment.SetEnvironmentVariable("DSHPP_STATE_DIR", Path.Combine(directory, "state"));
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var service = new DshService(new AppConfig { DshAppDir = app, DshHome = home, Mode = "local", Port = port });
            var updates = new UpdateService(service.Cfg, service);
            var plugin = updates.Profiles().Single().Plugins.Single();
            var result = await updates.UpdatePluginsAsync(new[] { plugin });
            check("real npm/pnpm install reaches the pre-resolved target", result.ok && updates.Profiles().Single().Plugins.Single().Installed == "1.1.0");
            if (!result.ok) throw new InvalidOperationException(result.msg);
            var locked = updates.Profiles().Single().Plugins.Single();
            check("real pnpm lock retains original range and registry resolution", locked.Spec == "^1.0.0" && locked.SourceIssue.Length == 0 && locked.ResolvedSource == "1.1.0");
            var frozen = await CommandRunner.RunAsync(CommandRunner.PackageManager("pnpm", new[] { "install", "--frozen-lockfile", "--offline", "--ignore-scripts", "--reporter=append-only" }, profile), TimeSpan.FromSeconds(30));
            check("real pnpm accepts frozen offline install after tracking restoration", frozen.Ok);
            if (!frozen.Ok) throw new InvalidOperationException(frozen.Output);
        }
        catch (Exception error) { check("real package-manager integration: " + error.Message, false); }
        finally
        {
            if (registry != null)
            {
                if (!registry.HasExited) registry.Kill(true);
                await registry.WaitForExitAsync();
                registry.Dispose();
            }
            Environment.SetEnvironmentVariable("DSHPP_STATE_DIR", originalState);
        }
    }
}
