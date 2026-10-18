using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DshControl;

public partial class MainWindow : Window
{
    readonly AppConfig cfg;
    readonly DshService svc;
    readonly DesktopService desktop;
    readonly UpdateService upd;
    readonly FreebuffService freebuff;
    readonly DispatcherTimer refreshTimer;
    List<PluginItem> plugins = new();
    CancellationTokenSource taskCancellation;
    CancellationTokenSource pluginCheckCancellation;
    Task<PluginCheckSummary> pluginCheckTask;
    CancellationTokenSource readCancellation = new();
    Task versionChecks = Task.CompletedTask;
    bool joiningReadChecks;
    bool ready;
    bool busy;
    bool refreshing;
    bool closingAfterTask;
    bool selfUpdating;
    bool freebuffReveal;
    string appUpdateZip;
    string stableVersion;
    string alphaVersion;
    string activeBind;
    bool running;
    bool desktopRunning;
    bool desktopInstalled;
    DateTime lastRemoteRefresh;
    readonly string[] pageNames = { "服务总览", "桌面版", "插件管理", "版本与备份", "远程连接", "启动设置", "数据与路径", "Freebuff", "诊断与日志", "会话记录" };

    public MainWindow()
    {
        cfg = App.Cfg;
        svc = new DshService(cfg, Log);
        desktop = new DesktopService(cfg, svc, Log);
        upd = new UpdateService(cfg, svc, desktop, Log);
        freebuff = new FreebuffService(cfg.FreebuffDir, Log, cfg.FreebuffDocker);
        InitializeComponent();
        SourceInitialized += (_, _) => RefreshMaterial();
        SizeChanged += (_, _) => NavList.ItemContainerStyle = (Style)FindResource(ActualHeight < 720 ? "CompactNavItemStyle" : "NavItemStyle");
        StateChanged += (_, _) => BrandMark.SetMotion(cfg.AnimateBrand && WindowState != WindowState.Minimized);
        RestoreWindow();
        LoadSettings();
        ReloadPlugins();
        RefreshBackups();
        BuildStorageRows();
        RefreshVersions();
        RefreshDesktopStatus();
        VersionText.Text = "v" + UpdateService.AppVersion();
        FreebuffPathsText.Text = cfg.FreebuffDir + "\n" + cfg.FreebuffDocker;
        ready = true;
        NavList.SelectedIndex = App.StartPage >= 0 ? App.StartPage : Math.Clamp(cfg.LastPage, 0, 9);
        svc.StartLogTail(AppendLog);
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        Closing += OnClosing;
        Loaded += async (_, _) =>
        {
            if (App.RenderDirectory != null)
            {
                await RefreshAsync();
                await RefreshHealthAsync();
                await RefreshSessionsAsync();
                await RenderCheck.CaptureAsync(this, App.RenderDirectory);
                selfUpdating = true;
                Close();
                return;
            }
            refreshTimer.Start();
            await RefreshAsync();
            _ = RefreshHealthAsync();
            _ = RefreshSessionsAsync();
            var controlsReady = BtnStart.IsEnabled || BtnRestart.IsEnabled;
            if (cfg.CheckUpdatesOnLaunch)
            {
                versionChecks = Task.WhenAll(CheckAppAsync(), CheckRuntimeAsync());
                await Task.WhenAll(versionChecks, CheckPluginsInUiAsync(plugins.Where(plugin => plugin.Profile == "web").ToList()));
            }
            if (App.UiCheck)
            {
                var directory = Path.Combine(AppConfig.ConfigDir, "ui-check");
                await RenderCheck.CaptureAsync(this, directory);
                var checkedCount = plugins.Count(plugin => plugin.Profile == "web" && plugin.CheckedAt.HasValue);
                File.WriteAllText(Path.Combine(directory, "startup-report.json"), JsonSerializer.Serialize(new
                {
                    version = UpdateService.AppVersion(), controlsReadyDuringChecks = controlsReady,
                    startupPluginChecks = checkedCount, expectedPluginChecks = plugins.Count(plugin => plugin.Profile == "web"),
                    checkText = PluginCheckText.Text,
                }));
                selfUpdating = true;
                Close();
            }
        };
    }

    Brush BrushFor(string key) => (Brush)FindResource(key);

