using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DshControl;

public partial class MainWindow
{
    Task sessionHistoryTask = Task.CompletedTask;
    SessionHistoryResult sessionHistory;

    Task RefreshSessionsAsync()
    {
        if (!sessionHistoryTask.IsCompleted) return sessionHistoryTask;
        sessionHistoryTask = ReadSessionsCoreAsync();
        return sessionHistoryTask;
    }

    async Task ReadSessionsCoreAsync()
    {
        BtnSessionRefresh.IsEnabled = false;
        SessionSummaryText.Text = "正在读取本机会话…";
        try
        {
            sessionHistory = await Task.Run(() => SessionHistory.ReadAsync(svc.HomeBase, cfg.SessionHistoryLimit, readCancellation.Token));
            var selected = SessionWorkspaceFilter.SelectedItem as string;
            SessionWorkspaceFilter.ItemsSource = new[] { "全部工作目录" }.Concat(sessionHistory.Items.Select(item => item.Workspace.Length > 0 ? item.Workspace : "无工作目录").Distinct().Order()).ToList();
            SessionWorkspaceFilter.SelectedItem = selected != null && SessionWorkspaceFilter.Items.Contains(selected) ? selected : "全部工作目录";
            RecentSessionList.ItemsSource = sessionHistory.Items.Take(8).ToList();
            ApplySessionFilter();
        }
        catch (OperationCanceledException) { SessionSummaryText.Text = "会话读取已取消"; }
        catch (Exception exception) { SessionSummaryText.Text = "会话记录读取失败：" + CommandRunner.Redact(exception.Message); }
        finally { BtnSessionRefresh.IsEnabled = true; }
    }

    void ApplySessionFilter()
    {
        if (sessionHistory == null || SessionGrid == null) return;
        var search = SessionSearch.Text.Trim();
        var workspace = SessionWorkspaceFilter.SelectedItem as string;
        var selected = (SessionGrid.SelectedItem as SessionHistoryItem)?.Id;
        var visible = sessionHistory.Items.Where(item => (workspace == null || workspace == "全部工作目录"
                || (item.Workspace.Length > 0 ? item.Workspace : "无工作目录") == workspace)
            && (item.Title + " " + item.Workspace + " " + item.Id).Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        SessionGrid.ItemsSource = visible;
        SessionGrid.SelectedItem = visible.FirstOrDefault(item => item.Id == selected);
        SessionEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionSummaryText.Text = $"{visible.Count} 条显示 / {sessionHistory.Total} 条本机记录 · 最近 {sessionHistory.Items.Count} 条 · 只读标题缓存"
            + (sessionHistory.Unreadable > 0 ? $" · {sessionHistory.Unreadable} 个缓存未能读取" : "");
    }

    void SessionFilter_Changed(object sender, RoutedEventArgs e) => ApplySessionFilter();
    async void BtnSessionRefresh_Click(object sender, RoutedEventArgs e) => await RefreshSessionsAsync();
    void SessionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SessionGrid.SelectedItem as SessionHistoryItem;
        BtnSessionCopy.IsEnabled = BtnSessionFolder.IsEnabled = selected != null;
        SessionDetailText.Text = selected == null ? "选择会话查看记录位置" : selected.Id + "\n" + selected.Directory;
    }
    void BtnSessionCopy_Click(object sender, RoutedEventArgs e)
    {
        if (SessionGrid.SelectedItem is SessionHistoryItem selected) Copy(selected.Id, BtnSessionCopy);
    }
    void BtnSessionFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SessionGrid.SelectedItem is SessionHistoryItem selected && Directory.Exists(selected.Directory)) svc.OpenDir(selected.Directory);
    }
    void BtnAllSessions_Click(object sender, RoutedEventArgs e) => NavList.SelectedIndex = 9;
    void RecentSession_Open(object sender, MouseButtonEventArgs e)
    {
        if (RecentSessionList.SelectedItem is not SessionHistoryItem selected) return;
        SessionSearch.Text = "";
        SessionWorkspaceFilter.SelectedIndex = 0;
        NavList.SelectedIndex = 9;
        SessionGrid.SelectedItem = sessionHistory?.Items.FirstOrDefault(item => item.Id == selected.Id);
        if (SessionGrid.SelectedItem != null) SessionGrid.ScrollIntoView(SessionGrid.SelectedItem);
    }

    void RefreshMaterial()
    {
        if (MaterialStateText == null) return;
        MaterialStateText.Text = GlassMaterial.Apply(this, cfg);
        BrandMark.SetMotion(cfg.AnimateBrand);
    }
    void Appearance_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        cfg.LiquidGlass = TglGlass.IsChecked == true;
        cfg.AnimateBrand = TglBrandMotion.IsChecked == true;
        cfg.Save();
        RefreshMaterial();
    }

    internal void UseSessionRenderFixture()
    {
        sessionHistory = new(new()
        {
            new("session-preview-a", "插件更新后启动检查", @"F:\Projects\harness", DateTime.UtcNow, 12, @"F:\Demo\session-a", true),
            new("session-preview-b", "浏览器桥接与工作区整理", @"F:\Projects\browser-tools", DateTime.UtcNow.AddHours(-2), 8, @"F:\Demo\session-b", true),
            new("session-preview-c", "桌面版配置同步", @"F:\Projects\harness", DateTime.UtcNow.AddDays(-1), 5, @"F:\Demo\session-c", true),
        }, 3, 0);
        SessionWorkspaceFilter.ItemsSource = new[] { "全部工作目录", @"F:\Projects\harness", @"F:\Projects\browser-tools" };
        SessionWorkspaceFilter.SelectedIndex = 0;
        RecentSessionList.ItemsSource = sessionHistory.Items;
        ApplySessionFilter();
    }
}
