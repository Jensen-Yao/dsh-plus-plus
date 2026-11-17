using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DshControl;

public partial class MainWindow
{
    Task healthCheckTask = Task.CompletedTask;
    CancellationTokenSource healthCancellation;
    HealthReport healthReport;

    Task RefreshHealthAsync()
    {
        if (!healthCheckTask.IsCompleted || busy) return healthCheckTask;
        healthCheckTask = CheckHealthCoreAsync();
        return healthCheckTask;
    }

    async Task CheckHealthCoreAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(readCancellation.Token);
        healthCancellation = cancellation;
        BtnHealthCheck.IsEnabled = false;
        HealthSummaryText.Text = "正在检查本地环境与插件文件…";
        try
        {
            healthReport = await Task.Run(() => HealthChecks.RunAsync(cfg, svc, upd, cancellation.Token));
            FilterHealth();
            OverviewHealthText.Text = healthReport.Summary;
            OverviewHealthText.Foreground = BrushFor(healthReport.Findings.Any(item => item.Level == "错误")
                ? "DangerBrush" : healthReport.Findings.Any(item => item.Level == "注意") ? "WarningTextBrush" : "SuccessTextBrush");
            BtnHealthExport.IsEnabled = true;
        }
        catch (OperationCanceledException) { HealthSummaryText.Text = "检查已取消"; }
        catch (Exception error)
        {
            HealthSummaryText.Text = "检查未完成：" + CommandRunner.Redact(error.Message);
            OverviewHealthText.Text = "本地检查未完成";
        }
        finally { healthCancellation = null; BtnHealthCheck.IsEnabled = !busy; }
    }

    async Task StopHealthCheckAsync()
    {
        healthCancellation?.Cancel();
        await healthCheckTask;
    }

    void FilterHealth()
    {
        if (healthReport == null || HealthList == null) return;
        var items = healthReport.Findings.Where(item => HealthOnlyIssues.IsChecked != true || item.NeedsAttention).ToList();
        HealthList.ItemsSource = items;
        HealthEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HealthSummaryText.Text = healthReport.Summary + $" · {healthReport.CheckedAt.ToLocalTime():HH:mm:ss} 本地检查";
    }

    async void BtnHealthCheck_Click(object sender, RoutedEventArgs e) => await RefreshHealthAsync();
    void HealthFilter_Changed(object sender, RoutedEventArgs e) => FilterHealth();

    async void BtnHealthRepairPatch_Click(object sender, RoutedEventArgs e)
    {
        var candidates = upd.Profiles().Where(profile => PatchOverlay.ReadStatus(profile.Dir).Level == "错误").ToList();
        if (candidates.Count == 0) { HealthSummaryText.Text = "没有检测到无法解析的 overlay patch；重复条目请用「从 Web 同步配置」或手动编辑。"; return; }
        if (MessageBox.Show(this, "将移除以下 profile 的 cordis.patch.yml 中孤立的 --- / ... 文档分隔符（先自动备份）：\n"
                + string.Join("\n", candidates.Select(profile => "· " + profile.Name)) + "\n\n服务不重启，修改在下一次启动生效。继续？",
                "修复 overlay patch", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var messages = new List<string>();
        foreach (var profile in candidates)
        {
            var repair = PatchOverlay.Repair(profile.Dir);
            messages.Add((repair.ok ? "已修复 " : "未修复 ") + profile.Name + "：" + repair.message.Split('\n')[0]);
            if (repair.ok) Log(repair.message);
        }
        HealthSummaryText.Text = string.Join(" · ", messages);
        await RefreshHealthAsync();
    }

    async void BtnHealthRepairMcp_Click(object sender, RoutedEventArgs e)
    {
        var patchPath = Path.Combine(svc.HomeBase, "cordis.patch.yml");
        var servers = McpSchemaCheck.ReadServers(patchPath);
        if (servers.Count == 0) { HealthSummaryText.Text = "全局 patch 中没有 stdio MCP server 注册。"; return; }
        HealthSummaryText.Text = "正在探测 MCP 工具 schema…";
        var candidates = new List<(McpServerEntry server, int tools, int bad)>();
        foreach (var server in servers)
        {
            if (server.Protected) continue;
            var probe = await Task.Run(() => McpSchemaCheck.ProbeAsync(server, TimeSpan.FromSeconds(25)));
            if (probe.badTools > 0) candidates.Add((server, probe.tools, probe.badTools));
        }
        if (candidates.Count == 0) { HealthSummaryText.Text = "MCP 工具 schema 全部通过，无需修复。"; return; }
        if (MessageBox.Show(this, "以下 MCP server 的工具 schema 含 DeepSeek API 不接受的 pattern，会导致带工具的聊天整轮失败：\n"
                + string.Join("\n", candidates.Select(item => $"· {item.server.ServerName}（{item.bad}/{item.tools} 个工具）"))
                + "\n\n将为它们插入 dsh++ 内嵌的 schema 清洗代理（patch 先自动备份），重启 DSH Web / 桌面版后生效。继续？",
                "修复 MCP schema", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        foreach (var item in candidates)
        {
            var repair = McpSchemaCheck.Repair(patchPath, item.server.Id);
            HealthSummaryText.Text = repair.ok ? "修复完成：" + item.server.ServerName : "未修复 " + item.server.ServerName + "：" + repair.message;
            if (repair.ok) Log(repair.message);
        }
        await RefreshHealthAsync();
    }

    void BtnHealthExport_Click(object sender, RoutedEventArgs e)
    {
        if (healthReport == null) return;
        var dialog = new SaveFileDialog { FileName = $"dsh-health-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "文本文件|*.txt" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, healthReport.Export());
    }

    void RefreshComparison()
    {
        if (ComparisonGrid == null) return;
        var rows = ProfileDifference.Compare(plugins);
        var visible = rows.Where(row => ComparisonOnlyChanges.IsChecked != true || row.Different).ToList();
        ComparisonGrid.ItemsSource = visible;
        ComparisonEmpty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ComparisonSummary.Text = $"{rows.Count} 个入口 · {rows.Count(row => row.State == "版本不同")} 版本不同 · {rows.Count(row => row.State == "声明不同")} 声明不同 · {rows.Count(row => row.State is "仅 Web" or "仅桌面版" or "未安装")} 缺失 / 单端";
    }

    void ComparisonFilter_Changed(object sender, RoutedEventArgs e) => RefreshComparison();
}
