using System.Drawing;
using System.Windows.Forms;

namespace PixelPin.Services;

/// <summary>Tray menu callbacks plus the shortcut labels shown next to each command.</summary>
public sealed record TrayCommands(
    Action CaptureRegion,
    Action CaptureRegionAndPin,
    Action CaptureFullScreen,
    Action CaptureWindow,
    Action ChooseWindow,
    Action ScrollingCapture,
    Action PasteImage,
    Action OpenImages,
    Action ShowHistory,
    Action ShowStickerManager,
    Action ShowSettings,
    Action ShowDiagnostics,
    Action CloseAllStickers,
    Action Exit,
    string CaptureHotKey = "",
    string CapturePinHotKey = "",
    string CaptureFullScreenHotKey = "",
    string PasteStickerHotKey = "",
    string CloseAllStickersHotKey = "");

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _applicationIcon;

    public TrayService(TrayCommands commands)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(MenuItem("区域截图", commands.CaptureHotKey, commands.CaptureRegion));
        menu.Items.Add(MenuItem("区域截图并贴图", commands.CapturePinHotKey, commands.CaptureRegionAndPin));
        menu.Items.Add(MenuItem("全屏截图", commands.CaptureFullScreenHotKey, commands.CaptureFullScreen));
        menu.Items.Add(MenuItem("截取当前窗口", string.Empty, commands.CaptureWindow));
        menu.Items.Add(MenuItem("选择窗口截图", string.Empty, commands.ChooseWindow));
        menu.Items.Add(MenuItem("滚动截图", string.Empty, commands.ScrollingCapture));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuItem("粘贴图片为贴图", commands.PasteStickerHotKey, commands.PasteImage));
        menu.Items.Add(MenuItem("打开图片文件", string.Empty, commands.OpenImages));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuItem("历史记录", string.Empty, commands.ShowHistory));
        menu.Items.Add(MenuItem("贴图管理", string.Empty, commands.ShowStickerManager));
        menu.Items.Add(MenuItem("设置", string.Empty, commands.ShowSettings));
        menu.Items.Add(MenuItem("隐私与诊断", string.Empty, commands.ShowDiagnostics));
        menu.Items.Add(MenuItem("关闭所有贴图", commands.CloseAllStickersHotKey, commands.CloseAllStickers));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuItem("退出", string.Empty, commands.Exit));

        _applicationIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath)
            ?? (Icon)SystemIcons.Application.Clone();
        _icon = new NotifyIcon
        {
            Text = "PixelPin 截图贴图工具",
            Icon = _applicationIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                commands.ShowSettings();
            }
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _applicationIcon.Dispose();
    }

    public void ShowMessage(string title, string message)
    {
        _icon.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
    }

    private static ToolStripMenuItem MenuItem(string text, string hotKey, Action action)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => action());
        if (!string.IsNullOrWhiteSpace(hotKey))
        {
            item.ShortcutKeyDisplayString = hotKey;
            item.ShowShortcutKeys = true;
        }

        return item;
    }
}