    internal void SelectPage(int index) => NavList.SelectedIndex = index;
    internal void ChangeTheme(string theme)
    {
        cfg.Theme = theme;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = new Uri($"Themes/{(theme == "light" ? "Light" : "Dark")}.xaml", UriKind.Relative) };
        RefreshMaterial();
    }

    void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        ChangeTheme(cfg.Theme == "dark" ? "light" : "dark");
        cfg.Save();
    }

    void RestoreWindow()
    {
        if (!double.IsNaN(cfg.WinWidth)) Width = Math.Max(MinWidth, cfg.WinWidth);
        if (!double.IsNaN(cfg.WinHeight)) Height = Math.Max(MinHeight, cfg.WinHeight);
        if (cfg.WinMaximized) WindowState = WindowState.Maximized;
        else if (!double.IsNaN(cfg.WinLeft) && !double.IsNaN(cfg.WinTop)
            && cfg.WinLeft >= SystemParameters.VirtualScreenLeft
            && cfg.WinLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && cfg.WinTop >= SystemParameters.VirtualScreenTop
            && cfg.WinTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = cfg.WinLeft;
            Top = cfg.WinTop;
        }
    }

    void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        var pages = new[] { PageSvc, PageDesktop, PagePlugins, PageUpdates, PagePhone, PageAdv, PageStore, PageFreebuff, PageLog, PageSessions };
        var selected = Math.Clamp(NavList.SelectedIndex, 0, pages.Length - 1);
        for (var index = 0; index < pages.Length; index++)
            pages[index].Visibility = index == selected ? Visibility.Visible : Visibility.Collapsed;
        BreadcrumbText.Text = "工作空间 / " + pageNames[selected];
        if (App.RenderDirectory == null) { cfg.LastPage = selected; cfg.Save(); }
        if (selected == 3) RefreshBackups();
        if (selected == 7 && App.RenderDirectory == null) _ = RefreshFreebuffAsync();
        if (selected == 9 && App.RenderDirectory == null) _ = RefreshSessionsAsync();
    }

    void LoadSettings()
    {
        TglLocalOnly.IsChecked = cfg.Mode == "local";
        TglAutoPlugin.IsChecked = cfg.CheckUpdatesOnLaunch;
        TglRestartAfterUpdate.IsChecked = cfg.RestartAfterUpdate;
        TglRequirePairing.IsChecked = cfg.RequirePairing;
        TglAutoTunnel.IsChecked = cfg.AutoTunnel;
        TglCompat.IsChecked = cfg.BrowserCompatibilityPatch;
        TglGlass.IsChecked = cfg.LiquidGlass;
        TglBrandMotion.IsChecked = cfg.AnimateBrand;
        PortBox.Text = cfg.Port.ToString();
        StartupTimeoutBox.Text = cfg.StartupTimeoutSeconds.ToString();
        UpdateTimeoutBox.Text = cfg.UpdateTimeoutMinutes.ToString();
        HostBox.Text = cfg.DisplayHost;
        RbIp.IsChecked = cfg.AccessMode == "ip";
        RbTs.IsChecked = cfg.AccessMode == "tailscale";
        RbDomain.IsChecked = cfg.AccessMode == "domain";
        HostBox.IsEnabled = cfg.AccessMode == "domain";
        BindAddressBox.ItemsSource = new[] { "自动（按连接方式）" }.Concat(DshService.AvailableAddresses()).ToList();
        BindAddressBox.SelectedItem = string.IsNullOrEmpty(cfg.BindHost) ? "自动（按连接方式）" : cfg.BindHost;
    }

    void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        cfg.Mode = TglLocalOnly.IsChecked == true ? "local" : "lan";
        cfg.CheckUpdatesOnLaunch = TglAutoPlugin.IsChecked == true;
        cfg.RestartAfterUpdate = TglRestartAfterUpdate.IsChecked == true;
        cfg.RequirePairing = TglRequirePairing.IsChecked == true;
        cfg.AutoTunnel = TglAutoTunnel.IsChecked == true;
        cfg.BrowserCompatibilityPatch = TglCompat.IsChecked == true;
        cfg.AutoUpdatePlugins = false;
        cfg.Save();
        SettingsHint.Text = "已保存。网络设置将在下次启动时生效。";
        _ = RefreshAsync();
    }

    void BindAddress_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        cfg.BindHost = BindAddressBox.SelectedIndex <= 0 ? "" : BindAddressBox.SelectedItem as string;
        cfg.Save();
        SettingsHint.Text = "已保存绑定地址，下次启动生效。";
    }

    void PortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (busy || running || !int.TryParse(PortBox.Text.Trim(), out var port) || port < 1024 || port > 65535)
        {
            PortBox.Text = cfg.Port.ToString();
            SettingsHint.Text = running ? "先停止服务再修改端口。" : "端口范围为 1024–65535。";
            return;
        }
        cfg.Port = port;
        cfg.Save();
    }

    void TimeoutBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!ready || busy) return;
        var startup = sender == StartupTimeoutBox;
        var box = (TextBox)sender;
        var minimum = startup ? 10 : 1;
        var maximum = startup ? 180 : 60;
        if (!int.TryParse(box.Text, out var value) || value < minimum || value > maximum)
        {
            box.Text = (startup ? cfg.StartupTimeoutSeconds : cfg.UpdateTimeoutMinutes).ToString();
            SettingsHint.Text = $"超时范围为 {minimum}–{maximum} " + (startup ? "秒。" : "分钟。");
            return;
        }
        if (startup) cfg.StartupTimeoutSeconds = value;
        else cfg.UpdateTimeoutMinutes = value;
        cfg.Save();
        SettingsHint.Text = "已保存超时设置。";
    }

    void Access_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        cfg.AccessMode = RbTs.IsChecked == true ? "tailscale" : RbDomain.IsChecked == true ? "domain" : "ip";
        cfg.BindHost = "";
        BindAddressBox.SelectedIndex = 0;
        HostBox.IsEnabled = cfg.AccessMode == "domain";
        cfg.Save();
        _ = RefreshAsync();
    }

    void HostBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var value = HostBox.Text.Trim().TrimEnd('/');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) value = uri.Host;
        if (!string.IsNullOrEmpty(value) && Uri.CheckHostName(value) != UriHostNameType.Dns)
        {
            SettingsHint.Text = "域名格式无效。";
            HostBox.Text = cfg.DisplayHost;
            return;
        }
        cfg.DisplayHost = value;
        HostBox.Text = value;
        cfg.Save();
        _ = RefreshAsync();
    }

    async Task RunTaskAsync(string title, Func<CancellationToken, Task<(bool ok, string message)>> operation, bool reloadPlugins = true)
    {
        if (busy) return;
        busy = true;
        await StopPluginCheckAsync();
        await StopHealthCheckAsync();
        taskCancellation = new();
        TaskPanel.Visibility = Visibility.Visible;
        TaskTitle.Text = title;
        TaskDetail.Text = "正在准备…";
        TaskProgress.Visibility = Visibility.Visible;
        BtnCancelTask.IsEnabled = true;
        BtnCancelTask.Visibility = Visibility.Visible;
        SetControlsEnabled(false);
        try
        {
            var result = await operation(taskCancellation.Token);
            TaskTitle.Text = result.ok ? "已完成 · " + title : "未完成 · " + title;
            TaskDetail.Text = result.message;
            TaskDetail.ToolTip = result.message;
            Log(result.message);
            if (!result.ok)
            {
                FailureText.Text = result.message;
                FailurePanel.Visibility = Visibility.Visible;
            }
            else FailurePanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { TaskDetail.Text = "已取消"; }
        catch (Exception exception)
        {
            TaskTitle.Text = "未完成 · " + title;
            TaskDetail.Text = CommandRunner.Redact(exception.Message);
            Log(TaskDetail.Text);
        }
        finally
        {
            busy = false;
            taskCancellation.Dispose();
            taskCancellation = null;
            TaskProgress.Visibility = Visibility.Collapsed;
            BtnCancelTask.Visibility = Visibility.Collapsed;
            SetControlsEnabled(true);
            if (reloadPlugins) ReloadPlugins();
            else ApplyPluginFilter();
            RefreshVersions();
            RefreshBackups();
            await RefreshAsync();
            _ = RefreshHealthAsync();
            if (closingAfterTask) Close();
            else if (reloadPlugins && cfg.CheckUpdatesOnLaunch)
                _ = CheckPluginsInUiAsync(plugins.Where(plugin => plugin.Profile == "web").ToList());
        }
    }

    void SetControlsEnabled(bool enabled)
    {
        PageDesktop.IsEnabled = enabled;
        PagePlugins.IsEnabled = enabled;
        PageUpdates.IsEnabled = enabled;
        PageAdv.IsEnabled = enabled;
        PageStore.IsEnabled = enabled;
        PagePhone.IsEnabled = enabled;
        BtnStart.IsEnabled = enabled && !running;
        BtnRestart.IsEnabled = enabled && running;
        BtnStop.IsEnabled = enabled && running;
        BtnPluginCheck.IsEnabled = enabled && pluginCheckTask is not { IsCompleted: false };
        RefreshDesktopControls(enabled);
    }

    void Progress(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TaskDetail.Text = CommandRunner.Redact(message);
            TaskDetail.ToolTip = TaskDetail.Text;
        });
    }

    async void BtnStart_Click(object sender, RoutedEventArgs e)
        => await RunTaskAsync("启动 DSH", async cancellation => await Task.Run(() => svc.StartAsync(cancellation)));
    async void BtnStop_Click(object sender, RoutedEventArgs e)
        => await RunTaskAsync("停止 DSH", async _ => await Task.Run(() => svc.StopAsync()));
    async void BtnRestart_Click(object sender, RoutedEventArgs e)
        => await RunTaskAsync("重启 DSH", async cancellation => await Task.Run(() => svc.RestartAsync(cancellation)));
    void BtnOpenUi_Click(object sender, RoutedEventArgs e) => svc.OpenUi();
    void BtnDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        DiagnosticTabs.SelectedIndex = 0;
        NavList.SelectedIndex = 8;
    }
    void BtnCancelTask_Click(object sender, RoutedEventArgs e)
    {
        taskCancellation?.Cancel();
        BtnCancelTask.IsEnabled = false;
        TaskDetail.Text = "正在取消；如已修改依赖，将先恢复备份。";
    }

    void RefreshVersions()
    {
        var (version, error) = upd.LocalDshVersion();
        OverviewVersion.Text = error == null ? version : "未安装";
        DshVerText.Text = error == null ? "已安装 " + version : error;
        AppVerText.Text = "当前版本 " + UpdateService.AppVersion();
    }

    void ReloadPlugins()
    {
        var previous = plugins;
        plugins = upd.Profiles().SelectMany(profile => profile.Plugins).ToList();
        foreach (var plugin in plugins)
        {
            var match = previous.FirstOrDefault(old => old.ProfileDir == plugin.ProfileDir && old.Name == plugin.Name && old.OwnerName == plugin.OwnerName
                && old.Spec == plugin.Spec && old.Installed == plugin.Installed && old.ResolvedSource == plugin.ResolvedSource && old.PackageDirectory == plugin.PackageDirectory);
            if (match != null) { plugin.CopyCheckFrom(match); plugin.Selected = match.Selected && plugin.CanUpdate; }
        }
        var selectedProfile = ProfileFilter.SelectedItem as string;
        ProfileFilter.ItemsSource = new[] { "全部 Profile" }.Concat(plugins.Select(plugin => plugin.Profile).Distinct()).ToList();
        ProfileFilter.SelectedItem = selectedProfile != null && ProfileFilter.Items.Contains(selectedProfile)
            ? selectedProfile : ProfileFilter.Items.Contains("web") ? "web" : "全部 Profile";
        ApplyPluginFilter();
        OverviewPlugins.Text = $"web · {plugins.Count(plugin => plugin.Profile == "web" && !plugin.IsIndirect)} 入口";
        RefreshComparison();
    }

    void ApplyPluginFilter()
    {
        if (PluginGrid == null || PluginSearch == null || ProfileFilter == null || PluginStatusFilter == null) return;
        var query = PluginSearch.Text.Trim();
        var profile = ProfileFilter.SelectedItem as string;
        var visible = plugins.Where(plugin => (string.IsNullOrEmpty(profile) || profile == "全部 Profile" || plugin.Profile == profile)
            && (plugin.Name + " " + plugin.Source + " " + plugin.Spec + " " + plugin.Installed + " " + plugin.Status).Contains(query, StringComparison.OrdinalIgnoreCase)
            && MatchesPluginStatus(plugin, PluginStatusFilter.SelectedIndex)).ToList();
        PluginGrid.ItemsSource = visible;
        PluginEmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginStatText.Text = $"{visible.Count} 项 · {visible.Count(plugin => plugin.IsIndirect)} 子包 · {visible.Count(plugin => plugin.HasUpdate)} 有更新 · {visible.Count(plugin => plugin.SourceIssue.Length > 0)} 来源提示";
        UpdateSelectedCount();
    }

    internal static bool MatchesPluginStatus(PluginItem plugin, int filter) => filter switch
    {
        1 => plugin.CanUpdate,
        2 => plugin.HasUpdate,
        3 => !plugin.IsIndirect && !plugin.CanUpdate && !plugin.IsLocal && !plugin.IsCore,
        4 => plugin.IsLocal,
        5 => plugin.IsCore,
        6 => string.IsNullOrEmpty(plugin.Installed) || plugin.CheckError.Length > 0 || plugin.SourceIssue.Length > 0 || (plugin.Detail ?? "").StartsWith("检查失败"),
        7 => plugin.IsIndirect,
        _ => true,
    };

    void PluginFilter_Changed(object sender, RoutedEventArgs e) => ApplyPluginFilter();
    void PluginGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PluginGrid.SelectedItem is PluginItem plugin)
        {
            PluginDetailText.Text = plugin.IsIndirect ? "更新入口：" + plugin.OwnerName + " · " + plugin.Spec : plugin.Spec;
            PluginDetailText.ToolTip = CommandRunner.Redact(plugin.Description);
            PluginHintText.Text = plugin.SourceIssue.Length > 0 ? plugin.SourceIssue : plugin.CheckError.Length > 0 ? "检查失败，悬停查看详情。"
                : plugin.IsIndirect ? "由所属入口安装，不单独提升为直接依赖。" : "本地链接由源码维护；固定版本可通过「指定版本」更改。";
            BtnPluginTarget.IsEnabled = !busy && !plugin.IsIndirect && !plugin.IsLocal && !plugin.IsCore;
        }
        else BtnPluginTarget.IsEnabled = false;
    }
    void PluginSelection_Click(object sender, RoutedEventArgs e) => UpdateSelectedCount();
    void UpdateSelectedCount()
    {
        var count = plugins.Count(plugin => plugin.Selected);
        BtnPluginUpdate.Content = count == 0 ? "更新所选" : $"更新所选 ({count})";
        BtnPluginUpdate.IsEnabled = !busy && count > 0;
    }
    void PluginSelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkbox && PluginGrid.ItemsSource is IEnumerable<PluginItem> visible)
            foreach (var plugin in visible.Where(plugin => plugin.CanUpdate)) plugin.Selected = checkbox.IsChecked == true;
        PluginGrid.Items.Refresh();
        UpdateSelectedCount();
    }
    async void BtnPluginRefresh_Click(object sender, RoutedEventArgs e) { await StopPluginCheckAsync(); ReloadPlugins(); }

    async void BtnPluginCheck_Click(object sender, RoutedEventArgs e)
    {
        var profile = ProfileFilter.SelectedItem as string;
        await CheckPluginsInUiAsync(plugins.Where(plugin => profile == "全部 Profile" || plugin.Profile == profile).ToList());
    }

    async Task<PluginCheckSummary> CheckPluginsInUiAsync(List<PluginItem> scope)
    {
        if (busy || pluginCheckTask is { IsCompleted: false }) return null;
        using var cancellation = new CancellationTokenSource();
        pluginCheckCancellation = cancellation;
        BtnPluginCheck.IsEnabled = false;
        PluginCheckText.Text = "正在后台检查插件…";
        try
        {
            pluginCheckTask = Task.Run(() => upd.CheckPluginsAsync(scope, cancellation.Token, message => Dispatcher.BeginInvoke(() => PluginCheckText.Text = message)));
            var summary = await pluginCheckTask;
            PluginCheckText.Text = $"{DateTime.Now:HH:mm} 检查完成 · {summary.Updates} 有更新 · {summary.Failed} 失败 · {summary.SourceIssues} 来源提示";
            OverviewPluginCheck.Text = summary.Failed > 0 || summary.SourceIssues > 0 ? "插件检查需要关注" : summary.Updates > 0 ? $"插件发现 {summary.Updates} 项更新" : "web 插件已检查";
            return summary;
        }
        catch (OperationCanceledException) { PluginCheckText.Text = "检查已取消，未修改依赖。"; }
        catch (Exception error) { PluginCheckText.Text = "检查未完成：" + CommandRunner.Redact(error.Message); }
        finally
        {
            pluginCheckCancellation = null;
            BtnPluginCheck.IsEnabled = !busy;
            ApplyPluginFilter();
        }
        return null;
    }

    async Task StopPluginCheckAsync()
    {
        pluginCheckCancellation?.Cancel();
        var current = pluginCheckTask;
        if (current == null) return;
        try { await current; }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log("插件检查未完成：" + error.Message); }
    }

    async void BtnPluginUpdate_Click(object sender, RoutedEventArgs e)
    {
        var selected = plugins.Where(plugin => plugin.Selected).ToList();
        if (selected.Count == 0 || !ConfirmMaintenance("更新所选插件", selected.Select(plugin =>
            new PlannedChange(plugin.Profile + " / " + plugin.Name, $"已安装 {plugin.Installed} → 声明 {plugin.Spec}")), selected.Select(plugin => plugin.Profile))) return;
        await RunTaskAsync("更新插件", async cancellation =>
            await Task.Run(() => upd.UpdatePluginsAsync(selected, Progress, cancellation)));
    }

    async void BtnPluginTarget_Click(object sender, RoutedEventArgs e)
    {
        if (PluginGrid.SelectedItem is not PluginItem plugin || plugin.IsIndirect || plugin.IsLocal || plugin.IsCore) return;
        var proposed = plugin.Spec;
        if (System.Text.RegularExpressions.Regex.IsMatch(plugin.Latest ?? "", @"^v?\d+\.\d+\.\d+(?:-[\w.-]+)?$"))
            proposed = plugin.IsGit ? plugin.Spec.Split('#')[0] + "#" + plugin.Latest : plugin.Latest;
        var dialog = new InputDialog("指定插件版本", plugin.Name + "\n版本 / 范围 / github:owner/repository#ref", proposed) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var target = dialog.Value.Trim();
        if (!UpdateService.ValidSpec(target)) { PluginHintText.Text = "目标版本无效；不接受本地路径或命令。"; return; }
        if (!ConfirmMaintenance("指定插件版本", new[] { new PlannedChange(plugin.Name, $"{plugin.Installed} → {target}") }, new[] { plugin.Profile })) return;
        await RunTaskAsync("更新 " + plugin.Name, async cancellation =>
            await Task.Run(() => upd.UpdatePluginsAsync(new[] { plugin }, Progress, cancellation, target)));
    }

    async Task CheckRuntimeAsync()
    {
        BtnDshCheck.IsEnabled = false;
        DshUpdHint.Text = "正在检查版本…";
        try
        {
            var versions = await Task.Run(() => upd.RemoteDshVersions(readCancellation.Token));
            if (versions.err != null) { DshUpdHint.Text = versions.err; return; }
            stableVersion = versions.latest;
            alphaVersion = versions.alpha;
            var (local, _) = upd.LocalDshVersion();
            DshRemoteText.Text = $"正式版 {stableVersion} · 预览版 {alphaVersion}";
            BtnDshUpdateStable.IsEnabled = !busy && UpdateService.IsNewer(stableVersion, local);
            BtnDshUpdateAlpha.IsEnabled = !busy && UpdateService.IsNewer(alphaVersion, local);
            DshUpdHint.Text = "已完成版本检查";
        }
        catch (Exception exception) { DshUpdHint.Text = exception.Message; }
        finally { BtnDshCheck.IsEnabled = true; }
    }
    async void BtnDshCheck_Click(object sender, RoutedEventArgs e)
    {
        var check = CheckRuntimeAsync();
        versionChecks = Task.WhenAll(versionChecks, check);
        await check;
    }
    async Task UpdateRuntimeAsync(string channel)
    {
        if (!ConfirmMaintenance("更新 DSH 内核", new[] { new PlannedChange("@deepseek-ai/dsh", "目标通道 " + channel
            + " · 当前查询 " + (channel == "latest" ? stableVersion : alphaVersion)),
            new PlannedChange("web profile", "仅同步官方伴随包，不批量更新社区插件。") })) return;
        await RunTaskAsync("更新 DSH 内核", async cancellation =>
            await Task.Run(() => upd.UpdateRuntimeAsync(channel, Progress, cancellation)));
        DshUpdHint.Text = TaskDetail.Text;
        BtnDshUpdateStable.IsEnabled = false;
        BtnDshUpdateAlpha.IsEnabled = false;
    }
    async void BtnDshUpdateStable_Click(object sender, RoutedEventArgs e) => await UpdateRuntimeAsync("latest");
    async void BtnDshUpdateAlpha_Click(object sender, RoutedEventArgs e) => await UpdateRuntimeAsync("alpha");
    async void BtnSyncCompanions_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmMaintenance("同步官方伴随包", new[] { new PlannedChange("web profile", "将官方依赖与已安装内核提供的对应版本保持一致。") })) return;
        await RunTaskAsync("同步官方伴随包", async cancellation =>
            await Task.Run(() => upd.SynchronizeCompanionsAsync(Progress, cancellation)));
    }

    bool ConfirmMaintenance(string title, IEnumerable<PlannedChange> changes, IEnumerable<string> profiles = null)
        => new UpdatePreviewWindow(title, changes, running, cfg.RestartAfterUpdate,
            MaintenanceImpact(profiles ?? new[] { "web" }, running, desktopRunning, cfg.RestartAfterUpdate)) { Owner = this }.ShowDialog() == true;

    internal static string MaintenanceImpact(IEnumerable<string> profiles, bool webRunning, bool desktopRunning, bool restart)
        => string.Join("\n", profiles.Distinct().Select(profile => profile == "desktop"
            ? "桌面版：" + (desktopRunning ? restart ? "暂时停止，完成后恢复。" : "停止后保持停止。" : "维护后保持停止。") + "维护期间会启动官方桌面版验证。"
            : profile == "web" ? "Web 服务：" + (webRunning ? restart ? "暂时停止，完成后恢复。" : "停止后保持停止。" : "维护后保持停止。")
            : profile + "：检查静态配置组合。"));

    async Task CheckAppAsync()
    {
        BtnAppCheck.IsEnabled = false;
        try
        {
            var (tag, zip, error) = await upd.LatestAppReleaseAsync(readCancellation.Token);
            appUpdateZip = error == null && UpdateService.IsNewer(tag.TrimStart('v', 'V'), UpdateService.AppVersion()) ? zip : null;
            BtnAppUpdate.IsEnabled = appUpdateZip != null;
            AppUpdHint.Text = error != null ? "检查未完成：" + error : appUpdateZip != null ? "新版本 " + tag : "当前已是最新版本";
        }
        finally { BtnAppCheck.IsEnabled = true; }
    }
    async void BtnAppCheck_Click(object sender, RoutedEventArgs e)
    {
        var check = CheckAppAsync();
        versionChecks = Task.WhenAll(versionChecks, check);
        await check;
    }
    async void BtnAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (busy || appUpdateZip == null) return;
        var result = await upd.ApplyAppUpdateAsync(appUpdateZip);
        AppUpdHint.Text = result.msg;
        if (result.ok) { selfUpdating = true; Close(); }
    }
    void BtnOpenReleases_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://github.com/Jensen-Yao/dsh-plus-plus/releases") { UseShellExecute = true });

    void RefreshBackups()
    {
        var selected = (HistoryList.SelectedItem as MaintenanceRecord)?.Id;
        BackupList.ItemsSource = upd.Backups();
        BackupList.SelectedIndex = BackupList.Items.Count > 0 ? 0 : -1;
        var history = upd.History();
        HistoryList.ItemsSource = history;
        HistoryList.SelectedItem = history.FirstOrDefault(record => record.Id == selected) ?? history.FirstOrDefault();
        var pending = history.Count(record => record.NeedsRecovery);
        HistoryHint.Text = pending > 0 ? $"{pending} 个任务待恢复，未自动执行联网修复。" : history.Count == 0 ? "暂无维护记录" : $"{history.Count} 次维护 · 最近记录在前";
        if (pending > 0)
        {
            FailurePanel.Visibility = Visibility.Visible;
            FailureText.Text = "发现中断的依赖维护，请在「版本与备份」中恢复；启动服务仍然可用。";
        }
    }

    void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var record = HistoryList.SelectedItem as MaintenanceRecord;
        HistoryDetail.Text = record == null ? "" : CommandRunner.Redact(record.Detail);
        BtnRecoverMaintenance.IsEnabled = !busy && record?.NeedsRecovery == true;
    }

    async void BtnRecoverMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not MaintenanceRecord record || !record.NeedsRecovery) return;
        if (!ConfirmMaintenance("恢复中断的依赖维护", record.Backups.Select(backup => new PlannedChange(backup.Directory, backup.Display)),
            UpdateService.MaintenanceProfiles(record.Backups.Select(backup => backup.Directory), svc.HomeBase, cfg.DshAppDir))) return;
        await RunTaskAsync("恢复中断任务", async cancellation =>
            await Task.Run(() => upd.RecoverMaintenanceAsync(record, Progress, cancellation)));
    }

    void BtnExportPlugins_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"dsh-plugins-{DateTime.Now:yyyyMMdd-HHmm}.json", Filter = "JSON 文件|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        var inventory = PluginGrid.Items.Cast<PluginItem>().Select(plugin => new
        { plugin.Profile, plugin.Name, plugin.OwnerName, plugin.Installed, plugin.Spec, plugin.ResolvedSource, plugin.Latest, plugin.Status, plugin.CheckedAt, plugin.CheckError });
        try
        {
            File.WriteAllText(dialog.FileName, CommandRunner.Redact(JsonSerializer.Serialize(inventory, new JsonSerializerOptions { WriteIndented = true })));
            PluginHintText.Text = "插件清单已导出。";
        }
        catch (IOException error) { PluginHintText.Text = "导出失败：" + error.Message; }
    }
    void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var profile = ProfileFilter.SelectedItem as string;
        if (string.IsNullOrEmpty(profile) || profile == "全部 Profile") profile = "web";
        var directory = Path.Combine(svc.HomeBase, "profiles", profile);
        try { upd.CreateBackup(directory, "手动备份 · " + profile); RefreshBackups(); }
        catch (Exception exception) { Log(exception.Message); }
    }
    async void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not MaintenanceBackup backup) return;
        if (MessageBox.Show(this, "恢复 " + backup.Display + "？当前依赖将先保存为新的恢复点。",
            "恢复备份", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await RunTaskAsync("恢复备份", async cancellation =>
            await Task.Run(() => upd.RestoreBackupAsync(backup, Progress, cancellation)));
    }

    async Task RefreshAsync()
    {
        if (refreshing || !ready) return;
        refreshing = true;
        try
        {
            var state = await Task.Run(() =>
            {
                var bind = svc.GetBindAddress(cfg.Port);
                var host = svc.DisplayHost();
                var tailscale = DateTime.UtcNow - lastRemoteRefresh > TimeSpan.FromSeconds(20) ? svc.GetTsState() : null;
                return (bind, host, tailscale, phase: svc.RuntimePhase(), desktop: desktop.Status());
            });
            running = state.bind != null;
            ApplyDesktopStatus(state.desktop);
            activeBind = state.bind;
            var label = svc.IsStarting ? "启动中" : state.phase == "restarting" ? "重启接续中" : running ? "运行中" : string.IsNullOrEmpty(svc.LastFailure) ? "已停止" : "启动失败";
            SidebarStatus.Text = label;
            SvcStateText.Text = label;
            SidebarDot.Fill = SvcDot.Fill = BrushFor(svc.IsStarting ? "WarningBrush" : running ? "SuccessBrush" : "TextTertiaryBrush");
            if (!busy) SetControlsEnabled(true);
            BtnOpenUi.IsEnabled = running;
            PcUrlText.Text = running ? svc.LocalUrl() : "服务尚未启动";
            BindText.Text = running ? $"绑定 {state.bind}:{cfg.Port} · PID {svc.GetPidOnPort(cfg.Port)}"
                : cfg.Mode == "local" ? "下次启动：127.0.0.1" : "下次启动：按所选连接方式绑定具体网卡";
            CkSvcText.Text = running ? "DSH 服务已启动" : "DSH 服务未运行";
            HeroUrlText.Text = cfg.Mode == "local" ? "仅本机访问" : $"http://{state.host}:{cfg.Port}";
            HeroUrlText.Tag = cfg.Mode == "local" ? null : HeroUrlText.Text;
            BtnCopyPhone.IsEnabled = cfg.Mode != "local";
            PhoneHintText.Text = cfg.Mode == "local" ? "在启动设置中关闭「仅本机访问」后可使用远程连接。"
                : cfg.AccessMode == "tailscale" ? "手机与电脑连接同一个 Tailscale 网络。" : "此地址使用所选网络连接。";
            if (state.tailscale != null)
            {
                CkTsText.Text = state.tailscale.CkText;
                TsDetailText.Text = state.tailscale.Detail;
                BtnTsFix.Content = state.tailscale.FixLabel;
                BtnTsFix.Tag = state.tailscale.FixAction;
                lastRemoteRefresh = DateTime.UtcNow;
            }
        }
        catch (Exception exception) { BindText.Text = exception.Message; }
        finally { refreshing = false; }
    }

    async void BtnTsFix_Click(object sender, RoutedEventArgs e)
    {
        var action = BtnTsFix.Tag as string;
        BtnTsFix.IsEnabled = false;
        await Task.Run(() =>
        {
            if (action == "install") svc.TsInstall();
            else if (action == "down") svc.TsDown();
            else svc.StartTsAsync(action == "login" ? "login" : "up");
        });
        BtnTsFix.IsEnabled = true;
        lastRemoteRefresh = DateTime.MinValue;
        await RefreshAsync();
    }
    void BtnFwAdd_Click(object sender, RoutedEventArgs e)
    {
        svc.AddFirewallRule();
        CkFwText.Text = "已请求防火墙放行；状态以 Windows 防火墙为准。";
    }
    void PcUrlText_Click(object sender, MouseButtonEventArgs e) { if (running) Copy(svc.LocalUrl()); }
    void HeroUrlText_Click(object sender, MouseButtonEventArgs e) { if (HeroUrlText.Tag is string url) Copy(url); }
    void BtnCopyPhone_Click(object sender, RoutedEventArgs e) { if (HeroUrlText.Tag is string url) Copy(url, BtnCopyPhone); }

    void RefreshDesktopStatus()
    {
        ApplyDesktopStatus(desktop.Status());
    }

    void ApplyDesktopStatus(DesktopStatus status)
    {
        desktopInstalled = status.Installed;
        desktopRunning = status.Running;
        var desktopPlugins = plugins.Where(plugin => plugin.Profile == "desktop").ToList();
        DesktopStateText.Text = status.State;
        DesktopVersionText.Text = status.Installed ? status.Version : "未安装";
        DesktopPluginText.Text = desktopPlugins.Count == 0 ? "空 Profile" : $"{desktopPlugins.Count(plugin => !plugin.IsIndirect)} 入口";
        DesktopPathText.Text = status.Installed ? status.Executable : cfg.DshDesktopDir + "\\DeepSeek Harness.exe";
        DesktopProfilePathText.Text = "desktop profile：" + status.ProfileDirectory;
        DesktopDataPathText.Text = "桌面状态与日志：" + status.DataDirectory;
        OverviewDesktopText.Text = status.Installed
            ? $"官方桌面版 {status.Version} · {status.State} · {status.ProcessCount} 进程"
            : "官方桌面版未安装";
        DesktopHintText.Text = !status.Installed
            ? "未找到桌面版程序。可在「数据与路径」修改安装目录。"
            : status.Running ? $"正在运行 {status.ProcessCount} 个进程；desktop profile 独立于 web profile。"
            : "桌面版当前未运行。启动它不会启动 dsh++ 管理的 Web 服务。";
        if (!string.IsNullOrEmpty(status.LatestCrashLog))
            DesktopHintText.Text += "\n最近一次 Host 崩溃日志：" + Path.GetFileName(status.LatestCrashLog);
        if (status.TaskLedgerOwner is "桌面版" or "Web 端")
            DesktopHintText.Text += $"\n任务看板单写锁当前由{status.TaskLedgerOwner}持有；另一端同时启动时该插件会降级，核心界面仍可用。";
        RefreshDesktopControls(!busy);
    }

    void RefreshDesktopControls(bool enabled)
    {
        if (BtnDesktopStart == null) return;
        BtnDesktopStart.IsEnabled = enabled && desktopInstalled && !desktopRunning;
        BtnDesktopStop.IsEnabled = enabled && desktopInstalled && desktopRunning;
        BtnDesktopRestart.IsEnabled = enabled && desktopInstalled && desktopRunning;
        BtnDesktopSync.IsEnabled = enabled && desktopInstalled && !upd.Busy;
        BtnDesktopCheck.IsEnabled = enabled && pluginCheckTask is not { IsCompleted: false };
    }

    async void BtnDesktopStart_Click(object sender, RoutedEventArgs e)
        => await RunTaskAsync("启动桌面版", async cancellation => await desktop.StartAsync());
    async void BtnDesktopStop_Click(object sender, RoutedEventArgs e)
        => await RunTaskAsync("停止桌面版", async _ => await desktop.StopAsync());
    async void BtnDesktopRestart_Click(object sender, RoutedEventArgs e)
        => await RunTaskAsync("重启桌面版", async cancellation => await desktop.RestartAsync());
    void BtnDesktopInstallDir_Click(object sender, RoutedEventArgs e) => desktop.OpenDirectory(cfg.DshDesktopDir);
    void BtnDesktopProfileDir_Click(object sender, RoutedEventArgs e) => desktop.OpenDirectory(desktop.ProfileDirectory);
    void BtnDesktopLogs_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(desktop.LogDirectory)) desktop.OpenDirectory(desktop.LogDirectory);
        else DesktopHintText.Text = "桌面日志目录尚不存在：" + desktop.LogDirectory;
    }

    async void BtnDesktopCheck_Click(object sender, RoutedEventArgs e)
    {
        var scope = plugins.Where(plugin => plugin.Profile == "desktop").ToList();
        if (scope.Count == 0) { DesktopCheckText.Text = "desktop profile 没有外部插件。"; return; }
        DesktopCheckText.Text = "正在后台检查桌面插件…";
        var summary = await CheckPluginsInUiAsync(scope);
        if (summary != null)
            DesktopCheckText.Text = $"{DateTime.Now:HH:mm} 桌面插件检查完成 · {summary.Updates} 有更新 · {summary.Failed} 失败 · {summary.SourceIssues} 来源提示";
    }

    async void BtnDesktopSync_Click(object sender, RoutedEventArgs e)
    {
        var web = plugins.FirstOrDefault(plugin => plugin.Profile == "web" && !plugin.IsIndirect);
        var desktopPlugin = plugins.FirstOrDefault(plugin => plugin.Profile == "desktop" && !plugin.IsIndirect);
        if (web == null || desktopPlugin == null) { DesktopCheckText.Text = "需要同时存在 web 与 desktop profile。"; return; }
        List<PlannedChange> changes;
        try
        {
            var plan = UpdateService.BuildDesktopSyncPlan(web.ProfileDir, desktopPlugin.ProfileDir);
            changes = plan.DependencyChanges.Select(change => new PlannedChange(change.Name,
                string.IsNullOrEmpty(change.Desktop) ? "新增 " + change.Web : change.Web == "<移除>" ? "移除当前声明" : $"{change.Desktop} → {change.Web}")).ToList();
            if (plan.BundleChanges.Count > 0) changes.Add(new PlannedChange("desktop bundle 层", plan.BundleChanges[0].Desktop + " → " + plan.BundleChanges[0].Web));
            changes.Add(new PlannedChange("cordis.patch.yml", "复制 web 用户配置；移除 web 远程自动隧道配置。"));
            changes.Add(new PlannedChange("任务看板", "桌面版与 Web 端共享全局单写锁；同一时间只有一端能提供完整任务看板。"));
        }
        catch (Exception error)
        {
            DesktopCheckText.Text = "无法生成同步计划：" + CommandRunner.Redact(error.Message);
            return;
        }
        if (!ConfirmMaintenance("从 Web 同步桌面配置", changes, new[] { "desktop" })) return;
        await RunTaskAsync("同步桌面版配置", async cancellation =>
            await Task.Run(() => upd.SynchronizeDesktopFromWebAsync(Progress, cancellation)));
        ReloadPlugins();
        RefreshDesktopStatus();
        BtnDesktopCheck_Click(sender, e);
    }

    async Task RefreshFreebuffAsync()
    {
        if (freebuff.IsOperationInProgress) return;
        var status = await Task.Run(() => freebuff.GetStatus());
        ApplyFreebuff(status);
    }
    void ApplyFreebuff(FreebuffStatus status)
    {
        FreebuffStateText.Text = status.State == FreebuffState.Running ? "运行中" : status.State == FreebuffState.Starting ? "启动中" : "已停止";
        FreebuffStatusText.Text = status.Message;
        BtnFreebuffStart.IsEnabled = !freebuff.IsOperationInProgress && status.State != FreebuffState.Running;
        BtnFreebuffStop.IsEnabled = !freebuff.IsOperationInProgress && status.State == FreebuffState.Running;
    }
    async Task RunFreebuffAsync(Func<FreebuffStatus> action)
    {
        if (freebuff.IsOperationInProgress) return;
        BtnFreebuffStart.IsEnabled = BtnFreebuffStop.IsEnabled = false;
        ApplyFreebuff(await Task.Run(action));
    }
    async void BtnFreebuffStart_Click(object sender, RoutedEventArgs e) => await RunFreebuffAsync(() => freebuff.Start());
    async void BtnFreebuffStop_Click(object sender, RoutedEventArgs e) => await RunFreebuffAsync(() => freebuff.Stop());
    async void BtnFreebuffRefresh_Click(object sender, RoutedEventArgs e) => await RefreshFreebuffAsync();
    void BtnFreebuffReveal_Click(object sender, RoutedEventArgs e)
    {
        freebuffReveal = !freebuffReveal;
        FreebuffConnectionText.Text = freebuffReveal ? $"Base URL: {freebuff.ConfiguredBaseUrl}\nAPI Key: {freebuff.ConfiguredApiKey}"
            : "Base URL: ********\nAPI Key: ********";
        BtnFreebuffReveal.Content = freebuffReveal ? "隐藏" : "显示";
    }
    void BtnFreebuffCopy_Click(object sender, RoutedEventArgs e) => Copy($"Base URL: {freebuff.ConfiguredBaseUrl}\nAPI Key: {freebuff.ConfiguredApiKey}", BtnFreebuffCopy);

    void BuildStorageRows()
    {
        StoragePanel.Children.Clear();
        var installation = new StorageRowInfo { Name = "DSH 安装目录", Path = cfg.DshAppDir, Kind = "installation", CanEdit = true };
        var desktopInstallation = new StorageRowInfo { Name = "官方桌面版安装目录", Path = cfg.DshDesktopDir, Kind = "desktop", CanEdit = true };
        foreach (var row in new[] { installation, desktopInstallation }.Concat(svc.StorageRows()))
        {
            var container = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            container.ColumnDefinitions.Add(new ColumnDefinition());
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            text.Children.Add(new TextBlock { Text = row.Name, Style = (Style)FindResource("TbBody"), FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = row.Path.Length == 0 ? row.Desc : row.Path, Style = (Style)FindResource("TbHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
            container.Children.Add(text);
            if (!row.Informational)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(actions, 1);
                var open = new Button { Content = "打开", Style = (Style)FindResource("BtnGhost") };
                open.Click += (_, _) => svc.OpenDir(row.IsFile ? Path.GetDirectoryName(row.Path) : row.Path);
                actions.Children.Add(open);
                if (row.CanEdit)
                {
                    var edit = new Button { Content = "修改", Style = (Style)FindResource("BtnGhost"), Margin = new Thickness(8, 0, 0, 0) };
                    edit.Click += (_, _) => EditPath(row);
                    actions.Children.Add(edit);
                }
                container.Children.Add(actions);
            }
            StoragePanel.Children.Add(container);
        }
    }
    void EditPath(StorageRowInfo row)
    {
        if (running || busy) { Log("请先停止服务再修改目录。"); return; }
        var dialog = new InputDialog("修改 " + row.Name, "请输入目录绝对路径。", row.Path) { Owner = this };
        if (dialog.ShowDialog() != true || !Path.IsPathFullyQualified(dialog.Value)) return;
        if (row.Kind == "installation") cfg.DshAppDir = dialog.Value;
        else if (row.Kind == "desktop") cfg.DshDesktopDir = dialog.Value;
        else if (row.Kind == "dsh") cfg.DshHome = dialog.Value;
        else cfg.AgentsHome = dialog.Value;
        cfg.Save();
        BuildStorageRows();
        ReloadPlugins();
        RefreshVersions();
        RefreshDesktopStatus();
    }

    async void BtnDiagnose_Click(object sender, RoutedEventArgs e)
    {
        BtnDiagnose.IsEnabled = false;
        try { AppendLog(await Task.Run(() => Diagnostics.ReportAsync(cfg, svc, upd))); }
        catch (Exception exception) { Log(exception.Message); }
        finally { BtnDiagnose.IsEnabled = true; }
    }
    void BtnCopyLogs_Click(object sender, RoutedEventArgs e) => Copy(CommandRunner.Redact(LogBox.Text));
    void BtnExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = $"dsh-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt", Filter = "文本文件|*.txt" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, CommandRunner.Redact(LogBox.Text), Encoding.UTF8);
    }
    void Copy(string text, Button source = null)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { Log("剪贴板被占用，请重试。"); return; }
        if (source == null) return;
        var original = source.Content;
        source.Content = "已复制";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) => { timer.Stop(); source.Content = original; };
        timer.Start();
    }
    void BtnHelp_Click(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();
    public void Log(string text) => AppendLog($"[{DateTime.Now:HH:mm:ss}] {text}\n");
    void AppendLog(string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (LogBox == null) return;
            if (LogBox.Text == "暂无日志") LogBox.Clear();
            LogBox.AppendText(CommandRunner.Redact(text));
            if (LogBox.Text.Length > 100000) LogBox.Text = LogBox.Text[^80000..];
            if (FollowLog.IsChecked == true) LogBox.ScrollToEnd();
            OverviewLog.Text = string.Join("\n", LogBox.Text.Split('\n').TakeLast(16));
            OverviewLog.ScrollToEnd();
        });
    }

    void OnClosing(object sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; closingAfterTask = true; taskCancellation?.Cancel(); return; }
        if (pluginCheckTask is { IsCompleted: false } || !versionChecks.IsCompleted || !healthCheckTask.IsCompleted || !sessionHistoryTask.IsCompleted)
        {
            e.Cancel = true;
            if (!joiningReadChecks) { joiningReadChecks = true; _ = JoinReadChecksAndCloseAsync(); }
            return;
        }
        if (App.RenderDirectory == null && !selfUpdating && running)
        {
            var answer = MessageBox.Show(this, "DSH 服务正在运行。是否保留后台服务？\n「否」将停止服务后退出。",
                "退出控制台", MessageBoxButton.YesNoCancel);
            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                joiningReadChecks = closingAfterTask = false;
                if (readCancellation.IsCancellationRequested) { readCancellation.Dispose(); readCancellation = new(); }
                return;
            }
            if (answer == MessageBoxResult.No) svc.StopDsh();
        }
        refreshTimer.Stop();
        if (App.RenderDirectory == null)
        {
            cfg.WinMaximized = WindowState == WindowState.Maximized;
            var bounds = RestoreBounds;
            cfg.WinLeft = bounds.Left; cfg.WinTop = bounds.Top;
            cfg.WinWidth = bounds.Width; cfg.WinHeight = bounds.Height;
            cfg.Save();
        }
        svc.Dispose();
        readCancellation.Dispose();
    }

    async Task JoinReadChecksAndCloseAsync()
    {
        readCancellation.Cancel();
        await Task.WhenAll(StopPluginCheckAsync(), StopHealthCheckAsync(), versionChecks, sessionHistoryTask);
        Close();
    }
}
