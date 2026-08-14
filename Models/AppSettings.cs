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
}
