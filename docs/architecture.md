# PixelPin architecture

PixelPin uses a .NET 8 WPF process with a small native interop boundary.

- Services/ScreenCaptureService captures the physical-pixel virtual desktop before any overlay windows are displayed.
- Windows/SelectionOverlayWindow is instantiated once per Screen. Each overlay receives only that monitor's crop, so WPF DIP mouse positions are converted back to that monitor's physical image pixels before a final crop is produced.
- Windows/StickyWindow is independent of the settings window. Closing settings therefore leaves active stickers and registered hotkeys intact.
- Services/HistoryService saves PNGs and a JSON index in %LocalAppData%\\PixelPin; bounded entry and disk limits remove old items, while the history list decodes thumbnails before a full image is needed.
- Services/HotKeyService owns a hidden native message window for RegisterHotKey and a low-level keyboard-hook fallback for applications that consume ordinary shortcut messages. It matches only configured shortcuts, suppresses duplicate delivery, and does not persist keyboard input.
- Services/ApplicationLogService retains a small local rolling diagnostics file. Its write path is isolated so a full disk cannot break capture, and it never receives image data.
- Services/ScreenCaptureService can enumerate visible top-level windows for a chosen-window capture. The current GDI implementation captures visible desktop pixels, so the window picker closes before capture and the UI does not claim occlusion-independent capture.
- Windows/StickerManagerWindow coordinates active independent sticker windows through explicit group, layer, mouse-through, and batch-close commands without making a manager window their owner.

The architecture deliberately keeps image capture, UI, and persistence separate. The initial GDI capture implementation can later be replaced by a Windows Graphics Capture or DXGI implementation without changing overlay and sticker workflows.
