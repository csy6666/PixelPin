# Verification matrix

## Automated gate

Run:

~~~powershell
dotnet build PixelPin.sln -c Release
dotnet run --project .\tests\PixelPin.Tests\PixelPin.Tests.csproj -c Release
~~~

The console checks validate shortcut parsing and duplicate detection, persisted-setting normalization and portability, legacy settings migration, selection geometry, window-edge snapping, keyboard nudging and its step sizes, z-order hit testing, sticker resize geometry, sticker alignment and guide generation, sticker clamping, pixelation, box blur, beautify layout and shadow fitting, beautify preset serialization, OCR text assembly and script detection, OCR unavailable reasons, window-render quality classification, occlusion measurement, window-capture route ordering, scroll offset measurement and its refusals, detached crops, and output filename expansion.

The alignment tests assert the pairing rule rather than only the resulting numbers: an edge snaps to an edge, a centre snaps to a centre, and neither snaps to the other kind. Matching every position against every line let a sticker's centre lock onto the screen edge, which pushed half the sticker off screen on what the user intended as a small placement. The clamp tests assert that a sticker pushed past any edge keeps a grabbable strip on screen, and that a sticker already inside the desktop is never moved by the clamp at all.

The route-ordering tests assert the invariants that make the fallback chain honest rather than merely asserting the current output: a fully visible window must be read from the screen; a covered window must never put the screen crop first, because that returns whatever is stacked on top; a mostly covered window must not offer a screen crop at all; a window hanging off the desktop must start with its composed surface; and a refusal must never be paired with a route list. Each assertion names the wrong behaviour it prevents, so a future change that reorders the chain fails with a description of the regression instead of a diff.

The scrolling tests assert the four things that make the feature honest rather than merely present: an uneven scroll of 37 pixels is measured exactly, not rounded to a wheel step; repeating content is refused instead of matched at whichever offset scores highest; unrelated frames are not forced into a match; and a scroll that leaves only the minimum overlap is refused rather than stitched on a sliver of evidence. The stitching assertion compares the result against the source content, so a duplicated or missing row fails it, and a deliberately wrong offset is appended as a control to prove the comparison can fail at all. A further case scrolls the content while a moving scrollbar occupies a tenth of the width, and asserts the offset is still measured: a real region always contains something that does not scroll, so requiring a near-perfect match would reject the very regions users select. That case fails against a 0.95 gate, which is how the defect it guards against was found.

The shortcut tests deliberately round-trip recorder output back through the parser. That check caught a real defect: `Enum.TryParse` reads a bare numeric string as the enum's underlying value, so a recorded `Ctrl+1` was displayed as `Ctrl+1` and read back as `Key.Cancel`, silently changing the shortcut after a restart.

## Desktop probe

The probe needs a signed-in interactive desktop and exercises the real capture path. It is opt-in:

~~~powershell
dotnet run --project .\tests\PixelPin.Tests\PixelPin.Tests.csproj -c Release -- --desktop-probe .\artifacts\probe
~~~

