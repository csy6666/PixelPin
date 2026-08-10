using System.Windows;
using System.Windows.Input;
using PixelPin.Models;

namespace PixelPin.Windows;

public partial class WindowPickerWindow : Window
{
    private readonly Func<IReadOnlyList<CaptureWindowDescriptor>> _enumerate;
    private readonly Action<CaptureWindowDescriptor> _capture;
    private bool _captureStarted;

    public WindowPickerWindow(
        Func<IReadOnlyList<CaptureWindowDescriptor>> enumerate,
        Action<CaptureWindowDescriptor> capture)
    {
        InitializeComponent();
        _enumerate = enumerate;
        _capture = capture;
        RefreshWindows();
    }

    public void RefreshWindows()
    {
        var selectedHandle = (WindowList.SelectedItem as CaptureWindowDescriptor)?.Handle;
        WindowList.ItemsSource = _enumerate();
        if (selectedHandle is not null)
        {
            WindowList.SelectedItem = WindowList.Items
                .OfType<CaptureWindowDescriptor>()
                .FirstOrDefault(item => item.Handle == selectedHandle);
        }

        if (WindowList.SelectedItem is null && WindowList.Items.Count > 0)
        {
            WindowList.SelectedIndex = 0;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private async void Capture_Click(object sender, RoutedEventArgs e) => await CaptureSelectedAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await CaptureSelectedAsync();

    private async Task CaptureSelectedAsync()
    {
        if (_captureStarted || WindowList.SelectedItem is not CaptureWindowDescriptor descriptor)
        {
            return;
        }

        _captureStarted = true;
        Close();
        await Task.Delay(120);
        _capture(descriptor);
    }
}
