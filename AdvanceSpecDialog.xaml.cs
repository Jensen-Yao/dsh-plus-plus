using System.Windows;

namespace DshControl;

/// <summary>「升级到最新」时选择升级后的依赖声明：浮动范围（默认）或固定精确版本。</summary>
public partial class AdvanceSpecDialog : Window
{
    readonly string pinnedSpec;
    readonly string floatingSpec;

    public string ChosenSpec => PinRadio.IsChecked == true ? pinnedSpec : floatingSpec;

    public AdvanceSpecDialog(string name, string installed, string latest, string pinnedSpec, string floatingSpec)
    {
        InitializeComponent();
        this.pinnedSpec = pinnedSpec;
        this.floatingSpec = floatingSpec;
        PromptText.Text = $"{name}：{installed} → {latest}\n升级后的依赖声明改为：";
        RangeRadio.Content = $"浮动 {floatingSpec}（之后「更新所选」可继续安装范围内新版）";
        PinRadio.Content = $"固定 {pinnedSpec}（版本保持不变，最可控）";
        NoteText.Text = RangeNote(latest);
        RangeRadio.IsChecked = true;
    }

    internal static string RangeNote(string version) => version.TrimStart('v', 'V').StartsWith("0.")
        ? "提示：0.x 阶段的 ^ 范围只跟随同一小版本（如 ^0.5.0 最高到 0.5.x），跨小版本仍需再次升级。"
        : "";

    void BtnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
