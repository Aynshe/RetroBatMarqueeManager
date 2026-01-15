
using RetroBatMarqueeManager.Core.Interfaces;
using RetroBatMarqueeManager.Infrastructure.UI;
using Microsoft.Extensions.Logging; 
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace RetroBatMarqueeManager.Infrastructure.Processes
{
    public class MpvController : IDisposable
    {
        private readonly IConfigService _config;
        private readonly ILogger<MpvController> _logger;
        
        private Thread? _uiThread;
        private MarqueeForm? _form;
        
        private IntPtr _mpvHandle = IntPtr.Zero;
        private bool _isInitialized = false;

        // Overlay Ping-Pong state
        private CancellationTokenSource? _overlayCts;
        private int _currentOverlaySlot = 0; 
        
        public MpvController(IConfigService config, IProcessService processService, ILogger<MpvController> logger)
        {
            _config = config;
            _logger = logger;
        }

        public void StartMpv()
        {
            if (!_config.IsMpvEnabled)
            {
                _logger.LogWarning("MPV is disabled in config. Skipping startup.");
                return;
            }

            if (_isInitialized && _mpvHandle != IntPtr.Zero) return;

            // Start UI Thread for the Window
            _uiThread = new Thread(UiThreadEntry);
            _uiThread.SetApartmentState(ApartmentState.STA); 
            _uiThread.IsBackground = true;
            _uiThread.Start();
        }

        private void UiThreadEntry()
        {
            try 
            {
                var screenNumberStr = _config.GetSetting("ScreenNumber", "1");
                int.TryParse(screenNumberStr, out int screenNum);
                
                _form = new MarqueeForm(screenNum, _logger);
                _form.Show(); 

                if (InitializeMpv(_form.RenderHandle))
                {
                    System.Windows.Forms.Application.Run(_form);
                }
                else
                {
                     _logger.LogWarning("[UI Thread] MPV Initialization failed. Form will not run.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[UI Thread] Error: {ex.Message}");
            }
        }

        private bool InitializeMpv(IntPtr windowHandle)
        {
            try
            {
                // Create LibMpv instance
                _mpvHandle = LibMpvNative.mpv_create();
                if (_mpvHandle == IntPtr.Zero) throw new Exception("mpv_create failed");

                // Set Options BEFORE Init (Critical for 'wid')
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "wid", windowHandle.ToInt64().ToString()));
                
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "idle", "yes"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "keep-open", "yes"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "vo", "gpu"));
                
                // Hardware Decoding (default: no)
                var hwDec = _config.MpvHwDecoding;
                if (!string.IsNullOrEmpty(hwDec) && !hwDec.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    // EN: Force copy mode for d3d11va/dxva2 to allow software filters (overlays) to work on top of video
                    // FR: Forcer mode copy pour d3d11va/dxva2 pour permettre aux filtres logiciels (overlays) de fonctionner
                    if (hwDec.Equals("d3d11va", StringComparison.OrdinalIgnoreCase)) hwDec = "d3d11va-copy";
                    else if (hwDec.Equals("dxva2", StringComparison.OrdinalIgnoreCase)) hwDec = "dxva2-copy";
                }
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "hwdec", hwDec));
                if (!string.IsNullOrEmpty(hwDec) && !hwDec.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "hwdec-codecs", "all"));
                    // EN: Use fast decoding for older CPUs (libmpv v2 context)
                    // FR: Utiliser le décodage rapide pour les anciens CPU (contexte libmpv v2)
                    CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "vd-lavc-fast", "yes"));
                    _logger.LogInformation("[MPV] Applied vd-lavc-fast=yes for performance optimization");
                }
                _logger.LogInformation($"[MPV] HW Usage: {hwDec}");
                
                // Scripts
                var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "lua");
                if (Directory.Exists(scriptPath))
                {
                     CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "scripts", scriptPath));
                }

                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "osd-level", "1"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "script-opts", "osc-visibility=never,osc-layout=box,osc-seekbarstyle=bar"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "mute", "yes"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "loop-file", "inf"));

                // Initialize
                CheckError(LibMpvNative.mpv_initialize(_mpvHandle));
                
                _isInitialized = true;
                _isInitialized = true;
                _logger.LogInformation("LibMpv initialized successfully (Native P/Invoke).");
                _logger.LogInformation($"[LibMpvResolver] \n{LibMpvResolver.ResolutionLog}");

                // Load Default Image if exists
                if (!string.IsNullOrEmpty(_config.DefaultImagePath) && File.Exists(_config.DefaultImagePath))
                {
                    _logger.LogInformation($"Loading default image: {_config.DefaultImagePath}");
                    // Need to use command because DisplayImage is async/Task based and we are in void
                    var safePath = _config.DefaultImagePath.Replace("\\", "/");
                    LibMpvNative.Command(_mpvHandle, new[] { "loadfile", safePath, "replace" });
                }
                
                return true;
            }
            catch (Exception ex)
            {
                var extraMsg = "";
                if (ex.Message.Contains("0x8007045A") || ex.Message.Contains("Unable to load DLL"))
                {
                    extraMsg = " -> POSSIBLE CAUSE: Missing Visual C++ Redistributable 2015-2022. Please install it.";
                }

                _logger.LogError($"Failed to initialize LibMpv: {ex.Message}. Ensure libmpv-2.dll is present.{extraMsg}");
                if (_form != null && !_form.IsDisposed)
                {
                    _form.Invoke(() => _form.Close());
                }
                return false;
            }
        }
        
        private void CheckError(int status)
        {
            if (status < 0)
            {
                // Simple error check
                _logger.LogWarning($"LibMpv Error: {status}");
            }
        }

        // --- Commands ---

        public async Task SendCommandAsync(string command, bool retry = true)
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;

            try 
            {
                // Parse legacy JSON commands
                if (string.IsNullOrWhiteSpace(command)) return;

                using var doc = JsonDocument.Parse(command);
                if (doc.RootElement.TryGetProperty("command", out var cmdElement) && cmdElement.ValueKind == JsonValueKind.Array)
                {
                     var args = cmdElement.EnumerateArray().Select(e => e.ToString()).ToArray();
                     ExecuteCommand(args);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[MPV] Failed to parse/execute legacy command: {ex.Message}");
            }
            
            await Task.CompletedTask;
        }
        
        private void ExecuteCommand(string[] args)
        {
             if (_mpvHandle == IntPtr.Zero) return;
             
             CheckError(LibMpvNative.Command(_mpvHandle, args));
        }

        public void Stop()
        {
            if (_isInitialized && _mpvHandle != IntPtr.Zero)
            {
                LibMpvNative.Command(_mpvHandle, new[] { "stop" });
                // Also clear playlist to be sure
                LibMpvNative.Command(_mpvHandle, new[] { "playlist-clear" });
            }

            if (_form != null && !_form.IsDisposed)
            {
                // EN: Hide the form to allow underlying windows (like dmdext) to be seen
                // FR: Cacher le formulaire pour permettre aux fenêtres sous-jacentes d'être vues
                try 
                {
                    _form.Invoke(() => _form.Hide()); 
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[MpvController] Error hiding form: {ex.Message}");
                }
            }
        }

        public async Task DisplayImage(string imagePath, bool loop = true, CancellationToken token = default)
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
            
            try
            {
                if (token.IsCancellationRequested) return;

                // EN: Ensure form is visible (in case it was stopped/hidden)
                // FR: S'assurer que le formulaire est visible
                if (_form != null && !_form.IsDisposed)
                {
                    _form.Invoke(() => 
                    {
                        if (!_form.Visible) _form.Show();
                    });
                }

                if (token.IsCancellationRequested) return;

                var safePath = imagePath.Replace("\\", "/");
                CheckError(LibMpvNative.mpv_set_property_string(_mpvHandle, "loop-file", loop ? "inf" : "no"));
                
                if (token.IsCancellationRequested) return;

                _logger.LogInformation($"[MPV] DisplayImage: Loading '{safePath}'");
                int res = LibMpvNative.Command(_mpvHandle, new[] { "loadfile", safePath, "replace" });
                CheckError(res);
                _logger.LogInformation($"[MPV] DisplayImage: LoadFile result = {res}");

                CheckError(LibMpvNative.mpv_set_property_string(_mpvHandle, "pause", "no"));
            }
            catch (Exception ex)
            {
                _logger.LogError($"[MPV] Error DisplayImage: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        public async Task PushRetroAchievementData(string type, params object[] values)
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;

            try 
            {
                var validValues = values.Select(v => v?.ToString() ?? "").ToList();
                var pipedData = string.Join("|", new[] { type }.Concat(validValues));
                
                ExecuteCommand(new[] { "script-message", "push-ra", pipedData });
                _logger.LogInformation($"Pushed RA Data: {type}");
            }
            catch (Exception ex)
            {
                 _logger.LogError($"[MPV] Error Push RA: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        public async Task ClearRetroAchievementData()
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;

            try 
            {
                // EN: Send explicit clear command to Lua script
                // FR: Envoyer une commande d'effacement explicite au script Lua
                ExecuteCommand(new[] { "script-message", "push-ra", "clear" });
                
                // EN: ALSO explicitly remove filter-based overlays (used for achievements)
                // FR: AUSSI supprimer explicitement les overlays basés sur des filtres (utilisés pour les succès)
                // Slots: 1 (Persistent), 3 (Stats), 4 (Achievement), 5 (Challenge)
                ExecuteCommand(new[] { "vf", "remove", "@ra_overlay_1" });
                ExecuteCommand(new[] { "vf", "remove", "@ra_overlay_3" });
                ExecuteCommand(new[] { "vf", "remove", "@ra_overlay_4" });
                ExecuteCommand(new[] { "vf", "remove", "@ra_overlay_5" });

                _logger.LogInformation("[MPV] Sent Clear RA Data command and removed filter overlays");
            }
            catch (Exception ex)
            {
                 _logger.LogError($"[MPV] Error Clear RA Data: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        public Task<string?> GetPropertyAsync(string propertyName)
        {
             if (!_isInitialized || _mpvHandle == IntPtr.Zero) return Task.FromResult<string?>(null);
             try
             {
                 return Task.FromResult(LibMpvNative.GetPropertyString(_mpvHandle, propertyName));
             }
             catch
             {
                 return Task.FromResult<string?>(null);
             }
        }

        public Task<double> GetCurrentTime()
        {
             if (!_isInitialized || _mpvHandle == IntPtr.Zero) return Task.FromResult(0.0);
             try
             {
                 var valStr = LibMpvNative.GetPropertyString(_mpvHandle, "time-pos");
                 if (!string.IsNullOrEmpty(valStr) && double.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
                 {
                     return Task.FromResult(d);
                 }
                 return Task.FromResult(0.0);
             }
             catch
             {
                 return Task.FromResult(0.0);
             }
        }

        // --- Overlay Logic (Identical Logic, New Implementation) ---

        public Task ShowOverlay(string imagePath, int durationMs, string position = "top-right")
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return Task.CompletedTask;

            try
            {
                _overlayCts?.Cancel();
                _overlayCts = new CancellationTokenSource();

                int nextSlot = (_currentOverlaySlot == 10) ? 11 : 10;
                string currentLabel = $"@ra_overlay_{_currentOverlaySlot}";
                string nextLabel = $"@ra_overlay_{nextSlot}";
                
                var safePath = imagePath.Replace("\\", "/"); 
                
                string overlayPosition;
                if (position == "bottom-left") overlayPosition = "0:main_h-overlay_h";
                else if (position == "top-left") overlayPosition = "0:0";
                else if (position == "center") overlayPosition = "(main_w-overlay_w)/2:(main_h-overlay_h)/2";
                else overlayPosition = "main_w-overlay_w-20:20"; // top-right with margin
                
                int w = _config.MarqueeWidth;
                int h = _config.MarqueeHeight;
                
                // Filter Graph
                // Note: Restoring [in] and [out] pads and using escaped quotes as per reference implementation.
                var filterGraph = $"[in]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}:(iw-ow)/2:(ih-oh)/2[bg];movie=\\'{safePath}\\'[logo];[bg][logo]overlay={overlayPosition}[out]";
                
                // 1. Add NEW overlay
                // Restore outer [] as they are part of lavfi-bridge syntax for mpv
                var commandArg = $"{nextLabel}:lavfi=[{filterGraph}]";
                _logger.LogInformation($"[MpvController] Applying Overlay: {commandArg}");
                
                CheckError(LibMpvNative.Command(_mpvHandle, new[] { "vf", "add", commandArg }));
                
                // 2. Remove OLD overlay
                if (_currentOverlaySlot != 0)
                {
                    ExecuteCommand(new[] { "vf", "remove", currentLabel });
                }
                
                _currentOverlaySlot = nextSlot;
                _logger.LogInformation($"[MPV] Swapped to Overlay Slot {nextSlot}");

                // Timer to remove
                var token = _overlayCts.Token;
                _ = Task.Delay(durationMs, token).ContinueWith(async t => 
                {
                    if (t.IsCanceled) return;
                    await RemoveOverlay(false); // don't cancel this timer as we are inside it
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[MPV] Error showing overlay: {ex.Message}");
            }
            return Task.CompletedTask;
        }
        
         public async Task ShowAchievementNotification(string cupPath, string finalOverlayPath, int cupDuration = 2000, int finalDuration = 8000)
         {
             // 1. Show Cup
             if (!string.IsNullOrEmpty(cupPath) && File.Exists(cupPath))
             {
                 await ShowOverlay(cupPath, cupDuration + 500, "center"); 
                 await Task.Delay(cupDuration);
             }
             
             // 2. Show Final
             if (!string.IsNullOrEmpty(finalOverlayPath) && File.Exists(finalOverlayPath))
             {
                 await ShowOverlay(finalOverlayPath, finalDuration + 2000, "top-left");
                 await Task.Delay(finalDuration); 
             }
         }

        public Task RemoveOverlay(bool cancelTimer = true)
        {
             if (!_isInitialized || _mpvHandle == IntPtr.Zero) return Task.CompletedTask;
             try
             {
                if (cancelTimer) _overlayCts?.Cancel();

                if (_currentOverlaySlot != 0)
                {
                    var label = $"@ra_overlay_{_currentOverlaySlot}";
                    ExecuteCommand(new[] { "vf", "remove", label });
                    _currentOverlaySlot = 0; 
                }
             }
             catch { }
             return Task.CompletedTask;
        }

        public Task OverlayImage(string imagePath, int id, string position = "top-left")
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return Task.CompletedTask;

            try
            {
                var label = $"@ra_overlay_{id}";
                var safePath = imagePath.Replace("\\", "/"); 
                
                // Assuming full screen overlay matching marquee resolution
                int w = _config.MarqueeWidth;
                int h = _config.MarqueeHeight;

                // 1. Remove existing overlay with same ID if exists (to update it)
                ExecuteCommand(new[] { "vf", "remove", label });

                // 2. Add new overlay
                string overlayPosition;
                if (position.Contains(":")) overlayPosition = position; // Custom X:Y
                else if (position == "bottom-left") overlayPosition = "0:main_h-overlay_h";
                else if (position == "top-left") overlayPosition = "0:0";
                else if (position == "center") overlayPosition = "(main_w-overlay_w)/2:(main_h-overlay_h)/2";
                else overlayPosition = "main_w-overlay_w-20:20"; // top-right with margin
                
                // EN: Optimized Filter: Avoid scale/crop of the background video [in] to save massive CPU/GPU on older hw.
                // FR: Filtre optimisé : Éviter le scale/crop de la vidéo de fond [in] pour économiser énormément de CPU/GPU sur vieux matériel.
                // We overlay directly on [in].
                
                // EN: REMOVED Forced loop=0. It caused freezes (on GIFs) and High CPU (on PNGs). 
                // We rely on the media file itself being properly generated with loop metadata (which we do for Scrolling GIFs).
                // FR: SUPPRIMÉ loop=0 forcé. Causait des figeages (GIFs) et CPU élevé (PNGs).
                // On se fie aux métadonnées du fichier (ce que l'on fait pour les GIFs défilants).

                var filterGraph = $"movie=\\'{safePath}\\'[logo];[in][logo]overlay={overlayPosition}[out]";

                // EN: Ensure player is not paused (can happen with some static image loops)
                // FR: S'assurer que le lecteur n'est pas en pause
                LibMpvNative.mpv_set_property_string(_mpvHandle, "pause", "no");

                var commandArg = $"{label}:lavfi=[{filterGraph}]";
                if (id == 4 || id == 3) _logger.LogInformation($"[MpvController] Adding persistent overlay {id}: {commandArg}");
                
                CheckError(LibMpvNative.Command(_mpvHandle, new[] { "vf", "add", commandArg }));
            }
            catch (Exception ex)
            {
                _logger.LogError($"[MPV] Error OverlayImage {id}: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public Task RemoveOverlay(int id, bool cancelTimer = false)
        {
             if (!_isInitialized || _mpvHandle == IntPtr.Zero) return Task.CompletedTask;
             try
             {
                if (cancelTimer) _overlayCts?.Cancel();
                var label = $"@ra_overlay_{id}";
                ExecuteCommand(new[] { "vf", "remove", label });
                
                // EN: If we explicitly remove the current active slot, reset it
                // FR: Si on supprime explicitement le slot actif, le réinitialiser
                if (id == _currentOverlaySlot) _currentOverlaySlot = 0;
             }
             catch { }
             return Task.CompletedTask;
        }

        public async Task RefreshPlayer()
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
            try
            {
                // EN: Force a frame step to redraw OSD/Overlays even if static
                // FR: Forcer un pas d'image pour redessiner l'OSD/Overlays même si statique
                LibMpvNative.Command(_mpvHandle, new[] { "frame-step" });
                
                // EN: Ensure strictly unpaused
                // FR: S'assurer strictement que la pause est désactivée
                CheckError(LibMpvNative.mpv_set_property_string(_mpvHandle, "pause", "no"));
            }
            catch (Exception ex)
            {
                _logger.LogError($"[MPV] RefreshPlayer error: {ex.Message}");
            }
            await Task.CompletedTask;
        }

        public void Dispose()
        {
             if (_mpvHandle != IntPtr.Zero)
             {
                 // Terminate
                 LibMpvNative.mpv_terminate_destroy(_mpvHandle);
                 _mpvHandle = IntPtr.Zero;
             }
             
             if (_form != null && !_form.IsDisposed)
             {
                 if (_form.InvokeRequired) _form.Invoke(() => _form.Close());
                 else _form.Close();
             }
        }
    }
}
