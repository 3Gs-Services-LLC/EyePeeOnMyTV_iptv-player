using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EyePeeOnMyTV.Models;
using EyePeeOnMyTV.Services;

namespace EyePeeOnMyTV.Dialogs;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings currentSettings)
    {
        InitializeComponent();

        M3uUrlBox.Text = currentSettings.M3uUrl;
        XtreamServerBox.Text = currentSettings.Xtream.ServerUrl;
        XtreamUsernameBox.Text = currentSettings.Xtream.Username;
        XtreamPasswordBox.Password = currentSettings.Xtream.Password;
        XtreamPortBox.Text = currentSettings.Xtream.Port;

        foreach (var epgUrl in currentSettings.EpgUrls)
        {
            AddEpgRow(epgUrl);
        }

        // Checking a radio button fires PlaylistModeRadio_Checked, which shows/hides the two
        // mode panels — set this last so the initial panel visibility reflects the loaded mode.
        if (currentSettings.PlaylistMode == PlaylistSourceMode.XtreamCodes)
        {
            XtreamModeRadio.IsChecked = true;
        }
        else
        {
            M3uModeRadio.IsChecked = true;
        }
    }

    private void PlaylistModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // Guard needed because both radios' Checked handlers can fire during construction
        // before M3uPanel/XtreamPanel are guaranteed non-null — InitializeComponent() always
        // runs first in the constructor, though, so this is mostly defensive.
        if (M3uPanel is null || XtreamPanel is null)
        {
            return;
        }

        var useXtream = ReferenceEquals(sender, XtreamModeRadio);
        M3uPanel.Visibility = useXtream ? Visibility.Collapsed : Visibility.Visible;
        XtreamPanel.Visibility = useXtream ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddEpgRowButton_Click(object sender, RoutedEventArgs e) => AddEpgRow(string.Empty);

    private void AddEpgRow(string url)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textBox = new TextBox { Text = url, Tag = "EpgUrl" };
        Grid.SetColumn(textBox, 0);

        var removeButton = new Button
        {
            Content = "✕",
            Margin = new Thickness(6, 0, 0, 0),
            Style = (Style)FindResource("SmallIconButtonStyle"),
        };
        Grid.SetColumn(removeButton, 1);
        removeButton.Click += (_, _) => EpgRowsPanel.Children.Remove(row);

        row.Children.Add(textBox);
        row.Children.Add(removeButton);
        EpgRowsPanel.Children.Add(row);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            PlaylistMode = XtreamModeRadio.IsChecked == true ? PlaylistSourceMode.XtreamCodes : PlaylistSourceMode.M3uUrl,
            M3uUrl = M3uUrlBox.Text.Trim(),
            Xtream = new XtreamCodesCredentials
            {
                ServerUrl = XtreamServerBox.Text.Trim(),
                Username = XtreamUsernameBox.Text.Trim(),
                Password = XtreamPasswordBox.Password,
                Port = XtreamPortBox.Text.Trim(),
            },
            EpgUrls = CollectEpgUrls(),
        };

        var error = Validate(settings);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        SettingsService.Save(settings);
        DialogResult = true;
    }

    private List<string> CollectEpgUrls()
    {
        var urls = new List<string>();
        foreach (var child in EpgRowsPanel.Children)
        {
            if (child is Grid row && row.Children.Count > 0 && row.Children[0] is TextBox textBox)
            {
                var url = textBox.Text.Trim();
                if (!string.IsNullOrEmpty(url))
                {
                    urls.Add(url);
                }
            }
        }

        return urls;
    }

    /// <summary>
    /// Basic non-empty / plausible-URL validation per the spec — not a live connectivity check.
    /// Empty EPG rows are silently skipped (CollectEpgUrls already filters them out), matching
    /// "support zero, one, or many EPG entries"; only a non-empty row with a malformed URL fails.
    /// </summary>
    private static string? Validate(AppSettings settings)
    {
        if (settings.PlaylistMode == PlaylistSourceMode.M3uUrl)
        {
            if (string.IsNullOrWhiteSpace(settings.M3uUrl))
            {
                return "Enter an M3U playlist URL.";
            }

            if (!IsValidHttpUrl(settings.M3uUrl))
            {
                return "The M3U playlist URL doesn't look like a valid http(s) URL.";
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.Xtream.ServerUrl) || !IsValidHttpUrl(settings.Xtream.ServerUrl))
            {
                return "Enter a valid Xtream Codes server URL (e.g. http://server.example.com).";
            }

            if (string.IsNullOrWhiteSpace(settings.Xtream.Username))
            {
                return "Enter your Xtream Codes username.";
            }

            if (string.IsNullOrWhiteSpace(settings.Xtream.Password))
            {
                return "Enter your Xtream Codes password.";
            }

            if (!string.IsNullOrWhiteSpace(settings.Xtream.Port) && !int.TryParse(settings.Xtream.Port, out _))
            {
                return "Port must be a number.";
            }
        }

        foreach (var epgUrl in settings.EpgUrls)
        {
            if (!IsValidHttpUrl(epgUrl))
            {
                return $"This EPG URL doesn't look valid: {epgUrl}";
            }
        }

        return null;
    }

    private static bool IsValidHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
