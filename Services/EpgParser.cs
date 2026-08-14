using System.Globalization;
using System.IO;
using System.Xml;
using EyePeeOnMyTV.Models;

namespace EyePeeOnMyTV.Services;

/// <summary>
/// Streams and parses XMLTV EPG data. Uses XmlReader rather than loading a full DOM
/// because public XMLTV feeds (e.g. epg.iptv.cat) can be tens of megabytes.
/// </summary>
public static class EpgParser
{
    public static Dictionary<string, List<EpgProgramme>> Parse(Stream xmlStream)
    {
        var result = new Dictionary<string, List<EpgProgramme>>(StringComparer.OrdinalIgnoreCase);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        using var reader = XmlReader.Create(xmlStream, settings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme")
            {
                continue;
            }

            var channelId = reader.GetAttribute("channel");
            var startRaw = reader.GetAttribute("start");
            var stopRaw = reader.GetAttribute("stop");

            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(startRaw))
            {
                reader.Skip();
                continue;
            }

            if (!TryParseXmltvDate(startRaw, out var start))
            {
                reader.Skip();
                continue;
            }

            var stop = start.AddHours(1);
            if (!string.IsNullOrWhiteSpace(stopRaw) && TryParseXmltvDate(stopRaw, out var parsedStop))
            {
                stop = parsedStop;
            }

            string? title = null;
            string? desc = null;

            using (var sub = reader.ReadSubtree())
            {
                sub.Read(); // move to <programme>
                while (sub.Read())
                {
                    if (sub.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (sub.Name == "title" && title is null)
                    {
                        title = sub.ReadElementContentAsString();
                    }
                    else if (sub.Name == "desc" && desc is null)
                    {
                        desc = sub.ReadElementContentAsString();
                    }
                }
            }

            var programme = new EpgProgramme
            {
                ChannelId = channelId,
                Start = start,
                Stop = stop,
                Title = string.IsNullOrWhiteSpace(title) ? "(no title)" : title!,
                Description = desc,
            };

            if (!result.TryGetValue(channelId, out var list))
            {
                list = new List<EpgProgramme>();
                result[channelId] = list;
            }

            list.Add(programme);
        }

        foreach (var list in result.Values)
        {
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        }

        return result;
    }

    /// <summary>
    /// XMLTV timestamps look like "20260810120000 +0000" (yyyyMMddHHmmss, optional space, optional zzz offset
    /// without a colon). DateTimeOffset's built-in "zzz" custom format requires a colon, so this is parsed by hand.
    /// </summary>
    private static bool TryParseXmltvDate(string raw, out DateTimeOffset result)
    {
        result = default;
        var value = raw.Trim();
        if (value.Length < 14)
        {
            return false;
        }

        var datePart = value[..14];
        if (!DateTime.TryParseExact(
                datePart,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateTime))
        {
            return false;
        }

        var offset = TimeSpan.Zero;
        var offsetPart = value[14..].Trim();
        if (offsetPart.Length >= 5)
        {
            var sign = offsetPart[0] == '-' ? -1 : 1;
            var digits = offsetPart.TrimStart('+', '-').Trim();
            if (digits.Length >= 4
                && int.TryParse(digits[..2], out var hours)
                && int.TryParse(digits[2..4], out var minutes))
            {
                offset = TimeSpan.FromMinutes(sign * (hours * 60 + minutes));
            }
        }

        result = new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified), offset);
        return true;
    }

    public static (EpgProgramme? Current, EpgProgramme? Next) GetNowAndNext(
        Dictionary<string, List<EpgProgramme>> epg,
        string? tvgId,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(tvgId) || !epg.TryGetValue(tvgId, out var programmes) || programmes.Count == 0)
        {
            return (null, null);
        }

        EpgProgramme? current = null;
        EpgProgramme? next = null;

        for (var i = 0; i < programmes.Count; i++)
        {
            var p = programmes[i];
            if (now >= p.Start && now < p.Stop)
            {
                current = p;
                next = i + 1 < programmes.Count ? programmes[i + 1] : null;
                break;
            }

            if (p.Start > now)
            {
                next = p;
                break;
            }
        }

        return (current, next);
    }
}
