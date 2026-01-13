# RetroBat Marquee Manager

Marquee Manager for RetroBat, strongly inspired by the original **MarqueeManager** by **Nelfe80**. 
Original Project: [https://github.com/Nelfe80/MarqueeManager](https://github.com/Nelfe80/MarqueeManager)

## Main Features

*   **Dynamic Marquee Display**: Automatically displays marquees for Systems, Collections, and Games on a second screen.
*   **Smart Composition**: If no specific marquee is found, it automatically composes one using the Game Logo + Fanart Background.
*   **Overlay Designer**: Built-in WYSIWYG editor to customize RetroAchievements overlays (DMD/MPV) and layout positions.
*   **Media Support**: Supports PNG, JPG, SVG, and MP4/Video animations via MPV.
*   **Live Real-Time Editor**: Adjust the position of your marquees directly on the screen while the game is running.
*   **User-Friendly Configuration**: Dedicated UI for easy setup.

## Configuration Menu

You can access the configuration interface in two ways:

1.  **From System Tray**: Right-click the RetroBat Marquee Manager icon in the taskbar and select **Configuration**.
2.  **Via Command Line**: Launch the application with the `-menu` argument:
    ```bash
    RetroBatMarqueeManager.exe -menu
    ```

> On the first run (if no `config.ini` exists), the configuration menu will open automatically.

## Overlay Designer

The manager includes a visual **Overlay Designer** to customize the appearance of RetroAchievements overlays:

1.  Open the **RetroBat Marquee Manager Launcher**.
2.  Click on **Design Overlays**.
3.  Adjust positions, sizes, colors, and font sizes for both DMD and MPV displays.
4.  Use **Live Preview** to see changes instantly on your second screen or DMD hardware.

## Live Editor Controls (In-Game)

Adjust your marquee layout instantly without leaving RetroBat.

| Action | Shortcut | Description |
| :--- | :--- | :--- |
| **Move Background** (Fanart/Video Crop) | **Ctrl** + U/J/H/K | Adjust Fanart/Crop position |
| **Move Foreground** (Logo) | **Alt** + U/J/H/K | Adjust Logo position |
| **Zoom Background** (Fanart/Video) | **Ctrl** + N / B | Zoom In / Out |
| **Scale Foreground** (Logo) | **Alt** + N / B | Scale Logo Up / Down |
| **Cycle DMD Style** | `CTRL` + `N` / `B` | Next / Back |
| **Video Adjustment Mode** | `CTRL` + `P` | Toggle video trimming mode |
| **Set Video Start** | `CTRL` + `I` | Mark trimming START point |
| **Set Video End** | `CTRL` + `O` | Mark trimming END point |
| **Exit Adjustment Mode** | `Esc` | Close editor / Stop trimming |
| **Video Editing Mode (Legacy)** | **Ctrl + V** | Toggle legacy video editing |
| **Turbo Mode** | Hold **Shift** + (Ctrl or Alt) | Move/Scale faster |
| **Save** | Automatic | Adjustments are saved per-game |

*Note: U/J/H/K map to keys: U (Up), J (Down), H (Left), K (Right).*


> [!IMPORTANT]
> **Video Regeneration Limitation**: When adjusting video crop (Ctrl+U/J/H/K), ensure the crop area stays WITHIN the video boundaries (no black bars). If you zoom out too much or move the crop area outside the video frame, the video regeneration will fail or produce unexpected results. Always keep "inside" the video content.

> [!CAUTION]
> **Editing Requirements**: Manual editing (Move/Scale/Trim) is **ONLY supported when MPV is enabled**.
> *   For **Video Trimming**: MPV is mandatory.
> *   For **Composed Images**: Manual adjustments (offsets) are only applied when displayed via MPV. If you use DMD display mode (RealDMD), manual edits to composed images will **NOT** be visible. In that case, please create a custom image file in a specific folder instead.

## Configuration (`config.ini`)

Edit `config.ini` in the plugin folder to customize behavior.

*   **General Settings**:
    *   `MarqueeWidth` / `MarqueeHeight`: Resolution of your secondary screen (e.g. 1920x360).
    *   `ScreenNumber`: Monitor index to display on (usually `2`). Set to `false` to disable MPV display entirely (only DMD).
    *   `ScreenNumber`: Monitor index to display on (usually `2`). Set to `false` to disable MPV display entirely (only DMD).
    *   `MarqueeCompose`: `true` to enable auto-composition (Fanart+Logo), `false` for simple image display.
    *   `AcceptedFormats`: List of supported file types (e.g. `mp4, png, jpg, svg`).
    *   **Hardware Acceleration** (Config.ini Only):
        *   `HwDecoding` (MPV): Video decoding (auto, d3d11va, dxva2, no).
        *   `FfmpegHwEncoding`: Video encoding for generation (nvenc, h264_amf, h264_qsv, cpu).

*   **Paths & Patterns**:
    *   `MarqueeFilePath`: Pattern to find game images (e.g. `{system_name}\{game_name}`).
    *   `SystemAliases`: Map short system names to folder names (e.g. `gc=gamecube`).
    *   **Topper Support**: If `MarqueeAutoConvert=false` and `MarqueeCompose=false`, the system will prioritize files ending in `-topper` (e.g. `game-topper.png`, `system-topper.png`) found in your configured theme folders (`logos` for systems, `images` for games). This override applies to both System and Game marquees.

*   **Scraping Settings**:
    *   `MarqueeAutoScraping`: Enable automatic media scraping. **Priority**: Scraped media takes precedence over default images found in the ROM folder.
    *   `PrioritySource`: Order of preference for scrapers (e.g. `ScreenScraper, arcadeitalia`).

## RetroAchievements Integration

Display live achievement progress overlays on both MPV and DMD displays.

### Supported Emulators & Modes

Full support for **Softcore** and **Hardcore** modes across the following systems:

*   **RetroArch (Libretro)**: Automatic detection via `es_launch_stdout.log` (All cores).
*   **PCSX2 (Standalone)**: Automatic detection via `emulog.txt`.
*   **DuckStation (Standalone)**: Automatic detection via `duckstation.log`.
*   **Dolphin (Standalone)**: Automatic detection via `dolphin.log`.
*   **PPSSPP (Standalone)**: Automatic detection via `log.txt`.

> **Hardcore Mode**: Automatically detects if Hardcore Mode is enabled in the emulator.
> *   Displays a distinct **"HC"** indicator on the Marquee.
> *   Validates achievements against the Hardcore leaderboard dates.
> *   Uses specific visual styling (Gold borders/overlays).

### Features

*   **Score Overlay**: Shows current points / total points (e.g. "250/1000 pts")
*   **Achievement Count**: Shows unlocked / total achievements (e.g. "5/35")
*   **Badge Ribbon**: Displays earned achievement badges in a scrolling ribbon
    *   **DMD**: Smoothly cycling achievement ribbon (automatically optimized for DMD resolution)
    *   **MPV**: High-resolution achievement ribbon (dynamically scaled for the marquee)
*   **Notifications**: Full-screen achievement unlock animations with cup + badge
*   **Scrolling Narration**: Supports long narrative text with automatic scrolling on both DMD and MPV.
*   **Auto-Refresh**: All overlays update immediately when achievements unlock
*   **Smart Cache**: Automatic cleanup of temporary files and expired preview data.

### Configuration

Add to `config.ini`:

```ini
; Generate your Web API Key at: https://retroachievements.org/settings
RetroAchievementsWebApiKey=YOUR_API_KEY_HERE

; Enable overlays (comma-separated: score,badges,count)
MarqueeRetroAchievementsOverlays=score,badges,count
```

### Display Layout

**MPV (Marquee):**
```
[5/35]                              [Score: 250/1000 pts]


          GAME LOGO


[badge][badge][badge][badge][badge]...
```

**DMD:**
- Game Start: Count 5s → Score 5s → Badge cycle
- Achievement Unlock: Cup+Badge 10s → Count 5s → Score 5s → Badge cycle (updated)

### Customization

**Fonts**: Place custom `.ttf` font in `medias/retroachievements/fonts/` for retro styling

**Badges**: 
- Unlocked badges display in color
- Locked badges display as grayscale with 50% opacity
- Badges sorted by DisplayOrder (RetroAchievements API)

**Custom Styling**:
- **DMD Narration**: Uses the same logic as scrolling text. You can customize `TextColor` and `FontSize` in the Overlay Designer (item `rp_narration`).
- **MPV Narration**: Includes a semi-transparent background box and scrolling support for long text.

### Tiered Image Resolution (Icons & Badges)

To ensure high performance and offline reliability, the manager uses a 3-tier system:
1.  **Emulator Cache**: Directly accesses images already downloaded by the emulator (e.g., PCSX2's `achievement_images` or RetroArch's `thumbnails`).
2.  **App Cache**: Stores images in `medias/retroachievements/` after the first download.
3.  **API Download**: Automatically fetches missing images from RetroAchievements.org.

## Installation
1.  Place the folder in `RetroBat\plugins\`.
2.  Enable the plugin in RetroBat settings (if applicable) or ensure it runs on startup.
3.  Check `logs/` if you encounter issues.

## Credits / Tools

This project uses the following open-source tools and APIs:

*   **MPV** (Media Player): [https://mpv.io](https://mpv.io)
*   **ImageMagick** (Image Processing): [https://imagemagick.org](https://imagemagick.org)
*   **dmd-extensions** (DMD Hardware Support by freezy): [https://github.com/freezy/dmd-extensions](https://github.com/freezy/dmd-extensions)
*   **FFmpeg** (Video Processing): [https://ffmpeg.org](https://ffmpeg.org)
*   **libzedmd** (Real DMD Support): [https://github.com/PPUC/libzedmd](https://github.com/PPUC/libzedmd)
*   **ScreenScraper**: [https://www.screenscraper.fr](https://www.screenscraper.fr)
*   **ArcadeItalia**: [http://adb.arcadeitalia.net](http://adb.arcadeitalia.net)
*   **libmpv** (Shared Library for MPV): [https://sourceforge.net/projects/mpv-player-windows/files/libmpv/](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/)

## Zaccaria Pinball & Virtual DMD Setup

For users of **Zaccaria Pinball**, it is possible to create a virtual secondary screen to serve as a DMD (Dot Matrix Display).

1.  Download and install the [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver).
2.  Configure a virtual display with a resolution of **1920x1080**.
3.  Set this virtual display as a secondary screen in Windows settings.
4.  In Zaccaria Pinball, configure the DMD to display on this specific virtual screen.
5.  **RetroBat Marquee Manager** can then mirror this screen if needed, or simply let Zaccaria manage the display directly.

> [!IMPORTANT]
> If you want to **keep MPV active** (e.g. to display a Marquee on another screen) while using Zaccaria, you must ensure the "Suspend MPV" option is disabled in your configuration command.
> The command format in `config.ini` is: `command;HandleDMD;SuspendMPV`.
>
> **Default (MPV Suspended):**
> `zaccariapinball=dmdext.exe mirror --source=screen --position !POSITION! -d {DmdModel};False;True`
>
> **To Keep MPV Active (Change last value to False):**
> `zaccariapinball=dmdext.exe mirror --source=screen --position !POSITION! -d {DmdModel};False;False`
