using System.Windows;
using System.Windows.Controls;

namespace PixelPin.Windows;

public partial class StickerManagerWindow : Window
{
    private readonly Func<IReadOnlyList<StickyWindow>> _getStickers;
    private readonly Action<StickyWindow, string> _assignGroup;
    private readonly Action<StickyWindow> _bringForward;
    private readonly Action<StickyWindow> _toggleTopLayer;
    private readonly Action<StickyWindow> _toggleInput;
    private readonly Action<StickyWindow> _closeSticker;
    private readonly Action<string> _closeGroup;
    private readonly Action _closeAll;

    public StickerManagerWindow(
        Func<IReadOnlyList<StickyWindow>> getStickers,
        Action<StickyWindow, string> assignGroup,
        Action<StickyWindow> bringForward,
        Action<StickyWindow> toggleTopLayer,
        Action<StickyWindow> toggleInput,
        Action<StickyWindow> closeSticker,
        Action<string> closeGroup,
        Action closeAll)
    {
        InitializeComponent();
        _getStickers = getStickers;
        _assignGroup = assignGroup;
        _bringForward = bringForward;
        _toggleTopLayer = toggleTopLayer;
        _toggleInput = toggleInput;
        _closeSticker = closeSticker;
        _closeGroup = closeGroup;
        _closeAll = closeAll;
        Refresh();
    }

    public void Refresh()
    {
        var selectedId = (StickerList.SelectedItem as StickyWindow)?.Id;
        StickerList.ItemsSource = _getStickers()
            .OrderBy(item => item.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedId is not null)
        {
            StickerList.SelectedItem = StickerList.Items
                .OfType<StickyWindow>()
                .FirstOrDefault(item => item.Id == selectedId);
        }

        if (StickerList.SelectedItem is null && StickerList.Items.Count > 0)
        {
            StickerList.SelectedIndex = 0;
        }

        SyncGroupName();
    }

    private void StickerList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncGroupName();

    private void AssignGroup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSticker is null)
        {
            return;
        }

        _assignGroup(SelectedSticker, GroupNameBox.Text);
        Refresh();
    }

    private void CloseGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = string.IsNullOrWhiteSpace(GroupNameBox.Text)
            ? SelectedSticker?.GroupName ?? "未分组"
            : GroupNameBox.Text.Trim();
        if (MessageBox.Show(
                $"确定要关闭“{group}”分组中的所有贴图吗？",
                "关闭贴图分组",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _closeGroup(group);
        Refresh();
    }

    private void BringForward_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSticker is null)
        {
            return;
        }

        _bringForward(SelectedSticker);
        Refresh();
    }

    private void ToggleTopLayer_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSticker is null)
        {
            return;
        }

        _toggleTopLayer(SelectedSticker);
        Refresh();
    }

    private void ToggleInput_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSticker is null)
        {
            return;
        }

        _toggleInput(SelectedSticker);
        Refresh();
    }

    private void CloseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSticker is null)
        {
            return;
        }

        _closeSticker(SelectedSticker);
        Refresh();
    }

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        if (StickerList.Items.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(
                "确定要关闭所有当前贴图吗？",
                "关闭所有贴图",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            _closeAll();
            Refresh();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private StickyWindow? SelectedSticker => StickerList.SelectedItem as StickyWindow;

    private void SyncGroupName()
    {
        GroupNameBox.Text = SelectedSticker?.GroupName ?? string.Empty;
    }
}
