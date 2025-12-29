using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using System.Linq;

namespace RetroBatMarqueeManager.Launcher
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // EN: Ultra-early logging to diagnose startup issues
            // FR: Logging très précoce pour diagnostiquer problèmes de démarrage
            try
            {
                var startupLog = Path.Combine(Path.GetTempPath(), "RetroBatMarqueeManager_LauncherStartup.log");
                File.AppendAllText(startupLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Launcher started with {args.Length} args\n");
            }
            catch { /* Ignore logging errors */ }
            
            // 1. Check for .NET 9 Desktop Runtime
            if (!IsNet9DesktopInstalled())
            {
                // EN: In headless mode (no explorer.exe), MessageBox won't work - log to file instead
                // FR: En mode headless (pas d'explorer.exe), MessageBox ne marche pas - log dans fichier
                LogLauncher("ERROR: .NET 9 Desktop Runtime not found");
                
                // Try to open help page (will fail silently in headless)
                var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Install_DotNet9.html");
                if (File.Exists(htmlPath))
                {
                    try { Process.Start(htmlPath); }
                    catch { /* Headless mode - ignore */ }
                }
                else
                {
                    try { Process.Start("https://dotnet.microsoft.com/en-us/download/dotnet/9.0"); }
                    catch { /* Headless mode - ignore */ }
                }
                return; // Exit Launcher
            }

            // 2. Locate Application
            var appPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RetroBatMarqueeManager.App.exe");
            if (!File.Exists(appPath))
            {
                LogLauncher($"FATAL ERROR: Application not found at {appPath}");
                return;
            }

            // 3. Watchdog Loop - Monitor and Auto-Restart on Crash
            // EN: Intelligent crash monitoring with consecutive crash limit
            // FR: Monitoring intelligent des crashs avec limite de crashs consécutifs
            int consecutiveCrashes = 0;
            DateTime lastSuccessfulStart = DateTime.Now;
            const int MAX_CONSECUTIVE_CRASHES = 3;
            const int RESET_WINDOW_MINUTES = 5;

            LogLauncher($"Starting watchdog for {Path.GetFileName(appPath)}");

            while (true)
            {
                LogLauncher($"Launch attempt #{consecutiveCrashes + 1} (consecutive crashes: {consecutiveCrashes})");
                
                var startTime = DateTime.Now;
                int exitCode = LaunchAndMonitor(appPath, args);
                var runDuration = DateTime.Now - startTime;

                LogLauncher($"Process exited with code {exitCode} after {runDuration.TotalSeconds:F1}s");

                // Check if graceful shutdown (exit code 0 or marker file exists)
                if (IsGracefulShutdown(exitCode))
                {
                    LogLauncher("Graceful shutdown detected - stopping watchdog");
                    break; // Stop launcher
                }

                // Crash detected
                consecutiveCrashes++;
                LogLauncher($"Crash detected (total consecutive: {consecutiveCrashes})");

                // Reset counter if app ran successfully for long enough
                if (runDuration.TotalMinutes >= RESET_WINDOW_MINUTES)
                {
                    LogLauncher($"App ran for {runDuration.TotalMinutes:F1} minutes - resetting crash counter");
                    consecutiveCrashes = 1; // Reset to 1 (current crash counts)
                }

                // Check crash limit
                if (consecutiveCrashes >= MAX_CONSECUTIVE_CRASHES)
                {
                    LogLauncher($"Crash limit reached ({MAX_CONSECUTIVE_CRASHES}) - stopping auto-restart");
                    MessageBox.Show(
                        $"L'application a crashé {MAX_CONSECUTIVE_CRASHES} fois consécutivement.\n\n" +
                        "Le redémarrage automatique a été désactivé.\n" +
                        "Veuillez consulter les logs (debug.log) pour diagnostiquer le problème.",
                        "Limite de Crashs Atteinte",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    break; // Stop auto-restart
                }

                // Exponential backoff before restart
                int delayMs = 2000 * consecutiveCrashes; // 2s, 4s, 6s
                LogLauncher($"Waiting {delayMs}ms before restart...");
                System.Threading.Thread.Sleep(delayMs);

                lastSuccessfulStart = DateTime.Now;
            }

            LogLauncher("Watchdog stopped");
        }

        static bool ShouldMinimize()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;
                        
                        var parts = trimmed.Split('=');
                        if (parts.Length >= 2)
                        {
                            if (parts[0].Trim().Equals("MinimizeToTray", StringComparison.OrdinalIgnoreCase))
                            {
                                return parts[1].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        static string EscapeArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            // If it contains spaces or quotes, wrap in quotes and escape existing quotes
            if (arg.Contains(" ") || arg.Contains("\"") || arg.Contains("\t"))
            {
                // Escape existing quotes with backslash
                // And wrap the whole thing in quotes
                return "\"" + arg.Replace("\"", "\\\"") + "\""; 
            }
            return arg;
        }

        /// <summary>
        /// EN: Launch application and monitor until exit
        /// FR: Lancer l'application et surveiller jusqu'à la fin
        /// </summary>
        static int LaunchAndMonitor(string appPath, string[] args)
        {
            try
            {
                // Pass through arguments with proper quoting for spaces
                var escapedArgs = args.Select(a => EscapeArgument(a));
                var argumentsString = string.Join(" ", escapedArgs);

                // Check if --tray argument is present (for silent startup)
                bool isTrayMode = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
                bool shouldHide = isTrayMode || ShouldMinimize();

                var startInfo = new ProcessStartInfo
                {
                    FileName = appPath,
                    Arguments = argumentsString,
                    UseShellExecute = false,
                    CreateNoWindow = shouldHide, // Hide console if --tray or configured in config.ini
                    WindowStyle = shouldHide ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal // Hide window completely
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        LogLauncher("ERROR: Failed to start process");
                        return -1;
                    }

                    process.WaitForExit();
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                LogLauncher($"ERROR launching app: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// EN: Check if shutdown was graceful (exit code 0 or marker file present)
        /// FR: Vérifier si l'arrêt était gracieux (code 0 ou fichier marker présent)
        /// </summary>
        static bool IsGracefulShutdown(int exitCode)
        {
            // Check exit code
            if (exitCode == 0)
                return true;

            // Check for marker file created by EmergencyCleanup
            var markerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".graceful_exit");
            if (File.Exists(markerPath))
            {
                try
                {
                    File.Delete(markerPath); // Cleanup marker
                }
                catch { }
                return true;
            }

            return false;
        }

        /// <summary>
        /// EN: Simple logging to launcher.log file
        /// FR: Logging simple dans le fichier launcher.log
        /// </summary>
        static void LogLauncher(string message)
        {
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher.log");
                var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n";
                File.AppendAllText(logPath, logMessage);
            }
            catch
            {
                // Silent fail - logging is non-critical
            }
        }

        static bool IsNet9DesktopInstalled()
        {
            try
            {
                // Method 1: Check via dotnet --list-runtimes
                // This is robust provided 'dotnet' is in PATH (which it usually is if any runtime is installed)
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "--list-runtimes",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return false;
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    
                    // Look for "Microsoft.WindowsDesktop.App 9."
                    if (output.Contains("Microsoft.WindowsDesktop.App 9."))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // dotnet command not found or failed -> implies runtime likely not installed properly or PATH issue
            }

            // Method 2 (Fallback): Check Registry? 
            // Registry check is faster but paths can change. 'dotnet --list-runtimes' is the official way.
            // If dotnet command fails, user definitely needs to install checks.
            return false;
        }
    }
}
