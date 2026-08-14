using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EyePeeOnMyTV.Models;

namespace EyePeeOnMyTV.Services;

/// <summary>
/// Fetches and parses the M3U playlist and XMLTV EPG feed(s) configured in Settings. Nothing
/// here persists anything itself — see SettingsService for that — results are just cached in
/// memory by the caller for the session.
/// </summary>
public sealed class DataSourceService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<List<Channel>> FetchChannelsAsync(AppSettings settings, CancellationToken ct = default)
    {
        var content = await Http.GetStringAsync(ResolvePlaylistUrl(settings), ct).ConfigureAwait(false);
        return M3uParser.Parse(content);
    }

    /// <summary>
    /// Fetches every configured EPG URL and merges them into one lookup, keyed by channel id.
    /// A channel id present in more than one source has its programmes combined and re-sorted,
    /// so overlapping or complementary EPG feeds both work correctly.
    /// </summary>
    public async Task<Dictionary<string, List<EpgProgramme>>> FetchEpgAsync(AppSettings settings, CancellationToken ct = default)
    {
        var merged = new Dictionary<string, List<EpgProgramme>>(StringComparer.OrdinalIgnoreCase);

        foreach (var epgUrl in settings.EpgUrls)
        {
            if (string.IsNullOrWhiteSpace(epgUrl))
            {
                continue;
            }

            await using var stream = await Http.GetStreamAsync(epgUrl, ct).ConfigureAwait(false);
            var parsed = await Task.Run(() => EpgParser.Parse(stream), ct).ConfigureAwait(false);

            foreach (var (channelId, programmes) in parsed)
            {
                if (!merged.TryGetValue(channelId, out var list))
                {
                    list = new List<EpgProgramme>();
                    merged[channelId] = list;
                }

                list.AddRange(programmes);
            }
        }

        foreach (var list in merged.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return merged;
    }

    public static string ResolvePlaylistUrl(AppSettings settings) => settings.PlaylistMode switch
    {
        PlaylistSourceMode.XtreamCodes => BuildXtreamPlaylistUrl(settings.Xtream),
        _ => settings.M3uUrl,
    };

    /// <summary>
    /// Xtream Codes / Smarters-compatible panels universally expose their playlist through this
    /// get.php endpoint — it's the same URL format VLC, TiviMate, and other Xtream clients build
    /// from a user's server/username/password. Producing that URL here means Xtream mode reuses
    /// the exact same M3U fetch/parse path as plain M3U mode, rather than needing a second
    /// channel-parsing pipeline for Xtream's alternative JSON API.
    /// </summary>
    private static string BuildXtreamPlaylistUrl(XtreamCodesCredentials credentials)
    {
        var server = credentials.ServerUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(credentials.Port) && !HasExplicitPort(server))
        {
            server = $"{server}:{credentials.Port}";
        }

        var username = Uri.EscapeDataString(credentials.Username);
        var password = Uri.EscapeDataString(credentials.Password);
        return $"{server}/get.php?username={username}&password={password}&type=m3u_plus&output=ts";
    }

    private static bool HasExplicitPort(string server)
    {
        var schemeIndex = server.IndexOf("://", StringComparison.Ordinal);
        var hostAndPort = schemeIndex >= 0 ? server[(schemeIndex + 3)..] : server;
        return hostAndPort.Contains(':');
    }
}
