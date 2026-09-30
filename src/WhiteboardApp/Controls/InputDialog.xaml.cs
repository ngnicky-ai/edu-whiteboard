using System.Windows;

namespace WhiteboardApp.Controls;

public partial class InputDialog : Window
{
    public string Value => InputBox.Text.Trim();

    public InputDialog(string prompt, string initialValue)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }
}
