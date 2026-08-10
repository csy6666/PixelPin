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
        menu.Items.Add("Capture region", null, (_, _) => captureRegion());
        menu.Items.Add("Capture active window", null, (_, _) => captureWindow());
        menu.Items.Add("Choose a window to capture", null, (_, _) => chooseWindow());
        menu.Items.Add("Scrolling capture", null, (_, _) => scrollingCapture());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Paste image as sticker", null, (_, _) => pasteImage());
        menu.Items.Add("Open image file", null, (_, _) => openImages());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("History", null, (_, _) => showHistory());
        menu.Items.Add("Sticker manager", null, (_, _) => showStickerManager());
        menu.Items.Add("Settings", null, (_, _) => showSettings());
        menu.Items.Add("Privacy and diagnostics", null, (_, _) => showDiagnostics());
        menu.Items.Add("Close all stickers", null, (_, _) => closeAllStickers());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

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
