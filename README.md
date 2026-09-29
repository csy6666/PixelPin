# PixelPin 截图贴图工具

PixelPin 是一款使用 .NET 8 和 WPF 独立实现的 Windows 原生截图与桌面贴图工具。界面为简体中文，截图、历史记录和诊断数据默认只保存在本机。

## 运行

~~~powershell
dotnet run --project .\src\PixelPin.App\PixelPin.App.csproj
~~~

程序启动后常驻系统托盘。使用托盘菜单或按 F1 开始区域截图。快捷键同时使用 Windows 全局热键和按键兼容通道，部分会吞掉普通键盘消息的应用也可以触发截图；兼容通道只匹配已配置的按键，不记录或上传键盘内容。

可以使用 `--settings`、`--diagnostics`、`--history`、`--windows` 或 `--stickers` 直接打开对应工具窗口。排查快捷键冲突时，可以追加 `--no-hotkeys` 禁用全局快捷键。

## 便携版

便携版无需安装，解压后直接运行 `PixelPin.exe`。使用下面的脚本可以生成自包含的单文件程序和 ZIP 压缩包：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Publish-Portable.ps1
```

输出目录为 `artifacts\PixelPin-win-x64-portable`，ZIP 文件名带有当前版本号。设置和历史记录保存在当前 Windows 用户的 `%LocalAppData%\PixelPin`，删除程序目录不会删除这些本地数据。

## 功能

- Per-monitor DPI-aware region overlays based on a physical-pixel desktop capture.
- Copy, save as PNG or JPEG, open in the editor, or pin a selected capture.
- Active-window capture and a window chooser for a specific visible application window.
- Always-on-top sticker windows with resize, rotation, opacity, copy, save, close, reversible mouse-through support, groups, and manager controls.
- Persistent hotkeys, save folder, default sticker opacity, startup registration, settings import/export, local capture history, tags, favorites, cache size limits, and thumbnail previews.
- Local privacy and diagnostics page with a bounded rolling log; screenshots, clipboard data, and logs are never uploaded.
- OCR、滚动截图和 WebP 在当前构建不可用时会明确提示，不会伪造结果。

## 验证

~~~powershell
dotnet build PixelPin.sln -c Release
dotnet run --project .\tests\PixelPin.Tests\PixelPin.Tests.csproj -c Release
~~~

自动检查覆盖设置规范化与可移植性、快捷键解析、选区几何和文件名展开。多显示器混合 DPI、睡眠恢复、受保护内容、HDR 和远程桌面场景需要真实 Windows 桌面测试，详见 [docs/verification.md](docs/verification.md)。
