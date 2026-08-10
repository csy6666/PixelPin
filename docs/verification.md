# Verification matrix

## Automated gate

Run:

~~~powershell
dotnet build PixelPin.sln -c Release
dotnet run --project .\\tests\\PixelPin.Tests\\PixelPin.Tests.csproj -c Release
~~~

The console checks validate shortcut parsing, persisted-setting normalization, selection geometry, and output filename expansion.

## Manual desktop gate

The following require a signed-in interactive desktop and are intentionally not claimed by automated tests:

| Scenario | Required evidence |
| --- | --- |
| Single monitor | Capture, copy, save, pin, close repeated ten times without orphan windows. |
| Dual monitor with mixed 125% / 150% DPI | Compare drawn selection edges with the exported pixel crop on both displays. |
| Hotkey latency | Measure F1-to-overlay response over common desktop workloads; target under 300 ms. |
| Sticker stress | Keep at least 20 normal-resolution stickers, then move, scale, close, and toggle mouse-through. |
| Sticker manager | Assign two groups, toggle layer/input state through the manager, close one group, and confirm only its stickers close. |
| Chosen window capture | Choose an unobscured target, confirm the picker disappears before the image is taken, and verify a closed target reports a clear failure. |
| Settings portability | Export settings, alter a shortcut/cache limit, import the file, review it, then apply it and verify global hotkeys are re-registered. |
| Diagnostics and privacy | Confirm the report contains only local environment metadata and the local data/log folder can be opened and cleared. |
| Power and display changes | Test sleep/resume, display reconnect, portrait monitor, and remote desktop reconnect. |
| Restricted content | Confirm protected windows, UAC desktop, exclusive fullscreen, HDR, and remote capture show an understandable limitation rather than a fabricated result. |

## Explicitly deferred feature gates

- OCR requires a configured Windows OCR language component and a tested Windows Runtime integration.
- Scrolling capture requires a controlled scroll path per supported application. It must fall back to a normal capture when that path cannot be verified.
- WebP export requires a verified encoder; PNG and JPEG are available now.
- Installer, upgrade, uninstall, signing, SHA-256 release assets, and Windows 10/11 hardware coverage need a release pipeline and target machines.
