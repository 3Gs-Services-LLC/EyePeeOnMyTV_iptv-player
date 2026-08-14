using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EyePeeOnMyTV.Dialogs;

public partial class ShortcutsWindow : Window
{
    private sealed record ShortcutEntry(string Keys, string Action, string Description);

    public ShortcutsWindow()
    {
        InitializeComponent();
        Populate();
    }

    private void Populate()
    {
        var groups = new (string Name, ShortcutEntry[] Entries)[]
        {
            ("File", new[]
            {
                new ShortcutEntry("Ctrl+O", "Open File", "Opens a local media file for playback"),
                new ShortcutEntry("Ctrl+N", "Open Network Stream", "Opens a URL (http, rtsp, rtmp, udp, mms, etc.) for playback"),
                new ShortcutEntry("Ctrl+,", "Settings", "Opens IPTV/EPG source settings"),
                new ShortcutEntry("Ctrl+Q", "Exit", "Closes the application"),
            }),
            ("Playback", new[]
            {
                new ShortcutEntry("Space", "Play / Pause", "Toggles playback of the current stream"),
                new ShortcutEntry("S", "Stop", "Stops playback"),
                new ShortcutEntry("Left", "Jump Backward 10s", "Seeks back 10 seconds (seekable streams only)"),
                new ShortcutEntry("Right", "Jump Forward 10s", "Seeks forward 10 seconds (seekable streams only)"),
                new ShortcutEntry("E", "Next Frame", "Advances one frame while paused"),
                new ShortcutEntry("Down", "Previous Channel", "Plays the previous channel in the current (filtered) list"),
                new ShortcutEntry("Up", "Next Channel", "Plays the next channel in the current (filtered) list"),
            }),
            ("Channel List", new[]
            {
                new ShortcutEntry("Up", "Select Previous Channel", "With the channel list focused, selects and plays the row above"),
                new ShortcutEntry("Down", "Select Next Channel", "With the channel list focused, selects and plays the row below"),
                new ShortcutEntry("Page Up", "Previous Page", "With the channel list focused, moves selection up one visible page"),
                new ShortcutEntry("Page Down", "Next Page", "With the channel list focused, moves selection down one visible page"),
                new ShortcutEntry("Ctrl+L", "Toggle Channel List", "Shows or hides the channel list sidebar"),
            }),
            ("Audio", new[]
            {
                new ShortcutEntry("Ctrl+Up", "Increase Volume", "Raises the volume by 5%"),
                new ShortcutEntry("Ctrl+Down", "Decrease Volume", "Lowers the volume by 5%"),
                new ShortcutEntry("M", "Mute", "Toggles audio mute"),
                new ShortcutEntry("Ctrl+E", "Equalizer", "Opens the audio equalizer window"),
            }),
            ("Video", new[]
            {
                new ShortcutEntry("F / F11", "Fullscreen", "Toggles fullscreen playback"),
                new ShortcutEntry("Esc", "Exit Fullscreen", "Leaves fullscreen mode"),
                new ShortcutEntry("Ctrl+T", "Always on Top", "Keeps the window above other windows"),
                new ShortcutEntry("Shift+S", "Take Snapshot", "Saves a snapshot of the current video frame"),
                new ShortcutEntry("Ctrl+J", "Video Adjustments", "Opens the brightness/contrast/hue/saturation/gamma dialog"),
            }),
            ("Subtitle", new[]
            {
                new ShortcutEntry("Ctrl+Shift+S", "Add Subtitle File", "Loads an external subtitle file"),
                new ShortcutEntry("Ctrl+Shift+Up", "Increase Subtitle Delay", "Delays subtitles further behind the audio"),
                new ShortcutEntry("Ctrl+Shift+Down", "Decrease Subtitle Delay", "Brings subtitles earlier relative to the audio"),
            }),
            ("Tools & Help", new[]
            {
                new ShortcutEntry("Ctrl+I", "Media Information", "Shows codec, track, and stream statistics for the current media"),
                new ShortcutEntry("Ctrl+M", "Messages", "Opens the libVLC message log"),
                new ShortcutEntry("Ctrl+F1", "About", "Shows application and libVLC version information"),
                new ShortcutEntry("F1", "Shortcuts", "Shows this window"),
            }),
        };

        foreach (var (name, entries) in groups)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 16, 0, 6),
                Foreground = (Brush)FindResource("AppAccentBrush"),
            });

            foreach (var entry in entries)
            {
                ContentPanel.Children.Add(BuildRow(entry));
            }
        }
    }

    private Grid BuildRow(ShortcutEntry entry)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var keyBadge = new Border
        {
            Background = (Brush)FindResource("AppSurfaceBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = entry.Keys,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = (Brush)FindResource("AppForegroundBrush"),
            },
        };
        Grid.SetColumn(keyBadge, 0);

        var actionText = new TextBlock
        {
            Text = entry.Action,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(actionText, 1);

        var descriptionText = new TextBlock
        {
            Text = entry.Description,
            Foreground = (Brush)FindResource("AppMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(descriptionText, 2);

        row.Children.Add(keyBadge);
        row.Children.Add(actionText);
        row.Children.Add(descriptionText);
        return row;
    }
}
