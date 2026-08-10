using System.Text.Json;
using System.Windows.Media.Imaging;
using PixelPin.Models;
using PixelPin.Utilities;

namespace PixelPin.Services;

public sealed class HistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string IndexPath => Path.Combine(AppPaths.Root, "history.json");

    private readonly List<HistoryItem> _items = new();

    public IReadOnlyList<HistoryItem> Items => _items;

    public void Load()
    {
        AppPaths.EnsureDirectories();
        _items.Clear();

        try
        {
            if (!File.Exists(IndexPath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(IndexPath), JsonOptions) ?? [];
            _items.AddRange(loaded
                .Where(item => File.Exists(item.FilePath))
                .OrderByDescending(item => item.CreatedAt));
        }
        catch (Exception)
        {
            // History is optional local cache. A corrupt index is ignored.
        }
    }

    public HistoryItem Add(BitmapSource image, int limit, int diskLimitMegabytes = 512)
    {
        AppPaths.EnsureDirectories();
        var item = new HistoryItem
        {
            FilePath = Path.Combine(AppPaths.HistoryDirectory, $"{Guid.NewGuid():N}.png"),
            PixelWidth = image.PixelWidth,
            PixelHeight = image.PixelHeight,
        };
        BitmapUtilities.SavePngToPath(image, item.FilePath);
        _items.Insert(0, item);
        TrimTo(limit, diskLimitMegabytes);
        Save();
        return item;
    }

    public void Delete(HistoryItem item)
    {
        _items.RemoveAll(candidate => candidate.Id == item.Id);
        TryDelete(item.FilePath);
        Save();
    }

    public void Clear()
    {
        foreach (var item in _items)
        {
            TryDelete(item.FilePath);
        }

        _items.Clear();
        Save();
    }

    public BitmapSource? LoadImage(HistoryItem item)
    {
        if (!File.Exists(item.FilePath))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(item.FilePath, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void SaveMetadata()
    {
        Save();
    }

    public void TrimToLimits(int limit, int diskLimitMegabytes)
    {
        TrimTo(limit, diskLimitMegabytes);
        Save();
    }

    public BitmapSource? LoadThumbnail(HistoryItem item, int decodePixelWidth = 540)
    {
        if (!File.Exists(item.FilePath))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(item.FilePath, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = Math.Max(64, decodePixelWidth);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void TrimTo(int limit, int diskLimitMegabytes)
    {
        limit = Math.Max(0, limit);
        while (_items.Count > limit)
        {
            var oldest = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            TryDelete(oldest.FilePath);
        }

        var maximumBytes = Math.Max(32, diskLimitMegabytes) * 1024L * 1024L;
        while (_items.Count > 0 && GetCacheBytes() > maximumBytes)
        {
            var oldest = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            TryDelete(oldest.FilePath);
        }
    }

    private long GetCacheBytes()
    {
        long total = 0;
        foreach (var item in _items)
        {
            try
            {
                total += new FileInfo(item.FilePath).Length;
            }
            catch
            {
                // A concurrently removed cache file is ignored.
            }
        }

        return total;
    }

    private void Save()
    {
        AppPaths.EnsureDirectories();
        var temporary = IndexPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_items, JsonOptions));
        File.Move(temporary, IndexPath, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Locked cache files are left for a later cleanup.
        }
    }
}
