using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DshControl;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        AddTab("快速开始", HelpTexts.QuickStart);
        AddTab("局域网互联", HelpTexts.Lan);
        AddTab("Tailscale", HelpTexts.Ts);
        AddTab("公网隧道与安全", HelpTexts.Tunnel);
        AddTab("存储位置", HelpTexts.Storage);
    }

    Brush B(string key) => (Brush)FindResource(key);

    void AddTab(string title, (string kind, string text)[] blocks)
    {
        var page = new TabItem { Header = title };
        var card = new Border
        {
            Style = (Style)FindResource("CardStyle"),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 8, 0, 0),
        };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var sp = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
        foreach (var (kind, text) in blocks)
            sp.Children.Add(BuildBlock(kind, text));
        scroll.Content = sp;
        card.Child = scroll;
        page.Content = card;
        Tabs.Items.Add(page);
    }

    FrameworkElement BuildBlock(string kind, string text)
    {
        switch (kind)
        {
            case "h":
                return new TextBlock
                {
                    Text = text,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = B("TextPrimaryBrush"),
                    Margin = new Thickness(0, 12, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                };
            case "b":
                return new TextBlock
                {
                    Text = "•  " + text,
                    FontSize = 12.5,
                    Foreground = B("TextPrimaryBrush"),
                    Margin = new Thickness(0, 1, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                };
            case "code":
                return new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12.5,
                    Foreground = B("PrimaryBrush"),
                    Margin = new Thickness(0, 1, 0, 2),
                    TextWrapping = TextWrapping.Wrap,
                };
            default:
                return new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    Foreground = B("TextPrimaryBrush"),
                    Margin = new Thickness(0, 1, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                };
        }
    }
}
