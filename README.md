# RetroBat Marquee Manager

Marquee Manager for RetroBat, strongly inspired by the original **MarqueeManager** by **Nelfe80**. 
Original Project: [https://github.com/Nelfe80/MarqueeManager](https://github.com/Nelfe80/MarqueeManager)

## Main Features

*   **Dynamic Marquee Display**: Automatically displays marquees for Systems, Collections, and Games on a second screen.
*   **Smart Composition**: If no specific marquee is found, it automatically composes one using the Game Logo + Fanart Background.
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
    *   `MarqueeCompose`: `true` to enable auto-composition (Fanart+Logo), `false` for simple image display.
    *   `AcceptedFormats`: List of supported file types (e.g. `mp4, png, jpg, svg`).

*   **Paths & Patterns**:
    *   `MarqueeFilePath`: Pattern to find game images (e.g. `{system_name}\{game_name}`).
    *   `SystemAliases`: Map short system names to folder names (e.g. `gc=gamecube`).

*   **Scraping Settings**:
    *   `MarqueeAutoScraping`: Enable automatic media scraping. **Priority**: Scraped media takes precedence over default images found in the ROM folder.
    *   `PrioritySource`: Order of preference for scrapers (e.g. `ScreenScraper, arcadeitalia`).

## RetroAchievements Integration

Display live achievement progress overlays on both MPV and DMD displays.

### Supported Emulators

*   **RetroArch (Libretro)**: Automatic detection via `es_launch_stdout.log`.
*   **PCSX2**: Automatic detection via `emulog.txt`.
*   **Other Emulators**: Designed to be extensible via log pattern matching.

### Features

*   **Score Overlay**: Shows current points / total points (e.g. "250/1000 pts")
*   **Achievement Count**: Shows unlocked / total achievements (e.g. "5/35")
*   **Badge Ribbon**: Displays earned achievement badges in a scrolling ribbon
    *   **DMD**: Groups of 4 badges, cycles every 5s with 10s pause
    *   **MPV**: Groups of ~29 badges, cycles every 5s with 2s pause
*   **Notifications**: Full-screen achievement unlock animations with cup + badge
*   **Auto-Refresh**: All overlays update immediately when achievements unlock

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
