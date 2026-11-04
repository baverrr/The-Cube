# The Cube plan

Goal: a Windows 10/11 app that behaves like KDE Plasma's Desktop Cube effect (KWin `cube`) as set up
on the author's Plasma 6.6 machine: 4 virtual desktops in one row, Meta+C toggles the cube,
Ctrl+F1..F4 switch desktops, KWin cube defaults, 2.83x animation duration (566 ms).

The KWin effect is GPL-licensed. The Cube reimplements its **behaviour** from scratch; no KWin
code is copied.

## Behaviour to mirror

| KWin cube | The Cube |
|---|---|
| Meta+C toggles | Win+C (configurable) |
| Opens by zooming out from the current desktop to `FaceDistance × 1.75`, tilted 20° down | same |
| Closes by zooming into the chosen face | same |
| Drag to rotate (azimuth free, elevation ±30°), wheel zooms (12 %/notch) | same |
| ← / → rotate one face (ignored while animating) | same |
| Enter / Return / Space switch to the face in front | same |
| Click a face → switch to it; click empty space → face in front | same |
| Esc closes without switching | same |
| < 3 desktops → placeholder with "Add Virtual Desktop" | same |
| Background `#212427` or a skybox image | same |
| Optional screen-edge activation | same (off by default) |
| Each monitor shows its own part of every desktop | same |
| Live window thumbnails on every face | current desktop: live grab; others: per-window `PrintWindow` composite, falling back to the last snapshot |

## Phases

1. **Core + scaffold** ✅ — solution, platform-neutral cube math (geometry, camera, animation,
   controller, hotkeys, settings) with unit tests, CI on `windows-latest`, MIT license.
2. **Windows virtual desktops** — COM interop with Windows' own desktops: count, current, switch,
   create, window→desktop, change notifications. Documented `IVirtualDesktopManager` plus the
   undocumented internal interfaces, with a GUID table per Windows build (10 22H2, 11 23H2/24H2/25H2)
   and a clear "unsupported build" message instead of a crash.
3. **Desktop images** — capture the current desktop per monitor before the overlay shows; compose
   other desktops from wallpaper + `PrintWindow` window images in z-order; cache snapshots on switch.
4. **Cube overlay** — borderless top-most window per monitor, WPF `Viewport3D` cube (vertical 60° FOV
   converted to WPF's horizontal FOV), hit testing, keyboard, drag, wheel, placeholder view.
5. **App shell** — tray icon + menu (open cube, settings, start with Windows, quit), global hotkeys
   (`RegisterHotKey`, low-level hook fallback when Windows already owns Win+C), screen edges,
   settings window, single instance, `%APPDATA%\The Cube\settings.json`.
6. **Release** — README with real screenshots from a Windows PC, installer or portable exe via
   GitHub Releases, security review, public repo `baverrr/The-Cube`.
