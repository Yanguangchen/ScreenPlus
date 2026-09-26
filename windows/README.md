# ScreenPlus for Windows

The Windows version of ScreenPlus. It records your screen, then renders a video that zooms in
where you're working, follows a smoothed cursor, zooms back out when you go idle, and adds motion
blur and click/keyboard sounds. It works the same way as the macOS app and saves recordings in the
same `events.json` format.

Requires Windows 10 version 2004 (May 2020 Update) or later, or Windows 11, on x64 or Arm64.

## Download

Get it from the [latest release](https://github.com/Yanguangchen/ScreenPlus/releases/latest). No GitHub account
is needed. These links always point to the newest version:

| | Installer (recommended) | Portable (no install) |
|---|---|---|
| Most Windows PCs (Intel/AMD) | [ScreenPlus-Setup-x64.exe](https://github.com/Yanguangchen/ScreenPlus/releases/latest/download/ScreenPlus-Setup-x64.exe) | [ScreenPlus-Portable-x64.exe](https://github.com/Yanguangchen/ScreenPlus/releases/latest/download/ScreenPlus-Portable-x64.exe) |
| Windows on Arm (e.g. Snapdragon) | [ScreenPlus-Setup-arm64.exe](https://github.com/Yanguangchen/ScreenPlus/releases/latest/download/ScreenPlus-Setup-arm64.exe) | [ScreenPlus-Portable-arm64.exe](https://github.com/Yanguangchen/ScreenPlus/releases/latest/download/ScreenPlus-Portable-arm64.exe) |

Not sure which one you need? Settings → System → About → **System type** says "x64-based" or "ARM-based".

The installer sets ScreenPlus up for your account (no admin rights needed; the first page also offers
"all users"). It adds ScreenPlus to the Start menu, and to the desktop if you choose, and registers an
uninstaller under Settings → Apps. Running a newer setup upgrades an existing install. The portable exe
runs from anywhere without installing.

Neither is code-signed yet, so Windows SmartScreen may say it "protected your PC" the first time. Click
**More info → Run anyway**.

Every push also builds test versions, which signed-in users can download from the **Actions** tab. They
expire after 90 days.

## Build & run

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download), then from this folder:

```powershell
dotnet run --project src/ScreenPlus          # build and start
.\scripts\publish.ps1                         # build dist\win-x64\ScreenPlus.exe
.\scripts\publish.ps1 -Runtime win-arm64      # for Windows on Arm
.\scripts\publish.ps1 -Installer              # also build dist\ScreenPlus-Setup-<version>-x64.exe
```

The installer needs [Inno Setup 6.3+](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`);
its script is `installer/ScreenPlus.iss`.

To publish a release, bump `<Version>` in `Directory.Build.props`, merge that to `main`, then tag the commit.
For example, for version 0.2.0:

```powershell
git tag windows-v0.2.0
git push origin windows-v0.2.0
```

The **Windows release** workflow then builds and tests both installers and the portable exes, and publishes
them as a GitHub Release. The tag has to match `<Version>`, or the workflow stops.

Visual Studio or Rider can open `ScreenPlus.Windows.slnx`.

## Using it

ScreenPlus starts as a floating toolbar at the bottom of the screen. **Record** counts down from 3 and
records the display the mouse pointer is on. **Stop** (on the toolbar, or in the notification-area
icon's menu) opens the editor. The toolbar never appears in recordings.

Recordings are saved to `Videos\ScreenPlus\`. The editor shows a **live preview**: changing a setting
updates it immediately, and nothing is rendered to a file until you click **Export MP4…** (Ctrl+E).
**Speed** plays the video up to 10× faster (2×, 4×, 6×, 8×, 10×) or up to 10× slower. The preview plays
at that speed, the sidebar shows how long the exported video will be, and click and keyboard sounds
stay at their normal pitch, landing wherever their moment falls in the sped-up or slowed-down video.
**Open…** (Ctrl+O) reopens an earlier recording. Recordings made with the Mac app open too, as long as
Windows can decode their video (HEVC needs the HEVC Video Extensions from the Microsoft Store).

| Shortcut | In the main window |
|---|---|
| Ctrl+N | New recording (shows the toolbar) |
| Ctrl+O | Open a recording |
| Ctrl+E | Export |
| Space | Play / pause the preview |
| Ctrl+Shift+R | Start / stop recording |

Unlike on macOS, Windows doesn't ask for any permissions. Mouse clicks and key presses are logged through
standard low-level input hooks. Only the timing and rough kind of each key press (regular, space,
return, delete) is saved, never which key.

On Windows 10, Windows draws a yellow border around the screen while it's being captured. It's a
privacy indicator and isn't part of the recording.

## Developer commands

```powershell
ScreenPlus.exe --render "$env:USERPROFILE\Videos\ScreenPlus\<Recording>\events.json" out.mp4
ScreenPlus.exe --render "$env:USERPROFILE\Videos\ScreenPlus\<Recording>\events.json" fast.mp4 4    # 4× speed
ScreenPlus.exe --preview-frame "$env:USERPROFILE\Videos\ScreenPlus\<Recording>\events.json" 2.5 frame.png
```

## How it works

The pipeline is the same as the Mac app's; only the platform pieces differ.

| Step | macOS | Windows |
|---|---|---|
| Capture the screen without the cursor | ScreenCaptureKit | Windows.Graphics.Capture (`IsCursorCaptureEnabled = false`) |
| Encode the raw recording | AVAssetWriter (HEVC `.mov`) | Media Foundation sink writer (H.264 `.mp4`, GPU encoder with software fallback) |
| Log mouse and keys | NSEvent monitors | `GetCursorPos` at 120 Hz + low-level mouse/keyboard hooks |
| Plan the camera | `CameraPath` | `CameraPath` (a direct port) |
| Compose frames | Core Image on Metal | SkiaSharp, with motion-blur sub-frames rendered on all cores |
| Preview | AVPlayer + AVVideoComposition | Media Foundation source reader + our own player, audio-clocked |
| Export | AVAssetWriter | Media Foundation sink writer (H.264 + AAC) |
| Keep controls out of the recording | `sharingType = .none` | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` |

Screens wider or taller than H.264 allows (for example 5120×1440) are recorded at half size.

Code layout:

- `src/ScreenPlus.Core`: platform-neutral parts: the recording format, camera path, frame composer,
  sound synthesis and color conversion.
- `src/ScreenPlus`: the WPF app: capture, input tracking, Media Foundation, preview player, UI.
- `tests/ScreenPlus.Core.Tests`: unit tests. These run on any OS (`dotnet test tests/ScreenPlus.Core.Tests`).
- `tests/ScreenPlus.Windows.Tests`: integration tests for encoding, decoding, export, preview, screen
  capture and the UI. These run on Windows only; CI saves the pictures and videos they produce.

The app icon comes from the shared `Resources/AppIcon.png`. To regenerate
`src/ScreenPlus/Assets/AppIcon.ico`, run `dotnet run scripts/build-icon.cs`.
