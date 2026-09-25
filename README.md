# ScreenPlus

A Screen Studio–style screen recorder for macOS: it records your screen, then renders a video that
zooms in where you're working, follows a smoothed cursor, zooms back out when you go idle, and adds
motion blur.

There's also a **Windows version** in [`windows/`](windows/README.md), with the same features and
recording format.

## Build & run

```bash
./scripts/build-app.sh --run      # builds build/ScreenPlus.app and opens it
```

On first use, macOS asks for **Screen Recording** permission. Grant it, then relaunch the app.
Recordings are saved to `~/Movies/ScreenPlus/`.

To work in Xcode, open `Package.swift`. For permission prompts to work, run the bundled app built by the script.

The app icon uses the transparent artwork in `Resources/AppIcon.png`, derived from the original
`icon.png`. Both build scripts generate `Resources/AppIcon.icns` with all standard and Retina sizes.
To regenerate only the icon, run `./scripts/build-icon.sh`.

After you stop recording, the editor opens with a **live preview**. Changing a setting updates it
immediately, and nothing is rendered to a file until you click **Export MP4…** (⌘E).
Use **File → Open Recording…** (⌘O) to reopen an earlier recording.

Mouse clicks and key presses get synthesized click/keyboard sounds in the preview and export; each
can be switched off in the editor's **Sound** section. Recording typing needs **Accessibility** access
(System Settings → Privacy & Security → Accessibility). Only the timing and rough kind of each key press
(regular, space, return, delete) is saved, never which key.

## Distributing

```bash
./scripts/package-dmg.sh              # Developer ID signed + notarized .dmg, for sharing
./scripts/package-dmg.sh --unsigned   # packaging test only; blocked by Gatekeeper on other Macs
```

The signed build needs a paid Apple Developer Program membership, a **Developer ID Application**
certificate, and a notarytool keychain profile. The one-time setup steps are at the top of the script.

### Developer commands

```bash
.build/release/ScreenPlus --render ~/Movies/ScreenPlus/<Recording>/events.json out.mp4
.build/release/ScreenPlus --preview-frame ~/Movies/ScreenPlus/<Recording>/events.json 2.5 frame.png
```

## How it works

1. **Record.** `ScreenRecorder` captures the display with ScreenCaptureKit, *without* the cursor, to `raw.mov`.
   `InputTracker` logs mouse position (120 Hz) and clicks to `events.json`.
2. **Plan the camera.** `CameraPath` turns clicks and mouse movement into zoom segments. Each segment
   starts slightly *before* the activity, since rendering happens afterwards. Critically damped
   springs then animate the zoom, the camera centre (with a dead zone), and the cursor.
3. **Compose.** `FrameComposer` builds each output frame. The live preview uses it through an
   `AVVideoComposition`, and export (`Renderer`) uses it frame by frame, so the preview matches the export.
4. **Render.** `Renderer` crops and scales each frame with Core Image on Metal, draws the cursor,
   averages several sub-frames when things move (motion blur), and composites the result onto a
   rounded, shadowed card over a gradient.
