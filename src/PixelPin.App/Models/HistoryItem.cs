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
}
