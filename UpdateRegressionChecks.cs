using System.IO;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace DshControl;

public static partial class SelfCheck
{
    static async Task RunUpdateRegressionsAsync(Action<string, bool> check, string fixture, string profile, string home, UpdateService updates)
    {
        var plugin = updates.Profiles().Single().Plugins.Single(item => item.Name == "example-plugin");
        var commandPath = Path.Combine(profile, "commands.log");
        var beforeCommands = File.ReadAllLines(commandPath).Length;
        var before = File.ReadAllText(Path.Combine(profile, "package.json"));
        var beforeLock = File.ReadAllText(Path.Combine(profile, "pnpm-lock.yaml"));
        var backupCount = updates.Backups().Count;
        var noChange = await updates.UpdatePluginsAsync(new[] { plugin });
        check("current target is a true no-op without installer or backups", noChange.ok && noChange.msg.Contains("未修改")
            && File.ReadAllLines(commandPath).Length == beforeCommands && updates.Backups().Count == backupCount);
        File.WriteAllText(Path.Combine(profile, "fail-query"), "");
        var unresolved = await updates.UpdatePluginsAsync(new[] { plugin }, explicitSpec: "^2.0.0");
        check("failed target query changes nothing and creates no recovery work", !unresolved.ok && updates.Backups().Count == backupCount
            && File.ReadAllText(Path.Combine(profile, "package.json")) == before);
        File.Delete(Path.Combine(profile, "fail-query"));
        File.WriteAllText(Path.Combine(profile, "stale-update"), "");
        var stale = await updates.UpdatePluginsAsync(new[] { plugin }, explicitSpec: "^2.0.0");
        check("exit zero with old artifact fails target verification and rolls back", !stale.ok && stale.msg.Contains("目标 2.0.0")
            && stale.msg.Contains("实际 1.1.0") && File.ReadAllText(Path.Combine(profile, "package.json")) == before
            && File.ReadAllText(Path.Combine(profile, "pnpm-lock.yaml")) == beforeLock);
        File.Delete(Path.Combine(profile, "stale-update"));

        var bundleProfile = Path.Combine(home, "profiles", "bundle-check");
        Directory.CreateDirectory(bundleProfile);
        File.WriteAllText(Path.Combine(bundleProfile, "package.json"), "{\"dependencies\":{\"wrapper\":\"github:owner/repo\",\"linked\":\"link:../local\"}}");
        var wrapper = Path.Combine(bundleProfile, "node_modules", "wrapper");
        var aggregate = Path.Combine(bundleProfile, "node_modules", "@author", "all");
        var child = Path.Combine(bundleProfile, "node_modules", "@author", "child");
        foreach (var directory in new[] { wrapper, aggregate, child }) Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(wrapper, "package.json"), "{\"name\":\"wrapper\",\"version\":\"1.0.0\",\"dsh\":{\"bundle\":{}},\"dependencies\":{\"@author/all\":\"^1.0.0\"}}");
        File.WriteAllText(Path.Combine(aggregate, "package.json"), "{\"name\":\"@author/all\",\"version\":\"1.0.0\",\"dsh\":{\"bundle\":{}},\"dependencies\":{\"@author/child\":\"^1.0.0\",\"wrapper\":\"^1.0.0\"}}");
        File.WriteAllText(Path.Combine(child, "package.json"), "{\"name\":\"@author/child\",\"version\":\"1.0.0\",\"dsh\":{\"client\":{}}}");
        File.WriteAllText(Path.Combine(bundleProfile, "pnpm-lock.yaml"), """
            lockfileVersion: '9.0'
            importers:
              .:
                dependencies:
                  wrapper:
                    specifier: github:owner/repo
                    version: file:../../packages/old-archive.tar.gz
            """);
        var discovered = updates.Profiles().Single(item => item.Name == "bundle-check");
        check("bundle discovery follows wrappers and nested aggregates without cycles", discovered.Plugins.Count == 4
            && discovered.Plugins.Single(item => item.Name == "@author/child").OwnerName == "wrapper");
        check("GitHub declaration pointing at local archive is visible", discovered.Plugins.Single(item => item.Name == "wrapper").SourceIssue.Contains("本地归档"));
        check("indirect plugins are reported but cannot become accidental direct dependencies", discovered.Plugins.Where(item => item.IsIndirect).All(item => !item.CanUpdate));
        var children = discovered.Plugins.Where(item => item.IsIndirect).ToList();
        var summary = await updates.CheckPluginsAsync(children);
        check("registry checks discover newer aggregate children", summary.Failed == 0 && summary.Updates == 2 && children.All(item => item.CheckedAt.HasValue));
        var preserved = new PluginItem();
        preserved.CopyCheckFrom(children[0]);
        check("refresh can retain checked metadata", preserved.CheckedAt == children[0].CheckedAt && preserved.Latest == "1.1.0");
        var failurePlugin = updates.Profiles().Single(item => item.Name == "web").Plugins.First();
        File.WriteAllText(Path.Combine(profile, "fail-query"), "");
        var failedSummary = await updates.CheckPluginsAsync(new[] { failurePlugin });
        check("failed read-only checks never claim current", failedSummary.Failed == 1 && failurePlugin.Latest == "检查失败" && failurePlugin.CheckError.Length > 0 && failurePlugin.CheckedAt.HasValue);
        File.Delete(Path.Combine(profile, "fail-query"));
        var references = UpdateService.ParseGitRefs(new string('a', 40) + "\trefs/tags/v1.0.0\n" + new string('b', 40) + "\trefs/tags/v1.0.0^{}\n");
        check("annotated tags resolve to peeled commit", UpdateService.ResolveGitCommit(UpdateService.ParseGitReference("github:owner/repo#v1.0.0"), references) == new string('b', 40));
        check("local archive filename is not mistaken for Git commit", PluginLock.Commit("file:../../old-" + new string('a', 40) + ".tgz") == "");
        check("Git tarball locks expose the exact commit", PluginLock.Commit("https://codeload.github.com/owner/repo/tar.gz/" + new string('c', 40)) == new string('c', 40));

