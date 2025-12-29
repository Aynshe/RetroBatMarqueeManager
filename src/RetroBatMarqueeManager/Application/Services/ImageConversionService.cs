using RetroBatMarqueeManager.Core.Interfaces;
using RetroBatMarqueeManager.Core.Models.RetroAchievements;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RetroBatMarqueeManager.Application.Services
{
    public class ImageConversionService
    {
        private readonly IConfigService _config;
        private readonly IProcessService _processService;
        private readonly ILogger<ImageConversionService> _logger;

        public ImageConversionService(IConfigService config, IProcessService processService, ILogger<ImageConversionService> logger)
        {
            _config = config;
            _processService = processService;
            _logger = logger;
            
            EnsureCacheDirectory();
        }

        private void EnsureCacheDirectory()
        {
            try
            {
                if (!Directory.Exists(_config.CachePath))
                {
                    Directory.CreateDirectory(_config.CachePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to create cache directory {_config.CachePath}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Processes an image (SVG, PNG, JPG) to match Marquee requirements:
        /// - Resized to MarqueeWidth x MarqueeHeight
        /// - Transparent background replaced with MarqueeBackgroundColor (Black)
        /// - Centered and Extented to fill the area
        /// </summary>
        public string ProcessImage(string sourcePath, string subFolder = "")
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return sourcePath;
            
            // BYPASS: Do not attempt to convert Video files or GIFs (let MPV handle them)
            var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            var skippedFormats = new HashSet<string> { "mp4", "avi", "mkv", "webm", "mov", "gif" };
            if (skippedFormats.Contains(ext))
            {
                // _logger.LogDebug($"Skipping conversion for video/animated file: {sourcePath}");
                return sourcePath;
            }

            // Determines target path
            // Cache Structure: _cache/{subFolder}/{filename}
            // If subFolder is empty, use root _cache (or throw?)
            
            string fileName = Path.GetFileNameWithoutExtension(sourcePath);
            string cacheDir = _config.CachePath;
            
            if (!string.IsNullOrEmpty(subFolder))
            {
                cacheDir = Path.Combine(_config.CachePath, subFolder);
            }
            
            // Ensure directory
            if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

            // EN: Use simple fixed filename for logo cache
            // FR: Utiliser un nom de fichier simple fixe pour le cache logo
            string uniqueName = $"{fileName}.png";
            
            // Systems folder uses same simple naming
            if (subFolder == "systems")
            {
                uniqueName = $"{fileName}.png";
            }
            
            string targetPath = Path.Combine(cacheDir, uniqueName);

            // If target exists, return it (unless source is newer? optimize later)
            if (File.Exists(targetPath))
            {
                 // Check dates?
                 if (File.GetLastWriteTime(sourcePath) <= File.GetLastWriteTime(targetPath))
                 {
                     return targetPath;
                 }
            }

            return ConvertImage(sourcePath, targetPath) ? targetPath : sourcePath;
        }

        private bool ConvertImage(string source, string target)
        {
            try
            {
                // Ensure output directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                var w = _config.MarqueeWidth;
                var h = _config.MarqueeHeight;
                var bg = _config.MarqueeBackgroundColor;

                var startInfo = new ProcessStartInfo
                {
                    FileName = _config.IMPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_config.IMPath)
                };

                // Check for Custom Command
                string customCmd = _config.IMConvertCommand;
                var sourceExt = Path.GetExtension(source).TrimStart('.').ToLowerInvariant();
                
                if (sourceExt == "svg" && !string.IsNullOrEmpty(_config.IMConvertCommandSVG))
                {
                    customCmd = _config.IMConvertCommandSVG;
                }

                if (!string.IsNullOrEmpty(customCmd))
                {
                     // Substitutions
                     var processedCmd = customCmd
                        .Replace("{source}", source)
                        .Replace("{target}", target)
                        .Replace("{width}", w.ToString())
                        .Replace("{height}", h.ToString())
                        .Replace("{background}", bg);

                     startInfo.Arguments = processedCmd;
                }
                else
                {
                    // Default Logic
                    var args = new List<string>
                    {
                        "-background", "none",
                        "-density", "300",
                        source,
                        "-resize", $"{w}x{h}",
                        "-background", bg,
                        "-flatten",
                        "-gravity", "center",
                        "-extent", $"{w}x{h}",
                        target
                    };
                    
                    // Assign ArgumentList
                    foreach(var arg in args) startInfo.ArgumentList.Add(arg);
                }

                using var process = Process.Start(startInfo);
                if (process == null) return false;

                process.WaitForExit(10000); 

                if (process.ExitCode != 0)
                {
                    var err = process.StandardError.ReadToEnd();
                    _logger.LogError($"ImageMagick Error: {err}");
                    return false;
                }

                // Robust Check: Wait for file to be truly ready (size stable + readable)
                if (WaitForFile(target))
                {
                    return true;
                }
                
                _logger.LogError($"File {target} was not ready after conversion.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error converting image {source}: {ex.Message}");
                return false;
            }
        }
        
       /// <summary>
        /// Processes an image for DMD (128x32 usually)
        /// FR: Traite une image pour le DMD (128x32 généralement)
        /// </summary>
        public string? ProcessDmdImage(string sourcePath, string subFolder, string? system = null, string? gameName = null, int offsetX = 0, int offsetY = 0)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return sourcePath;
            if (string.IsNullOrEmpty(gameName)) gameName = Path.GetFileNameWithoutExtension(sourcePath);
            string sanitizedName = Sanitize(gameName);

            var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (ext == "gif") return sourcePath; // Passthrough existing GIFs
            
            bool isVideo = new[] { "mp4", "avi", "webm", "mkv" }.Contains(ext);

            // User Request: medias\cache\dmd\[GenerateMarqueeVideoFolder]\[system]\[GameName].gif
            // _config.CachePath is .../medias/_cache
            
            string dmdCacheDir = Path.Combine(_config.CachePath, "dmd");
            
            // If system is provided, we use the new structure requested by user
            // Fix: Only use "generated_videos" folder if it IS actually a video.
            // Static images (logos) should typically go to defaults or standard system folder?
            // Actually, if system is present but it's a static image, let's keep legacy behavior (cache/dmd/[system] or defaults?)
            // The previous logic put EVERYTHING (logos too) in generated_videos if system was present.
            if (!string.IsNullOrEmpty(system) && isVideo)
            {
                var folderName = _config.GenerateMarqueeVideoFolder;
                if (string.IsNullOrWhiteSpace(folderName)) folderName = "generated_videos";
                
                // Override subFolder logic if system is present (assuming subFolder "defaults" is legacy/fallback)
                dmdCacheDir = Path.Combine(dmdCacheDir, folderName, system);
            }
            else if (!string.IsNullOrEmpty(system))
            {
                 // Static image with system context -> cache/dmd/[system]
                 dmdCacheDir = Path.Combine(dmdCacheDir, system);
            }
            else if (!string.IsNullOrEmpty(subFolder)) 
            {
                // Legacy path logic
                dmdCacheDir = Path.Combine(dmdCacheDir, subFolder);
            }
            
            if (!Directory.Exists(dmdCacheDir)) Directory.CreateDirectory(dmdCacheDir);

            // If video, target is GIF. If static, target is PNG.
            string targetExt = isVideo ? ".gif" : ".png";
            string uniqueName = $"{sanitizedName}{targetExt}";
            string targetPath = Path.Combine(dmdCacheDir, uniqueName);

            if (File.Exists(targetPath))
            {
                 if (File.GetLastWriteTime(sourcePath) <= File.GetLastWriteTime(targetPath))
                 {
                     return targetPath;
                 }
            }

            if (isVideo)
            {
                // If conversion succeeds, return target. 
                // If fails, we CANNOT return sourcePath (mp4) because dmdext crashes.
                
                if (ConvertVideoToGif(sourcePath, targetPath)) // Assuming ConvertVideoToGif is a method in this class
                {
                    return targetPath;
                }
                
                // Fallback: If conversion failed (timeout?), but we have an old file, use it!
                if (File.Exists(targetPath))
                {
                    _logger.LogWarning($"DMD Video Conversion failed for {sourcePath}, using existing cached file (stale): {targetPath}");
                    return targetPath;
                }
                
                _logger.LogError($"DMD Video Conversion failed and no cache available for: {sourcePath}");
                return null;
            }

            return ConvertDmdImage(sourcePath, targetPath) ? targetPath : sourcePath;
        }

        private string? _ffmpegPath = null;
        private string FindFfmpeg()
        {
            if (_ffmpegPath != null) return _ffmpegPath;

            // 1. Check tools/ffmpeg/ffmpeg.exe (Standard)
            // _config.MPVPath is .../tools/mpv/mpv.exe
            var toolsDir = Path.GetDirectoryName(Path.GetDirectoryName(_config.MPVPath));
            if (string.IsNullOrEmpty(toolsDir)) toolsDir = "tools";

            var pathsToCheck = new[] {
                Path.Combine(toolsDir, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(toolsDir, "mpv", "ffmpeg.exe"), // Sometimes bundled
                "ffmpeg.exe", // System path
                "ffmpeg" 
            };

            foreach(var p in pathsToCheck)
            {
                if (File.Exists(p)) 
                {
                    _ffmpegPath = p;
                    return p;
                }
                // Check if system command works? (too slow)
            }
            
            // Allow system path implicitly if not found explicitly?
            return "ffmpeg";
        }

        private bool ConvertVideoToGif(string source, string target)
        {
            try
            {
                var w = _config.DmdWidth;
                var h = _config.DmdHeight;
                var ffmpeg = FindFfmpeg();

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_config.MPVPath) // Use MPV dir as working dir just in case
                };

                // FFmpeg High Quality GIF:
                // fps=15: Limit framerate
                // scale=...: Resize to cover (forcing aspect ratio if needed or padding)
                // We want to FILL 128x32.
                // scale=128:32:force_original_aspect_ratio=decrease,pad=128:32:(ow-iw)/2:(oh-ih)/2
                // OR simple scale=128:32 (stretch)
                // Let's use simple stretch for DMD usually, or pad.
                // Let's try Aspect Ratio Preserving Pad:
                // scale=128:32:force_original_aspect_ratio=decrease,pad=128:32:-1:-1:color=black
                
                // High Quality (Palettegen) - Restored because fast filter looked bad
                // We now read stderr so this shouldn't hang anymore.
                // Filter: FPS -> Scale -> Pad -> Split -> PaletteGen -> PaletteUse
                string filterObj = $"fps=25,scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse";

                var args = new List<string>
                {
                    "-y", // Overwrite
                    "-i", source,
                    "-vf", filterObj,
                    "-loop", "0", // Infinite loop
                    target
                };
                
                foreach(var arg in args) startInfo.ArgumentList.Add(arg);
                
                // FR: Mise à jour du log pour refléter la réalité (High Quality)
                _logger.LogInformation($"Converting Video to GIF (High Quality): {source} -> {target}");
                
                using var process = new Process { StartInfo = startInfo };
                
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.RedirectStandardOutput = true; // UseOutput?
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger.LogWarning($"[FFMPEG STDERR] {e.Data}"); };

                process.Start();
                process.BeginErrorReadLine();
                
                if (process == null) return false;

                if (!process.WaitForExit(15000)) // Reduced to 15s
                {
                     _logger.LogWarning($"Video conversion timed out (15s) for {source}. Killing process.");
                     try { process.Kill(); } catch { }
                     return false;
                }

                if (process.ExitCode != 0)
                {
                    // Look out! StandardError might have been consumed if redirected?
                    // But we set RedirectStandardError=true.
                    // The original code was reading it.
                    var err = process.StandardError.ReadToEnd();
                    _logger.LogError($"FFmpeg Error ({process.ExitCode}): {err}");
                    
                    return false;
                }

                return WaitForFile(target);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error converting Video to GIF {source}: {ex.Message}");
                // If finding ffmpeg failed (System.ComponentModel.Win32Exception)
                if (ex.Message.Contains("find the file"))
                {
                    _logger.LogError("FFmpeg executable not found. Please install FFmpeg in 'tools/ffmpeg/ffmpeg.exe' or add to PATH.");
                }
                return false;
            }
        }

        /// <summary>
        /// Generates a Marquee-style video (Wide) from a standard game video by cropping and overlaying the logo.
        /// FR: Génère une vidéo format Marquee (Large) depuis une vidéo standard en tronquant et superposant le logo.
        /// </summary>
        public string? GenerateMarqueeVideo(string sourceVideo, string logoPath, string system, string gameName)
        {
            if (string.IsNullOrEmpty(sourceVideo) || !File.Exists(sourceVideo)) return null;

            try
            {
                // User Request: videos in medias/[GenerateMarqueeVideoFolder]/[system]/[game].mp4
                // _config.CachePath is .../medias/_cache. We want .../medias/[GenerateMarqueeVideoFolder]/[system]
                var subFolder = _config.GenerateMarqueeVideoFolder;
                if (string.IsNullOrWhiteSpace(subFolder)) subFolder = "generated_videos"; // Fallback to safe default if empty
                
                var parentDir = Directory.GetParent(_config.CachePath);
                if (parentDir == null) return null; // Should not happen if CachePath is valid

                string videoCacheDir = Path.Combine(parentDir.FullName, subFolder, system);
                if (!Directory.Exists(videoCacheDir)) Directory.CreateDirectory(videoCacheDir);

                string targetPath = Path.Combine(videoCacheDir, $"{gameName}.mp4");

                // Check cache reuse
                if (File.Exists(targetPath))
                {
                    if (File.GetLastWriteTime(sourceVideo) <= File.GetLastWriteTime(targetPath))
                    {
                        var logoTime = File.Exists(logoPath) ? File.GetLastWriteTime(logoPath) : DateTime.MinValue;
                        if (logoTime <= File.GetLastWriteTime(targetPath))
                        {
                            return targetPath;
                        }
                    }
                }

                _logger.LogInformation($"[VideoGen] Generating marquee video for {gameName}...");

                var ffmpeg = FindFfmpeg();
                var mw = _config.MarqueeWidth;
                var mh = _config.MarqueeHeight;

                // Heuristic for cropping:
                // We want to scale to width, then crop height.
                // To keep the character (often in the lower-middle), we crop with an offset.
                // y = (scaled_h - mh) * 0.5 (Center) -> User wants "character zone". 
                // Let's use 60% down for slightly lower focus.
                
                // Overlay Logo: 80% of Marquee Height
                int logoH = (int)(mh * 0.8);
                
                // FFmpeg filter complex:
                // [0:v] : Video input
                // [1:v] : Logo input (optional)
                // 1. Scale video to marquee width (aspect ratio preserved)
                // 2. Crop to marquee height (using character-centric vertical offset)
                // 3. Scale logo to 80% height
                // 4. Overlay logo at top-left (with slight padding)

                string filter;
                var inputs = new List<string> { "-i", sourceVideo };
                bool hasLogo = !string.IsNullOrEmpty(logoPath) && File.Exists(logoPath);

                if (hasLogo)
                {
                    inputs.Add("-i");
                    inputs.Add(logoPath);
                    // Filter: 
                    // [0:v] scale=W:H (Increase to cover) -> Crop to W:H (Center-ish) -> Format YUV420P
                    // [1:v] scale=-1:LogoH (Scale logo)
                    // [v][logo] overlay=10:10 
                    
                    filter = $"[0:v]scale={mw}:{mh}:force_original_aspect_ratio=increase,crop={mw}:{mh}:(iw-ow)/2:(ih-oh)*0.4,format=yuv420p[base];" +
                             $"[1:v]scale=-1:{logoH}[logo];" +
                             $"[base][logo]overlay=10:10,format=yuv420p";
                }
                else
                {
                    filter = $"scale={mw}:{mh}:force_original_aspect_ratio=increase,crop={mw}:{mh}:(iw-ow)/2:(ih-oh)*0.4,format=yuv420p";
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_config.MPVPath)
                };

                var args = new List<string> { "-y" }; // Overwrite
                args.AddRange(inputs);
                
                // Use -filter_complex for multiple inputs (video + logo), -vf for single input
                if (hasLogo)
                {
                    args.Add("-filter_complex"); args.Add(filter);
                }
                else
                {
                    args.Add("-vf"); args.Add(filter);
                }
                args.Add("-c:v"); args.Add("libopenh264");
                args.Add("-preset"); args.Add("veryfast"); // Balance speed/quality for on-the-fly generation
                args.Add("-crf"); args.Add("23"); // Decent quality
                args.Add("-an"); // Remove audio (Marquee is silent)
                args.Add(targetPath);

                foreach (var arg in args) startInfo.ArgumentList.Add(arg);

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return null;
                    
                    // Consume stderr to prevent hang - Log as Warning to be visible
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger.LogWarning($"[FFmpeg Gen] {e.Data}"); };
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(30000)) // 30s timeout for video generation
                    {
                        _logger.LogWarning($"[VideoGen] Timeout generating video for {gameName}");
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError($"[VideoGen] FFmpeg failed with code {process.ExitCode}");
                        return null;
                    }
                }

                if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                {
                    _logger.LogInformation($"[VideoGen] Successfully generated: {targetPath}");
                    return targetPath;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[VideoGen] Error generating marquee video: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// EN: Generate marquee video with custom offsets for crop/zoom/logo position
        /// FR: Générer vidéo marquee avec offsets personnalisés pour crop/zoom/position logo
        /// </summary>
        public string? GenerateMarqueeVideoWithOffsets(string sourceVideo, string logoPath, string system, string gameName, 
            int cropX, int cropY, double zoom, int logoX, int logoY, double logoScale, 
            double startTime = 0.0, double endTime = 0.0)
        {
            if (string.IsNullOrEmpty(sourceVideo) || !File.Exists(sourceVideo)) return null;

            try
            {
                var subFolder = _config.GenerateMarqueeVideoFolder;
                if (string.IsNullOrWhiteSpace(subFolder)) subFolder = "generated_videos";
                
                var parentDir = Directory.GetParent(_config.CachePath);
                if (parentDir == null) return null;

                string videoCacheDir = Path.Combine(parentDir.FullName, subFolder, system);
                if (!Directory.Exists(videoCacheDir)) Directory.CreateDirectory(videoCacheDir);

                string targetPath = Path.Combine(videoCacheDir, $"{gameName}.mp4");

                _logger.LogInformation($"[VideoGen] Generating marquee video with custom offsets for {gameName}...");
                _logger.LogInformation($"[VideoGen] Offsets: Crop({cropX},{cropY}) Zoom={zoom:F2} Logo({logoX},{logoY}) Scale={logoScale:F2} Time({startTime:F1}-{endTime:F1})");

                var ffmpeg = FindFfmpeg();
                var mw = _config.MarqueeWidth;
                var mh = _config.MarqueeHeight;

                // EN: Calculate crop position with offsets - default is (iw-ow)/2:(ih-oh)*0.4
                // FR: Calculer position crop avec offsets - défaut est (iw-ow)/2:(ih-oh)*0.4
                // IMPORTANT: Invert signs to match ImageMagick preview behavior
                // ImageMagick moves the background, FFmpeg moves the crop window
                var invertedCropX = -cropX;
                var invertedCropY = -cropY;
                var cropXExpr = invertedCropX >= 0 ? $"(iw-ow)/2+{invertedCropX}" : $"(iw-ow)/2{invertedCropX}";
                var cropYExpr = invertedCropY >= 0 ? $"(ih-oh)*0.4+{invertedCropY}" : $"(ih-oh)*0.4{invertedCropY}";
                
                // EN: Apply zoom by scaling before crop
                // FR: Appliquer zoom en scalant avant crop
                var scaleW = zoom > 0 ? (int)(mw * zoom) : mw;
                var scaleH = zoom > 0 ? (int)(mh * zoom) : mh;

                // EN: Calculate logo size with scale (default is 80% of marquee height)
                // FR: Calculer taille logo avec scale (défaut est 80% de hauteur marquee)
                int logoH = (int)(mh * 0.8 * logoScale);

                string filter;
                var inputs = new List<string>();
                
                // Input Seeking (faster) - apply -ss before -i
                if (startTime > 0)
                {
                    inputs.Add("-ss");
                    inputs.Add(startTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                
                // Standard end time handling (can be input or output, input is faster if copy, but we re-encode)
                // If using -ss before input, -to is relative to input start (so it becomes Duration if used as output option, or absolute timestamp?)
                // Actually -ss before -i seeks. Then timestamps reset to 0. So -to should be duration (EndTime - StartTime).
                // Or better, use -t (duration).
                string? durationArg = null;
                if (endTime > startTime && endTime > 0)
                {
                    durationArg = (endTime - startTime).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                inputs.Add("-i");
                inputs.Add(sourceVideo);
                
                bool hasLogo = !string.IsNullOrEmpty(logoPath) && File.Exists(logoPath);

                if (hasLogo)
                {
                    inputs.Add("-i");
                    inputs.Add(logoPath);
                    
                    filter = $"[0:v]scale={scaleW}:{scaleH}:force_original_aspect_ratio=increase,crop={mw}:{mh}:{cropXExpr}:{cropYExpr},format=yuv420p[base];" +
                             $"[1:v]scale=-1:{logoH}[logo];" +
                             $"[base][logo]overlay={logoX}:{logoY},format=yuv420p";
                    
                    _logger.LogInformation($"[VideoGen] FFmpeg filter: {filter}");
                }
                else
                {
                    filter = $"scale={scaleW}:{scaleH}:force_original_aspect_ratio=increase,crop={mw}:{mh}:{cropXExpr}:{cropYExpr},format=yuv420p";
                    _logger.LogInformation($"[VideoGen] FFmpeg filter: {filter}");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_config.MPVPath)
                };

                var args = new List<string> { "-y" };
                args.AddRange(inputs);
                
                // Apply Duration if set
                if (durationArg != null)
                {
                    args.Add("-t");
                    args.Add(durationArg);
                }

                
                if (hasLogo)
                {
                    args.Add("-filter_complex"); args.Add(filter);
                }
                else
                {
                    args.Add("-vf"); args.Add(filter);
                }
                args.Add("-c:v"); args.Add("libopenh264");
                args.Add("-preset"); args.Add("veryfast");
                args.Add("-crf"); args.Add("23");
                args.Add("-an");
                args.Add(targetPath);

                foreach (var arg in args) startInfo.ArgumentList.Add(arg);

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return null;
                    
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) _logger.LogWarning($"[FFmpeg Gen] {e.Data}"); };
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(30000))
                    {
                        _logger.LogWarning($"[VideoGen] Timeout generating video for {gameName}");
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError($"[VideoGen] FFmpeg failed with code {process.ExitCode}");
                        return null;
                    }
                }

                if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                {
                    _logger.LogInformation($"[VideoGen] Successfully generated with custom offsets: {targetPath}");
                    return targetPath;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[VideoGen] Error generating video with offsets: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Hybrid Loader: Use System.Drawing for bitmap formats, fallback to ImageMagick for SVG.
        /// </summary>
        private Bitmap? LoadBitmapWithSvgSupport(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".svg")
            {
                // Convert SVG to PNG in memory stream using ImageMagick
                try 
                {
                    // Temporary file for conversion output
                    string tempPng = Path.Combine(Path.GetTempPath(), $"dmd_svg_{Guid.NewGuid()}.png");
                    
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = _config.IMPath,
                        Arguments = $"\"{path}\" -background none -resize {_config.DmdWidth}x{_config.DmdHeight} \"{tempPng}\"", 
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null) return null;
                        process.WaitForExit(5000);
                        if (process.ExitCode == 0 && File.Exists(tempPng))
                        {
                            // Load the converted PNG
                            // Copy to MemoryStream to avoid file locking issues when deleting
                            using var fs = new FileStream(tempPng, FileMode.Open, FileAccess.Read);
                            var ms = new MemoryStream();
                            fs.CopyTo(ms);
                            ms.Position = 0;
                            
                            try { File.Delete(tempPng); } catch {}
                            
                            return new Bitmap(ms);
                        }
                    }
                    _logger.LogError($"Failed to convert SVG: {path}");
                    return null;
                }
                catch (Exception ex)
                {
                     _logger.LogError($"SVG Conversion Error for {path}: {ex.Message}");
                     return null;
                }
            }
            else
            {
                // Native Load
                // Use FileStream to avoid locking the file if possible, or just copy to MemoryStream
                // Bitmap(path) sometimes locks the file.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Position = 0;
                return new Bitmap(ms);
            }
        }

        private bool ConvertDmdImage(string source, string target)
        {
            try
            {
                var w = _config.DmdWidth;
                var h = _config.DmdHeight;

                // Use ImageMagick CLI: Match Python's opaque background approach
                var bgColor = _config.MarqueeBackgroundColor; // e.g. "Black" or "#000000"
                if (string.IsNullOrEmpty(bgColor) || bgColor.Equals("None", StringComparison.OrdinalIgnoreCase)) bgColor = "Black";
                
                var args = new List<string>
                {
                    "-density", "96",          // Force 96 DPI
                    "-background", bgColor,    // Use configured background (usually Black)
                    source,
                    "-resize", $"{w}x{h}",     // Force fit inside WxH while preserving aspect ratio
                    "-gravity", "center", 
                    "-extent", $"{w}x{h}",     // Pad with background color to fill WxH
                    "-background", bgColor,    // Ensure extent uses background
                    "-flatten",                // Flatten transparency onto background
                    "-alpha", "off",           // Remove alpha channel entirely (force opaque)
                    "-units", "PixelsPerInch",
                    "-density", "96",
                    target
                };

                var startInfo = new ProcessStartInfo
                {
                    FileName = _config.IMPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_config.IMPath)
                };

                foreach (var arg in args) startInfo.ArgumentList.Add(arg);

                using var process = Process.Start(startInfo);
                if (process == null) return false;

                process.WaitForExit(5000);

                if (process.ExitCode != 0)
                {
                    _logger.LogError($"ImageMagick DMD conversion error: {process.StandardError.ReadToEnd()}");
                    return false;
                }

                return File.Exists(target);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error converting DMD image {source}: {ex.Message}");
                return false;
            }
        }

        public string? GenerateMpvAchievementOverlay(string badgePath, string title, string description, int points, string subFolder = "overlays")
        {
            try
            {
                int w = _config.MarqueeWidth;
                int h = _config.MarqueeHeight;
                if (w <= 0) w = 1920; 
                if (h <= 0) h = 360;

                string cacheDir = Path.Combine(_config.CachePath, subFolder);
                if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

                // Unique filename based on title content to cache it
                string cleanTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()));
                string targetPath = Path.Combine(cacheDir, $"ach_overlay_{cleanTitle}_{points}.png");

                // Reuse cache? (Maybe not if badge changes? Badge is usually stable per achievement)
                if (File.Exists(targetPath)) return targetPath;

                using (var bitmap = new Bitmap(w, h))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent); // Transparent background

                    // Design:
                    // [ Badge (Left) ]  [ Title (Top) ]
                    //                   [ Desc (Bottom) ]
                    // + Cup icon or points somewhere?

                    // Let's mimic a "Toast" notification style at the bottom or top?
                    // User said "The badge implies... MPV can display everything at same time."
                    // Let's create a nice panel.

                    int margin = 20;
                    int badgeSize = (int)(h * 0.8); // 80% height
                    int badgeX = margin;
                    int badgeY = (h - badgeSize) / 2;

                    // Load Badge
                    using (var badgeImg = LoadBitmapWithSvgSupport(badgePath))
                    {
                        if (badgeImg != null)
                        {
                            g.DrawImage(badgeImg, badgeX, badgeY, badgeSize, badgeSize);
                        }
                    }

                    // Text Area
                    int textX = badgeX + badgeSize + margin;
                    int textWidth = w - textX - margin;
                    int textHeight = h - (margin * 2);

                    // Fonts
                    // Use standard fonts, ideally loaded from resources if available, but system fallback
                    using (var titleFont = new Font("Segoe UI", 48, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var descFont = new Font("Segoe UI", 32, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var pointsFont = new Font("Segoe UI", 36, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brushWhite = new SolidBrush(Color.White))
                    using (var brushGold = new SolidBrush(Color.Gold))
                    using (var brushGray = new SolidBrush(Color.LightGray))
                    {
                        // Draw Title
                        g.DrawString(title, titleFont, brushGold, new RectangleF(textX, margin + 40, textWidth, 60));

                        // Draw Description
                        g.DrawString(description, descFont, brushWhite, new RectangleF(textX, margin + 110, textWidth, 80));

                        // Draw Points
                        string pointsText = $"{points} pts";
                        g.DrawString(pointsText, pointsFont, brushGold, new RectangleF(textX, margin + 200, textWidth, 50));
                    }
                    
                    bitmap.Save(targetPath, ImageFormat.Png);
                }
                
                return targetPath;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ImageConversion] GenerateMpvAchievementOverlay Error: {ex.Message}");
                return null;
            }
        }

        public List<byte[]> GenerateDmdScrollingTextFrames(string text, int width, int height, bool useGrayscale)
        {
            var frames = new List<byte[]>();
            try
            {
                // Create a bitmap that holds the entire text
                // Estimate width: Chars * WidthPerChar (approx 8px?)
                // Use Graphics.MeasureString to be precise
                
                using (var measureBmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(measureBmp))
                using (var font = new Font("Arial", 10, FontStyle.Bold)) // Adjust font size for 32px height
                {
                    var textSize = g.MeasureString(text, font);
                    int textTotalWidth = (int)textSize.Width + 20; // Padding
                    int bmpWidth = Math.Max(width, textTotalWidth + width); // Extra width for scrolling in/out

                    // Create the full wide bitmap
                    using (var fullTextBmp = new Bitmap(bmpWidth, height))
                    using (var gText = Graphics.FromImage(fullTextBmp))
                    {
                        gText.Clear(Color.Black);
                        gText.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit; // Pixel look
                        
                        // Draw Text starting at 'width' (enters from right)
                        using (var brush = new SolidBrush(Color.White))
                        {
                            // Center vertically
                            float y = (height - textSize.Height) / 2;
                            gText.DrawString(text, font, brush, width, y);
                        }

                        // Now slice it into frames
                        // Scroll speed: 2 pixels per frame? 
                        // Frame rate: assume 30ms per frame
                        
                        int maxScroll = bmpWidth - width; 
                        // Actually we want to scroll untill text disappears or stops? 
                        // "Scrolling right to left" -> Starts with empty (or text at right edge), text moves left, exits left.
                        // Width of movement = TextWidth + ScreenWidth.

                         for (int x = 0; x < textTotalWidth + width; x += 2) // Step 2 pixels
                         {
                             using (var frameBmp = new Bitmap(width, height))
                             using (var gFrame = Graphics.FromImage(frameBmp))
                             {
                                 // Draw crop from source
                                 gFrame.DrawImage(fullTextBmp, new Rectangle(0, 0, width, height), new Rectangle(x, 0, width, height), GraphicsUnit.Pixel);
                                 
                                 // Convert to DMD bytes
                                 frames.Add(GetRawDmdBytes(frameBmp, width, height, useGrayscale));
                             }
                         }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ImageConversion] GenerateDmdScrollingTextFrames Error: {ex.Message}");
            }
            return frames;
        }


        /// <summary>
        /// Generates a composite image for DMD (128x32 usually)
        /// Fanart + Logo with Resize
        /// </summary>
        public string ProcessDmdComposition(string fanartPath, string logoPath, string subFolder, int offsetX = 0, int offsetY = 0, int logoOffsetX = 0, int logoOffsetY = 0, bool isPreview = false)
        {
            try
            {
                var w = _config.DmdWidth;
                var h = _config.DmdHeight;
                
                string dmdCacheDir = Path.Combine(_config.CachePath, "dmd");
                if (!string.IsNullOrEmpty(subFolder)) dmdCacheDir = Path.Combine(dmdCacheDir, subFolder);
                if (!Directory.Exists(dmdCacheDir)) Directory.CreateDirectory(dmdCacheDir);

                var logoName = Path.GetFileNameWithoutExtension(logoPath);
                // EN: Use simple fixed filename (offsets stored in offsets.json)
                // FR: Utiliser un nom de fichier simple fixe (offsets stockés dans offsets.json)
                string uniqueName = $"{logoName}_composed.png";

                if (isPreview) uniqueName = $"preview_dmd_{logoName}_{DateTime.Now.Ticks}.png";

                string targetPath = Path.Combine(dmdCacheDir, uniqueName);
                if (!isPreview && File.Exists(targetPath))
                {
                    _logger.LogInformation($"[DMD CACHE REUSE] Found existing: {uniqueName}");
                    return targetPath;
                }

                _logger.LogInformation($"Generating DMD composition (Fanart Off: {offsetX},{offsetY}) [Preview:{isPreview}]: {targetPath}");

                var layout = _config.MarqueeLayout.ToLowerInvariant();
                var logoH = (int)(h * 0.9);
                
                var args = new List<string>
                {
                    "-density", "96",
                    "-size", $"{w}x{h}", "xc:black", // Opaque black canvas (matches Python)
                    "-units", "PixelsPerInch"
                };

                // 1. Fanart Layer + Dark Overlay
                if (!string.IsNullOrEmpty(fanartPath) && File.Exists(fanartPath))
                {
                    args.Add("(");
                    args.Add(fanartPath);
                    args.Add("-resize"); args.Add($"{w}x{h}^");
                    args.Add("-gravity"); args.Add("Center");
                    args.Add("-extent"); args.Add($"{w}x{h}");
                    args.Add(")");
                    args.Add("-gravity"); args.Add("Center");
                    args.Add("-geometry"); args.Add($"{(offsetX >= 0 ? "+" : "")}{offsetX}{(offsetY >= 0 ? "+" : "")}{offsetY}");
                    args.Add("-composite");

                    // Dark overlay
                    args.Add("(");
                    args.Add("-size"); args.Add($"{w}x{h}");
                    args.Add("xc:black");
                    args.Add("-alpha"); args.Add("set");
                    args.Add("-channel"); args.Add("A");
                    args.Add("-evaluate"); args.Add("set"); args.Add("50%");
                    args.Add(")");
                    args.Add("-gravity"); args.Add("Center");
                    args.Add("-composite");
                }

                // 2. Gradient Layer
                if (layout.Contains("gradient"))
                {
                    if (layout == "gradient-left")
                    {
                        args.Add("(");
                        args.Add("-size"); args.Add($"{h}x{w}");
                        args.Add("gradient:black-none");
                        args.Add("-rotate"); args.Add("-90");
                        args.Add(")");
                        args.Add("-gravity"); args.Add("Center");
                        args.Add("-composite");
                    }
                    else if (layout == "gradient-right")
                    {
                        args.Add("(");
                        args.Add("-size"); args.Add($"{h}x{w}");
                        args.Add("gradient:none-black");
                        args.Add("-rotate"); args.Add("-90");
                        args.Add(")");
                        args.Add("-gravity"); args.Add("Center");
                        args.Add("-composite");
                    }
                    else if (layout == "gradient-standard")
                    {
                        var halfW = w / 2;
                        var remainderW = w - halfW;

                        args.Add("(");
                        args.Add("(");
                        args.Add("-size"); args.Add($"{h}x{halfW}");
                        args.Add("gradient:black-none");
                        args.Add("-rotate"); args.Add("-90");
                        args.Add(")");
                        args.Add("(");
                        args.Add("-size"); args.Add($"{h}x{remainderW}");
                        args.Add("gradient:none-black");
                        args.Add("-rotate"); args.Add("-90");
                        args.Add(")");
                        args.Add("+append");
                        args.Add(")");
                        args.Add("-gravity"); args.Add("Center");
                        args.Add("-composite");
                    }
                }

                // 3. Logo Layer (with transparent background)
                if (File.Exists(logoPath))
                {
                    var logoW = (int)(w * 0.9);
                    args.Add("(");
                    args.Add("-background"); args.Add("none"); // CRITICAL: Transparent background for SVG/PNG
                    args.Add(logoPath);
                    args.Add("-resize"); args.Add($"{logoW}x{logoH}>");
                    args.Add(")");

                    if (layout == "gradient-left")
                    {
                        args.Add("-gravity"); args.Add("West");
                        // Base padding +2, plus offset
                        int fx = 2 + logoOffsetX;
                        int fy = 0 + logoOffsetY;
                        args.Add("-geometry"); args.Add($"{(fx >= 0 ? "+" : "")}{fx}{(fy >= 0 ? "+" : "")}{fy}");
                    }
                    else if (layout == "gradient-right")
                    {
                        args.Add("-gravity"); args.Add("East");
                        int fx = 2 + logoOffsetX; // Normally logic might invert X for East? Let's assume standard offset adds to X.
                        int fy = 0 + logoOffsetY;
                        args.Add("-geometry"); args.Add($"{(fx >= 0 ? "+" : "")}{fx}{(fy >= 0 ? "+" : "")}{fy}");
                    }
                    else
                    {
                        args.Add("-gravity"); args.Add("Center");
                        int fx = 0 + logoOffsetX;
                        int fy = 0 + logoOffsetY;
                        args.Add("-geometry"); args.Add($"{(fx >= 0 ? "+" : "")}{fx}{(fy >= 0 ? "+" : "")}{fy}");
                    }

                    args.Add("-composite");
                }

                args.Add(targetPath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = _config.IMPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_config.IMPath)
                };

                foreach (var arg in args) startInfo.ArgumentList.Add(arg);

                using var process = Process.Start(startInfo);
                if (process == null) return logoPath;

                process.WaitForExit(10000);

                if (WaitForFile(targetPath))
                {
                    return targetPath;
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogError($"ImageMagick DMD Composition Error: {process.StandardError.ReadToEnd()}");
                    return logoPath;
                }

                return File.Exists(targetPath) ? targetPath : logoPath;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in ProcessDmdComposition: {ex.Message}");
                return logoPath;
            }
        }
        
        public string GenerateComposition(string fanartPath, string logoPath, string subFolder, int offsetX = 0, int offsetY = 0, int logoOffsetX = 0, int logoOffsetY = 0, double fanartScale = 1.0, double logoScale = 1.0, bool isPreview = false)
        {
            if (string.IsNullOrEmpty(fanartPath) || !File.Exists(fanartPath)) return logoPath;
            if (string.IsNullOrEmpty(logoPath) || !File.Exists(logoPath)) return fanartPath; 
            
            // Wait for inputs to be ready
            if (!WaitForFile(fanartPath) || !WaitForFile(logoPath))
            {
                _logger.LogWarning("Composition inputs not ready/readable. Returning logo path.");
                return logoPath;
            } 

            string cacheDir = _config.CachePath;
             if (!string.IsNullOrEmpty(subFolder))
            {
                cacheDir = Path.Combine(_config.CachePath, subFolder);
            }
             if (!Directory.Exists(cacheDir)) Directory.CreateDirectory(cacheDir);

            string fileName = Path.GetFileNameWithoutExtension(logoPath);
            string targetPath;
            
            if (isPreview)
            {
                // Preview Mode: Use Circular Buffer (Max 3 slots) to prevent cache flooding
                targetPath = GetPreviewSlot(cacheDir, fileName);
            }
            else
            {
                // EN: Permanent Mode - Use simple fixed filename with offset metadata validation
                // FR: Mode permanent - Utiliser un nom de fichier simple fixe avec validation des métadonnées d'offsets
                string simpleName = $"{fileName}_composed.png";
                targetPath = Path.Combine(cacheDir, simpleName);
                
                if (File.Exists(targetPath))
                {
                    // EN: Validate cached file has same offsets as current request
                    // FR: Valider que le fichier en cache a les mêmes offsets que la requête actuelle
                    bool offsetsMatch = ValidateOffsetMetadata(targetPath, offsetX, offsetY, fanartScale, logoOffsetX, logoOffsetY, logoScale);
                    
                    if (offsetsMatch)
                    {
                        _logger.LogInformation($"[CACHE REUSE] Found existing with matching offsets: {simpleName}");
                        return targetPath;
                    }
                    else
                    {
                        // EN: Offsets changed - Delete old cache and metadata, regenerate
                        // FR: Offsets changés - Supprimer ancien cache et métadonnées, regénérer
                        _logger.LogInformation($"[CACHE INVALID] Offsets changed, deleting old cache: {simpleName}");
                        try
                        {
                            File.Delete(targetPath);
                            var metadataPath = Path.ChangeExtension(targetPath, ".json");
                            if (File.Exists(metadataPath)) File.Delete(metadataPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"[CACHE INVALID] Failed to delete old cache: {ex.Message}");
                        }
                    }
                }
                
                _logger.LogInformation($"[CACHE NEW] Creating: {simpleName}");
            }

            try
            {
                var w = _config.MarqueeWidth;
                var h = _config.MarqueeHeight;
                var bg = _config.MarqueeBackgroundColor; // Use configured background color
                var logoH = (int)(h * 0.9);
                
                // Canvas approach for full control of Fanart placement (Offset)
                // 1. Create Canvas with configured background color
                // 2. Composite Fanart (Resized) at Center + Offset
                // 3. (Optional) Composite Gradient
                // 4. Composite Logo (Resized) at Center + Logo Offset (or Auto Gravity)

                var layout = _config.MarqueeLayout.ToLowerInvariant();
                
                // Apply fanart with scale
                var fanartW = (int)(w * fanartScale);
                var args = new List<string>
                {
                    "-size", $"{w}x{h}", $"xc:{bg}", // Base Canvas
                    
                    "(", 
                    fanartPath, 
                    "-resize", $"{fanartW}x",              
                    ")",
                    "-gravity", "Center",
                    "-geometry", $"{ (offsetX >= 0 ? "+" : "") }{offsetX}{ (offsetY >= 0 ? "+" : "") }{offsetY}",
                    "-composite"
                };

                // Gradient Logic
                // Gradient Logic
                if (layout == "gradient-left")
                {
                    // Goal: Black on Left -> Transparent on Right
                    // We generate a Vertical Gradient (Top->Bottom) and rotate it -90 degrees.
                    // Vertical Size must be swapped: H x W so that after 90deg rotation it becomes W x H.
                    
                    // gradient:black-none means Top=Black, Bottom=None
                    // Rotate -90: Top becomes Left (Black), Bottom becomes Right (None).
                    
                    args.Add("(");
                    args.Add("-size"); args.Add($"{h}x{w}"); // Swapped dimensions for rotation
                    args.Add("gradient:black-none");
                    args.Add("-rotate"); args.Add("-90");
                    args.Add(")"); 
                    args.Add("-gravity"); args.Add("Center");
                    args.Add("-composite");
                }
                else if (layout == "gradient-right")
                {
                     // Goal: Transparent on Left -> Black on Right
                     // gradient:none-black means Top=None, Bottom=Black
                     // Rotate -90: Top becomes Left (None), Bottom becomes Right (Black).
                     
                    args.Add("(");
                    args.Add("-size"); args.Add($"{h}x{w}"); // Swapped dimensions for rotation
                    args.Add("gradient:none-black");
                    args.Add("-rotate"); args.Add("-90");
                    args.Add(")"); 
                    args.Add("-gravity"); args.Add("Center");
                    args.Add("-composite");
                }
                else if (layout == "gradient-standard")
                {
                     // Bilateral Gradient: Black Left -> Transparent Center -> Black Right
                     // We construct this by appending two gradients horizontally.
                     // Part 1 (Left Half): Black->None. (Rotated 90 Left)
                     // Part 2 (Right Half): None->Black. (Rotated 90 Left)
                     
                     var halfW = w / 2;
                     var remainderW = w - halfW;

                     args.Add("(");
                        // Left Half
                        args.Add("(");
                        args.Add("-size"); args.Add($"{h}x{halfW}"); // H x HalfW (Swapped)
                        args.Add("gradient:black-none");
                        args.Add("-rotate"); args.Add("-90");
                        args.Add(")");
                        
                        // Right Half
                        args.Add("(");
                        args.Add("-size"); args.Add($"{h}x{remainderW}"); // H x RemainderW (Swapped)
                        args.Add("gradient:none-black");
                        args.Add("-rotate"); args.Add("-90");
                        args.Add(")");

                        // Join them
                        args.Add("+append");
                     args.Add(")");
                     
                     args.Add("-gravity"); args.Add("Center");
                     args.Add("-composite");
                }

                // Logo Logic with scale
                var logoW = (int)(w * 0.9 * logoScale); // Max 90% of canvas width * scale
                var scaledLogoH = (int)(logoH * logoScale);
                args.Add("(");
                args.Add("-background"); args.Add("none");
                args.Add(logoPath);
                args.Add("-resize"); args.Add($"{logoW}x{scaledLogoH}"); // Fit within bounds (allow both shrink and enlarge)
                args.Add(")");

                // Position Logo based on Layout
                if (layout == "gradient-left")
                {
                    args.Add("-gravity"); args.Add("West");
                    args.Add("-geometry"); args.Add("+20+0"); // Slight padding from left edge
                }
                else if (layout == "gradient-right")
                {
                    args.Add("-gravity"); args.Add("East");
                    args.Add("-geometry"); args.Add("+20+0"); // Slight padding from right edge
                }
                else
                {
                    // EN: Use NorthWest for video offset preview (absolute coords like FFmpeg), Center for normal image editing
                    // FR: Utiliser NorthWest pour preview offset vidéo (coords absolues comme FFmpeg), Center pour édition image normale  
                    // Video mode uses "video_preview" subfolder, image mode uses system subfolder (e.g., "gw")
                    string logoGravity = (subFolder == "video_preview") ? "NorthWest" : "Center";
                    args.Add("-gravity"); args.Add(logoGravity);
                    args.Add("-geometry"); args.Add($"{ (logoOffsetX >= 0 ? "+" : "") }{logoOffsetX}{ (logoOffsetY >= 0 ? "+" : "") }{logoOffsetY}");
                }
                
                args.Add("-composite");

                args.Add("-strip");
                args.Add("-depth"); args.Add("8");
                args.Add("-colorspace"); args.Add("sRGB");
                
                args.Add(targetPath);

                _logger.LogInformation($"Generating composition (Fanart Off: {offsetX},{offsetY} Scale: {fanartScale:F2} | Logo Off: {logoOffsetX},{logoOffsetY} Scale: {logoScale:F2}) [Preview:{isPreview}]: {targetPath}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = _config.IMPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_config.IMPath)
                };
                
                 foreach(var arg in args) startInfo.ArgumentList.Add(arg);

                using var process = Process.Start(startInfo);
                if (process == null) return logoPath; 

                process.WaitForExit(15000);
                
                if (WaitForFile(targetPath))
                {
                    // Clean up old permanent files when using preview mode (hotkeys)
                    // EN: Remove old composed files to prevent cache pollution
                    // FR: Supprimer les anciens fichiers composés pour éviter la pollution du cache
                    if (isPreview)
                    {
                        try
                        {
                            // Pattern: filename_composed_*.png
                            var pattern = $"{fileName}_composed_*.png";
                            var directory = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(directory))
                            {
                                var oldFiles = Directory.GetFiles(directory, pattern);
                                foreach (var oldFile in oldFiles)
                                {
                                    try
                                    {
                                        File.Delete(oldFile);
                                        _logger.LogDebug($"Deleted old permanent cache file: {Path.GetFileName(oldFile)}");
                                    }
                                    catch { } // Silent fail - non-critical
                                }
                            }
                        }
                        catch { } // Silent fail - non-critical
                    }
                    
                    // EN: Save offset metadata for permanent files (enables cache validation on next load)
                    // FR: Sauvegarder les métadonnées d'offsets pour les fichiers permanents (permet validation du cache au prochain chargement)
                    if (!isPreview)
                    {
                        SaveOffsetMetadata(targetPath, offsetX, offsetY, fanartScale, logoOffsetX, logoOffsetY, logoScale);
                    }
                    
                    return targetPath;
                }
                
                if (process.ExitCode != 0)
                {
                     _logger.LogError($"ImageMagick Composition Error: {process.StandardError.ReadToEnd()}");
                     return logoPath;
                }

                return File.Exists(targetPath) ? targetPath : logoPath;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating composition: {ex.Message}");
                return logoPath;
            }
        }
        private string GetPreviewSlot(string cacheDir, string fileName)
        {
            // Circular buffer of 3 files
            for (int i = 0; i < 3; i++)
            {
                var path = Path.Combine(cacheDir, $"{fileName}_preview_{i}.png");
                if (!File.Exists(path)) return path;

                try 
                {
                    // Check if we can write/delete
                    File.Delete(path); 
                    return path;
                }
                catch 
                { 
                    // Locked, try next
                }
            }
            // Fallback if all locked (unlikely with just 1 MPV instance)
            return Path.Combine(cacheDir, $"{fileName}_preview_overflow_{DateTime.Now.Ticks % 100}.png");
        }

        /// <summary>
        /// Waits for a file to be accessible (not locked) and stable (size > 0).
        /// Retries for up to 2 seconds.
        /// </summary>
        private bool WaitForFile(string path)
        {
            if (!File.Exists(path)) return false;
            
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length > 0) return true;
                    }
                }
                catch (IOException)
                {
                    // File is locked, wait and retry
                    System.Threading.Thread.Sleep(200);
                }
            }
            return false;
        }

        public async Task<byte[]> GetRawDmdBytes(string imagePath, int width, int height, bool grayscale = true)
        {
            if (!File.Exists(imagePath)) return Array.Empty<byte>();

            return await Task.Run(() => 
            {
                try
                {
                    using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var originalBitmap = new Bitmap(fs); 
                    return GetRawDmdBytes(originalBitmap, width, height, grayscale);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"GetRawDmdBytes file error: {ex.Message}");
                    return Array.Empty<byte>();
                }
            });
        }

        public byte[] GetRawDmdBytes(Bitmap originalInfo, int width, int height, bool grayscale = true)
        {
            try
            {
                Bitmap resized;
                bool needsDispose = false;

                // Only resize if necessary to avoid any interpolation artifacts
                if (originalInfo.Width == width && originalInfo.Height == height)
                {
                    // Image is already the correct size, use it directly
                    // Note: originalInfo might be a frame from a multi-frame image, 
                    // so we should probably clone it if we want to be safe, but typically it's fine.
                    resized = originalInfo; 
                }
                else
                {
                    // Resize with pixel-perfect mode (no smoothing) for DMD
                    resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    needsDispose = true;
                    using (var g = Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;  // Pixel-perfect for DMD
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.SmoothingMode = SmoothingMode.None;
                        g.DrawImage(originalInfo, 0, 0, width, height);
                    }
                }

                try
                {
                    // Extract bytes
                    var result = new byte[width * height * (grayscale ? 1 : 3)];
                    
                    var data = resized.LockBits(
                        new Rectangle(0, 0, width, height), 
                        ImageLockMode.ReadOnly, 
                        PixelFormat.Format32bppArgb);

                    int pixelSize = 4; // ARGB
                    byte[] buffer = new byte[data.Stride * height];
                    Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                    resized.UnlockBits(data);

                    int outIdx = 0;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int inIdx = (y * data.Stride) + (x * pixelSize);
                            
                            // B, G, R, A (ARGB format)
                            byte b = buffer[inIdx];
                            byte g = buffer[inIdx + 1];
                            byte r = buffer[inIdx + 2];
                            byte a = buffer[inIdx + 3];
                            
                            // CRITICAL: Handle transparency - treat transparent pixels as black
                            // This prevents white backgrounds on transparent PNGs/SVGs
                            if (a < 128) // Semi-transparent or fully transparent
                            {
                                r = 0;
                                g = 0;
                                b = 0;
                            }
                                                        if (grayscale)
                                {
                                    // Rec. 709 Weights (Modern Standard)
                                    // Attenduate slightly (0.85) to avoid "blinding whites" on physical DMDs
                                    byte gray = (byte)((r * 0.2126 + g * 0.7152 + b * 0.0722) * 0.85);
                                    result[outIdx++] = gray;
                                }
                            else
                            {
                                // RGB
                                result[outIdx++] = r;
                                result[outIdx++] = g;
                                result[outIdx++] = b;
                            }
                        }
                    }

                    return result;
                }
                finally
                {
                    if (needsDispose) resized.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"GetRawDmdBytes conversion error: {ex.Message}");
                return Array.Empty<byte>();
            }
        }
    
    /// <summary>
    /// EN: Save offset metadata for a composed file (only if custom offsets)
    /// FR: Sauvegarder les métadonnées d'offsets pour un fichier composé (seulement si offsets personnalisés)
    /// </summary>
    private void SaveOffsetMetadata(string composedFilePath, int fanartOffsetX, int fanartOffsetY, double fanartScale, int logoOffsetX, int logoOffsetY, double logoScale)
    {
        try
        {
            // EN: Only save metadata if offsets are not default (0,0,1.0)
            // FR: Sauvegarder les métadonnées seulement si offsets ne sont pas par défaut (0,0,1.0)
            bool isDefaultOffsets = 
                fanartOffsetX == 0 && fanartOffsetY == 0 && Math.Abs(fanartScale - 1.0) < 0.01 &&
                logoOffsetX == 0 && logoOffsetY == 0 && Math.Abs(logoScale - 1.0) < 0.01;
            
            var metadataPath = Path.ChangeExtension(composedFilePath, ".json");
            
            if (isDefaultOffsets)
            {
                // EN: Delete metadata file if it exists (offsets reset to default)
                // FR: Supprimer le fichier de métadonnées s'il existe (offsets réinitialisés par défaut)
                if (File.Exists(metadataPath))
                {
                    File.Delete(metadataPath);
                    _logger.LogInformation($"[OFFSET METADATA] Deleted (offsets reset to default): {Path.GetFileName(metadataPath)}");
                }
                return;
            }
            
            // EN: Save metadata for custom offsets
            // FR: Sauvegarder les métadonnées pour offsets personnalisés
            var metadata = new
            {
                fanartOffsetX,
                fanartOffsetY,
                fanartScale,
                logoOffsetX,
                logoOffsetY,
                logoScale
            };
            
            var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metadataPath, json);
            _logger.LogInformation($"[OFFSET METADATA] Saved custom offsets: {Path.GetFileName(metadataPath)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[OFFSET METADATA] Failed to save: {ex.Message}");
        }
    }
    
    /// <summary>
    /// EN: Check if offset metadata matches current offsets
    /// FR: Vérifier si les métadonnées d'offsets correspondent aux offsets actuels
    /// </summary>
    private bool ValidateOffsetMetadata(string composedFilePath, int fanartOffsetX, int fanartOffsetY, double fanartScale, int logoOffsetX, int logoOffsetY, double logoScale)
    {
        try
        {
            var metadataPath = Path.ChangeExtension(composedFilePath, ".json");
            
            // EN: Check if requested offsets are default
            // FR: Vérifier si les offsets demandés sont par défaut
            bool requestedIsDefault = 
                fanartOffsetX == 0 && fanartOffsetY == 0 && Math.Abs(fanartScale - 1.0) < 0.01 &&
                logoOffsetX == 0 && logoOffsetY == 0 && Math.Abs(logoScale - 1.0) < 0.01;
            
            if (!File.Exists(metadataPath))
            {
                // EN: No metadata = default offsets assumed. Valid if requested offsets are also default.
                // FR: Pas de métadonnées = offsets par défaut supposés. Valide si offsets demandés sont aussi par défaut.
                if (requestedIsDefault)
                {
                    _logger.LogInformation($"[OFFSET METADATA] No metadata (default offsets), requested offsets are also default - cache valid");
                    return true;
                }
                else
                {
                    _logger.LogInformation($"[OFFSET METADATA] No metadata (default offsets), but custom offsets requested - cache invalid");
                    return false;
                }
            }
            
            var json = File.ReadAllText(metadataPath);
            var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(json);
            
            if (metadata == null) return false;
            
            // Compare all offset values
            bool matches = 
                metadata.TryGetValue("fanartOffsetX", out var fx) && fx.GetInt32() == fanartOffsetX &&
                metadata.TryGetValue("fanartOffsetY", out var fy) && fy.GetInt32() == fanartOffsetY &&
                metadata.TryGetValue("fanartScale", out var fs) && Math.Abs(fs.GetDouble() - fanartScale) < 0.01 &&
                metadata.TryGetValue("logoOffsetX", out var lx) && lx.GetInt32() == logoOffsetX &&
                metadata.TryGetValue("logoOffsetY", out var ly) && ly.GetInt32() == logoOffsetY &&
                metadata.TryGetValue("logoScale", out var ls) && Math.Abs(ls.GetDouble() - logoScale) < 0.01;
            
            if (!matches)
            {
                _logger.LogInformation($"[OFFSET METADATA] Offsets changed - Fanart: {metadata["fanartOffsetX"].GetInt32()},{metadata["fanartOffsetY"].GetInt32()},{metadata["fanartScale"].GetDouble():F2} -> {fanartOffsetX},{fanartOffsetY},{fanartScale:F2} | Logo: {metadata["logoOffsetX"].GetInt32()},{metadata["logoOffsetY"].GetInt32()},{metadata["logoScale"].GetDouble():F2} -> {logoOffsetX},{logoOffsetY},{logoScale:F2}");
            }
            
            return matches;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[OFFSET METADATA] Validation failed: {ex.Message}");
            return false;
        }
    }
        public string GetScrapingPlaceholder(string type)
        {
            string fileName = type == "dmd" ? "scraping-dmd.png" : "scraping.png";
            string placeholderPath = Path.Combine(_config.MarqueeImagePath, fileName);

            if (File.Exists(placeholderPath)) return placeholderPath;

            // Generate if missing
            try
            {
                int w = type == "dmd" ? _config.DmdWidth : _config.MarqueeWidth;
                int h = type == "dmd" ? _config.DmdHeight : _config.MarqueeHeight;
                string label = type == "dmd" ? "Scraping..." : "ScreenScraper: Scraping...";
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = _config.IMPath,
                    Arguments = $"-size {w}x{h} xc:black -gravity center -fill white -pointsize {(type == "dmd" ? 12 : 24)} -draw \"text 0,0 '{label}'\" \"{placeholderPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    process?.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to generate scraping placeholder: {ex.Message}");
            }

            return File.Exists(placeholderPath) ? placeholderPath : _config.DefaultImagePath;
        }
        public string GenerateScoreOverlay(int currentPoints, int totalPoints, bool isDmd)
        {
            try
            {
                int canvasWidth, canvasHeight;
                if (isDmd)
                {
                    canvasWidth = _config.DmdWidth > 0 ? _config.DmdWidth : 128;
                    canvasHeight = _config.DmdHeight > 0 ? _config.DmdHeight : 32;
                }
                else
                {
                    if (!int.TryParse(_config.GetSetting("MarqueeWidth", "1920"), out canvasWidth)) canvasWidth = 1920;
                    if (!int.TryParse(_config.GetSetting("MarqueeHeight", "360"), out canvasHeight)) canvasHeight = 360;
                }

                string text = $"{currentPoints}/{totalPoints} pts";
                string outputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "retroachievements", "overlays");
                Directory.CreateDirectory(outputFolder);

                // EN: Clean up old score overlays to prevent accumulation
                // FR: Nettoyer les anciens overlays de score pour éviter l'accumulation
                try
                {
                    var prefix = $"score_overlay_{(isDmd ? "dmd" : "mpv")}_";
                    var oldFiles = Directory.GetFiles(outputFolder, $"{prefix}*.png");
                    foreach (var file in oldFiles)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                catch { }

                // EN: Use unique filename to avoid file locking issues
                // FR: Utiliser un nom de fichier unique pour éviter les problèmes de verrouillage de fichier
                string outputPath = Path.Combine(outputFolder, $"score_overlay_{(isDmd ? "dmd" : "mpv")}_{DateTime.Now.Ticks}.png");

                using (var bitmap = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bitmap))
                {
                    // EN: Pixel Text Rendering for Retro Look
                    // FR: Rendu de texte pixel pour look rétro
                    g.SmoothingMode = SmoothingMode.None;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    g.Clear(Color.Transparent);

                    // Style configuration
                    // EN: Adjust font size for small DMDs vs Large Marquee
                    // FR: Ajuster taille police pour petits DMD vs Grand Marquee
                    // Reduced sizes based on user feedback (overflow on DMD)
                    int fontSize = isDmd ? (canvasHeight < 64 ? 7 : 10) : 22;
                    var fontFamilyStr = _config.RAFontFamily;
                    var fontStyle = FontStyle.Bold;
                    
                    Font? font = null;
                    PrivateFontCollection? pfc = null;

                    try
                    {
                        // 1. Try Loading Custom Font File from medias/retroachievements/fonts/*.ttf
                        string fontsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "retroachievements", "fonts");
                        if (Directory.Exists(fontsDir))
                        {
                            var fontFiles = Directory.GetFiles(fontsDir, "*.ttf");
                            if (fontFiles.Length > 0)
                            {
                                pfc = new PrivateFontCollection();
                                pfc.AddFontFile(fontFiles[0]); // Load first found font
                                if (pfc.Families.Length > 0)
                                {
                                    font = new Font(pfc.Families[0], fontSize, fontStyle);
                                }
                            }
                        }

                        // 2. Fallback to Configured Font Family
                        if (font == null)
                        {
                             font = new Font(fontFamilyStr, fontSize, fontStyle);
                        }
                    }
                    catch
                    {
                         // EN: Fallback to generic sans serif
                         font = new Font(FontFamily.GenericSansSerif, fontSize, fontStyle);
                    }
                    
                    using (font)
                    {
                         var textSize = g.MeasureString(text, font);
                            
                         // Reduce padding for tighter fit but increase height for vertical centering
                         int paddingX = isDmd ? 2 : 8;
                         int paddingY = isDmd ? 4 : 10; 
                            
                         int boxWidth = (int)textSize.Width + (paddingX * 2);
                         int boxHeight = (int)textSize.Height + (paddingY * 2);

                         int x, y;

                         if (isDmd)
                         {
                             // Centered for DMD
                             x = (canvasWidth - boxWidth) / 2;
                             y = (canvasHeight - boxHeight) / 2;
                         }
                         else
                         {
                             // Top-Right for MPV with margin
                             int margin = 20;
                             x = canvasWidth - boxWidth - margin;
                             y = margin;
                         }

                         // Draw Background Box (Semi-transparent Gray)
                         using (var brush = new SolidBrush(Color.FromArgb(180, 40, 40, 40)))
                         {
                             g.FillRectangle(brush, x, y, boxWidth, boxHeight);
                         }
                            
                         // Draw Border
                         using (var pen = new Pen(Color.FromArgb(200, 255, 215, 0), 1)) // Gold border
                         {
                             g.DrawRectangle(pen, x, y, boxWidth, boxHeight);
                         }

                         // Draw Text (White) with Manual Centering and Nudge
                         using (var brush = new SolidBrush(Color.White))
                         {
                              // Calculate intended top-left of text based on padding
                              // This places the text 'box' exactly inside our padding
                              float textX = x + paddingX;
                              float textY = y + paddingY;

                              // EN: Nudge text down slightly for visual centering (compensate for ascenders/descenders)
                              // FR: Décaler légèrement le texte vers le bas pour le centrage visuel (compenser ascendants/descendants)
                              // Increased nudge based on user feedback "not centered vertically" (usually means too high)
                              float verticalNudge = isDmd ? 1.0f : 3.0f;
                              
                              g.DrawString(text, font, brush, textX, textY + verticalNudge);
                         }
                         
                         bitmap.Save(outputPath, ImageFormat.Png);
                    }
                    return outputPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error generating score overlay: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// EN: Generate locked badge from normal badge (grayscale + darkened)
        /// FR: Générer badge verrouillé depuis badge normal (niveaux de gris + assombri)
        /// </summary>
        public string? GenerateBadgeLockFromNormal(string normalBadgePath, int gameId, int achievementId)
        {
            if (string.IsNullOrEmpty(normalBadgePath) || !File.Exists(normalBadgePath))
            {
                _logger.LogWarning($"[RA Badge Lock] Source badge not found: {normalBadgePath}");
                return null;
            }

            try
            {
                var lockCacheDir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "medias", "retroachievements", "badges_lock",
                    gameId.ToString()
                );
                Directory.CreateDirectory(lockCacheDir);

                var lockPath = Path.Combine(lockCacheDir, $"{achievementId}_lock_generated.png");

                // EN: Check cache / FR: Vérifier cache
                if (File.Exists(lockPath))
                {
                    _logger.LogDebug($"[RA Badge Lock] Found cached: {lockPath}");
                    return lockPath;
                }

                // EN: Generate grayscale / FR: Générer niveaux de gris
                using (var sourceImage = Image.FromFile(normalBadgePath))
                using (var grayBitmap = new Bitmap(sourceImage.Width, sourceImage.Height))
                {
                    using (var g = Graphics.FromImage(grayBitmap))
                    {
                        // EN: Grayscale color matrix / FR: Matrice de couleur niveaux de gris
                        var colorMatrix = new ColorMatrix(new float[][]
                        {
                            new float[] {0.299f, 0.299f, 0.299f, 0, 0},
                            new float[] {0.587f, 0.587f, 0.587f, 0, 0},
                            new float[] {0.114f, 0.114f, 0.114f, 0, 0},
                            new float[] {0, 0, 0, 1, 0},
                            new float[] {0, 0, 0, 0, 1}
                        });

                        var attributes = new ImageAttributes();
                        attributes.SetColorMatrix(colorMatrix);

                        g.DrawImage(sourceImage,
                            new Rectangle(0, 0, sourceImage.Width, sourceImage.Height),
                            0, 0, sourceImage.Width, sourceImage.Height,
                            GraphicsUnit.Pixel,
                            attributes);
                    }

                    // EN: Darken (50% opacity black overlay) / FR: Assombrir (overlay noir 50% opacité)
                    using (var g = Graphics.FromImage(grayBitmap))
                    using (var brush = new SolidBrush(Color.FromArgb(128, Color.Black)))
                    {
                        g.FillRectangle(brush, 0, 0, grayBitmap.Width, grayBitmap.Height);
                    }

                    grayBitmap.Save(lockPath, ImageFormat.Png);
                    _logger.LogInformation($"[RA Badge Lock] Generated grayscale: {lockPath}");
                    return lockPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Badge Lock] Error generating locked badge: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// EN: Generate badge ribbon overlay (locked/unlocked badges in horizontal ribbon)
        /// FR: Générer overlay de bandeau de badges (badges verrouillés/déverrouillés en bandeau horizontal)
        /// </summary>
        public async Task<string> GenerateBadgeRibbonOverlay(
            Dictionary<string, Achievement> achievements,
            int gameId,
            RetroAchievementsService raService,
            bool isDmd)
        {
            if (achievements == null || achievements.Count == 0)
            {
                _logger.LogWarning("[RA Ribbon] No achievements to display");
                return string.Empty;
            }

            try
            {
                // EN: Calculate dimensions / FR: Calculer les dimensions
                int badgeSize = isDmd ? 32 : 64;
                int screenWidth = isDmd ? (_config.DmdWidth > 0 ? _config.DmdWidth : 128) : 1920;
                int screenHeight = isDmd ? (_config.DmdHeight > 0 ? _config.DmdHeight : 32) : 360;
                
                // EN: Locked badges show top 30% (peek effect) / FR: Badges verrouillés affichent haut 30% (effet aperçu)
                double peekPercentage = 0.30;
                int peekHeight = (int)(badgeSize * peekPercentage);
                
                // EN: Position badges so locked (30% top) stick to bottom edge (tile effect)
                // FR: Positionner badges pour que locked (30% haut) collent au bord bas (effet tuile)
                int baseY = screenHeight - peekHeight; // Locked badges at screen bottom
                
                // EN: Create transparent canvas / FR: Créer canevas transparent
                using (var bitmap = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    
                    int spacing = isDmd ? 0 : 1; // EN: No spacing for DMD (128/32=4), minimal for MPV / FR: Pas d'espace pour DMD (128/32=4), minimal pour MPV
                    int x = 0;
                    
                    // EN: Sort achievements by DisplayOrder / FR: Trier succès par DisplayOrder
                    var sortedAchievements = achievements.Values
                        .OrderBy(a => a.DisplayOrder)
                        .ToList();
                    
                    foreach (var achievement in sortedAchievements)
                    {
                        // EN: Stop if we exceed screen width / FR: Arrêter si on dépasse la largeur d'écran
                        if (x + badgeSize > screenWidth)
                        {
                            _logger.LogDebug($"[RA Ribbon] Reached screen width limit, displayed {sortedAchievements.IndexOf(achievement)} badges");
                            break;
                        }
                        
                        string? badgePath = null;
                        
                        if (achievement.Unlocked)
                        {
                            // EN: Get unlocked badge / FR: Obtenir badge déverrouillé
                            badgePath = await raService.GetBadgePath(gameId, achievement.ID);
                        }
                        else
                        {
                            // EN: Get locked badge / FR: Obtenir badge verrouillé
                            badgePath = await raService.GetBadgeLockPath(gameId, achievement.ID);
                        }
                        
                        if (string.IsNullOrEmpty(badgePath) || !File.Exists(badgePath))
                        {
                            _logger.LogWarning($"[RA Ribbon] Badge not found: Achievement {achievement.ID}");
                            continue;
                        }
                        
                        try
                        {
                            using (var badge = Image.FromFile(badgePath))
                            {
                                if (achievement.Unlocked)
                                {
                                    // EN: Draw full badge - rises up from locked position (tile effect)
                                    // FR: Dessiner badge complet - monte depuis position locked (effet tuile)
                                    int unlockedY = baseY - (badgeSize - peekHeight);
                                    g.DrawImage(badge, new Rectangle(x, unlockedY, badgeSize, badgeSize));
                                }
                                else
                                {
                                    // EN: Draw partial badge - top 30% stuck to bottom edge
                                    // FR: Dessiner badge partiel - haut 30% collé au bord bas
                                    int srcY = 0; // EN: Start from top / FR: Commencer depuis le haut
                                    int srcHeight = (int)(badge.Height * peekPercentage);
                                    
                                    var srcRect = new Rectangle(0, srcY, badge.Width, srcHeight);
                                    var destRect = new Rectangle(x, baseY, badgeSize, peekHeight);
                                    
                                    g.DrawImage(badge, destRect, srcRect, GraphicsUnit.Pixel);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"[RA Ribbon] Error drawing badge {achievement.ID}: {ex.Message}");
                        }
                        
                        x += badgeSize + spacing;
                    }
                    
                    // EN: Save overlay / FR: Sauvegarder overlay
                    string outputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "retroachievements", "overlays");
                    Directory.CreateDirectory(outputFolder);
                    
                    // EN: Clean up old ribbon overlays / FR: Nettoyer anciens overlays de bandeau
                    try
                    {
                        var prefix = $"badge_ribbon_{(isDmd ? "dmd" : "mpv")}_";
                        var oldFiles = Directory.GetFiles(outputFolder, $"{prefix}*.png");
                        foreach (var file in oldFiles)
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                    catch { }
                    
                    string outputPath = Path.Combine(outputFolder, $"badge_ribbon_{(isDmd ? "dmd" : "mpv")}_{DateTime.Now.Ticks}.png");
                    bitmap.Save(outputPath, ImageFormat.Png);
                    
                    _logger.LogInformation($"[RA Ribbon] Generated badge ribbon: {outputPath} ({sortedAchievements.Count} achievements)");
                    return outputPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Ribbon] Error generating badge ribbon: {ex.Message}");
                return string.Empty;
            }
        }
        
        /// <summary>
        /// EN: Compose score and badges into single overlay image for MPV
        /// FR: Composer score et badges en une seule image overlay pour MPV
        /// </summary>
        public string ComposeScoreAndBadges(string? scorePath, string? badgesPath, string? countPath = null, int screenWidth = 1920, int screenHeight = 360)
        {
            try
            {
                using (var canvas = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    
                    // EN: Draw badges at bottom-left / FR: Dessiner badges en bas-gauche
                    if (!string.IsNullOrEmpty(badgesPath) && File.Exists(badgesPath))
                    {
                        using (var badges = Image.FromFile(badgesPath))
                        {
                            int badgesY = screenHeight - badges.Height;
                            g.DrawImage(badges, 0, badgesY, badges.Width, badges.Height);
                        }
                    }
                    
                    // EN: Draw count at top-left / FR: Dessiner compteur en haut-gauche
                    if (!string.IsNullOrEmpty(countPath) && File.Exists(countPath))
                    {
                        using (var count = Image.FromFile(countPath))
                        {
                            // EN: Draw at (0,0) as margins are handled internally in GenerateAchievementCountOverlay
                            // FR: Dessiner à (0,0) car les marges sont gérées en interne
                            g.DrawImage(count, 0, 0, screenWidth, screenHeight);
                        }
                    }
                    
                    // EN: Draw score at top-right / FR: Dessiner score en haut-droite
                    if (!string.IsNullOrEmpty(scorePath) && File.Exists(scorePath))
                    {
                        using (var score = Image.FromFile(scorePath))
                        {
                            // EN: Draw at (0,0) as margins are handled internally in GenerateScoreOverlay
                            // FR: Dessiner à (0,0) car les marges sont gérées en interne
                            g.DrawImage(score, 0, 0, screenWidth, screenHeight);
                        }
                    }
                    
                    // EN: Save composed overlay / FR: Sauvegarder overlay composé
                    string outputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "retroachievements", "overlays");
                    Directory.CreateDirectory(outputFolder);
                    
                    string outputPath = Path.Combine(outputFolder, $"composed_mpv_{DateTime.Now.Ticks}.png");
                    canvas.Save(outputPath, ImageFormat.Png);
                    
                    _logger.LogInformation($"[RA Compose] Created MPV composed overlay: {outputPath}");
                    return outputPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Compose] Error composing score and badges: {ex.Message}");
                return string.Empty;
            }
        }
        
        /// <summary>
        /// EN: Generate achievement count overlay (unlocked/total)
        /// FR: Générer overlay compteur achievements (débloqués/total)
        /// </summary>
        public string GenerateAchievementCountOverlay(int unlockedCount, int totalCount, bool isDmd)
        {
            try
            {
                int canvasWidth, canvasHeight;
                if (isDmd)
                {
                    canvasWidth = _config.DmdWidth > 0 ? _config.DmdWidth : 128;
                    canvasHeight = _config.DmdHeight > 0 ? _config.DmdHeight : 32;
                }
                else
                {
                    if (!int.TryParse(_config.GetSetting("MarqueeWidth", "1920"), out canvasWidth)) canvasWidth = 1920;
                    if (!int.TryParse(_config.GetSetting("MarqueeHeight", "360"), out canvasHeight)) canvasHeight = 360;
                }

                string text = $"{unlockedCount}/{totalCount}";
                string outputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "retroachievements", "overlays");
                Directory.CreateDirectory(outputFolder);

                // EN: Clean up old count overlays / FR: Nettoyer anciens overlays compteur
                try
                {
                    var prefix = $"achievement_count_{(isDmd ? "dmd" : "mpv")}_";
                    var oldFiles = Directory.GetFiles(outputFolder, $"{prefix}*.png");
                    foreach (var file in oldFiles)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                catch { }

                string outputPath = Path.Combine(outputFolder, $"achievement_count_{(isDmd ? "dmd" : "mpv")}_{DateTime.Now.Ticks}.png");

                using (var bitmap = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bitmap))
                {
                    // EN: Pixel Text Rendering for Retro Look / FR: Rendu texte pixel look rétro
                    g.SmoothingMode = SmoothingMode.None;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                    g.Clear(Color.Transparent);

                    // EN: Same style as score / FR: Même style que score
                    int fontSize = isDmd ? (canvasHeight < 64 ? 7 : 10) : 22;
                    var fontStyle = FontStyle.Bold;
                    
                    Font? font = null;
                    PrivateFontCollection? pfc = null;

                    try
                    {
                        // EN: Try Loading Custom Font File / FR: Essayer charger fichier police personnalisé
                        string fontsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "retroachievements", "fonts");
                        if (Directory.Exists(fontsDir))
                        {
                            var fontFiles = Directory.GetFiles(fontsDir, "*.ttf");
                            if (fontFiles.Length > 0)
                            {
                                pfc = new PrivateFontCollection();
                                pfc.AddFontFile(fontFiles[0]);
                                if (pfc.Families.Length > 0)
                                {
                                    font = new Font(pfc.Families[0], fontSize, fontStyle);
                                }
                            }
                        }
                    }
                    catch { }

                    if (font == null)
                    {
                        font = new Font("Arial", fontSize, fontStyle);
                    }
                    
                    using (font)
                    {
                        var textSize = g.MeasureString(text, font);
                            
                        int paddingX = isDmd ? 2 : 8;
                        int paddingY = isDmd ? 4 : 10;
                            
                        int boxWidth = (int)textSize.Width + (paddingX * 2);
                        int boxHeight = (int)textSize.Height + (paddingY * 2);

                        int x, y;

                        if (isDmd)
                        {
                            // EN: Centered for DMD / FR: Centré pour DMD
                            x = (canvasWidth - boxWidth) / 2;
                            y = (canvasHeight - boxHeight) / 2;
                        }
                        else
                        {
                            // EN: Top-Left for MPV with margin / FR: Haut-gauche pour MPV avec marge
                            int margin = 20;
                            x = margin;
                            y = margin;
                        }

                        // EN: Draw Background Box (Semi-transparent Gray) / FR: Dessiner fond (gris semi-transparent)
                        using (var brush = new SolidBrush(Color.FromArgb(180, 40, 40, 40)))
                        {
                            g.FillRectangle(brush, x, y, boxWidth, boxHeight);
                        }
                            
                        // EN: Draw Border / FR: Dessiner bordure
                        using (var pen = new Pen(Color.FromArgb(200, 255, 215, 0), 1)) // Gold border
                        {
                            g.DrawRectangle(pen, x, y, boxWidth, boxHeight);
                        }

                        // EN: Draw Text (White) / FR: Dessiner texte (blanc)
                        using (var brush = new SolidBrush(Color.White))
                        {
                            float textX = x + paddingX;
                            float textY = y + paddingY;
                            
                            // EN: Nudge text down slightly for visual centering (compensate for ascenders/descenders)
                            // FR: Décaler légèrement le texte vers le bas pour le centrage visuel
                            float verticalNudge = isDmd ? 1.0f : 3.0f;
                            
                            g.DrawString(text, font, brush, textX, textY + verticalNudge);
                        }
                    }

                    bitmap.Save(outputPath, ImageFormat.Png);
                    _logger.LogInformation($"[RA Count] Generated count overlay: {outputPath}");
                    return outputPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Count] Error generating count overlay: {ex.Message}");
                return string.Empty;
            }
            finally
            {
                // Cleanup (nothing specific needed here)
            }
        }

        private string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            var result = input;
            
            // 1. Replace invalid filename chars
            var invalids = Path.GetInvalidFileNameChars();
            foreach (var c in invalids) result = result.Replace(c, '_');

            // 2. EN: Replace spaces and common delimiters that might cause issues with DMD drivers/CLI
            // FR: Remplacer les espaces et délimiteurs communs qui pourraient causer des soucis avec drivers DMD/CLI
            char[] delimiters = { ' ', '(', ')', '[', ']', '-', '.', ',' };
            foreach (var c in delimiters) result = result.Replace(c, '_');

            // 3. EN: Collapse multiple underscores into one
            // FR: Réduire les underscores multiples en un seul
            while (result.Contains("__")) result = result.Replace("__", "_");

            return result.Trim('_').Trim();
        }
    }
}
