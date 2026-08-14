using System.Windows;
using System.Windows.Input;

namespace EyePeeOnMyTV.Dialogs;

public partial class InputDialog : Window
{
    public string InputText => ValueBox.Text;

    public InputDialog(string title, string prompt, string defaultValue)
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = defaultValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public static string? Show(Window owner, string title, string prompt, string defaultValue = "")
    {
        var dialog = new InputDialog(title, prompt, defaultValue) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.InputText : null;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
