using System.Text;
using System.IO;
using System.Text.Json;

namespace DshControl;

public static partial class SelfCheck
{
    public static int Run()
    {
        var report = new StringBuilder();
        var failures = 0;
        void Check(string name, bool result) { report.AppendLine((result ? "PASS " : "FAIL ") + name); if (!result) failures++; }
        Check("runtime host is available even after exe-only upgrades", File.Exists(BundledAssets.Get("scripts/runtime-host.mjs")));
        Check("updater script is available even after exe-only upgrades", File.Exists(BundledAssets.Get("scripts/apply-update.ps1")));
        Check("compatibility source is available even after exe-only upgrades", !string.IsNullOrEmpty(FrontendPatch.PolyfillSource()));
        Check("pinned prerelease is preserved", !UpdateService.SpecUpdatable("0.2.1-rc.2"));
        Check("pinned GitHub tag is preserved", !UpdateService.SpecUpdatable("github:Jensen-Yao/dsh-model-palette#v0.16.3"));
        Check("local source is preserved", !new PluginItem { Name = "bridge", Spec = "link:C:/source" }.CanUpdate);
        Check("core companion is preserved", !new PluginItem { Name = "@deepseek-ai/dsh-client-store", Spec = "^0.2.1" }.CanUpdate);
        Check("npm range can update", new PluginItem { Name = "dshmarket", Spec = "^1.66.9" }.CanUpdate);
        Check("unpinned GitHub can update", new PluginItem { Name = "dsh-web", Spec = "github:author/repo" }.CanUpdate);
        Check("explicit tag accepted", UpdateService.ValidSpec("github:Jensen-Yao/dsh-model-palette#v0.16.3"));
        Check("local target rejected", !UpdateService.ValidSpec("link:C:/source"));
        Check("mixed case local target rejected", !UpdateService.ValidSpec("FILE:C:/source"));
        Check("workspace target rejected", !UpdateService.ValidSpec("workspace:*"));
        Check("lower remote version is not an update", !MainWindow.MatchesPluginStatus(new PluginItem { Installed = "2.0.0", Latest = "1.9.0" }, 2));
        Check("newer pinned version is filterable", MainWindow.MatchesPluginStatus(new PluginItem { Installed = "1.0.0", Latest = "1.1.0", Spec = "1.0.0" }, 2));
        Check("missing installation is filterable", MainWindow.MatchesPluginStatus(new PluginItem { Installed = "" }, 6));
        Check("shell payload rejected", !UpdateService.ValidSpec("latest & calc.exe"));
        Check("tokens redacted", !CommandRunner.Redact("http://host/?token=secret-value").Contains("secret-value"));
        Check("Bearer credentials redacted", !CommandRunner.Redact("Authorization: Bearer top-secret").Contains("top-secret"));
        Check("JSON credentials redacted", !CommandRunner.Redact("{\"apiKey\":\"top-secret\"}").Contains("top-secret"));
        Check("prerelease numbers compare numerically", UpdateService.IsNewer("0.2.1-rc.10", "0.2.1-rc.2"));
        Check("release supersedes prerelease", UpdateService.IsNewer("0.2.1", "0.2.1-rc.10"));
        Check("build metadata does not trigger upgrade", !UpdateService.IsNewer("0.2.1+new", "0.2.1+old"));
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = Task.Run(() => CommandRunner.RunAsync(CommandRunner.Create("node",
            new[] { "-e", "setInterval(()=>{},1000)" }, Path.GetTempPath()), TimeSpan.FromSeconds(10), cancellation: cancelled.Token)).GetAwaiter().GetResult();
        Check("cancel kills and joins child", cancellation.Cancelled && !cancellation.Ok);
        var timeout = Task.Run(() => CommandRunner.RunAsync(CommandRunner.Create("node",
            new[] { "-e", "setInterval(()=>{},1000)" }, Path.GetTempPath()), TimeSpan.FromMilliseconds(150))).GetAwaiter().GetResult();
        Check("timeout is reported separately", timeout.TimedOut && !timeout.Ok && !timeout.Cancelled);
        Task.Run(async () =>
        {
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var originalState = Environment.GetEnvironmentVariable("DSHPP_STATE_DIR");
            var fixture = Path.Combine(Path.GetTempPath(), "dshpp-maintenance-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                var app = Path.Combine(fixture, "app");
                var home = Path.Combine(fixture, "home");
                var profile = Path.Combine(home, "profiles", "web");
                var manager = Path.Combine(fixture, "bin", "node_modules", "pnpm", "bin");
                var bin = Path.Combine(app, "node_modules", "@deepseek-ai", "dsh", "lib");
                Directory.CreateDirectory(manager);
                Directory.CreateDirectory(bin);
                Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(bin, "bin.js"), "process.stdout.write('[]')");
                var cli = """
                    const fs = require('node:fs');
                    const path = require('node:path');
                    const args = process.argv.slice(2);
                    const directory = process.cwd();
                    const manifestPath = path.join(directory, 'package.json');
                    const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
                    fs.appendFileSync(path.join(directory, 'commands.log'), JSON.stringify(args)+'\n');
                    function installed(version) {
                      const destination = path.join(directory, 'node_modules', 'example-plugin');
                      fs.mkdirSync(destination, {recursive:true});
                      fs.writeFileSync(path.join(destination,'package.json'),JSON.stringify({version}));
                    }
                    if (args[0] === 'add') {
                      manifest.dependencies['example-plugin'] = args[1].split('@').slice(1).join('@');
                      fs.writeFileSync(manifestPath,JSON.stringify(manifest));
                      const version = args[1].endsWith('@2.0.0') ? '2.0.0' : '1.1.0';
                      fs.writeFileSync(path.join(directory,'pnpm-lock.yaml'),JSON.stringify({lockfileVersion:'9.0',importers:{'.':{dependencies:{'example-plugin':{specifier:manifest.dependencies['example-plugin'],version}}}},packages:{'unrelated@1.0.0':{resolution:{integrity:'unchanged-integrity'}}}}));
                      installed('2.0.0');
                      if (fs.existsSync(path.join(directory,'fail-update'))) process.exit(1);
                      if (fs.existsSync(path.join(directory,'slow-update'))) setInterval(()=>{},1000);
                      else installed(fs.existsSync(path.join(directory,'stale-update')) ? '1.1.0' : version);
                    } else if(args[0] === 'install') installed('1.1.0');
                    else process.exit(2);
                    """;
                File.WriteAllText(Path.Combine(manager, "pnpm.cjs"), cli);
                var npm = Path.Combine(fixture, "bin", "node_modules", "npm", "bin");
                Directory.CreateDirectory(npm);
                File.WriteAllText(Path.Combine(npm, "npm-cli.js"), """
                    const fs = require('node:fs');
                    const args = process.argv.slice(2);
                    if(args[0] === 'view') {
                      if(fs.existsSync('fail-query')) process.exit(1);
                      const query = args[1];
                      process.stdout.write(JSON.stringify(query.endsWith('@1.0.0') ? '1.0.0' : query.includes('@^2') || query.endsWith('@2.0.0') ? '2.0.0' : '1.1.0'));
                    } else fs.appendFileSync('npm-commands.log',JSON.stringify(args)+'\n');
                    """);
                File.WriteAllText(Path.Combine(profile, "package.json"), JsonSerializer.Serialize(new
                {
                    @private = true,
                    dependencies = new Dictionary<string, string> { ["example-plugin"] = "^1.0.0", ["fixed-plugin"] = "1.0.0", ["local-plugin"] = "link:../local" },
                }));
                File.WriteAllText(Path.Combine(profile, "pnpm-lock.yaml"), "{\"lockfileVersion\":\"9.0\",\"importers\":{\".\":{\"dependencies\":{\"example-plugin\":{\"specifier\":\"^1.0.0\",\"version\":\"1.0.0\"}}}}}");
                Environment.SetEnvironmentVariable("PATH", Path.Combine(fixture, "bin") + Path.PathSeparator + originalPath);
                Environment.SetEnvironmentVariable("DSHPP_STATE_DIR", Path.Combine(fixture, "state"));
                var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                portProbe.Start();
                var port = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port;
                portProbe.Stop();
                var config = new AppConfig { DshAppDir = app, DshHome = home, Mode = "local", Port = port };
                using var service = new DshService(config);
                var updates = new UpdateService(config, service);
                var plugin = updates.Profiles().Single().Plugins.Single(plugin => plugin.Name == "example-plugin");
                var updated = await updates.UpdatePluginsAsync(new[] { plugin });
                Check("selected plugin update completes through composition check", updated.ok);
                var commands = File.ReadAllLines(Path.Combine(profile, "commands.log"));
                Check("only selected package and resolved target are passed to package manager", commands.Length == 1 && commands[0].Contains("example-plugin@1.1.0"));
                Check("tracking range is retained in both manifest and lockfile", updates.Profiles().Single().Plugins.First().Spec == "^1.0.0"
                    && !updates.Profiles().Single().Plugins.First().SourceIssue.Contains("声明"));
                var before = File.ReadAllText(Path.Combine(profile, "package.json"));
                var beforeLock = File.ReadAllText(Path.Combine(profile, "pnpm-lock.yaml"));
                plugin = updates.Profiles().Single().Plugins.Single(item => item.Name == "example-plugin");
                using (var manifest = JsonDocument.Parse(before))
                    Check("pinned and linked dependency declarations survive", manifest.RootElement.GetProperty("dependencies").GetProperty("fixed-plugin").GetString() == "1.0.0"
                        && manifest.RootElement.GetProperty("dependencies").GetProperty("local-plugin").GetString() == "link:../local");
                File.WriteAllText(Path.Combine(profile, "fail-update"), "");
                var failure = await updates.UpdatePluginsAsync(new[] { plugin }, explicitSpec: "^2.0.0");
                Check("failed install reports failure", !failure.ok);
                Check("failed install restores exact manifest and lockfile", before == File.ReadAllText(Path.Combine(profile, "package.json"))
                    && beforeLock == File.ReadAllText(Path.Combine(profile, "pnpm-lock.yaml")));
                Check("rollback reinstalls prior dependency artifacts", updates.Profiles().Single().Plugins.First().Installed == "1.1.0");
                File.Delete(Path.Combine(profile, "fail-update"));
                File.WriteAllText(Path.Combine(profile, "slow-update"), "");
                using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var cancelledUpdate = await updates.UpdatePluginsAsync(new[] { plugin }, cancellation: abort.Token, explicitSpec: "^2.0.0");
                Check("cancelled install restores previous manifest", !cancelledUpdate.ok && before == File.ReadAllText(Path.Combine(profile, "package.json")));
                Check("each maintenance run preserves a separate recovery point", updates.Backups().Count == 3);
                using (service.AcquireOperationLock())
                {
                    var competing = await updates.UpdatePluginsAsync(new[] { plugin }, explicitSpec: "^2.0.0");
                    Check("competing maintenance is rejected without changing dependencies", !competing.ok && before == File.ReadAllText(Path.Combine(profile, "package.json")));
                }
                Check("maintenance outcomes persist across readers", updates.History().Count == 3
                    && updates.History().Count(record => record.Outcome == "完成") == 1
                    && updates.History().Count(record => record.Outcome == "已回滚") == 2);
                File.Delete(Path.Combine(profile, "slow-update"));
                var recoveryPoint = updates.Backups().First();
                using var worker = System.Diagnostics.Process.Start(CommandRunner.Create("node", new[] { "-e", "setInterval(()=>{},1000)" }, fixture));
                var interrupted = new MaintenanceRecord
                {
                    Label = "模拟中断", OwnerPid = int.MaxValue, Stage = "正在修改依赖",
                    Backups = new() { recoveryPoint }, Message = "token=not-for-export",
                    WorkerPid = worker.Id, WorkerStarted = worker.StartTime.ToUniversalTime(),
                };
                updates.SaveRecord(interrupted);
                var journal = updates.History().First(record => record.Id == interrupted.Id);
                Check("interrupted maintenance is detected and secrets redacted", journal.NeedsRecovery && !journal.Message.Contains("not-for-export"));
                var guarded = await updates.UpdatePluginsAsync(new[] { plugin }, explicitSpec: "^2.0.0");
                Check("interruption blocks dependency changes without blocking service operations", !guarded.ok && guarded.msg.Contains("服务启动不受此限制"));
                var recovered = await updates.RecoverMaintenanceAsync(journal);
                worker.Refresh();
                Check("recovery stops the orphaned installer before restoring", recovered.ok && worker.HasExited);
                Check("interrupted record becomes recovered", updates.History().Single(record => record.Id == interrupted.Id).Outcome == "已恢复"
                    && !updates.History().Any(record => record.NeedsRecovery));
                var errorBin = Path.Combine(bin, "bin.js");
                File.WriteAllText(errorBin, "process.stderr.write('token='+'x'.repeat(500));process.exit(1)");
                var invalidComposition = await updates.UpdatePluginsAsync(new[] { plugin }, explicitSpec: "^2.0.0");
                Check("shortened redacted composition errors do not overflow", !invalidComposition.ok && invalidComposition.msg.Contains("组合检查失败")
                    && !invalidComposition.msg.Contains(new string('x', 20)) && invalidComposition.msg.Contains("已恢复更新前依赖"));
                File.WriteAllText(errorBin, "process.stdout.write('[]')");
                await RunUpdateRegressionsAsync(Check, fixture, profile, home, updates);
                var unlocked = Path.Combine(home, "profiles", "unlocked");
                Directory.CreateDirectory(unlocked);
                File.WriteAllText(Path.Combine(unlocked, "package.json"), "{\"private\":true,\"dependencies\":{\"example-plugin\":\"^1.0.0\"}}");
                var unlockedBackup = updates.CreateBackup(unlocked, "无锁文件");
                var unlockedRestore = await updates.RestoreBackupAsync(unlockedBackup);
                Check("unlocked profile restores without npm ci", unlockedRestore.ok && File.ReadAllText(Path.Combine(unlocked, "npm-commands.log")).Contains("install")
                    && !File.ReadAllText(Path.Combine(unlocked, "npm-commands.log")).Contains("\"ci\""));
            }
            catch (Exception exception) { Check("maintenance fixture completes: " + exception, false); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                Environment.SetEnvironmentVariable("DSHPP_STATE_DIR", originalState);
            }
        }).GetAwaiter().GetResult();
        Task.Run(() => RunRealPackageCheckAsync(Check)).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsh-control-self-test.txt"), report.ToString());
        return failures == 0 ? 0 : 1;
    }
}
