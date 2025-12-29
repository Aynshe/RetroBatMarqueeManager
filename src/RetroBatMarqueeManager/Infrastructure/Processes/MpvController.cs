using RetroBatMarqueeManager.Core.Interfaces;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace RetroBatMarqueeManager.Infrastructure.Processes
{
    public class MpvController
    {
        private readonly IConfigService _config;
        private readonly IProcessService _processService;
        private readonly ILogger<MpvController> _logger;
        private const string PipeName = "mpv-pipe"; // Nom EXACT du Python : \\.\pipe\mpv-pipe

        public MpvController(IConfigService config, IProcessService processService, ILogger<MpvController> logger)
        {
            _config = config;
            _processService = processService;
            _logger = logger;
        }

        public void StartMpv()
        {
            if (!_config.IsMpvEnabled)
            {
                _logger.LogWarning("MPV is disabled in config (ScreenNumber=false). Skipping startup.");
                return;
            }

            var pipeName = @"\\.\pipe\mpv-pipe";
            var screenNumber = _config.GetSetting("ScreenNumber", "1");
            var bgColor = _config.MarqueeBackgroundColor; // e.g. #000000

            // 1. Custom Command Check
            string customCmd = _config.MpvCustomCommand;
            if (!string.IsNullOrEmpty(customCmd))
            {
                _logger.LogInformation("Using Custom MPV Command from config.ini");
                
                // Variable substitution
                // Supported: {MPVPath}, {IPCChannel}, {ScreenNumber}, {DefaultImagePath}, {MarqueeBackgroundCodeColor}
                
                var finalCmd = customCmd
                    .Replace("{MPVPath}", _config.MPVPath)
                    .Replace("{IPCChannel}", pipeName)
                    .Replace("{ScreenNumber}", screenNumber)
                    .Replace("{DefaultImagePath}", _config.DefaultImagePath)
                    .Replace("{DefaultImagePath}", _config.DefaultImagePath)
                    .Replace("{MarqueeBackgroundCodeColor}", bgColor);

                // Auto-correct legacy/incorrect template: --background=#... -> --background-color=#...
                if (finalCmd.Contains("--background=#"))
                {
                    finalCmd = finalCmd.Replace("--background=#", "--background-color=#");
                    _logger.LogWarning("Auto-corrected '--background' to '--background-color' in Custom MPV Command.");
                }

                // For robustness, if the user didn't quote MPVPath, we might want to ensure the first token is handled or just pass the whole thing
                // StartProcessWithLogging(str, str) treats first arg as filename.
                // We need to split filename (exe) and arguments.
                
                // Simple heuristic: The first quoted string or first space-delimited token is the EXE.
                // But usually, the user might provide just arguments if they assume we launch MPV? 
                // No, the requested feature implies full command line control including the exe path likely.
                // But SystemProcessService requires (FileName, Arguments). 
                
                // Let's parse simple:
                // If it starts with quote, find end quote. 
                // Else find first space.
                
                string fileName = _config.MPVPath;
                string arguments = finalCmd;

                // If the user's string starts with the MPV path, we should try to extract it, OR 
                // we can just run "cmd /c" ? No, that's messy.
                // Let's assume the user puts the full command.
                // We need to separate EXE from ARGS.
                
                // "C:\Path\mpv.exe" --arg1 ...
                
                var parts = SplitCommand(finalCmd);
                if (parts.Count > 0)
                {
                    fileName = parts[0];
                    // Re-join the rest or just substring? Substring is safer to preserve quote flavor if SplitCommand de-quotes.
                    // Actually, if we use the same StartProcessWithLogging that takes (fileName, arguments string), it puts them in ProcessStartInfo.Arguments.
                    // ProcessStartInfo.Arguments does NOT automatically quote the filename.
                    
                    // Better approach: Use the custom command as the ARGUMENTS to "cmd /c" ? 
                    // No, invalid PID tracking.
                    
                // Unified Parsing Logic
                // We must separate the executable (first token) from the arguments.
                // Logic: If starts with quote, read until next quote. Else read until space.
                
                if (finalCmd.StartsWith("\""))
                {
                    // Find closing quote
                    // Starting at index 1 to skip the first quote
                    var endQuote = finalCmd.IndexOf('"', 1);
                    if (endQuote != -1)
                    {
                        fileName = finalCmd.Substring(1, endQuote - 1);
                        arguments = finalCmd.Substring(endQuote + 1).Trim();
                    }
                    else
                    {
                        // Mismatched quotes? Treat as single file/command (unlikely to work but safe fallback)
                        fileName = finalCmd.Trim('"');
                        arguments = "";
                    }
                }
                else
                {
                    var firstSpace = finalCmd.IndexOf(' ');
                    if (firstSpace != -1)
                    {
                        fileName = finalCmd.Substring(0, firstSpace).Trim('"');
                        arguments = finalCmd.Substring(firstSpace + 1).Trim();
                    }
                    else
                    {
                        fileName = finalCmd.Trim('"');
                        arguments = "";
                    }
                }
                }
                
                _logger.LogInformation($"Executing Custom MPV: {fileName} {arguments}");
                try 
                {
                    _processService.StartProcessWithLogging(fileName, arguments, Path.GetDirectoryName(fileName));
                     // Verification logic
                    System.Threading.Thread.Sleep(2000);
                    if (_processService.IsProcessRunning("mpv")) _logger.LogInformation("Custom MPV started successfully!");
                    else _logger.LogError("Custom MPV failed to start or crashed immediately.");
                }
                catch(Exception ex) { _logger.LogError($"Custom MPV Error: {ex.Message}"); }
                
                return;
            }

            // 2. Default Logic
            var args = new List<string>
            {
                _config.DefaultImagePath, // First arg is file to play
                "--idle",
                "--keep-open=yes",
                "--loop-file=inf",
                $"--input-ipc-server={pipeName}",
                "--fs",
                $"--screen={screenNumber}",
                $"--fs-screen={screenNumber}",
                $"--background-color={bgColor}",
                "--osd-level=1", // Allow OSD
                // "--no-osc", // REMOVED: We need OSC script loaded to toggle it
                "--script-opts=osc-visibility=never,osc-layout=box,osc-seekbarstyle=bar", // Load OSC hidden
                "--ontop",
                "--vo=gpu", // Force standard GPU renderer (fix for black screen on older hardware/drivers)
                "--mute=yes",
                $"--scripts={Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "lua")}"
            };
            
            _logger.LogInformation($"Starting MPV (Default): {_config.MPVPath} with args...");
            
            try
            {
                // Usage of ArgumentList overload
                _processService.StartProcessWithLogging(_config.MPVPath, args, Path.GetDirectoryName(_config.MPVPath));
                
                System.Threading.Thread.Sleep(2000);
                if (_processService.IsProcessRunning("mpv"))
                {
                    _logger.LogInformation("MPV started successfully!");
                }
                else
                {
                    _logger.LogError("MPV process not found after start - it may have crashed. Check stderr logs above.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start MPV: {ex.Message}");
            }
        }
        
        private List<string> SplitCommand(string cmd)
        {
            // Basic splitter for fallback
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(cmd)) return list;
            // Native win32 split might be better but let's stick to safe managed for now if needed.
            // Actually redundant if we used the logic above.
            return list; 
        }

        public async Task SendCommandAsync(string command, bool retry = true)
        {
            if (!_config.IsMpvEnabled) return;

            try 
            {
                using var client = new NamedPipeClientStream(".", "mpv-pipe", PipeDirection.Out);
                await client.ConnectAsync(500);
                using var writer = new StreamWriter(client);
                await writer.WriteLineAsync(command);
                await writer.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to send MPV command: {ex.Message}");
                
                if (retry)
                {
                    _logger.LogInformation("Attempting to restart MPV and retry command...");
                    // Force restart
                    StartMpv();
                    
                    // Small delay to let MPV initialize pipe
                    await Task.Delay(2000);
                    
                    // Retry once (retry=false)
                    await SendCommandAsync(command, retry: false);
                }
                else
                {
                    _logger.LogError("MPV Auto-Restart failed or command failed twice.");
                }
            }
        }

        public async Task PushRetroAchievementData(string type, params object[] values)
        {
            if (!_config.IsMpvEnabled) return;

            try 
            {
                // Format: type|val1|val2|...
                var validValues = values.Select(v => v?.ToString() ?? "").ToList();
                var pipedData = string.Join("|", new[] { type }.Concat(validValues));
                
                // Escape for JSON string
                // Note: The Python script sends: echo {"command":["script-message","push-ra","{data}"]}>{IPCChannel}
                // We need to construct this JSON command.
                
                // Serialize the command array manually or via Newtonsoft/System.Text.Json
                // Using simple string construction to avoid dependency just for this if strict JSON not needed, 
                // BUT "script-message" might need quotes escaping inside the data string?
                // The data string itself is a single string argument.
                
                // Check for custom command in config
                // Template: echo {"command":["script-message","push-ra","{data}"]}>{IPCChannel}
                var customCmd = _config.GetSetting("MPVPushRetroAchievementsDatas", "");
                var pipeName = @"\\.\pipe\mpv-pipe";

                if (!string.IsNullOrWhiteSpace(customCmd))
                {
                    // Use Custom Shell Command Logic
                    var finalCmd = customCmd
                        .Replace("{data}", pipedData) // PipedData contains user|token|etc
                        .Replace("{IPCChannel}", pipeName);

                    _logger.LogInformation($"Executing Custom RA Push: {finalCmd}");
                    
                    // Executes via CMD because it likely contains redirection >
                    _processService.StartProcessWithLogging("cmd.exe", $"/c {finalCmd}", AppDomain.CurrentDomain.BaseDirectory);
                }
                else
                {
                    // Fallback: Internal logic (Direct Pipe Write)
                    var jsonCommand = System.Text.Json.JsonSerializer.Serialize(new 
                    { 
                        command = new[] { "script-message", "push-ra", pipedData } 
                    });
                    
                    // Send as JSON object (standard MPV IPC)
                    await SendCommandAsync(jsonCommand);
                    _logger.LogInformation($"Pushed RA Data (Internal): {type} ({values.Length} items)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to push RA data: {ex.Message}");
            }
        }

        public async Task DisplayImage(string imagePath, bool loop = true)
        {
            // Escape backslashes for MPV (Just for safety, though JSON Serialize handles it too)
            var safePath = imagePath.Replace("\\", "/");
            
            // EN: Set loop first, then load with append-play to ensure playback starts
            // FR: Définir loop d'abord, puis charger avec append-play pour garantir la lecture
            
            // 1. Set loop property before loading
            var loopValue = loop ? "inf" : "no";
            var loopCmd = System.Text.Json.JsonSerializer.Serialize(new 
            { 
                command = new[] { "set_property", "loop-file", loopValue } 
            });
            await SendCommandAsync(loopCmd);
            
            // 2. Load file with "replace" mode and force start playing
            var loadCmd = System.Text.Json.JsonSerializer.Serialize(new 
            { 
                command = new[] { "loadfile", safePath, "replace" } 
            });
            await SendCommandAsync(loadCmd);
            
            // 3. Small delay to let MPV start loading
            await Task.Delay(50);
            
            // 4. Force unpause
            var unpauseCmd = System.Text.Json.JsonSerializer.Serialize(new 
            { 
                command = new[] { "set_property", "pause", "no" } 
            });
            await SendCommandAsync(unpauseCmd);
            await SendCommandAsync(unpauseCmd);
        }

        public async Task<string?> GetPropertyAsync(string propertyName)
        {
            if (!_config.IsMpvEnabled) return null;

            int retries = 3;
            while (retries > 0)
            {
                retries--;
                try
                {
                    // Generate a unique request ID
                    int requestId = new Random().Next(1, 10000);
                    
                    var jsonCommand = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        command = new[] { "get_property", propertyName },
                        request_id = requestId 
                    });

                    // Use PipeOptions.Asynchronous for true async support
                    using var client = new NamedPipeClientStream(".", "mpv-pipe", PipeDirection.InOut, PipeOptions.Asynchronous);
                    
                    _logger.LogInformation($"[IPC] Connecting... (Retry {2-retries})");
                    await client.ConnectAsync(500);

                    if (!client.IsConnected)
                    {
                        _logger.LogWarning($"[IPC] Failed to connect (IsConnected=false) - {propertyName}");
                        await Task.Delay(100);
                        continue;
                    }

                    try 
                    {
                        // Using explicit logging and try/catch for stream creation
                        using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                        using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);

                        _logger.LogInformation($"[IPC] Sending: {jsonCommand}");
                        await writer.WriteLineAsync(jsonCommand);
                        
                        var timeoutTask = Task.Delay(1000);
                        
                        while (client.IsConnected)
                        {
                           var lineTask = reader.ReadLineAsync();
                           var completed = await Task.WhenAny(lineTask, timeoutTask);
                           if (completed == timeoutTask) 
                           {
                               _logger.LogWarning($"[IPC] Timeout waiting for {propertyName} (req_id: {requestId})");
                               break; // Retry
                           }

                           var line = await lineTask;
                           if (line == null) break; // End of stream
                           if (string.IsNullOrEmpty(line)) continue;
                           
                           // _logger.LogInformation($"[IPC] Recv: {line}");

                           if (line.Contains("\"request_id\"") || line.Contains("\"data\""))
                           {
                                using var doc = JsonDocument.Parse(line);
                                var root = doc.RootElement;
                                
                                if (root.TryGetProperty("request_id", out var resIdParam))
                                {
                                    if (resIdParam.GetInt32() != requestId) continue;
                                }
                                
                                if (root.TryGetProperty("error", out var errorElement))
                                {
                                    var err = errorElement.GetString();
                                    if (err != "success")
                                    {
                                        _logger.LogWarning($"[IPC] MPV Error for {propertyName}: {err}");
                                        return null; 
                                    }
                                }

                                if (root.TryGetProperty("data", out var dataElement))
                                {
                                    return dataElement.ToString();
                                }
                           }
                        }
                    }
                    catch (Exception innerEx)
                    {
                         _logger.LogWarning($"[IPC] Inner Exception during RW: {innerEx.Message}");
                         throw; // Rethrow to outer retry logic
                    }
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning($"[IPC] Connection Timeout for {propertyName}");
                }
                catch (IOException ioEx)
                {
                     // Pipe broken or not found
                     _logger.LogWarning($"[IPC] IO Error for {propertyName}: {ioEx.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[IPC] Unexpected Error for {propertyName} (Try {2-retries}): {ex.Message}");
                }
                
                await Task.Delay(100); // Wait before retry
            }
            
            _logger.LogError($"[IPC] Failed to get property {propertyName} after retries");
            return null;
        }

        public async Task<double> GetCurrentTime()
        {
            var result = await GetPropertyAsync("time-pos");
            if (double.TryParse(result, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var time))
            {
                return time;
            }
            return 0.0;
        }

        private CancellationTokenSource? _overlayCts;

        public async Task ShowOverlay(string imagePath, int durationMs = 5000, string position = "top-right")
        {
            if (!_config.IsMpvEnabled) return;

            try
            {
                // EN: Cancel previous overlay timer if any (prevents race conditions)
                // FR: Annuler timer overlay précédent s'il existe (évite conditions de course)
                _overlayCts?.Cancel();
                _overlayCts?.Dispose();
                _overlayCts = new CancellationTokenSource();

                // EN: Use lavfi overlay filter to show image on top of current video
                // FR: Utiliser filtre lavfi overlay pour afficher image sur vidéo courante
                // Syntax: vf add @name:lavfi=[movie='path'[logo];[in][logo]overlay=X:Y[out]]
                
                var safePath = imagePath.Replace("\\", "/"); // Forward slashes are safer
                // Note: Do NOT escape ':' for mapped drives inside movie='...' as it breaks avformat_open_input
                
                // EN: Calculate position based on parameter
                // FR: Calculer position selon paramètre
                string overlayPosition;
                if (position == "bottom-left")
                {
                    // Bottom-left with 0px padding
                    overlayPosition = "0:main_h-overlay_h";
                }
                else if (position == "top-left")
                {
                    // Top-left at 0,0 (for full-screen composed overlays)
                    overlayPosition = "0:0";
                }
                else
                {
                    // Top-right with 20px padding (default for score)
                    overlayPosition = "main_w-overlay_w-20:20";
                }
                
                var filterGraph = $"movie=\\'{safePath}\\'[logo];[in][logo]overlay={overlayPosition}[out]";
                var cmd = $"vf add @ra_overlay:lavfi=[{filterGraph}]";
                
                var jsonCmd = System.Text.Json.JsonSerializer.Serialize(new 
                { 
                    command = new[] { "vf", "add", $"@ra_overlay:lavfi=[{filterGraph}]" } 
                });

                _logger.LogInformation($"[MPV] Adding Overlay at {position}: {imagePath}");
                await SendCommandAsync(jsonCmd);

                // EN: Auto-remove after duration
                // FR: Auto-suppression après durée
                var token = _overlayCts.Token;
                _ = Task.Delay(durationMs, token).ContinueWith(async t => 
                {
                    if (t.IsCanceled) return;
                    await RemoveOverlay(false); // Pass false to avoid cancelling the token that just expired
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[MPV] Error showing overlay: {ex.Message}");
            }
        }

        public async Task ShowAchievementNotification(string cupPath, string finalOverlayPath, int cupDuration = 2000, int finalDuration = 8000)
        {
             // Sequence: Cup -> Final
             
             // 1. Show Cup
             if (!string.IsNullOrEmpty(cupPath) && File.Exists(cupPath))
             {
                 await RemoveOverlay(); // Ensure clean state
                 await Task.Delay(200); // Buffer
                 await ShowOverlay(cupPath, cupDuration);
                 await Task.Delay(cupDuration);
             }
             
             // 2. Show Final (Badge + Text)
             if (!string.IsNullOrEmpty(finalOverlayPath) && File.Exists(finalOverlayPath))
             {
                 await RemoveOverlay(); // Remove cup
                 await Task.Delay(200); // Buffer
                 await ShowOverlay(finalOverlayPath, finalDuration);
                 await Task.Delay(finalDuration); // EN: Wait for display to finish before returning / FR: Attendre la fin de l'affichage avant de retourner
             }
        }

        public async Task RemoveOverlay(bool cancelTimer = true)
        {
             if (!_config.IsMpvEnabled) return;
             try
             {
                if (cancelTimer)
                {
                    _overlayCts?.Cancel();
                }

                var jsonCmd = System.Text.Json.JsonSerializer.Serialize(new 
                { 
                    command = new[] { "vf", "remove", "@ra_overlay" } 
                });
                await SendCommandAsync(jsonCmd);
             }
             catch { /* Ignore if already removed */ }
        }
    }
}
