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

    // Keyed by Channel.TvgId when present, else StreamUrl (see MainWindow.GetFavoriteKey) —
    // channel objects themselves are rebuilt from scratch on every playlist fetch, so favorite
    // status can't live on the object; it has to be looked up by a stable identifier instead.
    public List<string> FavoriteChannelIds { get; set; } = new();
}
