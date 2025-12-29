using RetroBatMarqueeManager.Core.Interfaces;
using RetroBatMarqueeManager.Core.Models.RetroAchievements;
using RetroBatMarqueeManager.Infrastructure.Api;
using RetroBatMarqueeManager.Infrastructure.Configuration;
using System.Text.RegularExpressions;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: Event args for achievement unlocked
    /// FR: Arguments d'événement pour succès débloqué
    /// </summary>
    public class AchievementUnlockedEventArgs : EventArgs
    {
        public Achievement Achievement { get; set; } = new();
        public int GameId { get; set; }
        public string GameTitle { get; set; } = string.Empty;
    }

    /// <summary>
    /// EN: Event args for game started
    /// FR: Arguments d'événement pour jeu démarré
    /// </summary>
    public class GameStartedEventArgs : EventArgs
    {
        public int GameId { get; set; }
        public GameInfo? GameInfo { get; set; }
        public UserProgress? UserProgress { get; set; }
    }

    /// <summary>
    /// EN: Service to monitor RetroArch log for RetroAchievements events
    /// FR: Service pour surveiller le log RetroArch pour les événements RetroAchievements
    /// </summary>
    public class RetroAchievementsService : IHostedService, IDisposable
    {
        private readonly IConfigService _config;
        private readonly IEsSettingsService _esSettings;
        private readonly RetroAchievementsApiClient _apiClient;
        private readonly ILogger<RetroAchievementsService> _logger;
        private ImageConversionService? _imageService; // EN: Lazy loaded for grayscale generation / FR: Chargé paresseusement pour génération grayscale

        private readonly Dictionary<string, long> _logOffsets = new();
        private readonly List<FileSystemWatcher> _watchers = new();
        private System.Threading.Timer? _pollingTimer; // EN: Fallback timer for locked files / FR: Timer de secours pour fichiers verrouillés
        private int? _currentGameId;
        private string? _currentUsername;
        private UserProgress? _currentProgress;
        private bool _isEnabled;
        private readonly object _logLock = new(); // EN: Prevent concurrent log processing / FR: Empêcher le traitement concurrent des logs
        private int? _loadingGameId; // EN: Prevent multiple loads for same game / FR: Empêcher chargements multiples pour le même jeu

        // EN: Generalized regex patterns to support multiple emulators (prefix flexible)
        // FR: Patterns regex généralisés pour supporter plusieurs émulateurs (préfixe flexible)
        private static readonly Regex UserLoginPattern = new(@"(?:RCHEEVOS\]|Achievements:)\s*[:\s]*(.+?)\s+logged in successfully", RegexOptions.Compiled);
        private static readonly Regex GameLoadPattern = new(@"Identified game:\s*(\d+)|Fetching data for game\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex GameStopPattern = new(@"\[Core\]: Unloading game\.\.|Achievements: Unloading game", RegexOptions.Compiled);
        private static readonly Regex AchievementPattern = new(@"(?:Unlocked|Awarding) (?:unofficial |official )?achievement\s+(\d+):", RegexOptions.Compiled);
        private static readonly Regex HardcorePattern = new(@"Hardcore mode (?:enabled|paused|disabled)|cheevos_hardcore_mode_enable = ""(true|false)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private bool _isHardcoreMode = false;

        // EN: Events / FR: Événements
        public event EventHandler<AchievementUnlockedEventArgs>? AchievementUnlocked;
        public event EventHandler<GameStartedEventArgs>? GameStarted;
        public event EventHandler? GameStopped;

        // EN: Public Accessors for Overlay Data / FR: Accesseurs publics pour données Overlay
        public int CurrentGameUserPoints => _currentProgress?.Achievements?.Values.Where(a => a.Unlocked).Sum(a => a.Points) ?? 0;
        public int CurrentGameTotalPoints => _currentProgress?.Achievements?.Values.Sum(a => a.Points) ?? 0;
        public Dictionary<string, Achievement>? CurrentGameAchievements => _currentProgress?.Achievements;
        public int? CurrentGameId => _currentGameId;

        public RetroAchievementsService(
            IConfigService config,
            IEsSettingsService esSettings,
            RetroAchievementsApiClient apiClient,
            ILogger<RetroAchievementsService> logger)
        {
            _config = config;
            _esSettings = esSettings;
            _apiClient = apiClient;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // EN: Check if RetroAchievements is enabled / FR: Vérifier si RetroAchievements est activé
            _isEnabled = _config.GetSetting("MarqueeRetroAchievements", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
            
            if (!_isEnabled)
            {
                _logger.LogInformation("[RA Service] RetroAchievements is disabled in config.ini");
                return Task.CompletedTask;
            }

            _logger.LogInformation("[RA Service] Starting RetroAchievements monitoring...");

            // EN: Get credentials / FR: Récupérer identifiants
            _currentUsername = _esSettings.GetSetting("global.retroachievements.username");
            
            // EN: Define log files to monitor
            // FR: Définir les fichiers logs à surveiller
            var esLogDir = Path.Combine(_config.RetroBatPath, "emulationstation", ".emulationstation");
            var esLogPath = Path.Combine(esLogDir, "es_launch_stdout.log");
            
            var pcsx2LogPath = _config.Pcsx2LogPath;
            var pcsx2LogDir = Path.GetDirectoryName(pcsx2LogPath);

            var logsToMonitor = new List<string> { esLogPath };
            if (!string.IsNullOrEmpty(pcsx2LogPath)) logsToMonitor.Add(pcsx2LogPath);

            foreach (var logPath in logsToMonitor)
            {
                if (string.IsNullOrEmpty(logPath)) continue;
                
                var dir = Path.GetDirectoryName(logPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                try
                {
                    // EN: Set initial offset to end of file
                    // FR: Positionner l'offset à la fin du fichier
                    if (File.Exists(logPath))
                    {
                        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            _logOffsets[logPath] = fs.Length;
                        }
                    }
                    else
                    {
                        _logOffsets[logPath] = 0;
                    }

                    var watcher = new FileSystemWatcher(dir, Path.GetFileName(logPath))
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };

                    watcher.Changed += (s, e) => ProcessLogFile(logPath);
                    watcher.Created += (s, e) => ProcessLogFile(logPath);
                    _watchers.Add(watcher);

                    _logger.LogInformation($"[RA Service] Started monitoring: {logPath} (Initial Offset: {_logOffsets[logPath]})");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[RA Service] Failed to monitor {logPath}: {ex.Message}");
                }
            }

            // EN: Start polling timer as fallback / FR: Démarrer timer de polling comme fallback
            _pollingTimer = new System.Threading.Timer(_ => ProcessAllLogs(), null, 1000, 1000);

            // EN: Setup RetroArch logging
            EnsureRetroArchLogging();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _pollingTimer?.Dispose();
            _pollingTimer = null;
            
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _logger.LogInformation("[RA Service] Stopped monitoring");
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// EN: Ensure RetroArch has logging enabled
        /// FR: S'assurer que RetroArch a le logging activé
        /// </summary>
        private void EnsureRetroArchLogging()
        {
            try
            {
                var cfgPath = Path.Combine(_config.RetroBatPath, "emulators", "retroarch", "retroarch.cfg");
                _logger.LogInformation($"[RA Service] Checking retroarch.cfg at: {cfgPath}");
                
                if (!File.Exists(cfgPath))
                {
                    _logger.LogWarning($"[RA Service] retroarch.cfg NOT FOUND at: {cfgPath}");
                    return;
                }

                _logger.LogInformation("[RA Service] retroarch.cfg found, reading content...");
                var lines = File.ReadAllLines(cfgPath).ToList();
                _logger.LogInformation($"[RA Service] Read {lines.Count} lines from retroarch.cfg");
                
                bool changed = false;

                void SetKey(string key, string val)
                {
                    int idx = lines.FindIndex(l => l.StartsWith(key + " "));
                    if (idx != -1)
                    {
                        if (!lines[idx].Contains(val))
                        {
                            _logger.LogInformation($"[RA Service] Updating existing key: {key} = \"{val}\"");
                            lines[idx] = $"{key} = \"{val}\"";
                            changed = true;
                        }
                        else
                        {
                            _logger.LogInformation($"[RA Service] Key already correct: {key} = \"{val}\"");
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"[RA Service] Adding new key: {key} = \"{val}\"");
                        lines.Add($"{key} = \"{val}\"");
                        changed = true;
                    }
                }

                SetKey("log_to_file", "true");
                SetKey("log_to_file_timestamp", "false");
                SetKey("log_verbosity", "true");

                if (changed)
                {
                    _logger.LogInformation($"[RA Service] Writing {lines.Count} lines back to retroarch.cfg...");
                    File.WriteAllLines(cfgPath, lines);
                    _logger.LogInformation("[RA Service] ✅ retroarch.cfg updated successfully!");
                }
                else
                {
                    _logger.LogInformation("[RA Service] retroarch.cfg already has correct settings, no changes needed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Service] ❌ FAILED to update retroarch.cfg: {ex.Message}");
                _logger.LogError($"[RA Service] Exception details: {ex}");
            }
        }

        /// <summary>
        /// EN: Process all monitored logs
        /// FR: Traiter tous les logs surveillés
        /// </summary>
        private void ProcessAllLogs()
        {
            foreach (var logPath in _logOffsets.Keys.ToList())
            {
                ProcessLogFile(logPath);
            }
        }

        /// <summary>
        /// EN: Process change on a specific log file
        /// FR: Traiter changement sur un fichier log spécifique
        /// </summary>
        private void ProcessLogFile(string logPath)
        {
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return;

            lock (_logLock)
            {
                try
                {
                    using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var fileSize = stream.Length;

                    if (!_logOffsets.TryGetValue(logPath, out long lastPosition))
                    {
                        lastPosition = 0;
                    }

                    // rotation detected
                    if (fileSize < lastPosition)
                    {
                        _logger.LogInformation($"[RA Service] Log rotation detected for {Path.GetFileName(logPath)}, resetting position");
                        lastPosition = 0;
                        ResetState();
                    }

                    stream.Seek(lastPosition, SeekOrigin.Begin);

                    using var reader = new StreamReader(stream);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        ProcessLogLine(line);
                    }

                    _logOffsets[logPath] = stream.Position;
                }
                catch (IOException) { }
                catch (Exception ex)
                {
                    _logger.LogError($"[RA Service] Error processing log {logPath}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// EN: Process single log line
        /// FR: Traiter une ligne de log
        /// </summary>
        private void ProcessLogLine(string line)
        {
            // EN: User login / FR: Connexion utilisateur
            var loginMatch = UserLoginPattern.Match(line);
            if (loginMatch.Success)
            {
                _currentUsername = loginMatch.Groups[1].Value.Trim();
                _logger.LogInformation($"[RA Service] User logged in: {_currentUsername}");
                _ = LoadUserProfileAsync(_currentUsername);
                return;
            }

            // EN: Game load / FR: Chargement jeu
            var gameMatch = GameLoadPattern.Match(line);
            if (gameMatch.Success)
            {
                var gameIdStr = gameMatch.Groups[1].Success ? gameMatch.Groups[1].Value : gameMatch.Groups[2].Value;
                _currentGameId = int.Parse(gameIdStr);
                _logger.LogInformation($"[RA Service] Game loaded: {_currentGameId}");
                _ = LoadGameDataAsync(_currentGameId.Value);
                return;
            }

            // EN: Game stop / FR: Arrêt jeu
            var stopMatch = GameStopPattern.Match(line);
            if (stopMatch.Success)
            {
                _logger.LogInformation("[RA Service] Game stopped (Log Pattern detected)");
                ResetState();
                return;
            }

            // EN: Achievement unlocked / FR: Succès débloqué
            var achievementMatch = AchievementPattern.Match(line);
            if (achievementMatch.Success && _currentGameId.HasValue && _currentUsername != null)
            {
                var achievementIdStr = achievementMatch.Groups[1].Success ? achievementMatch.Groups[1].Value : achievementMatch.Groups[2].Value;
                var achievementId = int.Parse(achievementIdStr);
                _logger.LogInformation($"[RA Service] Achievement unlocked: {achievementId}");
                _ = HandleAchievementUnlockedAsync(achievementId);
                return;
            }

            // EN: Hardcore Mode Detection
            // FR: Détection Mode Hardcore
            var hardcoreMatch = HardcorePattern.Match(line);
            if (hardcoreMatch.Success)
            {
                var status = hardcoreMatch.Value.ToLowerInvariant();
                if (status.Contains("enabled") || status.Contains("true"))
                {
                    _isHardcoreMode = true;
                    _logger.LogInformation("[RA Service] Hardcore Mode DETECTED: ON");
                }
                else
                {
                    _isHardcoreMode = false;
                    _logger.LogInformation("[RA Service] Hardcore Mode DETECTED: OFF");
                }
            }
        }

        /// <summary>
        /// EN: Reset the current game state
        /// FR: Réinitialiser l'état du jeu actuel
        /// </summary>
        public void ResetState()
        {
            _logger.LogInformation("[RA Service] Resetting state...");
            bool wasRunning = _currentGameId.HasValue;
            
            _currentGameId = null;
            _currentProgress = null;
            _isHardcoreMode = false;

            if (wasRunning)
            {
                GameStopped?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// EN: Load user profile from API
        /// FR: Charger profil utilisateur depuis l'API
        /// </summary>
        private async Task LoadUserProfileAsync(string username)
        {
            try
            {
                var profile = await _apiClient.GetUserProfileAsync(username);
                if (profile != null)
                {
                    _logger.LogInformation($"[RA Service] Loaded profile for {username}: {profile.TotalPoints} points");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Service] Error loading user profile: {ex.Message}");
            }
        }

        /// <summary>
        /// EN: Load game data and user progress
        /// FR: Charger données jeu et progression utilisateur
        /// </summary>
        private async Task LoadGameDataAsync(int gameId)
        {
            // EN: Prevent multiple concurrent loads for the same game
            // FR: Empêcher plusieurs chargements simultanés pour le même jeu
            if (_loadingGameId == gameId) return;
            
            // EN: If already loaded, skip
            // FR: Si déjà chargé, ignorer
            if (_currentGameId == gameId && _currentProgress != null) return;

            try
            {
                _loadingGameId = gameId;

                // EN: Reset state before loading new data to ensure no stale data remains if API fails
                // FR: Réinitialiser l'état avant de charger de nouvelles données pour s'assurer qu'aucune donnée obsolète ne reste si l'API échoue
                ResetState();
                _currentGameId = gameId; // Restore ID for current load attempt

                if (_currentUsername == null)
                {
                    _logger.LogWarning("[RA Service] Cannot load game data: no user logged in");
                    return;
                }

                var gameInfo = await _apiClient.GetGameInfoAsync(gameId);
                var progress = await _apiClient.GetUserProgressAsync(gameId, _currentUsername);

                _currentProgress = progress;

                // EN: Check emulator caches for game icon before relying on API downloaded one
                // FR: Vérifier les caches des émulateurs pour l'icône du jeu avant de se fier à celle de l'API
                if (gameInfo != null)
                {
                    var emulatorIcon = ResolveGameIconPath(gameId, gameInfo.ImageIcon);
                    if (!string.IsNullOrEmpty(emulatorIcon) && File.Exists(emulatorIcon))
                    {
                        _logger.LogInformation($"[RA Service] Using game icon from emulator cache: {emulatorIcon}");
                        gameInfo.ImageIcon = emulatorIcon;
                        if (progress?.GameInfo != null) progress.GameInfo.ImageIcon = emulatorIcon;
                    }
                }

                _logger.LogInformation($"[RA Service] Loaded game: {gameInfo?.Title ?? $"ID {gameId}"} - {progress?.UserCompletion ?? "0%"} complete");

                GameStarted?.Invoke(this, new GameStartedEventArgs
                {
                    GameId = gameId,
                    GameInfo = gameInfo,
                    UserProgress = progress
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Service] Error loading game data: {ex.Message}");
            }
            finally
            {
                if (_loadingGameId == gameId) _loadingGameId = null;
            }
        }

        /// <summary>
        /// EN: Handle achievement unlocked event
        /// FR: Gérer événement succès débloqué
        /// </summary>
        private async Task HandleAchievementUnlockedAsync(int achievementId)
        {
            if (_currentGameId == null || _currentUsername == null) return;

            try
            {
                // EN: Refresh progress to get fresh data
                _currentProgress = await _apiClient.GetUserProgressAsync(_currentGameId.Value, _currentUsername);

                Achievement? achievement = null;
                if (_currentProgress?.Achievements != null)
                {
                    _currentProgress.Achievements.TryGetValue(achievementId.ToString(), out achievement);
                }

                if (achievement == null)
                {
                    _logger.LogInformation($"[RA Service] Achievement {achievementId} not in API data (unofficial) - creating fallback");
                    var badgePath = await ResolveBadgePathAsync(achievementId, _currentGameId);
                    achievement = new Achievement
                    {
                        ID = achievementId,
                        Title = $"Achievement #{achievementId}",
                        Description = "Unlocked achievement (unofficial)",
                        Points = 0,
                        BadgeName = badgePath ?? string.Empty,
                        Unlocked = true
                    };
                }
                else
                {
                    _logger.LogInformation($"[RA Service] Achievement {achievementId} found in API data (official)");
                    
                    // EN: Force local unlock status
                    if (!achievement.Unlocked)
                    {
                        achievement.Unlocked = true;
                        if (_isHardcoreMode) achievement.DateEarnedHardcore = DateTime.Now;
                        else achievement.DateEarned = DateTime.Now;
                    }
                    
                    // EN: Resolve badge ID from BadgeName or path
                    var badgeFileName = Path.GetFileNameWithoutExtension(achievement.BadgeName);
                    if (int.TryParse(badgeFileName, out var badgeId))
                    {
                        var badgePath = await ResolveBadgePathAsync(badgeId, _currentGameId);
                        achievement.BadgeName = badgePath ?? string.Empty;
                    }
                }

                _logger.LogInformation($"[RA Service] Achievement unlocked: {achievement.Title} ({achievement.Points} pts)");

                AchievementUnlocked?.Invoke(this, new AchievementUnlockedEventArgs
                {
                    Achievement = achievement,
                    GameId = _currentGameId.Value,
                    GameTitle = _currentProgress?.GameInfo?.Title ?? "Unknown Game"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Service] Error handling achievement unlock: {ex.Message}");
            }
        }

        /// <summary>
        /// EN: Resolve path for a badge, checking emulator caches then local cache then API
        /// FR: Résoudre le chemin d'un badge, vérifiant caches émulateurs puis cache local puis API
        /// </summary>
        private async Task<string?> ResolveBadgePathAsync(int badgeId, int? gameId)
        {
            // 1.1 - RetroArch Cache
            var retroArchBadgesDir = Path.Combine(_config.RetroBatPath, "emulators", "retroarch", "thumbnails", "cheevos", "badges");
            if (gameId.HasValue)
            {
                var gameBadgesDir = Path.Combine(retroArchBadgesDir, gameId.Value.ToString());
                var badgePath = Path.Combine(gameBadgesDir, $"{badgeId}.png");
                if (File.Exists(badgePath)) return badgePath;
            }
            
            // 1.2 - PCSX2 Cache
            var pcsx2CacheDir = _config.Pcsx2BadgeCachePath;
            if (!string.IsNullOrEmpty(pcsx2CacheDir) && Directory.Exists(pcsx2CacheDir))
            {
                var pcsx2BadgePath = Path.Combine(pcsx2CacheDir, $"{badgeId}.png");
                if (File.Exists(pcsx2BadgePath)) return pcsx2BadgePath;
            }
            
            // 2 - Local App Cache
            var localCacheDir = gameId.HasValue
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "retroachievements", "badges", gameId.Value.ToString())
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medias", "retroachievements", "badges");
            
            var localBadgePath = Path.Combine(localCacheDir, $"{badgeId}.png");
            if (File.Exists(localBadgePath)) return localBadgePath;

            // 3 - Download from RA API (Method RetroArch style download fallback)
            _logger.LogInformation($"[RA Badge] Not found locally, downloading: {badgeId}");
            try
            {
                Directory.CreateDirectory(localCacheDir);
                var badgeUrl = $"https://media.retroachievements.org/Badge/{badgeId}.png";
                
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);
                var response = await httpClient.GetAsync(badgeUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    var badgeData = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(localBadgePath, badgeData);
                    return localBadgePath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Badge] Download failed for {badgeId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// EN: Resolve path for game icon, checking emulator caches
        /// FR: Résoudre le chemin de l'icône du jeu, vérifiant les caches des émulateurs
        /// </summary>
        private string? ResolveGameIconPath(int gameId, string? fallbackPath)
        {
            // 1. PCSX2 Cache (game_{id}.png)
            var pcsx2CacheDir = _config.Pcsx2BadgeCachePath;
            if (!string.IsNullOrEmpty(pcsx2CacheDir) && Directory.Exists(pcsx2CacheDir))
            {
                var pcsx2IconPath = Path.Combine(pcsx2CacheDir, $"game_{gameId}.png");
                if (File.Exists(pcsx2IconPath)) return pcsx2IconPath;
            }

            // 2. RetroArch Cache
            var raIconPath = Path.Combine(_config.RetroBatPath, "emulators", "retroarch", "thumbnails", "cheevos", "badges", $"{gameId}.png");
            if (File.Exists(raIconPath)) return raIconPath;

            // 3. Fallback to what we already have (App Cache or API)
            return fallbackPath;
        }

        /// <summary>
        /// EN: Get path to badge image (.png)
        /// FR: Obtenir le chemin vers l'image de badge (.png)
        /// </summary>
        public async Task<string?> GetBadgePath(int gameId, int achievementId)
        {
            int badgeId = achievementId; 
            if (_currentProgress?.Achievements != null && _currentProgress.Achievements.TryGetValue(achievementId.ToString(), out var achievement))
            {
                var badgeName = Path.GetFileNameWithoutExtension(achievement.BadgeName);
                if (int.TryParse(badgeName, out var parsedBadgeId)) badgeId = parsedBadgeId;
            }

            return await ResolveBadgePathAsync(badgeId, gameId);
        }

        /// <summary>
        /// EN: Get path to locked badge image (_lock.png)
        /// FR: Obtenir le chemin vers l'image de badge verrouillé (_lock.png)
        /// </summary>
        public async Task<string?> GetBadgeLockPath(int gameId, int achievementId)
        {
            // EN: Generate from normal badge (grayscale + darkened)
            try
            {
                var normalBadgePath = await GetBadgePath(gameId, achievementId);
                if (string.IsNullOrEmpty(normalBadgePath)) return null;

                if (_imageService == null)
                {
                    _logger.LogWarning($"[RA Badge] ImageConversionService not initialized, cannot generate lock badge");
                    return null;
                }

                return _imageService.GenerateBadgeLockFromNormal(normalBadgePath, gameId, achievementId);
            }
            catch (Exception ex)
            {
                _logger.LogError($"[RA Badge] Error generating lock badge {achievementId}: {ex.Message}");
                return null;
            }
        }

        public void SetImageConversionService(ImageConversionService imageService) => _imageService = imageService;

        public void Dispose()
        {
            _pollingTimer?.Dispose();
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
