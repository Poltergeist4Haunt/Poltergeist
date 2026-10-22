# Poltergeist for Windows

The Windows version of Poltergeist: screenshots, screen recordings and file uploads to your Haunt Image Host, from the tray.
Same look as the Mac app (Haunt's dark theme, pink accent, the ghost icon), with a slim custom title bar on every window.

## Run it

Unzip and run `Poltergeist.exe` (Windows 10 1809 or newer, 64-bit; .NET is built in, nothing to install).
Windows SmartScreen may warn about an unsigned app the first time: *More info → Run anyway*.

1. The first launch opens **Settings → Account**: paste your Haunt API key (dashboard → create a key, enable *Image Host: Upload + Read*).
2. The ghost sits in the tray (click it, or right-click). Default hotkeys:

| Shortcut | Action |
| --- | --- |
| Ctrl+Shift+4 | Capture area (frozen, slightly dimmed screen while you drag) |
| Ctrl+Shift+3 | Capture the full screen your mouse is on |
| Ctrl+Shift+5 | Capture a window (click it) |
| Ctrl+Shift+2 | Capture area and open the editor |
| Ctrl+Shift+6 / 7 / 8 | Record area / full screen / window (press again to stop and upload) |

All of them can be changed in *Settings → General → Hotkeys*.

## What it does

- **Uploads** with progress, cancel and retry; link copied to the clipboard (or the image itself), preview popup with a Copy button that fades away, Windows notifications (with Copy / Open buttons), sounds, history of recent uploads in the tray menu. Drag files onto *Settings → Upload*, or use *Upload files…* / *Paste clipboard image* in the tray menu. Photo metadata (EXIF) is stripped by default.
- **Screenshot editor**: arrow, curved arrow, pen, marker, box, circle, text (double-click to edit), blur (pixelated or soft), pasted/dropped pictures, any colour, three sizes, undo/redo, zoom and pan. *Upload* has a ▾ with *Copy to clipboard* and *Save as…*.
- **Screen recording**: area, window or full screen at 15–60 fps and 2–35 Mbps, desktop audio and/or microphone on **separate tracks**, pause/resume, mute-mic, discard. The little control bar is hidden from the video.
- **Video editor**: trim, split, cut out clips, speed changes (¼×–4×), mute the desktop or microphone track for any stretch, audio fades, arrows/boxes/circles/text/blur/pictures/GIFs/videos laid over the picture with fade in/out, keyframes to move them over time, zoom-in camera moves, frame stepping (← →, or , .). Export re-encodes only when needed (plain trims are instant).
- **Settings**: Upload, Gallery, Account, General, Recordings, Effects, Embed, Domain and Stats tabs. Sounds can be Windows' own or any file you pick (capture, success and error sounds). Effects adds rounded corners, a border, a drop shadow and a watermark to screenshots. The gallery shows previews of your recent uploads, and Stats shows how much you use the app.
- The API key is stored encrypted for your Windows account (DPAPI). Settings live in `%APPDATA%\Poltergeist`.

## ffmpeg

Recording and the video editor use ffmpeg. It isn't bundled: Poltergeist offers to download it the first time you need it
(about 30 MB, stored in `%APPDATA%\Poltergeist\tools`). If you already have `ffmpeg.exe` and `ffprobe.exe`, put them next to
`Poltergeist.exe` or on your PATH and it uses those. Screenshots and plain uploads work without it.

## Build it yourself

- On Windows: `.\build.ps1` (needs the .NET 8 SDK) → `dist\Poltergeist-win-x64.zip`
- On macOS/Linux: `./build.sh` → same zip (cross-compiled)
- Open `Poltergeist.sln` in Visual Studio 2022 to work on it.

Layout: `src/Poltergeist.Core` (all logic and drawing, cross-platform), `src/Poltergeist` (the WPF app), `tests/Core.Harness`
(`dotnet run --project tests/Core.Harness`, 88 checks of the timeline, renderer, ffmpeg export and upload client).

## Known limits

- The video preview uses Windows' media player, which plays only the first audio track (the desktop audio): mutes on the microphone
  and the sound of layered videos are applied on export but can't be heard in the preview. Blur boxes show as a grey placeholder in
  the preview and are blurred in the export. Layered videos show a still in the preview.
- Recording captures through ffmpeg's `gdigrab`; on setups with different display scaling per monitor, record on the primary monitor
  for exact framing.
- The Embed and Domain tabs are saved locally only: Haunt's API has no endpoint for them yet (same as the Mac app).