        var realGit = Path.Combine(fixture, "git-tracking");
        Directory.CreateDirectory(realGit);
        File.WriteAllText(Path.Combine(realGit, "package.json"), "{\"dependencies\":{\"git-plugin\":\"github:owner/repo#" + new string('b', 40) + "\",\"untouched\":\"1.0.0\"}}");
        File.WriteAllText(Path.Combine(realGit, "pnpm-lock.yaml"), """
            lockfileVersion: '9.0'
            importers:
              .:
                dependencies:
                  git-plugin:
                    specifier: pinned-temporarily
                    version: https://codeload.github.com/owner/repo/tar.gz/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
                  untouched:
                    specifier: 1.0.0
                    version: 1.0.0
            packages:
              untouched@1.0.0:
                resolution:
                  integrity: preserved-integrity
            """);
        PluginLock.RestoreTrackingSpec(realGit, "git-plugin", "github:owner/repo#main");
        var tracked = new PluginItem { Name = "git-plugin", Spec = "github:owner/repo#main" };
        PluginLock.Read(realGit).Inspect(tracked);
        check("fresh Git resolution keeps floating branch declaration and exact commit", tracked.SourceIssue == "" && tracked.InstalledCommit == new string('b', 40)
            && File.ReadAllText(Path.Combine(realGit, "package.json")).Contains("github:owner/repo#main"));
        using var reader = File.OpenText(Path.Combine(realGit, "pnpm-lock.yaml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var integrity = PluginLock.Child(PluginLock.Child(PluginLock.Child(yaml.Documents[0].RootNode, "packages"), "untouched@1.0.0"), "resolution");
        check("tracking restoration preserves unrelated lockfile integrity", ((YamlScalarNode)PluginLock.Child(integrity, "integrity")).Value == "preserved-integrity");
        reader.Close();
        File.WriteAllText(Path.Combine(realGit, "pnpm-lock.yaml"), "not: [valid");
        check("malformed lockfile is reported rather than silently trusted", PluginLock.Read(realGit).Error.Length > 0);

        var syncWeb = Path.Combine(fixture, "sync-web");
        var syncDesktop = Path.Combine(fixture, "sync-desktop");
        Directory.CreateDirectory(syncWeb);
        Directory.CreateDirectory(syncDesktop);
        Directory.CreateDirectory(Path.Combine(syncWeb, "node_modules", "range-plugin"));
        File.WriteAllText(Path.Combine(syncWeb, "node_modules", "range-plugin", "package.json"), "{\"name\":\"range-plugin\",\"version\":\"1.2.3\"}");
        File.WriteAllText(Path.Combine(syncWeb, "package.json"), """
        {
          "name":"web","private":true,
          "dependencies":{
            "@deepseek-ai/ignored":"0.2.1-rc.2",
            "electron":"^44.0.0",
            "range-plugin":"^1.0.0",
            "git-plugin":"github:owner/repo#v1.0.0",
            "shared-plugin":"1.0.0"
          },
          "dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app","range-plugin","git-plugin"]}}
        }
        """);
        File.WriteAllText(Path.Combine(syncDesktop, "package.json"), """
        {
          "name":"desktop","private":true,
          "dependencies":{"obsolete-plugin":"0.9.0","shared-plugin":"1.0.0"},
          "dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}
        }
        """);
        File.WriteAllText(Path.Combine(syncWeb, "cordis.patch.yml"), """
        - id: web-ui-remote-web-ui
          config:
            plugin: '@linxin666/dsh-remote-web-ui'
            autoTunnel: true
        - id: ui-theme
          name: "@deepseek-ai/dsh-client-ui-theme"
          config:
            preference: light
        """);
        var syncPlan = UpdateService.BuildDesktopSyncPlan(syncWeb, syncDesktop);
        var plannedDependencies = syncPlan.Manifest["dependencies"]!.AsObject();
        check("desktop sync pins web npm ranges to installed versions", plannedDependencies["range-plugin"]!.GetValue<string>() == "1.2.3");
        check("desktop sync preserves Git declarations and excludes official shell dependencies",
            plannedDependencies["git-plugin"]!.GetValue<string>() == "github:owner/repo#v1.0.0"
            && plannedDependencies.ContainsKey("@deepseek-ai/ignored") == false && plannedDependencies.ContainsKey("electron") == false);
        check("desktop sync plans obsolete external dependency removal", syncPlan.DependencyChanges.Any(change => change.Name == "obsolete-plugin" && change.Web == "<移除>"));
        check("desktop sync preserves official shell bundles then web community bundles",
            syncPlan.Manifest["dsh"]!["profile"]!["bundles"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual(new[]
            { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "range-plugin", "git-plugin" }));
        check("desktop sync removes web-only remote tunnel patch but keeps user patches",
            !syncPlan.Patch.Contains("web-ui-remote-web-ui") && syncPlan.Patch.Contains("ui-theme"));
    }
}
