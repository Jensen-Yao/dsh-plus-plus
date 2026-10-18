using System.Windows;

namespace DshControl;

public record PlannedChange(string Name, string Versions);

public partial class UpdatePreviewWindow : Window
{
    public UpdatePreviewWindow(string title, IEnumerable<PlannedChange> changes, bool running, bool restart)
    {
        InitializeComponent();
        PreviewTitle.Text = title;
        ChangeList.ItemsSource = changes;
        ImpactText.Text = (running ? restart ? "服务将暂时停止，完成后重新启动。" : "服务将停止，完成后保持停止。" : "服务当前未运行，维护后保持停止。")
            + "\n依赖清单、锁文件及配置会先备份；安装失败将尝试恢复。传递依赖与共享锁文件可能一起变化。";
    }

    void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
