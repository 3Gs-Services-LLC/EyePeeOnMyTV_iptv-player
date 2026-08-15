using System.ComponentModel;

namespace EyePeeOnMyTV.Models;

public sealed class Channel : INotifyPropertyChanged
{
    public string Name { get; set; } = "Unnamed Channel";
    public string? LogoUrl { get; set; }
    public string? Group { get; set; }
    public string StreamUrl { get; set; } = string.Empty;
    public string? TvgId { get; set; }

    private bool _isFavorite;

    // The channel list is rebuilt fresh from plain POCOs on every playlist fetch/sort/filter, so
    // unlike the rest of this class, this one property needs change notification — it's the only
    // one a user can flip while its row is already on screen and expects an instant visual update.
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFavorite)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