| Probe check | What it proves |
| --- | --- |
| Monitor capture | Every reported monitor yields an image whose pixel size equals its bounds. |
| Z-order enumeration | The window list is non-empty and excludes shell surfaces and PixelPin itself. |
| Overlay dimming | The probe waits for the overlay's first layout pass, then confirms the rendered dimmer leaves the exact expected fraction (0.349 for `#A6000000`) of the captured screen. |
| Hover highlight | With the real cursor over a detected window, the overlay draws a highlight box containing the pointer, and the log reports the closest matching window. |
| Keyboard nudge | Driving the overlay's own arrow-key path moves the selection exactly one pixel, ten with Ctrl, and resizes only the named edge with Shift; the size the overlay reports matches. |
| Editor | Mosaic, blur, sequence, and rectangle annotations commit; undo and redo change the annotation count; the export keeps the source size. |
| Redaction pixels | Distinct colour counts inside the mosaic region drop in the exported image, proving the redaction reached the output rather than only the preview. Blur is asserted on the quantity it actually guarantees: the mean difference between horizontally adjacent pixels falls (8.80 to 0.94). A colour count is not valid for blur, because averaging flat areas creates intermediate shades and can raise the count even though the image is smoother. |
| Sticker rotation | A quarter turn swaps the window box and the drawn image still matches the source aspect ratio. |
| Sticker alignment | Two real stickers are created; dragging one near the other locks it onto the shared edge and draws a guide on that edge, and dragging away releases the snap and clears the guides. |
| Sticker drag gesture | The check the alignment test could never provide. A sticker is grabbed and dragged with real `mouse_event` input - press, eight moves, release - and the window must land within 8 DIP of the requested physical delta, converted through the window's own DPI scale. Because the alignment tests call the positioning code directly, a gesture that never reaches it looked identical to a working one, and a sticker that could not be dragged at all passed every test. The probe also asserts the sticker is no longer in its dragging state after the release, which catches a drag that starts and can never end. |
| Sticker clamp | The sticker is pushed far past the left edge and must still keep part of itself on screen, so it can always be grabbed again. |
| Sticker centre alignment | A sticker whose centre sits 5 DIP inside the left edge must land exactly where pointer-following puts it. Snapping its centre onto the screen edge moved it further out, so the assertion is on the position the pointer asked for rather than on how much of the sticker is visible. |
| Beautified export | The exported pixels are inspected: the frame is the gradient background rather than the shadow caster, the picture centre is unmoved, and the rounded corner is genuinely cut away. |
| Shortcut recorder | A real `PreviewKeyDown` is raised on the settings page's shortcut field, proving the XAML `EventSetter` is wired, the field is read-only, and `Backspace` and `Esc` behave. |
| OCR round trip | Text is rendered to a bitmap with known content and recognised back. Latin scores 1.00 similarity against `en-US` and Chinese scores 1.00 against `zh-Hans-CN`, which also proves the Chinese joining rule because the result must contain no spaces. A blank image must return success with no text rather than a failure, and an unknown language tag must fall back to the profile recognizer instead of failing. |
| Window rendering | Real windows on the desktop are rendered through `PrintWindow`, and each accepted result is checked for opaque pixels, matching bounds, and genuine colour variation. A fabricated all-black image must be rejected by the quality gate. |
| Graphics capture | The `Windows.Graphics.Capture` route is exercised directly on the covered window. The frame must be `Pbgra32` (so the window's real alpha survived), must contain no capture-border pixels, must report that the border was actually suppressed, and must return the covered window's own colour. The full chain is then run twice: once with the route available, which must select it, and once with it declared unavailable, which must degrade to the GDI render and still return the covered window's own content. |
| Covered window | The decisive check. Two known-coloured windows are stacked exactly on top of each other: the screen capture of that rectangle returns the upper window's colour while every window route returns the lower window's own, and the routes are then repeated through the overlay's own window-selection path. This is what proves occlusion-proof capture rather than merely proving that a capture returned without throwing. |
| Scrolling capture | A real scrollable window holds 2000 rows, each with a unique colour. The capture is driven end to end and the stitched image is decoded back into row numbers: the sequence must advance by exactly one per row, so a duplicated row, a missing row, and a wrong offset all fail it. The measured run stitches 22 frames into 2000 rows with no gap and no repeat. Before the capture, the probe decodes the first frame to confirm the content really renders one source row per physical pixel, so a failure there is attributed to the probe and not to the stitcher. |
| Scrolling refusal | The same window is filled with content that repeats every 16 rows, which leaves at least two offsets fitting equally well. The capture must return exactly one frame, an image no taller than the region, and a stated reason. A capture that appended anything here would have produced a long image with silently duplicated content. |
| Tool windows | Settings, history, diagnostics, window picker, sticker manager, the beautify dialog, and the OCR dialog each load their real XAML with the real `App.xaml` resources and render. This catches missing resources and handler ordering faults that would otherwise appear only when the user opens the window. |

It also writes `probe-fullscreen.png`, `probe-overlay.png`, `probe-editor.png`, `probe-sticker.png`, `probe-beautify.png`, and one `probe-<tool>.png` per tool window for visual review.

Two probe-side traps are worth remembering when extending it, because both look like product failures:

- The overlay renders the screenshot scaled into its device-independent surface, so comparing render pixels against raw screenshot pixels requires mapping through the scale factor.
- Window bounds change while the probe runs. Asserting an exact highlight rectangle against a window enumeration taken earlier is inherently racy; assert the invariant (the highlight contains the pointer) and log the closest match instead.

A third trap is a matter of test construction rather than coordinates: when a probe supplies its own peer list to a callback that is invoked with the object being moved, the predicate must return the *other* objects. Inverting it makes alignment look broken while the product code is correct.

A fourth trap is asserting a property the feature does not actually have. "Blur reduces the distinct colour count" held for photographic screen content and failed intermittently on content with large flat areas (332 to 453), because averaging flat regions *creates* intermediate shades. The assertion was replaced with the property blur genuinely guarantees. When a probe check fails intermittently, the first question is whether it is asserting the right invariant, not whether the product is broken.

A fifth trap is the reverse: a probe that is too permissive. The icon defect below was present in every build and invisible until the probe started pumping the dispatcher after startup, because `App.xaml`'s pending icon decode only runs when the queue is first pumped.

A sixth trap is specific to the Windows Runtime projection, and it cost several build cycles while bringing up graphics capture. `CastExtensions.As<T>` only accepts projected types, so casting to a hand-declared `[ComImport]` interface throws `ArgumentException` at runtime even though it compiles. The interop factory is therefore obtained with `Marshal.QueryInterface` against the raw `ThisPtr` of the `IObjectReference` that `ActivationFactory.Get` returns. Two further details are easy to get wrong and each fails silently rather than loudly: `RoGetActivationFactory` must be asked for the `IActivationFactory` IID (`00000035-…`), and `Direct3D11CaptureFramePool.Create` only delivers frames on a thread with a dispatcher, so a non-UI caller must use `CreateFreeThreaded` or it simply times out with no error. These were established with a throwaway spike before any product code was written, which is why the product code had one bug (an overload ambiguity) instead of five.

A seventh trap is that a probe which asserts something about the *screen* has to own the screen. The stacked-window check asserts that the screen really shows the upper window, which is what gives the comparison its meaning; but the probe windows originally used WPF's default placement and no z-order, so once another application window happened to sit above them the check failed with a message that looked like a product defect. The windows are now positioned explicitly and made top-most, so the probe owns the stacking it asserts. When a probe check starts failing without any code change, the first question is what in the environment the probe was implicitly relying on.

An eighth trap is subtler and cost real time: the scrolling probe failed with "offsets 72 and 327 fit equally well", which reads exactly like a stitcher defect. It was the probe's own content encoding. Row indices were packed into a byte, and a shift of 255 rows changes that byte by one, so offsets 255 apart are genuinely indistinguishable - 327 is 72 + 255. The stitcher was right to refuse. The content now gives every row a unique pseudo-random colour with a lookup table back to the row index, which has no such aliasing. The general lesson: when a "correct" component refuses plausible-looking input, check whether the input really is ambiguous before changing the component.

A ninth trap is that a diagnostic can lie in the same direction as the bug. The scoring loop prunes candidates that cannot reach the required match fraction and reports zero for them, and the "best match" shown on failure reused those pruned numbers. So a scroll that matched at 0.948 against a 0.95 gate was reported as matching nowhere at all, and the message pointed at changed content rather than at a threshold set too tight. The failure path now re-scores without pruning. When a diagnostic says "nothing matched", confirm it can distinguish "nothing" from "not enough" before trusting it.

A tenth trap is that a harness which is only *mostly* controlled will produce failures that look like product defects. The scrolling feature was driven end to end through the real UI against a separate hosted window, and the frames were then dumped and diffed outside the product: the content was shifting rigidly by exactly the wheel distance, so the product was at fault. The same harness also produced two failures that were its own - a selection that snapped to a window including fixed chrome, and a multi-notch step read while the target was still animating. Dumping the frames and measuring them independently is what separated the two, and it is the step worth reaching for first.

## UI render harness

The redesign is verified by a harness that needs no mouse and no desktop, so it runs in the same automated gate:

~~~powershell
dotnet run --project .\tests\PixelPin.UiRender -c Debug
~~~

It hosts the real `App.xaml` resources, renders settings, history, diagnostics, window picker, sticker manager, OCR, beautify, the editor and a sticker at 96 and 192 DPI, writes a text dump of every visible `TextBlock` with its slot, and asserts what a screenshot cannot show:

| Harness check | What it proves |
| --- | --- |
| Slider wiring | A custom `Slider` template can look right and be functionally dead. The check moves `Value` and confirms the thumb follows, then drags the thumb and confirms `Value` changes. |
| Check box mark | The tick is drawn, not just reserved space. |
| Preset swatches | The five beautify presets render five different fills rather than five white squares. |
| Text really drawn | Every visible `TextBlock` has text; a binding that silently produced nothing shows up as an empty line rather than a blank area nobody diffs. |
| Contrast pairs | Each text/surface pair is measured per theme against WCAG 4.5:1, reading the token rather than a literal, so editing a token cannot silently drop below the gate. |
| Editor tool state | Exactly one tool toggle is checked after each selection, and it is the tool that was selected. |
| Overlay bar bounds | The capture bar fits inside the screen and its buttons keep a usable size. The first version did not: it hung off the right edge on the first selection. |
| Sticker chrome | The floating toolbar's buttons are all visible and sized, and the context menu draws its tick and accelerator column. |
| Dark theme | The two token dictionaries declare the same keys, and every window re-rendered in dark actually changes, with near-white pixel counts collapsing. A window that kept a `StaticResource` brush would render unchanged. |
| DWM chrome | `DwmGetWindowAttribute` is asked directly: immersive dark mode must be 1 in dark and 0 in light, and the corner preference must be rounded. WPF cannot draw the title bar, so nothing else can see this. |
| Theme picker | Selecting 深色 switches the theme immediately, and Cancel restores the preference the window opened with. |
| Focus coverage | Every interactive control kind has a focus visual, or a template that reacts to focus. |
| Tab order | The walk drives real `MoveFocus` traversal rather than reading the visual tree, because the two disagree: the settings footer was declared before the form so the `DockPanel` could fill its last child, which put every action ahead of every field until the root became a two-row `Grid`. The first stop must be a field and the last must be the save button. |
| 150% scaling | Each window is measured at its own minimum size scaled by 1.5 and must not overflow. |
| Empty states | The window picker and the OCR window are re-opened with the condition their empty state reacts to, and the explanation must actually appear - and must stay hidden while OCR is still recognizing. |

Three of its findings were worth the harness existing: the capture bar really did hang off screen on the first selection, the settings window really did keep a previewed theme after Cancel, and the "dark render is identical to light" result was the harness's own fault - it was hosting `AppController`, which had already applied the desktop's own dark theme before the baseline renders.

## Defects the gate has caught

- `Enum.TryParse` reading a bare numeric string as the enum's underlying value, so a recorded `Ctrl+1` was saved correctly, displayed correctly, and read back as `Key.Cancel`.
- The editor's colour picker firing `SelectionChanged` during `InitializeComponent`, before `DrawingCanvas` existed, producing a `NullReferenceException` on first open.
- **The window icon resource was never embedded.** `App.xaml`'s implicit `Window` style points `Icon` at `pack://application:,,,/Assets/PixelPin.png`, but the WPF SDK only globs `.xaml` automatically, so a `.png` must be declared as `Resource` explicitly. The resource was therefore absent from the assembly and sealing the style threw `XamlParseException`. The running app survived only because `App.OnStartup` installs a `DispatcherUnhandledException` handler that swallows the fault. This is the strongest argument for the probe loading the real `App.xaml` resources and opening every tool window.

## Manual desktop gate

The following still require human judgement and are intentionally not claimed by automated tests:

| Scenario | Required evidence |
| --- | --- |
| Single monitor | Capture, copy, save, pin, close repeated ten times without orphan windows. |
| Dual monitor with mixed 125% / 150% DPI | Compare drawn selection edges with the exported pixel crop on both displays, and confirm hover highlighting lands on the same window on both. |
| Hotkey latency | Measure F1-to-overlay response over common desktop workloads; target under 300 ms. |
| Hotkey set | Confirm all six configured shortcuts trigger their own action and that none is stolen by another application. |
| Snapping feel | Drag a selection near a window border and confirm it locks on, then disable the option and confirm it does not. |
| Keyboard precision | Nudge a selection onto a known pixel boundary with the arrow keys and confirm the exported crop matches the reported size exactly at 150% scaling. |
| Shortcut recording | Record every one of the six shortcuts by pressing the keys, including a digit key and a punctuation key, restart the app, and confirm each one still fires its own action. |
| Sticker alignment feel | Drag a sticker across several others and confirm it locks onto their edges and centres, that the guides appear on the correct line, and that holding `Alt` suspends the snap. |
| Beautify appearance | Beautify a screenshot of each shape (wide, tall, small) and confirm the shadow is not clipped, the corners are clean, and the exported file opens correctly elsewhere. |
| Sticker stress | Keep at least 20 normal-resolution stickers, then move, scale, rotate, close, and toggle mouse-through. |
| Drag out | `Ctrl+drag` a sticker into a browser and a chat application and confirm the image arrives intact. |
| Sticker manager | Assign two groups, toggle layer/input state through the manager, close one group, and confirm only its stickers close. |
| Chosen window capture | Choose an unobscured target, confirm the picker disappears before the image is taken, and verify a closed target reports a clear failure. |
| Covered window capture | Cover a window with another, then capture the covered one by hovering it in the overlay and by picking it from the list. Confirm the result shows the covered window's own content and not the window in front. |
| Graphics capture on another machine | The graphics route is verified on this host only. Confirm on a machine without a usable GPU (remote desktop, virtual machine) that the WARP fallback still captures, and on a machine with a hardware-accelerated browser that the result is its real content rather than black. |
| Window capture border | Capture a window on Windows 11 and confirm no yellow border flashes around it. On Windows 10 the API to suppress it does not exist, so the flash is expected and must not be mistaken for a defect. |
| Rounded window corners | Capture a window with rounded corners and confirm the corners stay transparent rather than being filled with black squares. |
| Overlay keyboard focus | The overlay is placed without activation, so the smoke test checks that it is the foreground window the moment it appears, and that Esc cancels it without any click. Without this the overlay is visible and topmost but every shortcut goes to the application behind it - a fault that is invisible to anyone who happens to click the overlay before pressing a key. |
| Scrolling in real applications | Scrolling capture is verified against a window the probe controls. Confirm it against a browser, a PDF reader, and a chat application: the wheel must scroll them, and content with repeating structure (a table of identical rows) must be refused rather than stitched wrongly. Applications that render their own scroll animation are the most likely to need a longer settle delay. |
| Scrolling on an infinite feed | Scroll a feed that never ends and confirm the capture stops at the height limit and says so, rather than running until memory runs out. |
| OCR text | Recognise a screenshot containing both Chinese and English, confirm the text is copyable and correctly spaced, and confirm a machine without the language pack reports the missing component instead of an empty result. |
| Settings portability | Export settings, alter a shortcut/cache limit, import the file, review it, then apply it and verify global hotkeys are re-registered. |
| Diagnostics and privacy | Confirm the report contains only local environment metadata and the local data/log folder can be opened and cleared. |
| Power and display changes | Test sleep/resume, display reconnect, portrait monitor, and remote desktop reconnect. |
| Restricted content | Confirm protected windows, UAC desktop, exclusive fullscreen, HDR, and remote capture show an understandable limitation rather than a fabricated result. |

## Explicitly deferred feature gates

- Scrolling capture is verified end to end against a window the probe controls, including the refusal path, and once through the real UI against a separate hosted window (48 frames stitched into 11505 pixels, stopping at the end of the content). What is not verified is per-application scrolling behaviour: whether a specific application scrolls on a wheel event, and how far, is measured from pixels at runtime rather than assumed, so a mismatch degrades to a stated refusal instead of a wrong stitch. Applications that scroll horizontally, or that virtualise their content so rows are not stable between frames, are not supported and will be refused.
- WebP export requires a verified encoder; PNG and JPEG are available now.
- Installer, upgrade, uninstall, signing, SHA-256 release assets, and Windows 10/11 hardware coverage need a release pipeline and target machines.
- OCR is available and verified against the recognizers installed on this machine (`en-US`, `zh-Hans-CN`). Machines without any recognizer language pack report the missing component rather than guessing. OCR of very small or low-contrast text is inherently unreliable, which is why the recognised text stays editable.
- Windows.Graphics.Capture is verified against windows created by the probe itself and against this host's GPU. DRM-protected content and exclusive-fullscreen games cannot be captured by any supported API, so those stay a documented limitation rather than a claimed feature. HDR capture is not exercised here because this host is not in HDR mode; the route reads the composed surface, which is the part that matters, but the tone-mapping path is unproven.
