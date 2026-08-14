namespace EyePeeOnMyTV.Models;

public sealed class Channel
{
    public string Name { get; set; } = "Unnamed Channel";
    public string? LogoUrl { get; set; }
    public string? Group { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public string? TvgId { get; set; }
}
