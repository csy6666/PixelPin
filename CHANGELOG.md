# Changelog

## 0.2.1

- 将窗口、托盘菜单、工具提示、错误提示和诊断文本统一为简体中文。
- 增加 Windows x64 自包含单文件便携版发布脚本和 SHA-256 校验文件。

## 0.2.0

- Added a chosen-window capture flow, local privacy and diagnostics page, bounded rolling diagnostics log, and settings JSON import/export.
- Added active sticker management with named groups, group/bulk close, layer control, and mouse-through recovery.
- Added configurable history disk cache limits and thumbnail-first history previews for large local collections.
- Documented the visible-desktop limitation of the current chosen-window capture route and retained explicit unavailable states for unsupported advanced features.

## 0.1.0

- Added the native WPF screenshot selection, copy, PNG save, and desktop-sticker workflow.
- Added DPI-aware per-monitor overlays, active-window capture, configurable global shortcuts, tray residency, settings, startup registration, and local history.
- Added annotation tools, undo/redo, pixel mosaic, local history search/tags/favorites, image import, clipboard paste, sticker drag-and-drop, JPEG export, and reversible mouse-through mode.
- OCR, scrolling capture, WebP export, installer signing, and hardware-specific desktop regression coverage remain explicitly deferred; the UI provides clear unavailable states rather than false success.
