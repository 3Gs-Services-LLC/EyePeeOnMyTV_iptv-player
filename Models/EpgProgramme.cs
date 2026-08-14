namespace EyePeeOnMyTV.Models;

public sealed class EpgProgramme
{
    public string ChannelId { get; set; } = string.Empty;
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset Stop { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
}
