using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EyePeeOnMyTV.Dialogs;
using EyePeeOnMyTV.Models;
using EyePeeOnMyTV.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.Win32;

namespace EyePeeOnMyTV;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Default is the original playlist order (channel-number order); the toggle button only
    /// ever cycles between Ascending and Descending once tapped, per spec — it never returns here.
    /// </summary>
    private enum ChannelSortOrder
    {
        Default,
        Ascending,
        Descending,
    }

    private readonly DataSourceService _dataSourceService = new();

    // Loaded synchronously here — before InitializePlayer/LoadDataAsync ever run — so settings
    // are always available before the stream/EPG data is first requested, per spec.
    private AppSettings _appSettings = SettingsService.Load();

    private readonly DispatcherTimer _epgRefreshTimer;
    private readonly DispatcherTimer _controlsIdleTimer;
    private readonly DispatcherTimer _topOverlaysHideTimer;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly DispatcherTimer _mouseIdleWatchTimer;
    private NativePoint _lastCursorPos;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;

    private List<Channel> _allChannels = new();
    private readonly ObservableCollection<Channel> _visibleChannels = new();
    private Dictionary<string, List<EpgProgramme>> _epg = new(StringComparer.OrdinalIgnoreCase);
    private ChannelSortOrder _channelSortOrder = ChannelSortOrder.Default;

    private Channel? _currentChannel;
    private string? _currentStreamUrl;
    private bool _isFullscreen;
    private WindowState _preFullscreenState;
    private WindowStyle _preFullscreenStyle;
    private ResizeMode _preFullscreenResizeMode;

    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;

    private MessagesWindow? _messagesWindow;
    private EqualizerWindow? _equalizerWindow;
    private VideoAdjustmentsWindow? _videoAdjustmentsWindow;
    private ShortcutsWindow? _shortcutsWindow;

    public MainWindow()
    {
        InitializeComponent();

        ChannelListBox.ItemsSource = _visibleChannels;

        _epgRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _epgRefreshTimer.Tick += (_, _) => UpdateEpgOverlay();

        _controlsIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _controlsIdleTimer.Tick += ControlsIdleTimer_Tick;

        // Shared by the channel name/logo overlay (top-left) and the EPG "Now/Next" overlay
        // (top-right) — both are unobtrusive, auto-hiding overlays and hide together on one timer.
        _topOverlaysHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _topOverlaysHideTimer.Tick += (_, _) =>
        {
            ChannelInfoOverlay.Visibility = Visibility.Collapsed;
            EpgOverlay.Visibility = Visibility.Collapsed;
            _topOverlaysHideTimer.Stop();
        };

        _reconnectTimer = new DispatcherTimer();
        _reconnectTimer.Tick += ReconnectTimer_Tick;

        // VideoView hosts its overlay content in a detached native window (see the XAML comment
        // on VideoViewControl), so ordinary routed MouseMove events over the video/overlay area
        // never reach this Window reliably. Polling the raw cursor position sidesteps that
        // entirely — this is what actually drives the control bar's idle/auto-hide behavior
        // (see ControlsIdleTimer_Tick), in both windowed and fullscreen mode; it runs for the
        // whole app session rather than being started/stopped per mode.
        _mouseIdleWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _mouseIdleWatchTimer.Tick += MouseIdleWatchTimer_Tick;

        // Belt-and-braces on top of the movement-based detection above: a click/drag (e.g.
        // dragging the volume slider thumb) always involves at least some cursor movement in
        // practice, which the 300ms poll already catches, but this makes "interacting with the
        // control bar counts as activity" an explicit guarantee rather than an incidental one.
        ControlBar.PreviewMouseDown += (_, _) =>
        {
            ShowControlBar();
            RestartControlsIdleTimer();
        };

        UpdateChannelSortButton();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializePlayer();

        GetCursorPos(out _lastCursorPos);
        _mouseIdleWatchTimer.Start();
        RestartControlsIdleTimer();

        await LoadDataAsync();
    }

    private void InitializePlayer()
    {
        _libVlc = new LibVLC(enableDebugLogs: false);
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            EnableHardwareDecoding = true,
        };
        VideoViewControl.MediaPlayer = _mediaPlayer;

        _mediaPlayer.Volume = (int)VolumeSlider.Value;
        _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
        _mediaPlayer.Playing += MediaPlayer_Playing;
    }

    private async Task LoadDataAsync()
    {
        try
        {
            // Explicitly re-show the loading state: it's Visible by default in XAML for the very
            // first load, but LoadDataAsync also runs again after Settings are saved, when it's
            // already Collapsed from the previous load.
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingProgressBar.IsIndeterminate = true;

            LoadingStatusText.Text = "Fetching playlist…";
            var channelsTask = _dataSourceService.FetchChannelsAsync(_appSettings);
            var epgTask = FetchEpgSafeAsync();

            _allChannels = await channelsTask;

            LoadingStatusText.Text = "Fetching guide…";
            _epg = await epgTask;

            RefreshVisibleChannels(ChannelFilterBox.Text);

            LoadingOverlay.Visibility = Visibility.Collapsed;

            if (_allChannels.Count > 0)
            {
                PlayChannel(_allChannels[0]);
            }
            else
            {
                ShowPlaybackStatus("No channels found in the playlist.");
            }

            _epgRefreshTimer.Start();
        }
        catch (Exception ex)
        {
            LoadingStatusText.Text = $"Failed to load: {ex.Message}";
            LoadingProgressBar.IsIndeterminate = false;
        }
    }

    private async Task<Dictionary<string, List<EpgProgramme>>> FetchEpgSafeAsync()
    {
        try
        {
            return await _dataSourceService.FetchEpgAsync(_appSettings);
        }
        catch
        {
            // EPG is best-effort; a slow or failed guide fetch should never block playback.
            return new Dictionary<string, List<EpgProgramme>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ----- Channel list / filtering -----

    private void RefreshVisibleChannels(string? filter)
    {
        _visibleChannels.Clear();

        IEnumerable<Channel> source = _allChannels;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            source = source.Where(c =>
                c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (c.Group?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // OrderBy/OrderByDescending are documented as stable sorts, so channels with identical
        // names (case-insensitively) keep their original relative order either way.
        source = _channelSortOrder switch
        {
            ChannelSortOrder.Ascending => source.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
            ChannelSortOrder.Descending => source.OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase),
            _ => source,
        };

        foreach (var channel in source)
        {
            _visibleChannels.Add(channel);
        }
    }

    private void ChannelFilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshVisibleChannels(ChannelFilterBox.Text);
        ChannelFilterPlaceholder.Visibility = string.IsNullOrEmpty(ChannelFilterBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ChannelSortButton_Click(object sender, RoutedEventArgs e)
    {
        _channelSortOrder = _channelSortOrder == ChannelSortOrder.Ascending
            ? ChannelSortOrder.Descending
            : ChannelSortOrder.Ascending;

        UpdateChannelSortButton();
        RefreshVisibleChannels(ChannelFilterBox.Text);
    }

    private void UpdateChannelSortButton()
    {
        var (content, tooltip) = _channelSortOrder switch
        {
            ChannelSortOrder.Ascending => ("A-Z ↓", "Sorted A-Z — click to sort Z-A"),
            ChannelSortOrder.Descending => ("Z-A ↑", "Sorted Z-A — click to sort A-Z"),
            _ => ("⇅", "Click to sort channels A-Z"),
        };

        ChannelSortButton.Content = content;
        ChannelSortButton.ToolTip = tooltip;
    }

    private void ChannelListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ChannelListBox.SelectedItem is Channel selected)
        {
            PlayChannel(selected);
        }
    }

    // ----- Playback -----

    private void PlayChannel(Channel channel)
    {
        if (_mediaPlayer is null || _libVlc is null)
        {
            return;
        }

        _reconnectTimer.Stop();
        _reconnectAttempts = 0;
        HidePlaybackStatus();

        _currentChannel = channel;
        _currentStreamUrl = channel.StreamUrl;

        PlayStream(channel.StreamUrl);

        UpdateChannelInfoOverlay(channel.Name, channel.LogoUrl);
        UpdateEpgOverlay();
        PlayPauseButton.Content = "⏸";

        if (ChannelListBox.SelectedItem as Channel != channel)
        {
            ChannelListBox.SelectionChanged -= ChannelListBox_SelectionChanged;
            ChannelListBox.SelectedItem = channel;
            ChannelListBox.SelectionChanged += ChannelListBox_SelectionChanged;
        }
    }

    /// <summary>
    /// Plays a URL that isn't part of the parsed channel list (File &gt; Open File / Open Network
    /// Stream). Reuses the same reconnect/overlay plumbing as channel playback by clearing
    /// _currentChannel — EPG lookup and channel-list selection sync both no-op for null.
    /// </summary>
    private void PlayAdhocMedia(string streamUrl, string displayName)
    {
        if (_mediaPlayer is null || _libVlc is null)
        {
            return;
        }

        _reconnectTimer.Stop();
        _reconnectAttempts = 0;
        HidePlaybackStatus();

        _currentChannel = null;
        _currentStreamUrl = streamUrl;

        PlayStream(streamUrl);

        UpdateChannelInfoOverlay(displayName, logoUrl: null);
        EpgOverlay.Visibility = Visibility.Collapsed;
        PlayPauseButton.Content = "⏸";

        ChannelListBox.SelectionChanged -= ChannelListBox_SelectionChanged;
        ChannelListBox.SelectedItem = null;
        ChannelListBox.SelectionChanged += ChannelListBox_SelectionChanged;
    }

    private void PlayStream(string url)
    {
        if (_mediaPlayer is null || _libVlc is null)
        {
            return;
        }

        using var media = new Media(_libVlc, new Uri(url));
        media.AddOption(":network-caching=1500");
        _mediaPlayer.Play(media);
    }

    private void NextChannel() => MoveToAdjacentChannel(+1);

    private void PreviousChannel() => MoveToAdjacentChannel(-1);

    /// <summary>
    /// Moves Previous/Next relative to _visibleChannels — the same filtered-and-sorted list
    /// the sidebar is currently showing — instead of the unfiltered _allChannels, so navigation
    /// never jumps to a channel outside the active search results. _visibleChannels is rebuilt
    /// synchronously on every filter/sort change, so this always reads the current list live;
    /// there's no separate cached reference that could go stale when the search changes.
    /// </summary>
    private void MoveToAdjacentChannel(int direction)
    {
        if (_visibleChannels.Count == 0)
        {
            return;
        }

        var currentIndex = _currentChannel is not null ? _visibleChannels.IndexOf(_currentChannel) : -1;

        int targetIndex;
        if (currentIndex < 0)
        {
            // The playing channel isn't in the active filtered list (a search narrowed it out,
            // or an ad-hoc File/Open Network Stream is playing). Rather than disabling Previous/
            // Next — nothing else in the app disables these buttons on an edge case, they always
            // wrap via modulo — land on the nearest end of the filtered list: Next starts at the
            // top, Previous starts at the bottom.
            targetIndex = direction > 0 ? 0 : _visibleChannels.Count - 1;
        }
        else
        {
            targetIndex = (currentIndex + direction + _visibleChannels.Count) % _visibleChannels.Count;
        }

        PlayChannel(_visibleChannels[targetIndex]);
    }

    private void TogglePlayPause()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            PlayPauseButton.Content = "▶";
        }
        else
        {
            _mediaPlayer.Play();
            PlayPauseButton.Content = "⏸";

            // Resuming playback without any mouse movement (e.g. via the Space shortcut) would
            // otherwise leave the idle timer stopped forever from the pause-guard in
            // ControlsIdleTimer_Tick — restart it so the 5s countdown runs again now that there's
            // something playing to auto-hide over.
            RestartControlsIdleTimer();
        }
    }

    private void ToggleMute()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        MuteButton.Content = _mediaPlayer.Mute ? "🔇" : "🔊";
    }

    // ----- LibVLC events (fire off the UI thread) -----

    private void MediaPlayer_Playing(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _reconnectAttempts = 0;
            _reconnectTimer.Stop();
            HidePlaybackStatus();
        });
    }

    private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(BeginReconnect);
    }

    private void BeginReconnect()
    {
        if (string.IsNullOrEmpty(_currentStreamUrl))
        {
            return;
        }

        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            ShowPlaybackStatus("Stream unavailable. Choose another channel or try again later.");
            return;
        }

        _reconnectAttempts++;
        ShowPlaybackStatus($"Reconnecting… (attempt {_reconnectAttempts}/{MaxReconnectAttempts})");

        _reconnectTimer.Interval = TimeSpan.FromSeconds(Math.Min(2 * _reconnectAttempts, 10));
        _reconnectTimer.Start();
    }

    private void ReconnectTimer_Tick(object? sender, EventArgs e)
    {
        _reconnectTimer.Stop();

        if (string.IsNullOrEmpty(_currentStreamUrl))
        {
            return;
        }

        PlayStream(_currentStreamUrl);
    }

    private void ShowPlaybackStatus(string message)
    {
        PlaybackStatusText.Text = message;
        PlaybackStatusOverlay.Visibility = Visibility.Visible;
    }

    private void HidePlaybackStatus()
    {
        PlaybackStatusOverlay.Visibility = Visibility.Collapsed;
    }

    // ----- Overlays -----

    private void UpdateChannelInfoOverlay(string name, string? logoUrl)
    {
        ChannelNameText.Text = name;
        ChannelInfoOverlay.Visibility = Visibility.Visible;
        RestartTopOverlaysHideTimer();

        if (!string.IsNullOrWhiteSpace(logoUrl) && Uri.TryCreate(logoUrl, UriKind.Absolute, out var logoUri))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = logoUri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.EndInit();
                ChannelLogoImage.Source = bitmap;
                ChannelLogoImage.Visibility = Visibility.Visible;
            }
            catch
            {
                ChannelLogoImage.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ChannelLogoImage.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateEpgOverlay()
    {
        if (_currentChannel is null)
        {
            EpgOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var (current, next) = EpgParser.GetNowAndNext(_epg, _currentChannel.TvgId, DateTimeOffset.Now);

        if (current is null && next is null)
        {
            EpgOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        EpgNowText.Text = current is not null
            ? $"Now: {current.Title} ({current.Start:t} - {current.Stop:t})"
            : "Now: (no guide data)";
        EpgNextText.Text = next is not null ? $"Next: {next.Title} ({next.Start:t})" : "";

        EpgOverlay.Visibility = Visibility.Visible;
        RestartTopOverlaysHideTimer();
    }

    private void RestartTopOverlaysHideTimer()
    {
        _topOverlaysHideTimer.Stop();
        _topOverlaysHideTimer.Start();
    }

    // ----- Controls bar auto-hide -----
    //
    // Single shared mechanism for both windowed and fullscreen — there is only one ControlBar
    // element (see MainWindow.xaml); fullscreen is just a window-chrome/size state, not a
    // separate view, so there was never a need (or a way) to implement this twice.
    //
    // Mouse activity is detected two ways, both funnelling into the same RestartControlsIdleTimer:
    //   1. Window_MouseMove — an ordinary routed event, reliable for movement over WPF-rendered
    //      chrome (menu bar, control bar, sidebar).
    //   2. MouseIdleWatchTimer_Tick — polls the raw screen cursor position every 300ms. Needed
    //      because VideoView hosts its content in a detached native window (see the XAML comment
    //      on VideoViewControl), so routed MouseMove doesn't reliably fire while the cursor is
    //      over the video itself. This now runs for the whole app session (see MainWindow_Loaded)
    //      rather than being started/stopped per fullscreen toggle, so the same 5s idle behavior
    //      applies identically whether windowed or fullscreen.
    //
    // The mouse *leaving* the window entirely fires neither of these — by design, that just lets
    // whatever timer is already running continue/expire normally, per spec, rather than force-
    // hiding early.

    private static readonly TimeSpan ControlBarFadeDuration = TimeSpan.FromMilliseconds(180);

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        ShowControlBar();
        Cursor = Cursors.Arrow;
        RestartControlsIdleTimer();
    }

    private void ControlsIdleTimer_Tick(object? sender, EventArgs e)
    {
        _controlsIdleTimer.Stop();

        // Don't hide while paused/stopped — there's no playing video to watch unobstructed, so
        // auto-hiding the only way to resume playback would be actively unhelpful. Playback
        // resuming (TogglePlayPause) restarts this timer itself so the countdown still runs.
        if (_mediaPlayer is not null && !_mediaPlayer.IsPlaying)
        {
            return;
        }

        HideControlBar();
        if (_isFullscreen)
        {
            Cursor = Cursors.None;
        }
    }

    private void MouseIdleWatchTimer_Tick(object? sender, EventArgs e)
    {
        if (!GetCursorPos(out var current))
        {
            return;
        }

        if (Math.Abs(current.X - _lastCursorPos.X) > 2 || Math.Abs(current.Y - _lastCursorPos.Y) > 2)
        {
            _lastCursorPos = current;
            ShowControlBar();
            Cursor = Cursors.Arrow;
            RestartControlsIdleTimer();
        }
    }

    private void RestartControlsIdleTimer()
    {
        _controlsIdleTimer.Stop();
        _controlsIdleTimer.Start();
    }

    private void ShowControlBar()
    {
        ControlBar.Visibility = Visibility.Visible;
        ControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 1.0,
            Duration = ControlBarFadeDuration,
        });
    }

    private void HideControlBar()
    {
        var fadeOut = new DoubleAnimation
        {
            To = 0.0,
            Duration = ControlBarFadeDuration,
        };

        // Only collapse once the fade genuinely finishes — if this gets interrupted by a
        // ShowControlBar() call (new mouse movement arriving mid-fade), Completed never fires
        // for the superseded animation, so the bar correctly stays visible instead of vanishing.
        fadeOut.Completed += (_, _) => ControlBar.Visibility = Visibility.Collapsed;
        ControlBar.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ----- Fullscreen -----

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _preFullscreenState = WindowState;
            _preFullscreenStyle = WindowStyle;
            _preFullscreenResizeMode = ResizeMode;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        }
        else
        {
            WindowStyle = _preFullscreenStyle;
            ResizeMode = _preFullscreenResizeMode;
            WindowState = _preFullscreenState;
            _isFullscreen = false;
        }

        // The idle-hide timer/watcher now run continuously for the whole session (not per mode —
        // see the "Controls bar auto-hide" section), so entering/exiting fullscreen only needs to
        // make sure the bar is visible and the countdown restarts fresh, same as any other
        // deliberate user action.
        ShowControlBar();
        Cursor = Cursors.Arrow;
        RestartControlsIdleTimer();
    }

    private void ExitFullscreenIfActive()
    {
        if (_isFullscreen)
        {
            ToggleFullscreen();
        }
    }

    // ----- UI event handlers -----

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void NextButton_Click(object sender, RoutedEventArgs e) => NextChannel();

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => PreviousChannel();

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Volume = (int)e.NewValue;
        if (_mediaPlayer.Volume > 0 && _mediaPlayer.Mute)
        {
            _mediaPlayer.Mute = false;
            MuteButton.Content = "🔊";
        }
    }

    private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        SidebarPanel.Visibility = SidebarPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Global app hotkeys. Menu items display their shortcut via InputGestureText, but WPF
    /// InputGestureText is display-only — it doesn't wire up the key itself — so this switch is
    /// what actually makes every shortcut in the Help &gt; Shortcuts window work. Every branch
    /// calls the same private method the corresponding menu item's Click handler calls, so
    /// there's exactly one implementation of each action regardless of how it's triggered.
    /// </summary>
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Don't hijack hotkeys while the user is typing — e.g. typing "fox" into the channel
        // search box would otherwise also toggle Mute ("m") and Fullscreen ("f") as a side effect.
        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        switch (Keyboard.Modifiers, e.Key)
        {
            // ----- No modifier: primary playback controls (mirrors VLC's own bare-key scheme) -----
            case (ModifierKeys.None, Key.Space):
                TogglePlayPause();
                break;
            case (ModifierKeys.None, Key.S):
                StopPlayback();
                break;
            case (ModifierKeys.None, Key.Left):
                JumpTime(-10_000);
                break;
            case (ModifierKeys.None, Key.Right):
                JumpTime(10_000);
                break;
            case (ModifierKeys.None, Key.E):
                _mediaPlayer?.NextFrame();
                break;
            case (ModifierKeys.None, Key.Up):
            case (ModifierKeys.None, Key.PageUp):
                NextChannel();
                break;
            case (ModifierKeys.None, Key.Down):
            case (ModifierKeys.None, Key.PageDown):
                PreviousChannel();
                break;
            case (ModifierKeys.None, Key.M):
                ToggleMute();
                break;
            case (ModifierKeys.None, Key.F):
            case (ModifierKeys.None, Key.F11):
                ToggleFullscreen();
                break;
            case (ModifierKeys.None, Key.Escape):
                ExitFullscreenIfActive();
                break;
            case (ModifierKeys.None, Key.F1):
                ShowShortcuts();
                break;

            // ----- Shift -----
            case (ModifierKeys.Shift, Key.S):
                TakeSnapshot();
                break;

            // ----- Ctrl -----
            case (ModifierKeys.Control, Key.O):
                OpenFile();
                break;
            case (ModifierKeys.Control, Key.N):
                OpenNetworkStream();
                break;
            case (ModifierKeys.Control, Key.Q):
                Close();
                break;
            case (ModifierKeys.Control, Key.Up):
                IncreaseVolume();
                break;
            case (ModifierKeys.Control, Key.Down):
                DecreaseVolume();
                break;
            case (ModifierKeys.Control, Key.E):
                OpenEqualizer();
                break;
            case (ModifierKeys.Control, Key.T):
                ToggleAlwaysOnTop();
                break;
            case (ModifierKeys.Control, Key.J):
                OpenVideoAdjustments();
                break;
            case (ModifierKeys.Control, Key.I):
                ShowMediaInfo();
                break;
            case (ModifierKeys.Control, Key.M):
                OpenMessages();
                break;
            case (ModifierKeys.Control, Key.L):
                ToggleSidebar();
                break;
            case (ModifierKeys.Control, Key.OemComma):
                OpenSettings();
                break;
            case (ModifierKeys.Control, Key.F1):
                ShowAbout();
                break;

            // ----- Ctrl+Shift -----
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.S):
                AddSubtitleFile();
                break;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.Up):
                IncreaseSubtitleDelay();
                break;
            case (ModifierKeys.Control | ModifierKeys.Shift, Key.Down):
                DecreaseSubtitleDelay();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Issue 1 fix: WPF's ListBox natively consumes plain Up/Down/PageUp/PageDown to move its own
    /// selection cursor, but doesn't reliably commit that as a real selection/play action in this
    /// app's setup — so channel switching silently did nothing while the list had focus. This
    /// handler takes those four keys over explicitly and drives them through
    /// ChannelListBox.SelectedIndex, which is exactly what a mouse click sets — so it fires the
    /// same ChannelListBox_SelectionChanged → PlayChannel() path a click does, not a parallel
    /// implementation of "select a channel".
    /// Only plain (unmodified) presses are handled here — Ctrl/Shift combinations (Ctrl+Up for
    /// volume, Ctrl+Shift+Up for subtitle delay, etc.) must keep bubbling to Window_KeyDown so
    /// those menu shortcuts keep working even while the channel list has focus.
    /// </summary>
    private void ChannelListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        int delta;
        switch (e.Key)
        {
            case Key.Up:
                delta = -1;
                break;
            case Key.Down:
                delta = 1;
                break;
            case Key.PageUp:
                delta = -GetChannelListPageSize();
                break;
            case Key.PageDown:
                delta = GetChannelListPageSize();
                break;
            default:
                return;
        }

        SelectChannelRelative(delta);
        e.Handled = true;
    }

    /// <summary>
    /// Moves the channel list's selection by a fixed number of rows — no wraparound, matching
    /// standard Windows list-box Up/Down/PageUp/PageDown conventions (unlike the Previous/Next
    /// Channel commands, which intentionally wrap like a TV remote's channel up/down).
    /// </summary>
    private void SelectChannelRelative(int delta)
    {
        if (_visibleChannels.Count == 0)
        {
            return;
        }

        var currentIndex = ChannelListBox.SelectedIndex;
        if (currentIndex < 0)
        {
            // Nothing selected (e.g. the playing channel was filtered out by search, or an
            // ad-hoc File/Network stream is playing). Land at the end nearest the requested
            // direction, same convention used by Previous/Next Channel for this edge case.
            currentIndex = delta > 0 ? -1 : _visibleChannels.Count;
        }

        var targetIndex = Math.Clamp(currentIndex + delta, 0, _visibleChannels.Count - 1);
        ChannelListBox.SelectedIndex = targetIndex;
        ChannelListBox.ScrollIntoView(ChannelListBox.SelectedItem);
    }

    /// <summary>
    /// Number of rows currently visible in the channel list's viewport, for PageUp/PageDown.
    /// Measured from the real container/viewport sizes rather than assumed, since the row
    /// height and window size can both vary.
    /// </summary>
    private int GetChannelListPageSize()
    {
        if (ChannelListBox.Items.Count == 0)
        {
            return 1;
        }

        if (FindVisualDescendant<ScrollViewer>(ChannelListBox) is not { ViewportHeight: > 0 } scrollViewer)
        {
            return 1;
        }

        if (ChannelListBox.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement { ActualHeight: > 0 } firstContainer)
        {
            return 1;
        }

        return Math.Max(1, (int)(scrollViewer.ViewportHeight / firstContainer.ActualHeight));
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _epgRefreshTimer.Stop();
        _controlsIdleTimer.Stop();
        _topOverlaysHideTimer.Stop();
        _reconnectTimer.Stop();
        _mouseIdleWatchTimer.Stop();

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;
            _mediaPlayer.Playing -= MediaPlayer_Playing;
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }

        _libVlc?.Dispose();

        _messagesWindow?.Close();
        _equalizerWindow?.Close();
        _videoAdjustmentsWindow?.Close();
        _shortcutsWindow?.Close();
    }

    // ----- File menu -----

    private void File_OpenFile_Click(object sender, RoutedEventArgs e) => OpenFile();

    private void File_OpenNetworkStream_Click(object sender, RoutedEventArgs e) => OpenNetworkStream();

    private void File_Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void File_Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void OpenSettings()
    {
        var dialog = new SettingsWindow(_appSettings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            // SettingsWindow already persisted the new values via SettingsService.Save — reload
            // them here and immediately re-fetch the playlist/EPG so the change takes effect
            // right away instead of requiring an app restart.
            _appSettings = SettingsService.Load();
            await LoadDataAsync();
        }
    }

    private void OpenFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open File",
            Filter = "Media files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.mp3;*.flac;*.aac;*.m3u;*.m3u8|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            PlayAdhocMedia(dialog.FileName, System.IO.Path.GetFileName(dialog.FileName));
        }
    }

    private void OpenNetworkStream()
    {
        var url = InputDialog.Show(this, "Open Network Stream",
            "Enter a network URL (http, rtsp, rtmp, udp, mms, etc.):", "http://");

        if (!string.IsNullOrWhiteSpace(url))
        {
            PlayAdhocMedia(url, url);
        }
    }

    // ----- Playback menu -----

    private void Playback_Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        _reconnectTimer.Stop();
        _mediaPlayer?.Stop();
        PlayPauseButton.Content = "▶";
    }

    private void Playback_JumpForward_Click(object sender, RoutedEventArgs e) => JumpTime(10_000);

    private void Playback_JumpBackward_Click(object sender, RoutedEventArgs e) => JumpTime(-10_000);

    private void JumpTime(long deltaMs)
    {
        if (_mediaPlayer is null || !_mediaPlayer.IsSeekable)
        {
            return;
        }

        _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time + deltaMs);
    }

    private void Playback_NextFrame_Click(object sender, RoutedEventArgs e) => _mediaPlayer?.NextFrame();

    private void Playback_Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string rateText } &&
            float.TryParse(rateText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            _mediaPlayer?.SetRate(rate);
        }
    }

    // ----- Audio menu -----

    private void AudioTrackMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateTrackSubmenu(AudioTrackMenu, _mediaPlayer?.AudioTrackDescription, _mediaPlayer?.AudioTrack ?? -1,
            id => _mediaPlayer?.SetAudioTrack(id));
    }

    private void VideoTrackMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateTrackSubmenu(VideoTrackMenu, _mediaPlayer?.VideoTrackDescription, _mediaPlayer?.VideoTrack ?? -1,
            id => _mediaPlayer?.SetVideoTrack(id));
    }

    private void SubtitleTrackMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        PopulateTrackSubmenu(SubtitleTrackMenu, _mediaPlayer?.SpuDescription, _mediaPlayer?.Spu ?? -1,
            id => _mediaPlayer?.SetSpu(id));
    }

    private static void PopulateTrackSubmenu(MenuItem menu, TrackDescription[]? tracks, int currentId, Action<int> onSelect)
    {
        menu.Items.Clear();

        if (tracks is null || tracks.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(none available)", IsEnabled = false });
            return;
        }

        foreach (var track in tracks)
        {
            var trackId = track.Id;
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(track.Name) ? $"Track {trackId}" : track.Name,
                IsCheckable = true,
                IsChecked = trackId == currentId,
            };
            item.Click += (_, _) => onSelect(trackId);
            menu.Items.Add(item);
        }
    }

    private void AudioOutputDeviceMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        AudioOutputDeviceMenu.Items.Clear();

        var devices = _mediaPlayer?.AudioOutputDeviceEnum ?? Array.Empty<AudioOutputDevice>();
        if (devices.Length == 0)
        {
            AudioOutputDeviceMenu.Items.Add(new MenuItem { Header = "(default device)", IsEnabled = false });
            return;
        }

        var currentDevice = _mediaPlayer?.OutputDevice;
        foreach (var device in devices)
        {
            var deviceId = device.DeviceIdentifier;
            var item = new MenuItem
            {
                Header = device.Description,
                IsCheckable = true,
                IsChecked = deviceId == currentDevice,
            };
            item.Click += (_, _) => _mediaPlayer?.SetOutputDevice(deviceId);
            AudioOutputDeviceMenu.Items.Add(item);
        }
    }

    private void Audio_VolumeUp_Click(object sender, RoutedEventArgs e) => IncreaseVolume();

    private void Audio_VolumeDown_Click(object sender, RoutedEventArgs e) => DecreaseVolume();

    private void IncreaseVolume()
    {
        VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
    }

    private void DecreaseVolume()
    {
        VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
    }

    private void Audio_Equalizer_Click(object sender, RoutedEventArgs e) => OpenEqualizer();

    private void OpenEqualizer()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        if (_equalizerWindow is null || !_equalizerWindow.IsLoaded)
        {
            _equalizerWindow = new EqualizerWindow(_mediaPlayer) { Owner = this };
            _equalizerWindow.Show();
        }
        else
        {
            _equalizerWindow.Activate();
        }
    }

    // ----- Video menu -----

    private void Video_AlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        // WPF already flips AlwaysOnTopMenuItem.IsChecked before Click fires here (it's a
        // checkable MenuItem) — just sync Topmost to match. The Ctrl+T shortcut goes through
        // ToggleAlwaysOnTop() instead, which flips IsChecked itself since no click occurred.
        Topmost = AlwaysOnTopMenuItem.IsChecked;
    }

    private void ToggleAlwaysOnTop()
    {
        AlwaysOnTopMenuItem.IsChecked = !AlwaysOnTopMenuItem.IsChecked;
        Topmost = AlwaysOnTopMenuItem.IsChecked;
    }

    private void Video_AspectRatio_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.AspectRatio = sender is MenuItem { Tag: string ratio } ? ratio : null;
    }

    private void Video_Crop_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.CropGeometry = sender is MenuItem { Tag: string crop } ? crop : null;
    }

    private void Video_Deinterlace_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.SetDeinterlace(sender is MenuItem { Tag: string mode } ? mode : null);
    }

    private void Video_Snapshot_Click(object sender, RoutedEventArgs e) => TakeSnapshot();

    private void TakeSnapshot()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Snapshot",
            Filter = "PNG image|*.png",
            FileName = $"EyePeeOnMyTV-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };

        if (dialog.ShowDialog(this) == true)
        {
            var saved = _mediaPlayer.TakeSnapshot(0, dialog.FileName, 0, 0);
            ShowPlaybackStatus(saved ? $"Snapshot saved to {dialog.FileName}" : "Snapshot failed.");
        }
    }

    private void Video_Adjustments_Click(object sender, RoutedEventArgs e) => OpenVideoAdjustments();

    private void OpenVideoAdjustments()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        if (_videoAdjustmentsWindow is null || !_videoAdjustmentsWindow.IsLoaded)
        {
            _videoAdjustmentsWindow = new VideoAdjustmentsWindow(_mediaPlayer) { Owner = this };
            _videoAdjustmentsWindow.Show();
        }
        else
        {
            _videoAdjustmentsWindow.Activate();
        }
    }

    // ----- Subtitle menu -----

    private void Subtitle_AddFile_Click(object sender, RoutedEventArgs e) => AddSubtitleFile();

    private void AddSubtitleFile()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Add Subtitle File",
            Filter = "Subtitle files|*.srt;*.vtt;*.ass;*.ssa;*.sub|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(dialog.FileName).AbsoluteUri, true);
        }
    }

    private void Subtitle_DelayIncrease_Click(object sender, RoutedEventArgs e) => IncreaseSubtitleDelay();

    private void Subtitle_DelayDecrease_Click(object sender, RoutedEventArgs e) => DecreaseSubtitleDelay();

    private void IncreaseSubtitleDelay()
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.SetSpuDelay(_mediaPlayer.SpuDelay + 100_000);
        }
    }

    private void DecreaseSubtitleDelay()
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.SetSpuDelay(_mediaPlayer.SpuDelay - 100_000);
        }
    }

    // ----- Tools menu -----

    private void Tools_MediaInfo_Click(object sender, RoutedEventArgs e) => ShowMediaInfo();

    private void ShowMediaInfo()
    {
        if (_mediaPlayer?.Media is null)
        {
            ShowPlaybackStatus("No media is currently playing.");
            return;
        }

        new MediaInfoWindow(_mediaPlayer.Media, _currentStreamUrl) { Owner = this }.Show();
    }

    private void Tools_Messages_Click(object sender, RoutedEventArgs e) => OpenMessages();

    private void OpenMessages()
    {
        if (_libVlc is null)
        {
            return;
        }

        if (_messagesWindow is null || !_messagesWindow.IsLoaded)
        {
            _messagesWindow = new MessagesWindow(_libVlc) { Owner = this };
            _messagesWindow.Show();
        }
        else
        {
            _messagesWindow.Activate();
        }
    }

    // ----- Help menu -----

    private void Help_About_Click(object sender, RoutedEventArgs e) => ShowAbout();

    private void ShowAbout()
    {
        if (_libVlc is not null)
        {
            new AboutWindow(_libVlc) { Owner = this }.ShowDialog();
        }
    }

    private void Help_Shortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcuts();

    private void ShowShortcuts()
    {
        if (_shortcutsWindow is null || !_shortcutsWindow.IsLoaded)
        {
            _shortcutsWindow = new ShortcutsWindow { Owner = this };
            _shortcutsWindow.Show();
        }
        else
        {
            _shortcutsWindow.Activate();
        }
    }
}
