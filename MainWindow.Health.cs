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
