# PixelPin

PixelPin is a native Windows screenshot and desktop-sticker utility implemented independently with .NET 8 and WPF.

## Run

~~~powershell
dotnet run --project .\src\PixelPin.App\PixelPin.App.csproj
~~~

The app starts in the notification area. Use the tray menu, or press F1, to start a region capture.

Use the --settings, --diagnostics, --history, --windows, or --stickers launch argument to open a corresponding utility window directly.
Add --no-hotkeys when troubleshooting a desktop where another application owns the configured global shortcut.

## Included workflow

- Per-monitor DPI-aware region overlays based on a physical-pixel desktop capture.
- Copy, save as PNG or JPEG, open in the editor, or pin a selected capture.
- Active-window capture and a window chooser for a specific visible application window.
- Always-on-top sticker windows with resize, rotation, opacity, copy, save, close, reversible mouse-through support, groups, and manager controls.
- Persistent hotkeys, save folder, default sticker opacity, startup registration, settings import/export, local capture history, tags, favorites, cache size limits, and thumbnail previews.
- Local privacy and diagnostics page with a bounded rolling log; screenshots, clipboard data, and logs are never uploaded.
- Explicit unavailable states for OCR, scrolling capture, or WebP when the host cannot supply a verified implementation.

## Verification

~~~powershell
dotnet build PixelPin.sln -c Release
dotnet run --project .\tests\PixelPin.Tests\PixelPin.Tests.csproj -c Release
~~~

The automated checks cover settings normalization and portability, hotkey parsing, selection geometry, and filename generation. Multi-monitor mixed-DPI, sleep/resume, protected-content, HDR, and remote-desktop scenarios require a real Windows desktop test matrix and are documented in docs/verification.md.
