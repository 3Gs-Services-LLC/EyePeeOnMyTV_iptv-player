namespace EyePeeOnMyTV.Models;

public enum PlaylistSourceMode
{
    M3uUrl,
    XtreamCodes,
}

public sealed class XtreamCodesCredentials
{
    public string ServerUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public PlaylistSourceMode PlaylistMode { get; set; } = PlaylistSourceMode.M3uUrl;
    public string M3uUrl { get; set; } = string.Empty;
    public XtreamCodesCredentials Xtream { get; set; } = new();
    public List<string> EpgUrls { get; set; } = new();

    // Matches Search's original hardcoded green, so an existing settings.json (saved before this
    // field existed) still resolves to the same color and nothing visually changes on upgrade.
    public string AccentColor { get; set; } = "#39FF14";

    // Matches the channel list's original hardcoded width.
    public double SidebarWidth { get; set; } = 300;

    // Keyed by Channel.TvgId when present, else StreamUrl (see MainWindow.GetChannelKey) —
    // channel objects themselves are rebuilt from scratch on every playlist fetch, so favorite
    // status can't live on the object; it has to be looked up by a stable identifier instead.
    public List<string> FavoriteChannelIds { get; set; } = new();

    // Defaults to on, per spec: any settings.json saved before this field existed deserializes
    // to true here too, since a missing JSON property just falls back to the C# default.
    public bool PlayLastViewedChannelOnStartup { get; set; } = true;

    // Same stable identifier scheme as FavoriteChannelIds (see MainWindow.GetChannelKey), updated
    // on every channel change rather than only on close, so a crash or forced quit doesn't lose
    // it. Empty on first run; resuming falls back to the first playlist channel if this is empty
    // or no longer matches anything in the current playlist.
    public string LastViewedChannelId { get; set; } = string.Empty;

    // Matches VolumeSlider's original hardcoded XAML default. Restored on launch independently of
    // PlayLastViewedChannelOnStartup — that toggle only decides which channel plays, not at what
    // volume/mute level. Updated on every volume/mute change rather than only on close (see
    // MainWindow.SaveVolumeAndMute), same reasoning as LastViewedChannelId above.
    public int Volume { get; set; } = 90;
    public bool Muted { get; set; }

    // Matches the Video menu / Ctrl+T checkable item's original unpersisted default (unchecked).
    // Re-applied on every launch (see MainWindow.RestoreAlwaysOnTop) and kept in sync across all
    // three ways to toggle it — this Settings switch, the Video menu item, and Ctrl+T — by routing
    // every one of them through MainWindow.SetAlwaysOnTop.
    public bool AlwaysOnTop { get; set; }
}
