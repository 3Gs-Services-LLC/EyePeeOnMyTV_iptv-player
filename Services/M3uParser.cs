using System.Text.RegularExpressions;
using EyePeeOnMyTV.Models;

namespace EyePeeOnMyTV.Services;

/// <summary>
/// Parses extended M3U (#EXTM3U / #EXTINF) IPTV playlists.
/// Tolerant of missing attributes and malformed lines commonly found in public IPTV feeds.
/// </summary>
public static class M3uParser
{
    private static readonly Regex AttributeRegex = new(
        "(?<key>[a-zA-Z0-9-]+)=\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled);

    public static List<Channel> Parse(string m3uContent)
    {
        var channels = new List<Channel>();
        if (string.IsNullOrWhiteSpace(m3uContent))
        {
            return channels;
        }

        var lines = m3uContent.Split('\n');

        string? pendingName = null;
        string? pendingLogo = null;
        string? pendingGroup = null;
        string? pendingTvgId = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var commaIndex = line.IndexOf(',');
                var attributesPart = commaIndex >= 0 ? line[..commaIndex] : line;
                var namePart = commaIndex >= 0 && commaIndex + 1 < line.Length
                    ? line[(commaIndex + 1)..].Trim()
                    : null;

                pendingName = string.IsNullOrWhiteSpace(namePart) ? "Unnamed Channel" : namePart;
                pendingLogo = ExtractAttribute(attributesPart, "tvg-logo");
                pendingGroup = ExtractAttribute(attributesPart, "group-title");
                pendingTvgId = ExtractAttribute(attributesPart, "tvg-id");
                continue;
            }

            if (line.StartsWith('#'))
            {
                // Other directive lines (#EXTGRP, #EXTVLCOPT, etc.) are ignored for v1.
                continue;
            }

            // Any non-comment, non-empty line following an #EXTINF is treated as the stream URL.
            if (!Uri.TryCreate(line, UriKind.Absolute, out _))
            {
                // Not a usable URL; discard whatever metadata was pending and move on.
                pendingName = null;
                pendingLogo = null;
                pendingGroup = null;
                pendingTvgId = null;
                continue;
            }

            channels.Add(new Channel
            {
                Name = pendingName ?? "Unnamed Channel",
                LogoUrl = pendingLogo,
                Group = pendingGroup,
                TvgId = pendingTvgId,
                StreamUrl = line,
            });

            pendingName = null;
            pendingLogo = null;
            pendingGroup = null;
            pendingTvgId = null;
        }

        return channels;
    }

    private static string? ExtractAttribute(string source, string key)
    {
        foreach (Match match in AttributeRegex.Matches(source))
        {
            if (string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
            {
                var value = match.Groups["value"].Value;
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
