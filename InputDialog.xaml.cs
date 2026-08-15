using System.Windows;
using System.Windows.Input;

namespace DshControl;

/// <summary>简单的单行输入对话框，用于修改存储位置等路径。</summary>
public partial class InputDialog : Window
{
    public string Value => InputBox.Text.Trim();

    public InputDialog(string title, string prompt, string current)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = current;
        InputBox.Focus();
        InputBox.SelectAll();
    }

    void BtnOk_Click(object sender, RoutedEventArgs e) { DialogResult = true; }
    void BtnCancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

    void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
    }
}
