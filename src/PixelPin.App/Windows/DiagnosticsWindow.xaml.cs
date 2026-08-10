using System.Windows;

namespace PixelPin.Windows;

public partial class DiagnosticsWindow : Window
{
    private readonly Func<string> _buildReport;
    private readonly Action _openDataFolder;
    private readonly Action _clearLog;

    public DiagnosticsWindow(Func<string> buildReport, Action openDataFolder, Action clearLog)
    {
        InitializeComponent();
        _buildReport = buildReport;
        _openDataFolder = openDataFolder;
        _clearLog = clearLog;
        RefreshReport();
    }

    public void RefreshReport()
    {
        ReportBox.Text = _buildReport();
        ReportBox.ScrollToHome();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshReport();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(ReportBox.Text);
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => _openDataFolder();

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _clearLog();
        RefreshReport();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
