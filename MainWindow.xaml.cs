using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace DshControl;

public partial class MainWindow : Window
{
    readonly AppConfig cfg;
    readonly DshService svc;
    readonly FreebuffService freebuff;
    readonly UpdateService upd;
    readonly DispatcherTimer refreshTimer;
    bool fwDone;
    bool ready;
    bool freebuffReveal;
    int freebuffProbe;
    bool dshUpdating;
    bool appUpdating;
    bool pluginsUpdating;
    bool appUpdateReady;
    string appUpdateZip;
    DateTime lastTunnelQuery;
    Action tsAction = () => { };

    public MainWindow()
    {
        cfg = App.Cfg;
        svc = new DshService(cfg, Log);
        InitializeComponent();
        freebuff = new FreebuffService(@"F:\freebuffapi", Log);
        upd = new UpdateService(cfg, svc, Log);
        SetThemeIcon();
        BuildStorageRows();
        LoadConfigToUi();
        FrontendPatch.Ensure(cfg, Log);
        svc.StartLogTail(AppendLogLines);
        RefreshAll();
        ready = true;
        NavList.SelectedIndex = App.StartPage >= 0 ? App.StartPage : 0;
        // 更新中心：先显示本地信息，再后台自动检查（dsh++ 新版本 / 插件自动更新）
        Dispatcher.BeginInvoke(new Action(InitUpdateCenter), DispatcherPriority.Background);
        if (App.UiCheck)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { new HelpWindow { Owner = this }.ShowDialog(); }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "uicheck-err.txt"), ex.ToString());
                }
            }));

        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        refreshTimer.Tick += (s, e) => RefreshAll();
        refreshTimer.Start();
        Closing += OnClosing;
        // Freebuff 组件不随 dsh++ 启动自动启动：只查一次状态，由「② Freebuff 服务」页面手动启动/停止
        Dispatcher.BeginInvoke(new Action(() => RefreshFreebuffStatus(true)), DispatcherPriority.Background);
    }

    Brush B(string key) => (Brush)FindResource(key);
    Brush ToneBrush(Tone t) => t switch
    {
        Tone.Green => B("SuccessBrush"),
        Tone.Orange => B("WarningBrush"),
        _ => B("TextTertiaryBrush"),
    };

    // ---------------------------------------------------------------- 主题

    void SetThemeIcon()
    {
        var geom = cfg.Theme == "dark" ? "SunGeom" : "MoonGeom";
        ThemeIcon.Data = (Geometry)FindResource(geom);
    }

    void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        cfg.Theme = cfg.Theme == "dark" ? "light" : "dark";
        cfg.Save();
        var exe = Environment.ProcessPath;
        try { if (!string.IsNullOrEmpty(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch { }
        Application.Current.Shutdown();
    }

    // ---------------------------------------------------------------- 页面切换

    void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageSvc == null) return;
        var idx = NavList.SelectedIndex;
        PageSvc.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageFreebuff.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        PagePhone.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageAdv.Visibility = idx == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageStore.Visibility = idx == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageUpdates.Visibility = idx == 5 ? Visibility.Visible : Visibility.Collapsed;
        PageLog.Visibility = idx == 6 ? Visibility.Visible : Visibility.Collapsed;
        if (idx == 5) RefreshUpdateStats();
    }

    // ---------------------------------------------------------------- 配置

    void LoadConfigToUi()
    {
        PortBox.Text = cfg.Port.ToString();
        TglLocalOnly.IsChecked = cfg.Mode == "local";
        var am = cfg.AccessMode ?? "ip";
        RbIp.IsChecked = am == "ip";
        RbDomain.IsChecked = am == "domain";
        RbTs.IsChecked = am == "tailscale";
        HostBox.Text = cfg.DisplayHost ?? "";
        HostBox.IsEnabled = am == "domain";
        TglAutoTunnel.IsChecked = cfg.AutoTunnel;
        TglRequirePairing.IsChecked = !cfg.RequirePairing;
        TglAutoPlugin.IsChecked = cfg.AutoUpdatePlugins;
        UpdatePhonePreview();
    }

    void SetAccess(string mode)
    {
        if (!ready) return;
        cfg.AccessMode = mode;
        HostBox.IsEnabled = mode == "domain";
        cfg.Save();
        UpdatePhonePreview();
    }

    void PortBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        if (int.TryParse(PortBox.Text.Trim(), out var p) && p >= 1024 && p <= 65535 && cfg.Port != p)
        {
            cfg.Port = p;
            cfg.Save();
            UpdatePhonePreview();
        }
    }

    void PortBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(c => !char.IsDigit(c));
    }

    void TglLocalOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        cfg.Mode = TglLocalOnly.IsChecked == true ? "local" : "lan";
        cfg.Save();
        UpdatePhonePreview();
    }

    void RbIp_Checked(object sender, RoutedEventArgs e) { if (ready && RbIp.IsChecked == true) SetAccess("ip"); }
    void RbDomain_Checked(object sender, RoutedEventArgs e) { if (ready && RbDomain.IsChecked == true) SetAccess("domain"); }
    void RbTs_Checked(object sender, RoutedEventArgs e) { if (ready && RbTs.IsChecked == true) SetAccess("tailscale"); }

    void HostBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        cfg.DisplayHost = HostBox.Text.Trim();
        cfg.Save();
        UpdatePhonePreview();
    }

    void TglAutoTunnel_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        cfg.AutoTunnel = TglAutoTunnel.IsChecked == true;
        cfg.Save();
        lastTunnelQuery = DateTime.MinValue;
    }

    void TglRequirePairing_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        cfg.RequirePairing = !(TglRequirePairing.IsChecked == true);
        cfg.Save();
    }

    void UpdatePhonePreview()
    {
        if (HeroUrlText == null) return;
        if (cfg.Mode == "local")
        {
            HeroUrlText.Text = "（已关闭——取消「只让本机使用」勾选后开放）";
            HeroUrlText.Foreground = B("TextSecondaryBrush");
            HeroUrlText.Tag = null;
            BtnCopyPhone.IsEnabled = false;
            PhoneHintText.Text = "你在①里勾选了「只让本机使用」，取消勾选后这里会出现手机地址";
            return;
        }
        BtnCopyPhone.IsEnabled = true;
        HeroUrlText.Foreground = B("PrimaryBrush");
        var url = $"http://{svc.DisplayHost()}:{cfg.Port}";
        HeroUrlText.Text = url;
        HeroUrlText.Tag = url;
        string hint;
        if (cfg.AccessMode == "tailscale")
            hint = TailscaleCli.Exists(cfg) && TailscaleCli.Status(cfg).Ips.Count > 0
                ? "Tailscale 已就绪：手机在任何网络都能打开，完全实时"
                : "Tailscale 还没登录：先完成下面「连接检查」第 2 项";
        else if (cfg.AccessMode == "domain")
            hint = "确保域名解析到这台电脑；修改后需先「停止服务」再「启动服务」才生效";
        else
            hint = "手机需要和电脑在同一网络（同一 Wi-Fi 或手机热点）";
        PhoneHintText.Text = hint;
    }

    // ---------------------------------------------------------------- 按钮动作

    void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        var (ok, message) = svc.StartDsh();
        if (!ok) MessageBox.Show(this, message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void BtnStop_Click(object sender, RoutedEventArgs e) => svc.StopDsh();
    void BtnOpenUi_Click(object sender, RoutedEventArgs e) => svc.OpenUi();
    void BtnFwAdd_Click(object sender, RoutedEventArgs e) { svc.AddFirewallRule(); fwDone = true; }

    // ---------------------------------------------------------------- 更新中心

    void InitUpdateCenter()
    {
        AppVerText.Text = "当前版本：" + UpdateService.AppVersion();
        var (ver, err) = upd.LocalDshVersion();
        DshVerText.Text = err == null ? "当前版本：" + ver : "当前版本：读取失败（" + err + "）";
        RefreshUpdateStats();
        // 后台检查一次 dsh++ 新版本（不阻塞、失败不打扰）
        Task.Run(async () =>
        {
            var (tag, zip, e2) = await upd.LatestAppReleaseAsync();
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (e2 != null) return; // 静默失败，需要时手动点「检查」
                var latest = (tag ?? "").TrimStart('v', 'V');
                if (UpdateService.IsNewer(latest, UpdateService.AppVersion()))
                {
                    appUpdateReady = true;
                    appUpdateZip = zip;
                    BtnAppUpdate.IsEnabled = true;
                    AppUpdHint.Text = $"发现新版本 {tag}（当前 {UpdateService.AppVersion()}）——点「一键更新」自动下载并重启。";
                    Log("[自更新] 发现 dsh++ 新版本 " + tag);
                }
                else
                {
                    AppUpdHint.Text = $"dsh++ 已是最新（{UpdateService.AppVersion()}）。";
                }
            }));
        });
        // 插件自动更新：开关打开且 dsh 未运行时执行一次
        if (cfg.AutoUpdatePlugins && !svc.IsDshRunning())
        {
            Task.Run(() =>
            {
                var (ok, msg) = upd.UpdateAllPlugins();
                Log("[插件] 自动更新：" + msg);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    PluginHintText.Text = msg;
                    RefreshUpdateStats();
                }));
            });
        }
        else if (cfg.AutoUpdatePlugins)
        {
            Log("[插件] dsh 正在运行，跳过启动时自动更新；停止服务后可在「⑥ 检查更新」手动更新。");
        }
    }

    void RefreshUpdateStats()
    {
        if (pluginsUpdating) return;
        try { PluginStatText.Text = "插件：" + upd.PluginSummary(); } catch { }
    }

    async void BtnDshCheck_Click(object sender, RoutedEventArgs e)
    {
        if (dshUpdating) return;
        BtnDshCheck.IsEnabled = false;
        DshUpdHint.Text = "正在检查 npm 上的版本（正式版 / 抢先体验版）…";
        try
        {
            var (local, lerr) = upd.LocalDshVersion();
            var (latest, alpha, rerr) = await Task.Run(() => upd.RemoteDshVersions());
            if (lerr != null) { DshUpdHint.Text = "读取本地版本失败：" + lerr; return; }
            DshVerText.Text = "当前版本：" + local;
            if (rerr != null) { DshUpdHint.Text = "检查失败：" + (rerr.Split('\n').FirstOrDefault()?.Trim() ?? rerr); return; }
            var stableNewer = UpdateService.IsNewer(latest, local);
            var alphaNewer = UpdateService.IsNewer(alpha, local);
            BtnDshUpdateStable.IsEnabled = stableNewer;
            BtnDshUpdateAlpha.IsEnabled = alphaNewer;
            DshRemoteText.Text = $"最新版本：正式 {latest} · 抢先体验 {alpha}" + (string.IsNullOrEmpty(alpha) ? "（源未提供）" : "");
            if (stableNewer || alphaNewer)
                DshUpdHint.Text = $"当前 {local}，有可更新的版本——正式版更稳，抢先体验版（对应 GitHub alpha Release）功能更新但可能不稳定。";
            else
                DshUpdHint.Text = $"当前 {local}，正式版与抢先体验版都已是最新或更高。";
        }
        finally { BtnDshCheck.IsEnabled = true; }
    }

    async void DoDshUpdate(string spec)
    {
        if (dshUpdating) return;
        var label = spec == "alpha" ? "抢先体验版" : "正式版";
        if (svc.IsDshRunning())
        {
            var r = MessageBox.Show(this, $"更新 dsh 需要先停止服务。现在停止并更新到{label}吗？",
                "更新 dsh", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            svc.StopDsh();
            for (var i = 0; i < 15 && svc.IsDshRunning(); i++) await Task.Delay(1000);
            if (svc.IsDshRunning())
            {
                MessageBox.Show(this, "服务未能停止，请稍后手动重试。", "更新 dsh", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        dshUpdating = true;
        BtnDshUpdateStable.IsEnabled = false;
        BtnDshUpdateAlpha.IsEnabled = false;
        BtnDshCheck.IsEnabled = false;
        DshUpdHint.Text = $"正在更新 dsh 到{label}（npm install，可能需要几分钟，详见运行日志）…";
        try
        {
            var (ok, msg) = await Task.Run(() => upd.UpdateDsh(spec));
            DshUpdHint.Text = msg;
            if (ok)
            {
                var (ver, _) = upd.LocalDshVersion();
                DshVerText.Text = "当前版本：" + ver;
                DshRemoteText.Text = "最新版本：未检查";
                try { FrontendPatch.Ensure(cfg, Log); } catch { }
            }
            else
            {
                BtnDshUpdateStable.IsEnabled = true;
                BtnDshUpdateAlpha.IsEnabled = true;
            }
        }
        finally { dshUpdating = false; BtnDshCheck.IsEnabled = true; }
    }

    void BtnDshUpdateStable_Click(object sender, RoutedEventArgs e) => DoDshUpdate("latest");
    void BtnDshUpdateAlpha_Click(object sender, RoutedEventArgs e) => DoDshUpdate("alpha");

    async void BtnAppCheck_Click(object sender, RoutedEventArgs e)
    {
        if (appUpdating) return;
        BtnAppCheck.IsEnabled = false;
        AppUpdHint.Text = "正在检查 GitHub 最新 Release…";
        try
        {
            var (tag, zip, err) = await upd.LatestAppReleaseAsync();
            if (err != null) { AppUpdHint.Text = "检查失败：" + err; return; }
            var latest = (tag ?? "").TrimStart('v', 'V');
            if (UpdateService.IsNewer(latest, UpdateService.AppVersion()))
            {
                appUpdateReady = true;
                appUpdateZip = zip;
                BtnAppUpdate.IsEnabled = true;
                AppUpdHint.Text = $"发现新版本 {tag}（当前 {UpdateService.AppVersion()}）——点「一键更新」自动下载并重启。";
            }
            else
            {
                BtnAppUpdate.IsEnabled = false;
                AppUpdHint.Text = $"dsh++ 已是最新（{UpdateService.AppVersion()}）。";
            }
        }
        finally { BtnAppCheck.IsEnabled = true; }
    }

    async void BtnAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (appUpdating || !appUpdateReady) return;
        var r = MessageBox.Show(this, "将下载新版本并自动替换重启（更新期间 dsh++ 会关闭一次，dsh 服务不受影响）。继续吗？",
            "更新 dsh++", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return;
        appUpdating = true;
        BtnAppUpdate.IsEnabled = false;
        AppUpdHint.Text = "正在下载更新包…";
        var (ok, msg) = await upd.ApplyAppUpdateAsync(appUpdateZip);
        if (ok)
        {
            AppUpdHint.Text = msg;
            await Task.Delay(800);
            Application.Current.Shutdown();
        }
        else
        {
            AppUpdHint.Text = msg;
            appUpdating = false;
            BtnAppUpdate.IsEnabled = true;
        }
    }

    void BtnOpenReleases_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/Jensen-Yao/dsh-plus-plus/releases") { UseShellExecute = true }); } catch { }
    }

    void BtnPluginRefresh_Click(object sender, RoutedEventArgs e) => RefreshUpdateStats();

    void BtnPluginVersions_Click(object sender, RoutedEventArgs e)
    {
        if (PluginListBorder.Visibility == Visibility.Visible)
        {
            PluginListBorder.Visibility = Visibility.Collapsed;
            BtnPluginVersions.Content = "查看插件版本";
            return;
        }
        BtnPluginVersions.Content = "隐藏插件版本";
        PluginListText.Text = "正在读取插件版本…";
        PluginListBorder.Visibility = Visibility.Visible;
        Task.Run(() =>
        {
            var report = upd.PluginVersionsReport();
            Dispatcher.BeginInvoke(new Action(() => PluginListText.Text = report));
        });
    }

    async void BtnPluginUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (pluginsUpdating) return;
        if (svc.IsDshRunning())
        {
            MessageBox.Show(this, "插件更新需要先停止 dsh 服务：请到「① 服务开关」点「停止服务」，再回来更新。",
                "更新插件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        pluginsUpdating = true;
        BtnPluginUpdate.IsEnabled = false;
        BtnPluginRefresh.IsEnabled = false;
        PluginHintText.Text = "正在更新插件（逐 profile 执行 pnpm update，详见运行日志）…";
        try
        {
            var (ok, msg) = await Task.Run(() => upd.UpdateAllPlugins());
            PluginHintText.Text = msg;
            Log("[插件] " + msg);
        }
        finally
        {
            pluginsUpdating = false;
            BtnPluginUpdate.IsEnabled = true;
            BtnPluginRefresh.IsEnabled = true;
            RefreshUpdateStats();
        }
    }

    void TglAutoPlugin_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        cfg.AutoUpdatePlugins = TglAutoPlugin.IsChecked == true;
        cfg.Save();
    }

    void BtnFreebuffStart_Click(object sender, RoutedEventArgs e)
        => RunFreebuffOperation(() => freebuff.Start());

    void BtnFreebuffStop_Click(object sender, RoutedEventArgs e)
        => RunFreebuffOperation(() => freebuff.Stop());

    void BtnFreebuffRefresh_Click(object sender, RoutedEventArgs e)
        => RefreshFreebuffStatus(true);

    void BtnFreebuffReveal_Click(object sender, RoutedEventArgs e)
    {
        freebuffReveal = !freebuffReveal;
        ApplyFreebuffConnectionInfo();
    }

    void BtnFreebuffCopy_Click(object sender, RoutedEventArgs e)
    {
        CopyText($"Base URL: {freebuff.ConfiguredBaseUrl}{Environment.NewLine}API Key: {freebuff.ConfiguredApiKey}");
    }

    void RunFreebuffOperation(Func<FreebuffStatus> operation)
    {
        if (freebuff.IsOperationInProgress) return;
        BtnFreebuffStart.IsEnabled = false;
        BtnFreebuffStop.IsEnabled = false;
        BtnFreebuffRefresh.IsEnabled = false;
        FreebuffStateText.Text = "◌ 正在处理...";
        Task.Run(operation).ContinueWith(t =>
        {
            var status = t.IsFaulted
                ? new FreebuffStatus { State = FreebuffState.Error, Message = t.Exception?.GetBaseException().Message ?? "操作失败" }
                : t.Result;
            Dispatcher.BeginInvoke(new Action(() => ApplyFreebuffStatus(status)));
        }, TaskScheduler.Default);
    }

    void RefreshFreebuffStatus(bool immediate = false)
    {
        if (!immediate && NavList.SelectedIndex != 1 && freebuffProbe != 0) return;
        if (Interlocked.Exchange(ref freebuffProbe, 1) != 0) return;
        Task.Run(() => freebuff.GetStatus()).ContinueWith(t =>
        {
            var status = t.IsFaulted
                ? new FreebuffStatus { State = FreebuffState.Error, Message = t.Exception?.GetBaseException().Message ?? "状态检查失败" }
                : t.Result;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref freebuffProbe, 0);
                ApplyFreebuffStatus(status);
            }));
        }, TaskScheduler.Default);
    }

    void ApplyFreebuffStatus(FreebuffStatus status)
    {
        Interlocked.Exchange(ref freebuffProbe, 0);
        var color = status.State == FreebuffState.Running ? "SuccessBrush" :
            status.State is FreebuffState.Starting ? "WarningBrush" : "TextTertiaryBrush";
        FreebuffDot.Fill = B(color);
        FreebuffStateText.Text = status.State switch
        {
            FreebuffState.Running => "● 正在运行",
            FreebuffState.Starting => "◌ 正在启动",
            FreebuffState.DockerUnavailable => "○ Docker 未就绪",
            FreebuffState.Error => "○ 启动失败",
            _ => "○ 未启动",
        };
        FreebuffStateText.Foreground = B(color);
        FreebuffStatusText.Text = status.Message;
        BtnFreebuffStart.IsEnabled = status.State != FreebuffState.Running && !freebuff.IsOperationInProgress;
        BtnFreebuffStop.IsEnabled = status.State == FreebuffState.Running && !freebuff.IsOperationInProgress;
        BtnFreebuffRefresh.IsEnabled = !freebuff.IsOperationInProgress;
        ApplyFreebuffConnectionInfo();
    }

    void ApplyFreebuffConnectionInfo()
    {
        if (freebuffReveal)
        {
            FreebuffConnectionText.Text = $"Base URL: {freebuff.ConfiguredBaseUrl}{Environment.NewLine}API Key:  {freebuff.ConfiguredApiKey}";
            BtnFreebuffReveal.Content = "隐藏 Base 和 Key";
        }
        else
        {
            FreebuffConnectionText.Text = "Base URL: ********\nAPI Key:  ********";
            BtnFreebuffReveal.Content = "显示 Base 和 Key";
        }
    }

    void BtnTsFix_Click(object sender, RoutedEventArgs e) => tsAction();

    void BtnCopyPhone_Click(object sender, RoutedEventArgs e) { if (HeroUrlText.Tag is string s) CopyText(s); }
    void PcUrlText_Click(object sender, MouseButtonEventArgs e) { if (PcUrlText.Tag is string s) CopyText(s); }
    void HeroUrlText_Click(object sender, MouseButtonEventArgs e) { if (HeroUrlText.Tag is string s) CopyText(s); }
    void BtnCopyTunnel_Click(object sender, RoutedEventArgs e) { if (TunnelText.Tag is string s) CopyText(s); }

    void CopyText(string s)
    {
        try { Clipboard.SetText(s); Log("已复制: " + s); }
        catch { }
    }

    void BtnHelp_Click(object sender, RoutedEventArgs e) => new HelpWindow { Owner = this }.ShowDialog();
    void LinkSessions_Click(object sender, RoutedEventArgs e) => svc.OpenDir(Path.Combine(svc.HomeBase, "sessions"));
    void LinkConfigDir_Click(object sender, RoutedEventArgs e) => svc.OpenDir(svc.HomeBase);

    // ---------------------------------------------------------------- 存储位置

    void BuildStorageRows()
    {
        if (StoragePanel == null) return;
        StoragePanel.Children.Clear();
        foreach (var r in svc.StorageRows())
            StoragePanel.Children.Add(BuildStorageRow(r));
    }

    FrameworkElement BuildStorageRow(StorageRowInfo r)
    {
        var exists = !string.IsNullOrEmpty(r.Path) && (r.IsFile ? File.Exists(r.Path) : Directory.Exists(r.Path));
        var root = new StackPanel { Margin = new Thickness(0, 2, 0, 12) };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = B(exists ? "SuccessBrush" : "TextTertiaryBrush"),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = r.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = B("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(r.Tag))
        {
            var blue = r.Tag.Contains("MCP");
            var chip = new Border
            {
                Background = B(blue ? "ChipBlueBgBrush" : "ChipGreenBgBrush"),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 1, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            chip.Child = new TextBlock
            {
                Text = r.Tag,
                FontSize = 11,
                Foreground = B(blue ? "ChipBlueTextBrush" : "ChipGreenTextBrush"),
            };
            head.Children.Add(chip);
        }
        root.Children.Add(head);

        if (!string.IsNullOrEmpty(r.Path))
            root.Children.Add(new TextBlock
            {
                Text = r.Path,
                FontSize = 12,
                Foreground = B("TextTertiaryBrush"),
                Margin = new Thickness(15, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        if (!string.IsNullOrEmpty(r.Desc))
            root.Children.Add(new TextBlock
            {
                Text = r.Desc,
                FontSize = 12,
                Foreground = B("TextSecondaryBrush"),
                Margin = new Thickness(15, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        if (!string.IsNullOrEmpty(r.Sub))
            root.Children.Add(new TextBlock
            {
                Text = "技能查找范围：此目录下的 skills 子目录",
                FontSize = 12,
                Foreground = B("TextSecondaryBrush"),
                Margin = new Thickness(15, 1, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

        if (!r.Informational)
        {
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(15, 6, 0, 0) };
            btns.Children.Add(MakeBtn(r.IsFile ? "打开所在目录" : "打开目录",
                () => svc.OpenDir(r.IsFile ? Path.GetDirectoryName(r.Path) : r.Path)));
            btns.Children.Add(MakeBtn("复制路径", () =>
            {
                try { Clipboard.SetText(r.Path); Log("已复制: " + r.Path); } catch { }
            }));
            if (r.CanEdit)
                btns.Children.Add(MakeBtn("修改…", () => { if (r.Kind == "dsh") EditDshHome(); else EditAgentsHome(); }, primary: true));
            root.Children.Add(btns);
        }
        return root;
    }

    Button MakeBtn(string text, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource(primary ? "BtnPrimary" : "BtnSecondary"),
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(0, 0, 8, 0),
        };
        b.Click += (s, e) => onClick();
        return b;
    }

    void EditDshHome()
    {
        var dlg = new InputDialog("修改 DSH 主目录",
            "输入新的 DSH 主目录（支持 ~ 开头；留空 = 默认 ~/.dsh）：\n改完后需要「停止→启动」服务才生效；把旧目录内容复制过去即可迁移。",
            cfg.DshHome) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            cfg.DshHome = dlg.Value;
            cfg.Save();
            Log("DSH 主目录已改为: " + (cfg.DshHome == "" ? "默认 ~/.dsh" : cfg.DshHome));
            BuildStorageRows();
        }
    }

    void EditAgentsHome()
    {
        var dlg = new InputDialog("修改 Agents 技能目录",
            "输入新的 Agents 目录（支持 ~ 开头；留空 = 默认 ~/.agents）：\n改完后需要「停止→启动」服务才生效。",
            cfg.AgentsHome) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            cfg.AgentsHome = dlg.Value;
            cfg.Save();
            Log("Agents 目录已改为: " + (cfg.AgentsHome == "" ? "默认 ~/.agents" : cfg.AgentsHome));
            BuildStorageRows();
        }
    }

    // ---------------------------------------------------------------- 刷新

    void RefreshAll()
    {
        try
        {
            var running = svc.IsDshRunning();
            SvcDot.Fill = B(running ? "SuccessBrush" : "TextTertiaryBrush");
            SvcStateText.Text = running ? "● 服务运行中" : "○ 服务未启动";
            BtnStart.IsEnabled = !running;
            BtnStop.IsEnabled = running;
            BtnOpenUi.IsEnabled = running;
            PcUrlText.Text = running
                ? $"电脑自己打开用：{svc.LocalUrl()}（只有这台电脑能用；手机用下面的地址）"
                : $"电脑打开用：{svc.LocalUrl()}（先点「▶ 启动服务」）";
            PcUrlText.Tag = svc.LocalUrl();

            if (running)
            {
                var bind = svc.GetBindAddress(cfg.Port);
                var open = bind == "0.0.0.0";
                var desc = open
                    ? "所有网络都能连进来（局域网 / Tailscale / 隧道）——手机入口已开放"
                    : "只有这台电脑能用——手机连不进来";
                var mismatch = (cfg.Mode == "local") == open;
                BindText.Foreground = B(open ? "WarningBrush" : "TextSecondaryBrush");
                BindText.Text = $"对外绑定：{bind ?? "?"}　{desc}" + (mismatch ? "　（与①的勾选不一致，重启服务后生效）" : "");
            }
            else
            {
                BindText.Foreground = B("TextSecondaryBrush");
                BindText.Text = cfg.Mode == "local"
                    ? "对外绑定（未运行）：启动后只绑 127.0.0.1 —— 手机连不进来"
                    : "对外绑定（未运行）：启动后绑 0.0.0.0 全网卡 —— 手机可连";
            }

            CkSvcText.Text = running ? "✓ 服务已启动（对外已开放）" : "✗ 服务没启动——手机连不上，点上方「▶ 启动服务」";
            CkSvcText.Foreground = B(running ? "SuccessBrush" : "WarningBrush");

            CkFwText.Text = fwDone
                ? "✓ 防火墙已放行（如手机仍打不开，多半是路由器问题，改用 Tailscale）"
                : "？ 防火墙未确认——手机打不开时点右边「一键放行」（弹 UAC 点「是」）";
            CkFwText.Foreground = B(fwDone ? "SuccessBrush" : "TextTertiaryBrush");
        }
        catch { }
        try { RefreshTs(); } catch { }
        try { RefreshTunnel(); } catch { }
        try { RefreshFreebuffStatus(); } catch { }
    }

    void RefreshTs()
    {
        var u = svc.GetTsState();
        CkTsText.Text = u.CkText;
        CkTsText.Foreground = ToneBrush(u.Tone);
        TsDetailText.Text = u.Detail;
        BtnTsFix.Content = u.FixLabel;
        tsAction = u.FixAction switch
        {
            "install" => () => svc.TsInstall(),
            "login" => () => svc.StartTsAsync("login"),
            "up" => () => svc.StartTsAsync("up"),
            "down" => () => svc.TsDown(),
            _ => () => { },
        };
    }

    void RefreshTunnel()
    {
        if (TglAutoTunnel.IsChecked != true)
        {
            TunnelText.Text = "";
            TunnelText.Tag = null;
            BtnCopyTunnel.IsEnabled = false;
            return;
        }
        if (!svc.IsDshRunning())
        {
            TunnelText.Text = "启动服务后自动生成临时随机域名（仅用于手机临时扫码）";
            TunnelText.Tag = null;
            BtnCopyTunnel.IsEnabled = false;
            return;
        }
        if ((DateTime.Now - lastTunnelQuery).TotalSeconds < 10) return;
        lastTunnelQuery = DateTime.Now;
        Task.Run(() =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var json = client.GetStringAsync($"http://127.0.0.1:{cfg.Port}/api/pair/status").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("publicUrl", out var pu) && pu.GetString() is string url && url.Length > 0)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TunnelText.Text = $"当前临时随机域名：{url}（每次启动会变）";
                        TunnelText.Tag = url;
                        BtnCopyTunnel.IsEnabled = true;
                    }));
            }
            catch { }
        });
    }

    // ---------------------------------------------------------------- 日志

    public void Log(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LogBox == null) return;
            EnsureLogReady();
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            TrimLog();
        }));
    }

    void AppendLogLines(string text)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (LogBox == null) return;
            EnsureLogReady();
            LogBox.AppendText(text);
            TrimLog();
        }));
    }

    void EnsureLogReady()
    {
        if (LogBox.Text.StartsWith("（尚无日志")) LogBox.Clear();
    }

    void TrimLog()
    {
        if (LogBox.LineCount > 600)
        {
            var lines = LogBox.Text.Split('\n').ToList();
            if (lines.Count > 600)
                LogBox.Text = string.Join("\n", lines.Skip(lines.Count - 400));
        }
        LogBox.ScrollToEnd();
    }

    // ---------------------------------------------------------------- 退出

    void OnClosing(object sender, CancelEventArgs e)
    {
        svc.Dispose();
        if (svc.IsDshRunning())
        {
            var r = MessageBox.Show(this,
                "dsh web 正在运行。\n\n点击「是」保持它继续在后台运行（本应用关闭不影响服务）；\n点击「否」先停止服务再退出。",
                "退出确认", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.No) svc.StopDsh();
            else if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        }
        cfg.Save();
    }
}
