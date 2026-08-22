using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

    // ----- Global keyboard hook plumbing (see the "Global keyboard shortcuts" region below for why) -----

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

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
    private bool _showFavoritesOnly;

    private Channel? _currentChannel;
    private string? _currentStreamUrl;

    // The user's actual mute intent, updated only by ToggleMute(). LibVLC builds a fresh audio
    // output for every Play(media) call (see PlayStream), and on Windows that new output can pick
    // up a stale muted/volume state from the OS's per-app audio session memory — independent of
    // what _mediaPlayer.Mute itself reports, since that getter just reflects whatever the new
    // native output currently thinks, not what the user asked for. Re-asserting this field once
    // that output actually exists (MediaPlayer_Playing) is what makes launch — and every channel
    // switch/reconnect after it — actually match the icon, the same way VolumeSlider.Value already
    // anchors volume across the same recreation.
    private bool _desiredMute;

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

    private IntPtr _windowHandle;
    private LowLevelKeyboardProc? _keyboardHookProc;
    private IntPtr _keyboardHookHandle;

    // Tracks which physical keys are currently held down, as observed through the low-level
    // hook (see KeyboardHookCallback) — used to tell a genuine keypress apart from Windows'
    // auto-repeat resending WM_KEYDOWN while a key is held, since KBDLLHOOKSTRUCT carries no
    // repeat flag the way a normal WM_KEYDOWN lParam does. WPF's own KeyEventArgs.IsRepeat
    // covers the same need for the Window_KeyDown path.
    private readonly HashSet<Key> _hookKeysDown = new();

    public MainWindow()
    {
        InitializeComponent();

        ApplyAccentColor();
        RestoreAlwaysOnTop();

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
        UpdateFavoritesFilterButton();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Before InitializePlayer, which reads VolumeSlider.Value for the media player's initial
        // volume — restoring here means that first read already reflects the saved level instead
        // of VolumeSlider's XAML design-time default, so there's no audible blip at launch.
        RestoreVolumeAndMute();

        // Yield back to the dispatcher at a priority below Render/Loaded before doing any heavy
        // synchronous work, so WPF actually paints this window — the boot logo and progress bar
        // are already in the visual tree by this point via InitializeComponent — before
        // InitializePlayer's native libVLC load (see the comment there) gets a chance to block the
        // UI thread. Without this, the window is already Show()n but nothing's been painted yet,
        // so Windows shows a blank/white frame for however long that load takes.
        await Dispatcher.Yield(DispatcherPriority.Background);

        InitializePlayer();

        GetCursorPos(out _lastCursorPos);
        _mouseIdleWatchTimer.Start();
        RestartControlsIdleTimer();

        _windowHandle = new WindowInteropHelper(this).Handle;
        InstallKeyboardHook();

        await LoadDataAsync();
    }

    private void InitializePlayer()
    {
        // Locates/loads the native libvlc library (worse than a normal build, in a single-file
        // publish, which has to self-extract libvlc's plugin set to a temp directory first) — was
        // previously called from App.OnStartup, synchronously blocking the UI thread before this
        // window had painted anything at all. Called here instead, after MainWindow_Loaded yields
        // once, so the boot logo/progress bar are already on screen before this runs.
        Core.Initialize();

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

    /// <summary>
    /// Applies the persisted volume/mute level to the slider and _desiredMute before anything
    /// plays — independent of PlayLastViewedChannelOnStartup, which only decides which channel
    /// starts, not at what volume. Setting VolumeSlider.Value here does fire
    /// VolumeSlider_ValueChanged, but that's harmless: it guards on _mediaPlayer being non-null,
    /// which it isn't yet at this point in startup (InitializePlayer runs right after this).
    /// _mediaPlayer.Mute itself gets applied later by MediaPlayer_Playing, once playback actually
    /// starts and a real audio output exists to apply it to (see the comment there) — setting
    /// MuteButton's icon here too just means it's already correct during the loading screen,
    /// rather than flipping the instant playback begins.
    /// </summary>
    private void RestoreVolumeAndMute()
    {
        VolumeSlider.Value = _appSettings.Volume;
        _desiredMute = _appSettings.Muted;
        MuteButton.Content = _desiredMute ? "🔇" : "🔊";
    }

    /// <summary>
    /// The one place volume/mute state is written to disk — called on every change (see
    /// VolumeSlider_ValueChanged and ToggleMute) rather than only on close, so a crash or forced
    /// quit doesn't lose it, the same reasoning PlayChannel already applies to LastViewedChannelId.
    /// </summary>
    private void SaveVolumeAndMute()
    {
        _appSettings.Volume = (int)VolumeSlider.Value;
        _appSettings.Muted = _desiredMute;
        SettingsService.Save(_appSettings);
    }

    /// <summary>
    /// Genuine app-startup load only — always needs both the playlist and the guide, so this
    /// fetches them concurrently for a faster first paint. A Settings save no longer routes
    /// through here (see OpenSettings/ReloadPlaylistAsync/ReloadEpgAsync): most saves don't touch
    /// either the IPTV or EPG fields at all (accent color, the startup-resume toggle, ...), and
    /// unconditionally re-fetching both — which also means yanking the user to a fresh/resumed
    /// channel via PlayChannel — on every save regardless was the actual bug being fixed there.
    /// </summary>
    private async Task LoadDataAsync()
    {
        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingProgressBar.IsIndeterminate = true;

            LoadingStatusText.Text = "Fetching playlist…";
            var channelsTask = _dataSourceService.FetchChannelsAsync(_appSettings);
            var epgTask = FetchEpgSafeAsync();

            _allChannels = await channelsTask;
            ApplyFavoriteState(_allChannels);

            LoadingStatusText.Text = "Fetching guide…";
            _epg = await epgTask;

            RefreshVisibleChannels(ChannelFilterBox.Text);

            LoadingOverlay.Visibility = Visibility.Collapsed;

            if (_allChannels.Count > 0)
            {
                PlayChannel(ResolveStartupChannel());
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

    /// <summary>
    /// Re-fetches only the playlist — the half of a Settings-triggered reload that should run when
    /// the M3U/Xtream fields actually changed. Deliberately independent of ReloadEpgAsync (rather
    /// than both always running together, as at startup) so OpenSettings can fire only the side(s)
    /// that actually changed instead of coupling an EPG-only edit to a playlist refetch or vice
    /// versa.
    /// </summary>
    private async Task ReloadPlaylistAsync()
    {
        LoadingStatusText.Text = "Fetching playlist…";
        _allChannels = await _dataSourceService.FetchChannelsAsync(_appSettings);
        ApplyFavoriteState(_allChannels);
        RefreshVisibleChannels(ChannelFilterBox.Text);

        if (_allChannels.Count > 0)
        {
            PlayChannel(ResolveStartupChannel());
        }
        else
        {
            ShowPlaybackStatus("No channels found in the playlist.");
        }
    }

    /// <summary>
    /// Re-fetches only the EPG — the half of a Settings-triggered reload that should run when the
    /// EPG URL list actually changed. See ReloadPlaylistAsync for why this is kept independent
    /// rather than always paired with a playlist refetch.
    /// </summary>
    private async Task ReloadEpgAsync()
    {
        LoadingStatusText.Text = "Fetching guide…";
        _epg = await FetchEpgSafeAsync();
        UpdateEpgOverlay();
    }

    /// <summary>
    /// Which channel to play once the playlist finishes (loading), used both at genuine app
    /// startup and when Settings save triggers a reload — both cases are "the channel list was
    /// just (re)loaded and something has to start playing", so gating this once here applies the
    /// setting consistently in either case. Falls back to the first playlist channel — the app's
    /// original, unconditional behavior — whenever the setting is off, or it's on but there's no
    /// stored last-viewed channel (first run) or that channel is no longer in the playlist.
    /// </summary>
    private Channel ResolveStartupChannel()
    {
        if (_appSettings.PlayLastViewedChannelOnStartup)
        {
            var lastViewed = _allChannels.FirstOrDefault(c => GetChannelKey(c) == _appSettings.LastViewedChannelId);
            if (lastViewed is not null)
            {
                return lastViewed;
            }
        }

        return _allChannels[0];
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

        if (_showFavoritesOnly)
        {
            source = source.Where(c => c.IsFavorite);
        }

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

    // ----- Favorites -----

    /// <summary>
    /// Channel objects are rebuilt from scratch by the M3U/Xtream parser on every fetch, so
    /// per-channel state (favorite status, last-viewed) can't be carried on the object between
    /// fetches — it's looked up here by a stable identifier and re-applied to the fresh objects
    /// instead. Shared by favorites and "play last viewed channel on startup" alike.
    /// </summary>
    private static string GetChannelKey(Channel channel) =>
        !string.IsNullOrEmpty(channel.TvgId) ? channel.TvgId : channel.StreamUrl;

    private void ApplyFavoriteState(List<Channel> channels)
    {
        foreach (var channel in channels)
        {
            channel.IsFavorite = _appSettings.FavoriteChannelIds.Contains(GetChannelKey(channel));
        }
    }

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not Channel channel)
        {
            return;
        }

        channel.IsFavorite = !channel.IsFavorite;

        var key = GetChannelKey(channel);
        if (channel.IsFavorite)
        {
            if (!_appSettings.FavoriteChannelIds.Contains(key))
            {
                _appSettings.FavoriteChannelIds.Add(key);
            }
        }
        else
        {
            _appSettings.FavoriteChannelIds.Remove(key);
        }

        SettingsService.Save(_appSettings);

        // Un-favoriting a channel while the favorites-only filter is active should drop it from
        // the visible list immediately, same as the search box already does for a text filter.
        if (_showFavoritesOnly)
        {
            RefreshVisibleChannels(ChannelFilterBox.Text);
        }
    }

    private void FavoritesFilterButton_Click(object sender, RoutedEventArgs e)
    {
        _showFavoritesOnly = !_showFavoritesOnly;
        UpdateFavoritesFilterButton();
        RefreshVisibleChannels(ChannelFilterBox.Text);
    }

    private void UpdateFavoritesFilterButton()
    {
        FavoritesFilterButton.Content = _showFavoritesOnly ? "★" : "☆";
        FavoritesFilterButton.Foreground = _showFavoritesOnly
            ? (System.Windows.Media.Brush)Application.Current.Resources["UserAccentBrush"]
            : (System.Windows.Media.Brush)Application.Current.Resources["AppMutedBrush"];
        FavoritesFilterButton.ToolTip = _showFavoritesOnly
            ? "Showing favorites only — click to show all channels"
            : "Show favorites only";
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

        // Persisted on every channel change (not just on close/exit) so a crash or forced quit
        // doesn't lose it — this is the one place a channel actually starts playing, reached by
        // every entry point (selection, Next/Previous, favorites, startup resume alike).
        _appSettings.LastViewedChannelId = GetChannelKey(channel);
        SettingsService.Save(_appSettings);

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

        // Compute the new state once and drive both the player and the icon off that single
        // local value, rather than mutating _mediaPlayer.Mute and then reading it back — the
        // read-back relies on LibVLC's native mute call having already taken effect by the next
        // line, which isn't a guarantee this code should lean on. This is the one place mute
        // actually flips; MuteButton_Click and the "M" shortcut (both keyboard-hook and
        // Window_KeyDown paths) all call this same method, so there is exactly one code path that
        // decides the mute state and the icon that reflects it.
        bool muted = !_mediaPlayer.Mute;
        _mediaPlayer.Mute = muted;
        MuteButton.Content = muted ? "🔇" : "🔊";
        _desiredMute = muted;
        SaveVolumeAndMute();
    }

    // ----- LibVLC events (fire off the UI thread) -----

    private void MediaPlayer_Playing(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _reconnectAttempts = 0;
            _reconnectTimer.Stop();
            HidePlaybackStatus();

            // LibVLC creates a fresh audio output for each Play(media) call, and doesn't reliably
            // carry over Volume/Mute set before that output exists — the assignment in
            // InitializePlayer happens before any media is loaded, so on first launch it can be
            // silently dropped (or overridden by Windows' own remembered per-app session state)
            // once the real output spins up, leaving playback silent even though the icon looks
            // right. Reasserting both here, once playback has actually started and the output is
            // guaranteed to exist, is what makes it stick — for the first channel and for every
            // channel switch/reconnect after it. _desiredMute (not _mediaPlayer.Mute) is the
            // source of truth here since the getter can just as easily be reporting whatever the
            // fresh native output picked up, not what the user last chose.
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.Volume = (int)VolumeSlider.Value;
                _mediaPlayer.Mute = _desiredMute;
                MuteButton.Content = _desiredMute ? "🔇" : "🔊";
            }
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
            _desiredMute = false;
        }

        SaveVolumeAndMute();
    }

    /// <summary>
    /// Mouse wheel over the volume slider adjusts volume — up/forward raises it, down/back lowers
    /// it (standard Windows scroll convention, e.Delta &gt; 0 for forward), by the same 5-point
    /// step and clamping Ctrl+Up/Ctrl+Down already use, through the same IncreaseVolume/
    /// DecreaseVolume methods, so wheel, keyboard, and dragging the slider can never drift out of
    /// sync. Preview (tunneling) so it fires no matter which visual part of the Slider the pointer
    /// is over, and e.Handled stops it from bubbling anywhere else (mirrors the web `wheel` +
    /// preventDefault pattern) — there's no other scroll behavior in the control bar for it to
    /// conflict with. This is ordinary WPF routed input on a normal control-bar element, so — like
    /// every other button here — it works identically in windowed and fullscreen mode.
    /// </summary>
    private void VolumeSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            IncreaseVolume();
        }
        else if (e.Delta < 0)
        {
            DecreaseVolume();
        }

        e.Handled = true;
    }

    // Below this, the channel list would clip channel names and its own header controls.
    private const double MinSidebarWidth = 220;

    private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        bool opening = SidebarPanel.Visibility != Visibility.Visible;

        SidebarPanel.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = SidebarPanel.Visibility;

        if (opening)
        {
            // MinWidth has to come back before Width, or a persisted value below it would just
            // get silently clamped up without _appSettings.SidebarWidth ever reflecting that.
            SidebarColumn.MinWidth = MinSidebarWidth;
            SidebarColumn.Width = new GridLength(
                Math.Clamp(_appSettings.SidebarWidth, MinSidebarWidth, SidebarColumn.MaxWidth));
            SidebarSplitterColumn.Width = new GridLength(SidebarSplitter.Width);
        }
        else
        {
            // MinWidth has to drop to 0 too — otherwise it floors the column and Width=0 below
            // has no effect, leaving a MinSidebarWidth-wide gap where the "closed" sidebar was.
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
        }
    }

    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _appSettings.SidebarWidth = SidebarColumn.ActualWidth;
        SettingsService.Save(_appSettings);
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

        if (TryHandleShortcut(Keyboard.Modifiers, e.Key, e.IsRepeat))
        {
            e.Handled = true;
        }
    }

    // ----- Global keyboard shortcuts (works even when the native video surface has focus) -----
    //
    // VideoView.Content (see the XAML comments on VideoViewControl) hosts the entire UI — menu
    // bar, control bar, sidebar, everything — but the actual video frame is rendered by a
    // separate native child HWND that LibVLCSharp.WPF creates purely for the video pixels.
    // Clicking directly on the video (not on any of the WPF-rendered buttons/menu/overlays,
    // which keep working normally) hands real Win32 keyboard focus to that native child window.
    // libVLC's video panel doesn't forward unhandled keys back into WPF's keyboard-input-sink
    // chain, so once that happens, Window_KeyDown above — an ordinary WPF routed event — never
    // fires again for any key, no matter which key or modifier, until focus is somehow moved
    // back onto a WPF element. This is most likely to bite in fullscreen, where the video fills
    // almost the entire window, but it can happen in windowed mode too if the video area is
    // clicked (confirmed live: click video → fullscreen → Space no longer paused playback).
    //
    // The fix is a low-level keyboard hook that steps in ONLY for that specific situation: our
    // window is the OS-foreground app (so this never fires while a totally different app, or one
    // of our own owned dialogs like Settings/Equalizer, is active — GetForegroundWindow() would
    // return THEIR hwnd instead), AND real Win32 focus has left our WPF root entirely (GetFocus()
    // returns our root hwnd for every normal WPF focus state — Button, ListBox, TextBox, or
    // nothing focused at all, since WPF controls aren't separate HWNDs; it only diverges once the
    // native video child has stolen focus). Whenever WPF still owns focus, Window_KeyDown already
    // fires normally, so skipping in that case (rather than always handling here) avoids every
    // shortcut firing twice.

    private void InstallKeyboardHook()
    {
        _keyboardHookProc = KeyboardHookCallback;
        _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, GetModuleHandle(null), 0);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && GetForegroundWindow() == _windowHandle && GetFocus() != _windowHandle)
        {
            var hookStruct = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var key = KeyInterop.KeyFromVirtualKey((int)hookStruct.VkCode);

            if (wParam == (IntPtr)WmKeyUp)
            {
                _hookKeysDown.Remove(key);
            }
            else if (wParam == (IntPtr)WmKeyDown)
            {
                // HashSet.Add returns false when the key was already in the set, i.e. this
                // WM_KEYDOWN is Windows auto-repeat resending the same held key rather than a
                // fresh press.
                bool isRepeat = !_hookKeysDown.Add(key);

                if (TryHandleShortcut(Keyboard.Modifiers, key, isRepeat))
                {
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private bool TryHandleShortcut(ModifierKeys modifiers, Key key, bool isRepeat = false)
    {
        switch (modifiers, key)
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
                PreviousChannel();
                break;
            case (ModifierKeys.None, Key.Down):
            case (ModifierKeys.None, Key.PageDown):
                NextChannel();
                break;
            case (ModifierKeys.None, Key.M):
                // Swallow the key either way (falls through to `return true` below) so a held
                // "M" doesn't leak into anything else, but only actually flip mute on the
                // original press — auto-repeat while the key is held would otherwise toggle
                // mute on/off many times a second, leaving the icon looking out of sync with
                // whatever the user actually intended.
                if (!isRepeat)
                {
                    ToggleMute();
                }
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
                return false;
        }

        return true;
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

        if (_keyboardHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookHandle);
            _keyboardHookHandle = IntPtr.Zero;
        }

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
        var previousSettings = _appSettings;
        var dialog = new SettingsWindow(_appSettings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            // SettingsWindow already persisted the new values via SettingsService.Save — reload
            // them here so every other setting (accent color, the startup-resume toggle, ...)
            // takes effect right away instead of requiring an app restart. Re-fetching the
            // playlist/EPG, though, only makes sense — and only happens — when the fields that
            // actually feed those fetches changed; see ReloadIfIptvOrEpgChangedAsync.
            _appSettings = SettingsService.Load();
            ApplyAccentColor();

            // Topmost itself is already correct — SettingsWindow applied it live via Owner while
            // the dialog was open, and that's exactly the value just saved — this just brings the
            // Video menu's checkmark back in sync now that the dialog (and its own independent
            // live-preview path) is done.
            AlwaysOnTopMenuItem.IsChecked = _appSettings.AlwaysOnTop;

            await ReloadIfIptvOrEpgChangedAsync(previousSettings, _appSettings);
        }
    }

    /// <summary>
    /// Compares specifically the IPTV (playlist mode, M3U URL, Xtream server/username/password/
    /// port) and EPG (URL list) fields old vs. new, and reloads only whichever side actually
    /// changed — instead of the previous blanket "any Settings save re-fetches everything"
    /// behavior, which meant e.g. changing only the accent color also silently re-fetched the
    /// whole playlist and jumped playback to a resumed/first channel via ReloadPlaylistAsync.
    /// </summary>
    private async Task ReloadIfIptvOrEpgChangedAsync(AppSettings previous, AppSettings current)
    {
        var iptvChanged =
            previous.PlaylistMode != current.PlaylistMode ||
            previous.M3uUrl != current.M3uUrl ||
            previous.Xtream.ServerUrl != current.Xtream.ServerUrl ||
            previous.Xtream.Username != current.Xtream.Username ||
            previous.Xtream.Password != current.Xtream.Password ||
            previous.Xtream.Port != current.Xtream.Port;

        var epgChanged = !previous.EpgUrls.SequenceEqual(current.EpgUrls);

        if (!iptvChanged && !epgChanged)
        {
            return;
        }

        try
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingProgressBar.IsIndeterminate = true;

            if (iptvChanged)
            {
                await ReloadPlaylistAsync();
            }

            if (epgChanged)
            {
                await ReloadEpgAsync();
            }

            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            LoadingStatusText.Text = $"Failed to load: {ex.Message}";
            LoadingProgressBar.IsIndeterminate = false;
        }
    }

    /// <summary>
    /// Swaps the app-wide UserAccentBrush resource (declared in App.xaml) for a new brush built
    /// from _appSettings.AccentColor. Replacing the dictionary entry — rather than mutating the
    /// existing brush's Color — is what makes every {DynamicResource UserAccentBrush} consumer
    /// (the Search placeholder, the per-channel favorite star, the startup-resume toggle) pick up
    /// the change live, since XAML-declared brushes are frozen and can't be mutated in place.
    ///
    /// FavoritesFilterButton is the one exception: its Foreground is a plain code-behind property
    /// assignment (see UpdateFavoritesFilterButton), not a {DynamicResource} binding, so it
    /// captures whatever brush *object* was in the dictionary at the time and — unlike the
    /// XAML-bound consumers — never notices the dictionary entry being replaced out from under it.
    /// Re-running that same assignment here is what keeps it in sync instead of freezing on
    /// whatever color was active the last time favorites-filter was toggled.
    /// </summary>
    private void ApplyAccentColor()
    {
        Application.Current.Resources["UserAccentBrush"] = new System.Windows.Media.SolidColorBrush(ParseAccentColor(_appSettings.AccentColor));
        UpdateFavoritesFilterButton();
    }

    private static System.Windows.Media.Color ParseAccentColor(string hex)
    {
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#39FF14");
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
        // checkable MenuItem) — read that as the new target state. The Ctrl+T shortcut goes
        // through ToggleAlwaysOnTop() instead, which computes the flip itself since no click
        // (and so no automatic IsChecked flip) occurred.
        SetAlwaysOnTop(AlwaysOnTopMenuItem.IsChecked);
    }

    private void ToggleAlwaysOnTop() => SetAlwaysOnTop(!Topmost);

    /// <summary>
    /// The one place Always on Top actually changes and persists — reached from the Video menu
    /// item, the Ctrl+T shortcut, and (via SettingsService.Save in SaveButton_Click) the Settings
    /// toggle, which instead applies live through Owner.Topmost while the dialog is still open and
    /// only reaches here once Saved. Keeping all three entry points routed through one method is
    /// what keeps the menu checkmark, the window's actual layering, and the persisted setting from
    /// ever drifting out of sync with each other.
    /// </summary>
    private void SetAlwaysOnTop(bool alwaysOnTop)
    {
        Topmost = alwaysOnTop;
        AlwaysOnTopMenuItem.IsChecked = alwaysOnTop;
        _appSettings.AlwaysOnTop = alwaysOnTop;
        SettingsService.Save(_appSettings);
    }

    /// <summary>
    /// Applies the persisted Always-on-Top state at launch — deliberately not routed through
    /// SetAlwaysOnTop, since that also re-saves settings.json on every call and there's nothing to
    /// persist here that isn't already on disk (this is a restore, not a change).
    /// </summary>
    private void RestoreAlwaysOnTop()
    {
        Topmost = _appSettings.AlwaysOnTop;
        AlwaysOnTopMenuItem.IsChecked = _appSettings.AlwaysOnTop;
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
