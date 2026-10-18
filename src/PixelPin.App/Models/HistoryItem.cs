using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace PixelPin.Models;

public sealed class HistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string FilePath { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public int PixelWidth { get; set; }

    public int PixelHeight { get; set; }

    public bool IsFavorite { get; set; }

    public string Tags { get; set; } = string.Empty;

    public string DisplayName => $"{(IsFavorite ? "★ " : string.Empty)}{CreatedAt.LocalDateTime:g} - {PixelWidth} x {PixelHeight}";

    /// <summary>
    /// Decoded preview for the history list. Never serialised: the index file holds
    /// paths and metadata only, and a bitmap in history.json would both bloat the file
    /// and fail to round-trip.
    /// </summary>
    [JsonIgnore]
    public BitmapSource? Thumbnail { get; set; }
}
