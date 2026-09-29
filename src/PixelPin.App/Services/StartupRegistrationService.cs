using Microsoft.Win32;

namespace PixelPin.Services;

public sealed class StartupRegistrationService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PixelPin";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

        if (enabled)
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法获取程序路径。");
            key.SetValue(ValueName, $"\"{processPath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
