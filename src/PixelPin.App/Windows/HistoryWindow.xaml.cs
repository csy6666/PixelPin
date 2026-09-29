using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PixelPin.Models;
using PixelPin.Services;

namespace PixelPin.Windows;

public partial class HistoryWindow : Window
{
    private readonly HistoryService _history;
    private readonly Action<BitmapSource> _pin;
    private readonly Action<BitmapSource> _copy;
    private readonly Action<BitmapSource> _save;
    private readonly Action<BitmapSource> _edit;

    public HistoryWindow(
        HistoryService history,
        Action<BitmapSource> pin,
        Action<BitmapSource> copy,
        Action<BitmapSource> save,
        Action<BitmapSource> edit)
    {
        InitializeComponent();
        _history = history;
        _pin = pin;
        _copy = copy;
        _save = save;
        _edit = edit;
        Refresh();
    }

    public void Refresh()
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        HistoryList.ItemsSource = _history.Items
            .Where(item => string.IsNullOrEmpty(query)
                || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Tags.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.IsFavorite)
            .ThenByDescending(item => item.CreatedAt)
            .ToList();
        if (HistoryList.Items.Count > 0)
        {
            HistoryList.SelectedIndex = 0;
        }
        else
        {
            PreviewImage.Source = null;
        }
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PreviewImage.Source = HistoryList.SelectedItem is HistoryItem item
            ? _history.LoadThumbnail(item)
            : null;
        TagsBox.Text = (HistoryList.SelectedItem as HistoryItem)?.Tags ?? string.Empty;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Copy_Click(object sender, RoutedEventArgs e) => WithSelection(_copy);

    private void Pin_Click(object sender, RoutedEventArgs e) => WithSelection(_pin);

    private void Edit_Click(object sender, RoutedEventArgs e) => WithSelection(_edit);

    private void Save_Click(object sender, RoutedEventArgs e) => WithSelection(_save);

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItem item)
        {
            return;
        }

        _history.Delete(item);
        Refresh();
    }

    private void SetTags_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItem item)
        {
            return;
        }

        item.Tags = TagsBox.Text.Trim();
        _history.SaveMetadata();
        Refresh();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItem item)
        {
            return;
        }

        item.IsFavorite = !item.IsFavorite;
        _history.SaveMetadata();
        Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确定要删除 PixelPin 历史记录中缓存的全部截图吗？",
                "清空历史记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _history.Clear();
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void WithSelection(Action<BitmapSource> action)
    {
        var image = GetSelectedImage();
        if (image is not null)
        {
            action(image);
        }
    }

    private BitmapSource? GetSelectedImage()
    {
        return HistoryList.SelectedItem is HistoryItem item ? _history.LoadImage(item) : null;
    }
}
