
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

        // Overlay & State
        private CancellationTokenSource? _overlayCts;
        private int _currentOverlaySlot = 0; 
        private readonly SemaphoreSlim _overlaySemaphore = new(1, 1); // EN: Serialize overlay updates / FR: Sérialiser les MAJ d'overlay
        private Thread? _eventThread;
        private bool _isEventLoopRunning = false;
        
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
                     _logger.LogWarning("[UI Thread] MPV Initialization failed.");
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
                _mpvHandle = LibMpvNative.mpv_create();
                if (_mpvHandle == IntPtr.Zero) throw new Exception("mpv_create failed");

                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "wid", windowHandle.ToInt64().ToString()));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "idle", "yes"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "keep-open", "yes"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "vo", "gpu"));
                
                var hwDec = _config.MpvHwDecoding;
                if (!string.IsNullOrEmpty(hwDec) && !hwDec.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    if (hwDec.Equals("d3d11va", StringComparison.OrdinalIgnoreCase)) hwDec = "d3d11va-copy";
                    else if (hwDec.Equals("dxva2", StringComparison.OrdinalIgnoreCase)) hwDec = "dxva2-copy";
                }
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "hwdec", hwDec));
                if (!string.IsNullOrEmpty(hwDec) && !hwDec.Equals("no", StringComparison.OrdinalIgnoreCase))
                {
                    CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "hwdec-codecs", "all"));
                    CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "vd-lavc-fast", "yes"));
                }
                
                var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "lua");
                if (Directory.Exists(scriptPath))
                {
                     CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "scripts", scriptPath));
                }

                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "osd-level", "1"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "script-opts", "osc-visibility=never,osc-layout=box,osc-seekbarstyle=bar"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "mute", "yes"));
                CheckError(LibMpvNative.mpv_set_option_string(_mpvHandle, "loop-file", "inf"));

                _logger.LogInformation($"[MPV] Initializing with handle on window {windowHandle}");
                int initStatus = LibMpvNative.mpv_initialize(_mpvHandle);
                if (initStatus < 0) 
                {
                    _logger.LogError($"[MPV] mpv_initialize FAILED: {initStatus}");
                    return false;
                }
                
                _isInitialized = true;
                _logger.LogInformation($"[MPV] Initialization complete. Handle: {_mpvHandle}");

                // EN: Start Event Loop Thread / FR: Démarrer le thread de la boucle d'événements
                _isEventLoopRunning = true;
                _eventThread = new Thread(EventLoop);
                _eventThread.IsBackground = true;
                _eventThread.Name = "MpvEventLoop";
                _eventThread.Start();
                
                if (!string.IsNullOrEmpty(_config.DefaultImagePath) && File.Exists(_config.DefaultImagePath))
                {
                    var safePath = _config.DefaultImagePath.Replace("\\", "/");
                    _logger.LogInformation($"[MPV] Loading default image: {safePath}");
                    LibMpvNative.Command(_mpvHandle, new[] { "loadfile", safePath, "replace" });
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize LibMpv: {ex.Message}");
                return false;
            }
        }

        private void EventLoop()
        {
            _logger.LogInformation("[MPV] Event loop started.");
            while (_isEventLoopRunning && _mpvHandle != IntPtr.Zero)
            {
                var eventPtr = LibMpvNative.mpv_wait_event(_mpvHandle, 0.1); // Wait 100ms
                if (eventPtr == IntPtr.Zero) 
                {
                     // EN: NULL means the context is being destroyed or invalid
                     // FR: NULL signifie que le contexte est détruit ou invalide
                     if (_isEventLoopRunning) _logger.LogWarning("[MPV] mpv_wait_event returned NULL while loop should be running.");
                     break;
                }

                var evt = Marshal.PtrToStructure<LibMpvNative.mpv_event>(eventPtr);
                if (evt.event_id == 0) continue; // MPV_EVENT_NONE
                
                if (evt.event_id == 6) // MPV_EVENT_SHUTDOWN
                {
                    _logger.LogInformation($"[MPV] Shutdown event received (Status: {evt.error}, Running: {_isEventLoopRunning}).");
                    if (_isEventLoopRunning)
                    {
                         _logger.LogWarning("[MPV] UNEXPECTED SHUTDOWN. MPV might have crashed or been closed externally.");
                    }
                    break;
                }
            }
            _logger.LogInformation("[MPV] Event loop stopped.");
            _isEventLoopRunning = false;
        }
        
        private void CheckError(int status)
        {
            if (status < 0) _logger.LogWarning($"LibMpv Error: {status}");
        }

        public async Task SendCommandAsync(string command, bool retry = true)
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
            try 
            {
                if (string.IsNullOrWhiteSpace(command)) return;
                using var doc = JsonDocument.Parse(command);
                if (doc.RootElement.TryGetProperty("command", out var cmdElement) && cmdElement.ValueKind == JsonValueKind.Array)
                {
                     var args = cmdElement.EnumerateArray().Select(e => e.ToString()).ToArray();
                     ExecuteCommand(args);
                }
            }
            catch { }
            await Task.CompletedTask;
        }
        
        private void ExecuteCommand(string[] args)
        {
             if (_mpvHandle == IntPtr.Zero) return;
             CheckError(LibMpvNative.Command(_mpvHandle, args));
        }

        public async Task Stop()
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
            
            await _overlaySemaphore.WaitAsync();
            try
            {
                _overlayCts?.Cancel();
                LibMpvNative.Command(_mpvHandle, new[] { "stop" });
                LibMpvNative.Command(_mpvHandle, new[] { "playlist-clear" });
                
                if (_form != null && !_form.IsDisposed)
                {
                    try { _form.Invoke(() => _form.Hide()); } catch { }
                }
            }
            finally
            {
                _overlaySemaphore.Release();
            }
        }

        public async Task DisplayImage(string imagePath, bool loop = true, CancellationToken token = default)
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
            
            await _overlaySemaphore.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested) return;
                if (_form != null && !_form.IsDisposed)
                {
                    _form.Invoke(() => { if (!_form.Visible) _form.Show(); });
                }

                var safePath = imagePath.Replace("\\", "/");
                LibMpvNative.mpv_set_property_string(_mpvHandle, "loop-file", loop ? "inf" : "no");
                _logger.LogInformation($"[MPV] DisplayImage: Loading '{safePath}'");
                ExecuteCommand(new[] { "loadfile", safePath, "replace" });
                LibMpvNative.mpv_set_property_string(_mpvHandle, "pause", "no");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError($"[MPV] Error DisplayImage: {ex.Message}"); }
            finally
            {
                _overlaySemaphore.Release();
            }
        }

        public async Task PushRetroAchievementData(string type, params object[] values)
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
            try 
            {
                var pipedData = string.Join("|", new[] { type }.Concat(values.Select(v => v?.ToString() ?? "")));
                ExecuteCommand(new[] { "script-message", "push-ra", pipedData });
            }
            catch { }
            await Task.CompletedTask;
        }

        public async Task ClearRetroAchievementData()
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
            
            await _overlaySemaphore.WaitAsync();
            try
            {
                _overlayCts?.Cancel();
                for (int i = 1; i <= 12; i++)
                {
                    ExecuteCommand(new[] { "vf", "remove", $"@ra_overlay_{i}" });
                }
                _currentOverlaySlot = 0;
                _logger.LogInformation("[MPV] Cleared all RA overlays.");
            }
            finally
            {
                _overlaySemaphore.Release();
            }
        }

        public Task<string?> GetPropertyAsync(string propertyName)
        {
             if (!_isInitialized || _mpvHandle == IntPtr.Zero) return Task.FromResult<string?>(null);
             try { return Task.FromResult(LibMpvNative.GetPropertyString(_mpvHandle, propertyName)); }
             catch { return Task.FromResult<string?>(null); }
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
             }
             catch { }
             return Task.FromResult(0.0);
        }

        // --- Low-level Overlay (Persistent/Template base) ---
        public async Task OverlayImage(string imagePath, int slot, string position = "0:0", bool isPersistent = true, int loopCount = 0)
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero || string.IsNullOrEmpty(imagePath)) return;

            await _overlaySemaphore.WaitAsync();
            try
            {
                var label = isPersistent ? $"@ra_overlay_{slot}" : "@preview_overlay";
                var safePath = imagePath.Replace("\\", "/");
                var ffmpegPath = safePath; // EN: Simple path, single quotes provide enough safety in movie filter / FR: Chemin simple, les guillemets simples suffisent
                var ext = Path.GetExtension(imagePath).ToLowerInvariant();

                ExecuteCommand(new[] { "vf", "remove", label });

                string overlayPos = GetMpvPosition(position);
                
                // EN: Reverted to simple movie filter without explicit loop count to avoid MPV stops/freezes.
                // FR: Retour au filtre simple sans boucle explicite pour éviter les arrêts/gels MPV.
                var filterGraph = $"movie=\\'{ffmpegPath}\\'[logo];[in][logo]overlay={overlayPos}[out]";

                _logger.LogInformation($"[MpvController] Adding overlay {slot}: {label}:lavfi=[{filterGraph.Replace("\\'", "'")}]");

                // EN: Ensure unpaused BEFORE adding overlay to prevent timing issues
                // FR: S'assurer que ce n'est pas en pause AVANT d'ajouter l'overlay
                CheckError(LibMpvNative.mpv_set_property_string(_mpvHandle, "pause", "no"));

                ExecuteCommand(new[] { "vf", "add", $"{label}:lavfi=[{filterGraph}]" });
                
                if (!isPersistent) _ = Task.Delay(5000).ContinueWith(_ => RemoveOverlay(slot));
            }
            catch (Exception ex) { _logger.LogError($"[MPV] Error OverlayImage {slot}: {ex.Message}"); }
            finally { _overlaySemaphore.Release(); }
        }

        private string GetMpvPosition(string position)
        {
            if (string.IsNullOrEmpty(position)) return "0:0";
            if (position.Contains(":")) return position; // Coordinate format

            return position.ToLowerInvariant() switch
            {
                "top-left" => "0:0",
                "top-right" => "main_w-overlay_w-20:20",
                "bottom-left" => "20:main_h-overlay_h-20",
                "bottom-right" => "main_w-overlay_w-20:main_h-overlay_h-20",
                "center" => "(main_w-overlay_w)/2:(main_h-overlay_h)/2",
                _ => "0:0"
            };
        }

        // --- High-level Achievement Overlays (Ping-Pong) ---
        public async Task ShowOverlay(string imagePath, int durationMs, string position = "top-right")
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;

            await _overlaySemaphore.WaitAsync();
            try
            {
                _overlayCts?.Cancel();
                _overlayCts = new CancellationTokenSource();

                int nextSlot = (_currentOverlaySlot == 10) ? 11 : 10;
                string currentLabel = $"@ra_overlay_{_currentOverlaySlot}";
                string nextLabel = $"@ra_overlay_{nextSlot}";
                
                var safePath = imagePath.Replace("\\", "/");
                var ffmpegPath = safePath;

                string overlayPosition = GetMpvPosition(position);
                
                int w = _config.MarqueeWidth;
                int h = _config.MarqueeHeight;
                
                // Composite with scaling to ensure full screen coverage of the background if needed
                var filterGraph = $"[in]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}:(iw-ow)/2:(ih-oh)/2[bg];movie=\\'{ffmpegPath}\\'[logo];[bg][logo]overlay={overlayPosition}[out]";
                
                CheckError(LibMpvNative.Command(_mpvHandle, new[] { "vf", "add", $"{nextLabel}:lavfi=[{filterGraph}]" }));
                
                if (_currentOverlaySlot != 0) ExecuteCommand(new[] { "vf", "remove", currentLabel });
                
                _currentOverlaySlot = nextSlot;

                var token = _overlayCts.Token;
                _ = Task.Delay(durationMs, token).ContinueWith(async t => 
                {
                    if (t.IsCanceled) return;
                    await RemoveOverlay(false); 
                });
            }
            catch (Exception ex) { _logger.LogError($"[MPV] Error ShowOverlay: {ex.Message}"); }
            finally { _overlaySemaphore.Release(); }
        }

        public async Task ShowAchievementNotification(string cupPath, string finalOverlayPath, int cupDuration = 2000, int finalDuration = 8000)
        {
            if (!string.IsNullOrEmpty(cupPath) && File.Exists(cupPath))
            {
                await ShowOverlay(cupPath, cupDuration + 500, "center"); 
                await Task.Delay(cupDuration);
            }
            if (!string.IsNullOrEmpty(finalOverlayPath) && File.Exists(finalOverlayPath))
            {
                await ShowOverlay(finalOverlayPath, finalDuration + 2000, "top-left");
                await Task.Delay(finalDuration); 
            }
        }

        public async Task RemoveOverlay(int id, bool cancelTimer = false)
        {
             if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
             
             await _overlaySemaphore.WaitAsync();
             try
             {
                if (cancelTimer) _overlayCts?.Cancel();
                ExecuteCommand(new[] { "vf", "remove", $"@ra_overlay_{id}" });
                if (id == _currentOverlaySlot) _currentOverlaySlot = 0;
             }
             catch (Exception ex) 
             {
                 _logger.LogWarning($"[MPV] Error removing overlay {id}: {ex.Message}");
             }
             finally
             {
                 _overlaySemaphore.Release();
             }
        }

        public async Task RemoveOverlay(bool cancelTimer = true) // Overload used by timer
        {
             if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
             
             await _overlaySemaphore.WaitAsync();
             try
             {
                if (cancelTimer) _overlayCts?.Cancel();
                if (_currentOverlaySlot != 0)
                {
                    ExecuteCommand(new[] { "vf", "remove", $"@ra_overlay_{_currentOverlaySlot}" });
                    _currentOverlaySlot = 0; 
                }
             }
             catch (Exception ex)
             {
                 _logger.LogWarning($"[MPV] Error removing active overlay: {ex.Message}");
             }
             finally
             {
                 _overlaySemaphore.Release();
             }
        }

        public async Task RefreshPlayer()
        {
            if (!_isInitialized || _mpvHandle == IntPtr.Zero) return;
            ExecuteCommand(new[] { "frame-step" });
            CheckError(LibMpvNative.mpv_set_property_string(_mpvHandle, "pause", "no"));
            await Task.CompletedTask;
        }

        public void Dispose()
        {
             _isEventLoopRunning = false;
             
             if (_mpvHandle != IntPtr.Zero)
             {
                 LibMpvNative.mpv_terminate_destroy(_mpvHandle);
                 _mpvHandle = IntPtr.Zero;
             }
             
             _eventThread?.Join(1000); // Wait for thread to exit
             
             if (_form != null && !_form.IsDisposed)
             {
                 if (_form.InvokeRequired) _form.Invoke(() => _form.Close());
                 else _form.Close();
             }
             
             _overlaySemaphore.Dispose();
        }
    }
}
