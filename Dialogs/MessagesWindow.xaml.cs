using System.Windows;
using LibVLCSharp.Shared;

namespace EyePeeOnMyTV.Dialogs;

public partial class MessagesWindow : Window
{
    private readonly LibVLC _libVlc;
    private const int MaxLines = 2000;

    public MessagesWindow(LibVLC libVlc)
    {
        InitializeComponent();
        _libVlc = libVlc;
        _libVlc.Log += LibVlc_Log;
    }

    private void LibVlc_Log(object? sender, LogEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogTextBox.AppendText($"[{e.Level}] {e.FormattedLog}{Environment.NewLine}");

            if (LogTextBox.LineCount > MaxLines)
            {
                var excess = LogTextBox.LineCount - MaxLines;
                var cutoff = LogTextBox.GetCharacterIndexFromLineIndex(excess);
                LogTextBox.Text = LogTextBox.Text[cutoff..];
            }

            LogTextBox.ScrollToEnd();
        });
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _libVlc.Log -= LibVlc_Log;
    }
}
