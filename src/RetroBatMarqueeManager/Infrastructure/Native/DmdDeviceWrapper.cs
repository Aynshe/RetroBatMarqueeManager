using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace RetroBatMarqueeManager.Infrastructure.Native
{
    public class DmdDeviceWrapper : IDisposable
    {
        private readonly ILogger<DmdDeviceWrapper> _logger;
        private IntPtr _dllHandle = IntPtr.Zero;

        // Delegates for Native Functions
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenDelegate();
        
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool CloseDelegate();
        
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RenderDelegate(ushort width, ushort height, IntPtr buffer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]

        private delegate void SetDmdDeviceConfigDelegate(string path);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EnableUpscalingDelegate(IntPtr handle);  // ZeDMD_EnableUpscaling specific? Or is it Device_EnableUpscaling?

        // ZeDMD specific exports
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ZeDMD_GetInstanceDelegate();
        
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ZeDMD_EnableUpscalingDelegate(IntPtr instance);

        private OpenDelegate? _open;
        private CloseDelegate? _close;
        private RenderDelegate? _render;
        private ZeDMD_GetInstanceDelegate? _zeDmdGetInstance;
        private ZeDMD_EnableUpscalingDelegate? _zeDmdEnableUpscaling;

        public bool IsLoaded => _dllHandle != IntPtr.Zero;
        public string RenderMethodName { get; private set; } = string.Empty;

        public DmdDeviceWrapper(ILogger<DmdDeviceWrapper> logger)
        {
            _logger = logger;
        }

        public bool Load(string folderPath)
        {
            if (IsLoaded) return true;

            string dllName = Environment.Is64BitProcess ? "DmdDevice64.dll" : "DmdDevice.dll";
            string fullPath = Path.Combine(folderPath, dllName);

            // Fallback check
            if (!File.Exists(fullPath))
            {
                // Try alternate bitness if preferred not found? usually strict.
                // Try simplified name "DmdDevice.dll" even on 64bit if specific 64 file missing? 
                // Some distributions just name it DmdDevice.dll
                string altPath = Path.Combine(folderPath, "DmdDevice.dll");
                if (File.Exists(altPath)) fullPath = altPath;
            }

            if (!File.Exists(fullPath))
            {
                _logger.LogError($"DmdDevice DLL not found at: {fullPath}");
                return false;
            }

            try
            {
                // Ensure dependencies are found? usually DmdDevice is standalone or has local deps.
                // Loading with LoadWithAlteredSearchPath might be needed if it has dependencies in same folder.
                _dllHandle = NativeLibrary.Load(fullPath);
                
                if (_dllHandle == IntPtr.Zero)
                {
                    _logger.LogError("Failed to load DmdDevice library (handle is zero).");
                    return false;
                }

                _logger.LogInformation($"Loaded DmdDevice library from: {fullPath}");

                // Map Functions
                _open = LoadFunction<OpenDelegate>("Open");
                if (_open == null) _logger.LogError("Failed to find export 'Open'");

                _close = LoadFunction<CloseDelegate>("Close");
                if (_close == null) _logger.LogError("Failed to find export 'Close'");

                if (_close == null) _logger.LogError("Failed to find export 'Close'");

                // Try to map ZeDMD specific functions (optional)
                _zeDmdGetInstance = LoadFunction<ZeDMD_GetInstanceDelegate>("ZeDMD_GetInstance");
                _zeDmdEnableUpscaling = LoadFunction<ZeDMD_EnableUpscalingDelegate>("ZeDMD_EnableUpscaling");

                if (_zeDmdEnableUpscaling != null) _logger.LogInformation("Mapped ZeDMD_EnableUpscaling.");
                string[] renderNames = new[] { "Render_RGB24", "Render_RGB", "Render_RGBA", "Render", "PM_Render", "Render_16_Shades", "Render_4_Shades", "Render_Grey" };
                foreach (var name in renderNames)
                {
                    _render = LoadFunction<RenderDelegate>(name);
                    if (_render != null) 
                    {
                        RenderMethodName = name;
                        _logger.LogInformation($"Successfully mapped Render function: '{name}'");
                        break;
                    }
                }
                
                if (_render == null) _logger.LogError($"Failed to find any known Render export ({string.Join(", ", renderNames)})");

                if (_open == null || _close == null || _render == null)
                {
                    _logger.LogError("Failed to map required functions (Open, Close, Render) from DmdDevice DLL.");
                    Unload();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading DmdDevice DLL: {ex.Message}");
                Unload();
                return false;
            }
        }

        private T? LoadFunction<T>(string functionName) where T : Delegate
        {
            try
            {
                if (NativeLibrary.TryGetExport(_dllHandle, functionName, out IntPtr procAddress))
                {
                    return Marshal.GetDelegateForFunctionPointer<T>(procAddress);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Function {functionName} not found in DLL: {ex.Message}");
            }
            return null;
        }

        public int Open()
        {
            if (!IsLoaded || _open == null) return -1;
            try
            {
                return _open();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error calling Open(): {ex.Message}");
                return -1;
            }
        }



        public void EnableUpscaling()
        {
            if (_zeDmdGetInstance != null && _zeDmdEnableUpscaling != null)
            {
                try
                {
                    var instance = _zeDmdGetInstance();
                    if (instance != IntPtr.Zero)
                    {
                        _zeDmdEnableUpscaling(instance);
                        _logger.LogInformation("Called ZeDMD_EnableUpscaling.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error calling ZeDMD_EnableUpscaling: {ex.Message}");
                }
            }
        }

        public void Close()
        {
            if (!IsLoaded || _close == null) return;
            try
            {
                _close();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error calling Close(): {ex.Message}");
            }
        }

        public void Render(ushort width, ushort height, byte[] buffer)
        {
            if (!IsLoaded || _render == null) return;
            
            // Pin buffer
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                _render(width, height, handle.AddrOfPinnedObject());
            }
            catch (Exception)
            {
                 // Log flood protection?
                 // _logger.LogError($"Error rendering frame: {ex.Message}"); 
            }
            finally
            {
                handle.Free();
            }
        }

        private void Unload()
        {
            if (_dllHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_dllHandle);
                _dllHandle = IntPtr.Zero;
            }
            _open = null;
            _close = null;
            _render = null;
        }

        public void Dispose()
        {
            Close();
            Unload();
            GC.SuppressFinalize(this);
        }
    }
}
