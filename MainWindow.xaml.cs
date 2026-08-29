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
    readonly DispatcherTimer refreshTimer;
    bool fwDone;
    bool ready;
    bool freebuffReveal;
    int freebuffProbe;
    DateTime lastTunnelQuery;
    Action tsAction = () => { };

    public MainWindow()
    {
        cfg = App.Cfg;
        svc = new DshService(cfg, Log);
        InitializeComponent();
        freebuff = new FreebuffService(@"F:\freebuffapi", Log);
        SetThemeIcon();
        BuildStorageRows();
        LoadConfigToUi();
        FrontendPatch.Ensure(cfg, Log);
        svc.StartLogTail(AppendLogLines);
        RefreshAll();
        ready = true;
        NavList.SelectedIndex = App.StartPage >= 0 ? App.StartPage : 0;
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
        PageLog.Visibility = idx == 5 ? Visibility.Visible : Visibility.Collapsed;
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
