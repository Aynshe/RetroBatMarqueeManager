using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace RetroBatMarqueeManager.Infrastructure.Processes
{
    /// <summary>
    /// Uses NativeLibrary.SetDllImportResolver to route "libmpv-2.dll" 
    /// to either the v3 (AVX2 optimized) or v2 (Standard/Compat) version
    /// based on the Host CPU capabilities.
    /// </summary>
    public static class LibMpvResolver
    {
        private static IntPtr _loadedHandle = IntPtr.Zero;

        public static void Register()
        {
            NativeLibrary.SetDllImportResolver(typeof(LibMpvResolver).Assembly, ResolveDll);
        }

        public static string ResolutionLog { get; private set; } = "Not initialized";

        private static IntPtr ResolveDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            // Only handle libmpv-2.dll
            if (libraryName != "libmpv-2.dll")
            {
                return IntPtr.Zero;
            }

            // Return if already loaded
            if (_loadedHandle != IntPtr.Zero)
            {
                return _loadedHandle;
            }

            var logBuilder = new System.Text.StringBuilder();
            logBuilder.AppendLine("Starting LibMpv Resolution...");

            // Determine which version to load
            string version = "v2"; // Default (Standard)
            string reason = "Standard compatibility mode";

            // Check for AVX2 support (Haswell+, Ryzen)
            if (Avx2.IsSupported)
            {
                version = "v3";
                reason = "AVX2 instruction set detected (High Performance)";
            }
            
            // Construct path: [AppDir]/libmpv/[v2|v3]/libmpv-2.dll
            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libmpv", version, "libmpv-2.dll");

            // Fallback safety: if v3 was chosen but not found, try v2
            if (!File.Exists(dllPath) && version == "v3")
            {
                logBuilder.AppendLine($"[Warning] Optimized v3 DLL not found at '{dllPath}'. Falling back to v2.");
                version = "v2";
                reason += " (Fallback: configured v3 missing)";
                dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libmpv", "v2", "libmpv-2.dll");
            }

            if (File.Exists(dllPath))
            {
                logBuilder.AppendLine($"[Success] Selected Version: {version}");
                logBuilder.AppendLine($"Description: {reason}");
                logBuilder.AppendLine($"Loading Path: {dllPath}");
                
                // Explicitly load it
                _loadedHandle = NativeLibrary.Load(dllPath);
                
                ResolutionLog = logBuilder.ToString();
                return _loadedHandle;
            }

            // If we are here, we couldn't find the file.
            logBuilder.AppendLine($"[CRITICAL] Failed to find ANY libmpv-2.dll at predicted path: {dllPath}");
            ResolutionLog = logBuilder.ToString();
            
            return IntPtr.Zero;
        }
    }
}
