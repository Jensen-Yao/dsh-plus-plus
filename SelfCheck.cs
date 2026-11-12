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
        var runtimeAsset = BundledAssets.Get("scripts/runtime-host.mjs");
        Check("runtime preload never depends on the build or install directory", runtimeAsset.StartsWith(Path.Combine(AppConfig.ConfigDir, "runtime-assets"), StringComparison.OrdinalIgnoreCase)
            && !runtimeAsset.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
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
        var localAdvance = new PluginItem { Name = "side-chat", Spec = "link:F:/source", Installed = "0.1.0", Latest = "0.1.2" };
        localAdvance.RemoteIdentityVerified = true;
        Check("verified local plugin can switch to a checked remote release", localAdvance.CanAdvance && localAdvance.AdvanceTarget == "0.1.2" && !localAdvance.CanUpdate);
        var impersonatedLocal = new PluginItem { Name = "same-name", Spec = "link:F:/source", Installed = "0.1.0", Latest = "9.0.0" };
        Check("unverified npm same-name package cannot replace a local plugin", !impersonatedLocal.CanAdvance);
        var pinnedAdvance = new PluginItem { Name = "palette", Spec = "github:owner/repo#v0.1.0", Installed = "v0.1.0", Latest = "v0.1.2" };
        Check("pinned Git tag advances to a newer tag", pinnedAdvance.CanAdvance && pinnedAdvance.AdvanceTarget == "github:owner/repo#v0.1.2");
        var rangeLimited = new PluginItem { Name = "dsh-web-all", Spec = "^0.4.16", Installed = "0.4.16", Latest = "0.5.0", TargetVersion = "0.4.16" };
        Check("range-limited update exposes target, latest, and guidance", rangeLimited.DeclarationRestricted
            && rangeLimited.Description.Contains("声明内最高：0.4.16") && rangeLimited.Description.Contains("远端最新：0.5.0")
            && rangeLimited.Description.Contains("「升级到最新」"));
        var versionPinned = new PluginItem { Name = "modlens", Spec = "3.27.3", Installed = "3.27.3", Latest = "3.28.0", TargetVersion = "3.27.3" };
        Check("pinned npm version is fixed, not range-limited", versionPinned.IsPinned && !versionPinned.DeclarationRestricted
            && versionPinned.Description.Contains("版本已固定"));
        Check("git tag pin is not treated as npm pin", !pinnedAdvance.IsPinned);
        Check("floating declaration is derived from the remote version", UpdateService.FloatingSpec("^0.4.16", "0.5.0") == "^0.5.0"
            && UpdateService.FloatingSpec("npm:alias@^1.0.0", "1.2.0") == "npm:alias@^1.2.0");
        Check("zero-major floating ranges carry a scope note", AdvanceSpecDialog.RangeNote("0.5.0").Length > 0 && AdvanceSpecDialog.RangeNote("v1.2.0").Length == 0);
        var feedConfig = DesktopService.ReadDesktopUpdateConfig("provider: generic\nurl: https://download.deepseek.com/dsh-desk/feeds/win-x64/\nchannel: nightly\n");
        Check("desktop feed URL follows the configured channel", feedConfig.Url == "https://download.deepseek.com/dsh-desk/feeds/win-x64/" && feedConfig.Channel == "nightly");
        var feed = DesktopService.ReadDesktopUpdateFeed("version: 0.2.2-rc.1\nfiles:\n  - url: https://download.deepseek.com/dsh-desk/bin/win-x64/app.exe\n    size: 123\n    sha512: abcd\nreleaseDate: '2026-11-04T09:31:49.677Z'\n");
        Check("desktop update feed exposes verified installer metadata", feed.Version == "0.2.2-rc.1" && feed.Size == 123 && feed.Sha512 == "abcd");
        var asar = Path.Combine(Path.GetTempPath(), "dshpp-asar-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var package = Encoding.UTF8.GetBytes("{\"version\":\"0.2.1-rc.2\"}");
            var header = Encoding.UTF8.GetBytes("{\"files\":{\"dsh\":{\"files\":{\"package.json\":{\"size\":" + package.Length + ",\"offset\":\"0\"}}}}}");
            var prefix = new byte[16];
            BitConverter.GetBytes(4).CopyTo(prefix, 0);
            BitConverter.GetBytes(header.Length + 8).CopyTo(prefix, 4);
            BitConverter.GetBytes(header.Length + 4).CopyTo(prefix, 8);
            BitConverter.GetBytes(header.Length).CopyTo(prefix, 12);
            File.WriteAllBytes(asar, prefix.Concat(header).Concat(package).ToArray());
            Check("desktop version reads the bundled runtime manifest", DesktopService.ReadAsarPackageVersion(asar) == "0.2.1-rc.2");
        }
        finally { File.Delete(asar); }
        var cancelled = new CancellationTokenSource();
        var commandFailure = CommandRunner.DescribeFailure("测试命令", "npm view example", new CommandResult(17, "token=secret"));
        Check("command failures include stage, exit code, command, output, and redaction", commandFailure.Contains("测试命令失败") && commandFailure.Contains("退出码 17") && commandFailure.Contains("npm view example") && !commandFailure.Contains("secret"));
        Check("HTTP failures include status, reason, and body length", CommandRunner.DescribeHttpStatus("测试下载", 503, "Service Unavailable", 128).Contains("HTTP 503 Service Unavailable") && CommandRunner.DescribeHttpStatus("测试下载", 503, "Service Unavailable", 128).Contains("Content-Length 128"));
        Check("hash failures include both digests", CommandRunner.DescribeHashFailure("测试校验", "SHA-512", "aa", "bb").Contains("期望：aa") && CommandRunner.DescribeHashFailure("测试校验", "SHA-512", "aa", "bb").Contains("实际：bb"));
        Check("size failures include expected and actual bytes", CommandRunner.DescribeSizeFailure("测试下载", 1024, 512).Contains("期望 1024 字节") && CommandRunner.DescribeSizeFailure("测试下载", 1024, 512).Contains("实际 512 字节"));
        Check("long command output keeps beginning and end", CommandRunner.Brief(new string('a', 1000) + "END", 100).StartsWith(new string('a', 70)) && CommandRunner.Brief(new string('a', 1000) + "END", 100).EndsWith("END"));
        Check("invalid JSON registry output is retained", CommandRunner.Brief("{\"error\":\"E404\"}").Contains("E404"));
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
                var config = new AppConfig { DshAppDir = app, DshHome = home, DshDesktopDir = Path.Combine(fixture, "missing-desktop"), Mode = "local", Port = port };
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
                await RunHealthRegressionsAsync(Check, fixture, home, updates, config);
            }
            catch (Exception exception) { Check("maintenance fixture completes: " + exception, false); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                Environment.SetEnvironmentVariable("DSHPP_STATE_DIR", originalState);
            }
        }).GetAwaiter().GetResult();
        Task.Run(() => RunRealPackageCheckAsync(Check)).GetAwaiter().GetResult();
        Task.Run(() => RunSessionHistoryChecksAsync(Check)).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "dsh-control-self-test.txt"), report.ToString());
        return failures == 0 ? 0 : 1;
    }
}
