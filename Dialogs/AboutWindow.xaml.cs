using System.Reflection;
using System.Windows;
using LibVLCSharp.Shared;

namespace EyePeeOnMyTV.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow(LibVLC libVlc)
    {
        InitializeComponent();

        var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {appVersion} — libVLC {libVlc.Version}";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
