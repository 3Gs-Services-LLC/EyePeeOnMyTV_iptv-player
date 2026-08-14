using System.Text;
using System.Windows;
using LibVLCSharp.Shared;

namespace EyePeeOnMyTV.Dialogs;

public partial class MediaInfoWindow : Window
{
    public MediaInfoWindow(Media media, string? streamUrl)
    {
        InitializeComponent();
        InfoText.Text = BuildInfoText(media, streamUrl);
    }

    private static string BuildInfoText(Media media, string? streamUrl)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"URI: {streamUrl ?? media.Mrl}");
        sb.AppendLine($"Duration: {(media.Duration >= 0 ? TimeSpan.FromMilliseconds(media.Duration).ToString() : "unknown (live stream)")}");
        sb.AppendLine();

        var tracks = media.Tracks;
        if (tracks.Length == 0)
        {
            sb.AppendLine("No track information available yet.");
        }

        foreach (var track in tracks)
        {
            sb.AppendLine($"[{track.TrackType}] codec={FourCcToString(track.Codec)} bitrate={track.Bitrate} language={track.Language ?? "?"}");

            switch (track.TrackType)
            {
                case TrackType.Audio:
                    sb.AppendLine($"    channels={track.Data.Audio.Channels} rate={track.Data.Audio.Rate}Hz");
                    break;
                case TrackType.Video:
                    sb.AppendLine($"    {track.Data.Video.Width}x{track.Data.Video.Height}" +
                                   (track.Data.Video.FrameRateDen > 0
                                       ? $" @ {(double)track.Data.Video.FrameRateNum / track.Data.Video.FrameRateDen:0.##}fps"
                                       : string.Empty));
                    break;
                case TrackType.Text:
                    sb.AppendLine($"    encoding={track.Data.Subtitle.Encoding ?? "?"}");
                    break;
            }

            sb.AppendLine();
        }

        var stats = media.Statistics;
        sb.AppendLine("--- Statistics ---");
        sb.AppendLine($"Input bitrate: {stats.InputBitrate:0.0} kb/s");
        sb.AppendLine($"Demux bitrate: {stats.DemuxBitrate:0.0} kb/s");
        sb.AppendLine($"Decoded video frames: {stats.DecodedVideo}");
        sb.AppendLine($"Decoded audio blocks: {stats.DecodedAudio}");
        sb.AppendLine($"Displayed pictures: {stats.DisplayedPictures}");
        sb.AppendLine($"Lost pictures: {stats.LostPictures}");

        return sb.ToString();
    }

    private static string FourCcToString(uint fourCc)
    {
        var bytes = BitConverter.GetBytes(fourCc);
        var chars = bytes.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray();
        return new string(chars);
    }
}
