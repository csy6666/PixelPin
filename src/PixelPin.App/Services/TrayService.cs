using System.Drawing;
using System.Windows.Forms;

namespace PixelPin.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayService(
        Action captureRegion,
        Action captureWindow,
        Action chooseWindow,
        Action pasteImage,
        Action openImages,
        Action showHistory,
        Action showStickerManager,
        Action showSettings,
        Action showDiagnostics,
        Action closeAllStickers,
        Action scrollingCapture,
        Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("区域截图", null, (_, _) => captureRegion());
        menu.Items.Add("截取当前窗口", null, (_, _) => captureWindow());
        menu.Items.Add("选择窗口截图", null, (_, _) => chooseWindow());
        menu.Items.Add("滚动截图", null, (_, _) => scrollingCapture());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("粘贴图片为贴图", null, (_, _) => pasteImage());
        menu.Items.Add("打开图片文件", null, (_, _) => openImages());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("历史记录", null, (_, _) => showHistory());
        menu.Items.Add("贴图管理", null, (_, _) => showStickerManager());
        menu.Items.Add("设置", null, (_, _) => showSettings());
        menu.Items.Add("隐私与诊断", null, (_, _) => showDiagnostics());
        menu.Items.Add("关闭所有贴图", null, (_, _) => closeAllStickers());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Text = "PixelPin",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                showSettings();
            }
        };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    public void ShowMessage(string title, string message)
    {
        _icon.ShowBalloonTip(2500, title, message, ToolTipIcon.Info);
    }
}
