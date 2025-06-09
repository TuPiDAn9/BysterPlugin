using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Carbon.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins
{
    [Info("BysterPlugin", "TuPiDAn", "3.0.0")]
    [Description("Adds bots with movement, infinite ammo, and other features.")]
    public class BysterPlugin : CarbonPlugin
    {
        private const string BotPrefab = "assets/prefabs/player/player.prefab";
        private readonly HashSet<ulong> _spawnedBots = new HashSet<ulong>();
        private readonly HashSet<ulong> infiniteAmmoPlayers = new HashSet<ulong>();
        private readonly Dictionary<ulong, Coroutine> botMovementCoroutines = new Dictionary<ulong, Coroutine>();
        private readonly Dictionary<ulong, Vector3> botDeathPositions = new Dictionary<ulong, Vector3>();
        private bool _isClearingBots = false;
        private bool botsMovementEnabled = true; // Флаг для управления движением всех ботов
        
        // Классы конфигурации вынесены на уровень класса BysterPlugin
        public class BotMovementConfig
        {
            public float PathDelay = 0.1f;                // Delay between path changes
            public float StepSize = 0.3f;                 // Movement step size
            public float MaxClimbHeight = 2.0f;           // Maximum climb height for bots
            public float MovementUpdateRate = 0.05f;      // Update rate for movement (seconds)
            public float MovementDistance = 15.0f;        // Distance to move in one cycle
            public bool EnableJumping = false;            // Whether bots can jump
            
            // Настройки поворота при спавне
            public string SpawnRotationMode = "random";     // "fixed", "random", "range"
            public float SpawnRotationDegrees = 0.0f;       // Базовый угол для "fixed" и "range"
            public float SpawnRotationRange = 180.0f;       // Диапазон для режима "range"
            
            // Настройки поворота во время движения
            public string MovementRotationMode = "random";  // "fixed", "random", "forward"
            public bool KeepSpawnRotation = false;          // Сохранять изначальный поворот при спавне
        }
        
        public class BotSpawningConfig
        {
            public float RespawnDelay = 0.1f;             // Delay before respawning
            public bool AutoRespawn = true;               // Auto-respawn bots when killed
            public int MaxBotsAllowed = 50;               // Maximum number of bots
            public bool ClearBotsOnServerShutdown = true; // Clear bots on server shutdown
        }
        
        public class SystemConfig
        {
            public string ChatColor = "#B8860B";          // Default chat color
            public bool LogDebugInfo = false;             // Whether to log debug info
            public bool AllowInfiniteAmmo = true;         // Whether infinite ammo is allowed
        }
        
        public class ConfigData
        {
            // Bot Movement Configuration
            public BotMovementConfig MovementConfig = new BotMovementConfig();
            
            // Bot Spawning Configuration
            public BotSpawningConfig SpawningConfig = new BotSpawningConfig();
            
            // Visual and System Configuration
            public SystemConfig SystemConfig = new SystemConfig();
        }

        private ConfigData config;

        protected override void LoadDefaultConfig()
        {
            config = new ConfigData();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            config = Config.ReadObject<ConfigData>();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        #region Infinite Ammo
        [Command("infammo")]
        private void InfAmmoCommand(BasePlayer player, string command, string[] args)
        {
            if (!config.SystemConfig.AllowInfiniteAmmo)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Команда infammo отключена в конфигурации.</color>");
                return;
            }

            if (infiniteAmmoPlayers.Contains(player.userID))
            {
                infiniteAmmoPlayers.Remove(player.userID);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Бесконечные патроны выключены.</color>");
            }
            else
            {
                infiniteAmmoPlayers.Add(player.userID);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Бесконечные патроны включены.</color>");
            }
        }

        private void OnWeaponFired(BaseProjectile projectile, BasePlayer player)
        {
            if (player == null || projectile == null || !infiniteAmmoPlayers.Contains(player.userID)) return;

            var heldEntity = projectile.GetItem();
            if (heldEntity != null)
                heldEntity.condition = heldEntity.info.condition.max;

            projectile.primaryMagazine.contents = projectile.primaryMagazine.capacity;
            projectile.SendNetworkUpdateImmediate();
        }

        private void OnRocketLaunched(BasePlayer player)
        {
            if (player == null || !infiniteAmmoPlayers.Contains(player.userID)) return;

            var heldEntity = player.GetActiveItem();
            if (heldEntity == null) return;

            heldEntity.condition = heldEntity.info.condition.max;

            if (heldEntity.GetHeldEntity() is BaseProjectile weapon)
            {
                weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
                weapon.SendNetworkUpdateImmediate();
            }
        }

        private void OnMeleeThrown(BasePlayer player, Item item)
        {
            if (player == null || item == null || !infiniteAmmoPlayers.Contains(player.userID)) return;
            
            var newMelee = ItemManager.CreateByItemID(item.info.itemid, item.amount, item.skin);
            if (newMelee != null)
            {
                newMelee.condition = newMelee.info.condition.max;
                player.GiveItem(newMelee, BaseEntity.GiveItemReason.PickedUp);
            }
        }

        private void OnPlayerInit(BasePlayer player)
        {
            if (player == null) return;
            
            if (config.SystemConfig.AllowInfiniteAmmo)
                infiniteAmmoPlayers.Add(player.userID);
            else
                infiniteAmmoPlayers.Remove(player.userID);
        }
        
        private void ApplyInfiniteAmmoDefaultToAll()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (config.SystemConfig.AllowInfiniteAmmo)
                    infiniteAmmoPlayers.Add(player.userID);
                else
                    infiniteAmmoPlayers.Remove(player.userID);
            }
        }
        #endregion

        #region Bot Management
        [Command("bspawn")]
        private void SpawnBotCommand(BasePlayer player, string command, string[] args)
        {
            if (_spawnedBots.Count >= config.SpawningConfig.MaxBotsAllowed)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Достигнут лимит ботов ({config.SpawningConfig.MaxBotsAllowed}).</color>");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 100f, LayerMask.GetMask("Terrain", "World", "Construction")))
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Не удалось найти подходящую поверхность. Пожалуйста, посмотрите на землю.</color>");
                return;
            }
            SpawnBot(hit.point);
            player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Бот успешно заспавнен в точке: {hit.point}</color>");
        }
        
        [Command("bclear")]
        private void ClearBotsCommand(BasePlayer player, string command, string[] args)
        {
            var count = ClearAllBots();
            player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Удалено {count} ботов.</color>");
        }

        [Command("remove")]
        private void RemoveCommand(BasePlayer player, string command, string[] args)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 100f))
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Вы ни на что не смотрите.</color>");
                return;
            }

            var entity = hit.GetEntity();
            if (entity == null || entity == player)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Неверная цель.</color>");
                return;
            }
            
            entity.Kill();
            player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Объект удален.</color>");
        }

        private void OnServerShutdown()
        {
            if (config.SpawningConfig.ClearBotsOnServerShutdown)
            {
                ClearAllBots();
            }
        }
        
        private void SpawnBot(Vector3 position)
        {
            var rotation = GetSpawnRotation();
            var bot = GameManager.server.CreateEntity(BotPrefab, position, rotation)?.ToPlayer();
            if (bot == null) return;
            
            bot.Spawn();
            _spawnedBots.Add(bot.userID);
            
            timer.Once(0.2f, () => {
                if (bot != null && !bot.IsDestroyed)
                {
                    bot.inventory.Strip();
                    
                    // Устанавливаем углы обзора в соответствии с поворотом
                    bot.viewAngles = new Vector3(0, rotation.eulerAngles.y, 0);
                    bot.SendNetworkUpdate();
                    
                    StartBotMovement(bot);
                }
            });
        }

        private Quaternion GetSpawnRotation()
        {
            float rotationY = 0f;
            
            switch (config.MovementConfig.SpawnRotationMode.ToLower())
            {
                case "fixed":
                    rotationY = config.MovementConfig.SpawnRotationDegrees;
                    break;
                    
                case "random":
                    rotationY = UnityEngine.Random.Range(0f, 360f);
                    break;
                    
                case "range":
                    float halfRange = config.MovementConfig.SpawnRotationRange / 2f;
                    rotationY = config.MovementConfig.SpawnRotationDegrees + 
                               UnityEngine.Random.Range(-halfRange, halfRange);
                    break;
                    
                default:
                    rotationY = UnityEngine.Random.Range(0f, 360f);
                    break;
            }
            
            // Нормализуем угол в диапазон 0-360
            rotationY = rotationY % 360f;
            if (rotationY < 0f) rotationY += 360f;
            
            return Quaternion.Euler(0, rotationY, 0);
        }

        private PlayerCorpse FindCorpse(ulong steamID)
        {
            return BaseEntity.serverEntities.OfType<PlayerCorpse>().FirstOrDefault(c => c.playerSteamID == steamID && c.playerSteamID != 0);
        }

        private int ClearAllBots()
        {
            if (_spawnedBots.Count == 0) return 0;

            _isClearingBots = true;

            // Останавливаем все корутины движения
            foreach (var botId in botMovementCoroutines.Keys.ToList())
            {
                StopBotMovement(botId);
            }
            botMovementCoroutines.Clear();
            
            // Очищаем позиции смерти
            botDeathPositions.Clear();

            var botIdsToKill = new List<ulong>(_spawnedBots);
            int killCount = 0;
            _spawnedBots.Clear();
            
            var allPlayersOnServer = BaseNetworkable.serverEntities.OfType<BasePlayer>().ToList();
            foreach (var player in allPlayersOnServer)
            {
                if (botIdsToKill.Contains(player.userID))
                {
                    player.Kill(BaseNetworkable.DestroyMode.Gib);
                    killCount++;
                }
            }
            
            // Удаляем трупы ботов
            timer.Once(0.5f, () => {
                foreach (var botId in botIdsToKill)
                {
                    var corpse = FindCorpse(botId);
                    if (corpse != null)
                    {
                        corpse.Kill();
                    }
                }
                _isClearingBots = false;
            });

            Puts($"[/bclear] Finished. Removed {killCount} of {botIdsToKill.Count} tracked bots.");
            return killCount;
        }

        [Command("bmove")]
        private void BotMovementToggleCommand(BasePlayer player, string command, string[] args)
        {
            if (botsMovementEnabled)
            {
                // Останавливаем движение всех ботов
                foreach (var botId in botMovementCoroutines.Keys.ToList())
                {
                    StopBotMovement(botId);
                }
                botsMovementEnabled = false;
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Движение всех ботов остановлено.</color>");
                Puts("[BMOVE] Движение всех ботов остановлено.");
            }
            else
            {
                // ВАЖНО: Включаем флаг ПЕРЕД запуском ботов
                botsMovementEnabled = true;

                // Запускаем движение всех живых ботов
                var allPlayers = BaseNetworkable.serverEntities.OfType<BasePlayer>().ToList();
                int startedCount = 0;

                foreach (var bot in allPlayers)
                {
                    if (_spawnedBots.Contains(bot.userID) && bot != null && !bot.IsDestroyed && bot.IsAlive())
                    {
                        // Проверяем что бот не ранен
                        if (!bot.IsWounded() && !bot.HasPlayerFlag(BasePlayer.PlayerFlags.Wounded))
                        {
                            StartBotMovement(bot);
                            startedCount++;
                        }
                    }
                }

                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Движение ботов запущено. Активных ботов: {startedCount}</color>");
                Puts($"[BMOVE] Движение ботов запущено. Активных ботов: {startedCount}");
            }
        }

        [Command("bdelay")]
        private void SetBotPathDelayCommand(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Текущая задержка смены пути: {config.MovementConfig.PathDelay} сек.</color>");
                return;
            }

            if (float.TryParse(args[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float delay) && delay >= 0.01f && delay <= 10f)
            {
                config.MovementConfig.PathDelay = delay;
                SaveConfig();
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Задержка смены пути бота установлена: {delay} сек.</color>");
            }
            else
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Некорректное значение. Введите число от 0.01 до 10.</color>");
            }
        }
        
        [Command("bdegree")]
        private void SetBotSpawnRotationCommand(BasePlayer player, string command, string[] args)
        {
            var chatColor = config.SystemConfig.ChatColor;
            
            if (args.Length == 0)
            {
                player.ChatMessage($"<color={chatColor}>Текущие настройки поворота:</color>");
                player.ChatMessage($"<color={chatColor}>Режим при спавне: {config.MovementConfig.SpawnRotationMode}</color>");
                player.ChatMessage($"<color={chatColor}>Базовый угол: {config.MovementConfig.SpawnRotationDegrees}°</color>");
                player.ChatMessage($"<color={chatColor}>Диапазон: {config.MovementConfig.SpawnRotationRange}°</color>");
                player.ChatMessage($"<color={chatColor}>Режим при движении: {config.MovementConfig.MovementRotationMode}</color>");
                player.ChatMessage($"<color={chatColor}>Сохранять поворот при спавне: {config.MovementConfig.KeepSpawnRotation}</color>");
                player.ChatMessage($"<color={chatColor}>Доступные режимы спавна: fixed, random, range</color>");
                player.ChatMessage($"<color={chatColor}>Доступные режимы движения: fixed, random, forward</color>");
                return;
            }

            string subCommand = args[0].ToLower();

            switch (subCommand)
            {
                case "mode":
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"<color={chatColor}>Использование: /bdegree mode [fixed/random/range]</color>");
                        return;
                    }
                    string mode = args[1].ToLower();
                    if (mode == "fixed" || mode == "random" || mode == "range")
                    {
                        config.MovementConfig.SpawnRotationMode = mode;
                        SaveConfig();
                        player.ChatMessage($"<color={chatColor}>Режим поворота при спавне установлен: {mode}</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={chatColor}>Неверный режим. Доступны: fixed, random, range</color>");
                    }
                    break;

                case "angle":
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"<color={chatColor}>Использование: /bdegree angle [0-360]</color>");
                        return;
                    }
                    if (float.TryParse(args[1], out float degrees) && degrees >= 0f && degrees <= 360f)
                    {
                        config.MovementConfig.SpawnRotationDegrees = degrees;
                        SaveConfig();
                        player.ChatMessage($"<color={chatColor}>Базовый угол поворота установлен: {degrees}°</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={chatColor}>Введите число от 0 до 360</color>");
                    }
                    break;

                case "range":
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"<color={chatColor}>Использование: /bdegree range [0-360]</color>");
                        return;
                    }
                    if (float.TryParse(args[1], out float range) && range >= 0f && range <= 360f)
                    {
                        config.MovementConfig.SpawnRotationRange = range;
                        SaveConfig();
                        player.ChatMessage($"<color={chatColor}>Диапазон поворота установлен: {range}°</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={chatColor}>Введите число от 0 до 360</color>");
                    }
                    break;
                    
                case "keep":
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"<color={chatColor}>Использование: /bdegree keep [true/false]</color>");
                        return;
                    }
                    if (bool.TryParse(args[1], out bool keepRotation))
                    {
                        config.MovementConfig.KeepSpawnRotation = keepRotation;
                        SaveConfig();
                        player.ChatMessage($"<color={chatColor}>Сохранение поворота при спавне: {keepRotation}</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={chatColor}>Введите true или false</color>");
                    }
                    break;

                case "movement":
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"<color={chatColor}>Использование: /bdegree movement [fixed/random/forward]</color>");
                        return;
                    }
                    string movementMode = args[1].ToLower();
                    if (movementMode == "fixed" || movementMode == "random" || movementMode == "forward")
                    {
                        config.MovementConfig.MovementRotationMode = movementMode;
                        SaveConfig();
                        player.ChatMessage($"<color={chatColor}>Режим поворота при движении: {movementMode}</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={chatColor}>Доступны: fixed, random, forward</color>");
                    }
                    break;

                default:
                    // Обратная совместимость - если первый аргумент число, устанавливаем фиксированный угол
                    if (float.TryParse(args[0], out float legacyDegrees) && legacyDegrees >= 0f && legacyDegrees <= 360f)
                    {
                        config.MovementConfig.SpawnRotationMode = "fixed";
                        config.MovementConfig.SpawnRotationDegrees = legacyDegrees;
                        SaveConfig();
                        player.ChatMessage($"<color={chatColor}>Установлен фиксированный угол поворота: {legacyDegrees}°</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={chatColor}>Использование:</color>");
                        player.ChatMessage($"<color={chatColor}>/bdegree - показать настройки</color>");
                        player.ChatMessage($"<color={chatColor}>/bdegree mode [fixed/random/range]</color>");
                        player.ChatMessage($"<color={chatColor}>/bdegree angle [0-360]</color>");
                        player.ChatMessage($"<color={chatColor}>/bdegree range [0-360]</color>");
                        player.ChatMessage($"<color={chatColor}>/bdegree keep [true/false]</color>");
                        player.ChatMessage($"<color={chatColor}>/bdegree movement [fixed/random/forward]</color>");
                    }
                    break;
            }
        }
        #endregion

        #region Configuration Management
        [Command("bconfig")]
        private void ConfigCommand(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>У вас недостаточно прав для этой команды.</color>");
                return;
            }

            if (args.Length == 0)
            {
                DisplayConfigHelp(player);
                return;
            }

            string subCommand = args[0].ToLower();
            
            switch (subCommand)
            {
                case "list":
                    DisplayAllConfig(player);
                    break;
                case "get":
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Использование: /bconfig get [имя_настройки]</color>");
                        return;
                    }
                    GetConfigValue(player, args[1]);
                    break;
                case "set":
                    if (args.Length < 3)
                    {
                        player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Использование: /bconfig set [имя_настройки] [значение]</color>");
                        return;
                    }
                    SetConfigValue(player, args[1], string.Join(" ", args.Skip(2)));
                    break;
                case "reset":
                    if (args.Length < 2)
                    {
                        player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Использование: /bconfig reset [имя_настройки/all]</color>");
                        return;
                    }
                    ResetConfigValue(player, args[1]);
                    break;
                default:
                    DisplayConfigHelp(player);
                    break;
            }
        }

        private void DisplayConfigHelp(BasePlayer player)
        {
            var color = config.SystemConfig.ChatColor;
            player.ChatMessage($"<color={color}>=== Система конфигурации BysterPlugin ===</color>");
            player.ChatMessage($"<color={color}>/bconfig list - Показать все настройки</color>");
            player.ChatMessage($"<color={color}>/bconfig get [путь] - Показать значение настройки</color>");
            player.ChatMessage($"<color={color}>/bconfig set [путь] [значение] - Установить значение настройки</color>");
            player.ChatMessage($"<color={color}>/bconfig reset [путь/all] - Сбросить настройку или все настройки</color>");
            player.ChatMessage($"<color={color}>Примеры путей: MovementConfig.PathDelay, SpawningConfig.MaxBotsAllowed</color>");
        }
        
        private void DisplayAllConfig(BasePlayer player)
        {
            var color = config.SystemConfig.ChatColor;
            player.ChatMessage($"<color={color}>=== Текущие настройки ===</color>");
            
            // Bot Movement
            player.ChatMessage($"<color={color}>=== Настройки движения ботов ===</color>");
            player.ChatMessage($"<color={color}>MovementConfig.PathDelay: {config.MovementConfig.PathDelay}</color>");
            player.ChatMessage($"<color={color}>MovementConfig.StepSize: {config.MovementConfig.StepSize}</color>");
            player.ChatMessage($"<color={color}>MovementConfig.MaxClimbHeight: {config.MovementConfig.MaxClimbHeight}</color>");
            player.ChatMessage($"<color={color}>MovementConfig.MovementUpdateRate: {config.MovementConfig.MovementUpdateRate}</color>");
            player.ChatMessage($"<color={color}>MovementConfig.MovementDistance: {config.MovementConfig.MovementDistance}</color>");
            player.ChatMessage($"<color={color}>MovementConfig.EnableJumping: {config.MovementConfig.EnableJumping}</color>");
            
            // Bot Spawning
            player.ChatMessage($"<color={color}>=== Настройки спавна ботов ===</color>");
            player.ChatMessage($"<color={color}>SpawningConfig.RespawnDelay: {config.SpawningConfig.RespawnDelay}</color>");
            player.ChatMessage($"<color={color}>SpawningConfig.AutoRespawn: {config.SpawningConfig.AutoRespawn}</color>");
            player.ChatMessage($"<color={color}>SpawningConfig.MaxBotsAllowed: {config.SpawningConfig.MaxBotsAllowed}</color>");
            player.ChatMessage($"<color={color}>SpawningConfig.ClearBotsOnServerShutdown: {config.SpawningConfig.ClearBotsOnServerShutdown}</color>");
            
            // System
            player.ChatMessage($"<color={color}>=== Системные настройки ===</color>");
            player.ChatMessage($"<color={color}>SystemConfig.ChatColor: {config.SystemConfig.ChatColor}</color>");
            player.ChatMessage($"<color={color}>SystemConfig.LogDebugInfo: {config.SystemConfig.LogDebugInfo}</color>");
            player.ChatMessage($"<color={color}>SystemConfig.AllowInfiniteAmmo: {config.SystemConfig.AllowInfiniteAmmo}</color>");
        }

        private void GetConfigValue(BasePlayer player, string configPath)
        {
            var value = GetConfigValueByPath(configPath);
            var color = config.SystemConfig.ChatColor;
            
            if (value != null)
            {
                player.ChatMessage($"<color={color}>{configPath} = {value}</color>");
            }
            else
            {
                player.ChatMessage($"<color={color}>Настройка '{configPath}' не найдена.</color>");
            }
        }

        private object GetConfigValueByPath(string path)
        {
            string[] parts = path.Split('.');
            if (parts.Length != 2)
                return null;
                
            string category = parts[0];
            string property = parts[1];
            
            switch (category)
            {
                case "MovementConfig":
                    return typeof(BotMovementConfig).GetProperty(property)?.GetValue(config.MovementConfig);
                case "SpawningConfig":
                    return typeof(BotSpawningConfig).GetProperty(property)?.GetValue(config.SpawningConfig);
                case "SystemConfig":
                    return typeof(SystemConfig).GetProperty(property)?.GetValue(config.SystemConfig);
                default:
                    return null;
            }
        }

        private void SetConfigValue(BasePlayer player, string configPath, string valueStr)
        {
            string[] parts = configPath.Split('.');
            if (parts.Length != 2)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Неверный формат пути. Используйте: Категория.Настройка</color>");
                return;
            }
            
            string category = parts[0];
            string property = parts[1];
            var color = config.SystemConfig.ChatColor;
            
            bool success = false;
            string message = "";
            
            switch (category)
            {
                case "MovementConfig":
                    success = SetPropertyValue(config.MovementConfig, property, valueStr, out message);
                    break;
                case "SpawningConfig":
                    success = SetPropertyValue(config.SpawningConfig, property, valueStr, out message);
                    break;
                case "SystemConfig":
                    success = SetPropertyValue(config.SystemConfig, property, valueStr, out message);
                    // Если изменили настройку AllowInfiniteAmmo, применяем её ко всем игрокам
                    if (success && property == "AllowInfiniteAmmo")
                    {
                        ApplyInfiniteAmmoDefaultToAll();
                    }
                    break;
                default:
                    message = $"Категория '{category}' не найдена.";
                    break;
            }
            
            if (success)
            {
                SaveConfig();
                player.ChatMessage($"<color={color}>Настройка {configPath} установлена: {valueStr}</color>");
            }
            else
            {
                player.ChatMessage($"<color={color}>Ошибка: {message}</color>");
            }
        }
        
        private bool SetPropertyValue(object target, string propertyName, string valueStr, out string errorMessage)
        {
            var property = target.GetType().GetProperty(propertyName);
            errorMessage = "";
            
            if (property == null)
            {
                errorMessage = $"Настройка '{propertyName}' не найдена.";
                return false;
            }
            
            try
            {
                object convertedValue;
                Type propertyType = property.PropertyType;
                
                if (propertyType == typeof(int))
                {
                    if (!int.TryParse(valueStr, out int intValue))
                    {
                        errorMessage = "Значение должно быть целым числом.";
                        return false;
                    }
                    convertedValue = intValue;
                }
                else if (propertyType == typeof(float))
                {
                    if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float floatValue))
                    {
                        errorMessage = "Значение должно быть числом.";
                        return false;
                    }
                    convertedValue = floatValue;
                }
                else if (propertyType == typeof(bool))
                {
                    if (!bool.TryParse(valueStr, out bool boolValue))
                    {
                        errorMessage = "Значение должно быть true или false.";
                        return false;
                    }
                    convertedValue = boolValue;
                }
                else if (propertyType == typeof(string))
                {
                    convertedValue = valueStr;
                }
                else
                {
                    errorMessage = $"Неподдерживаемый тип настройки: {propertyType.Name}";
                    return false;
                }
                
                // Validate values for specific properties
                if (propertyName == "PathDelay" && ((float)convertedValue < 0.01f || (float)convertedValue > 10f))
                {
                    errorMessage = "PathDelay должен быть между 0.01 и 10.";
                    return false;
                }
                
                property.SetValue(target, convertedValue);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Ошибка при установке значения: {ex.Message}";
                return false;
            }
        }

        private void ResetConfigValue(BasePlayer player, string configPath)
        {
            var color = config.SystemConfig.ChatColor;
            
            if (configPath.ToLower() == "all")
            {
                config = new ConfigData();
                SaveConfig();
                player.ChatMessage($"<color={color}>Все настройки сброшены к значениям по умолчанию.</color>");
                
                // Применяем настройки бесконечных патронов при сбросе всех настроек
                ApplyInfiniteAmmoDefaultToAll();
                return;
            }
            
            string[] parts = configPath.Split('.');
            if (parts.Length != 2)
            {
                player.ChatMessage($"<color={color}>Неверный формат пути. Используйте: Категория.Настройка</color>");
                return;
            }
            
            string category = parts[0];
            string property = parts[1];
            
            switch (category)
            {
                case "MovementConfig":
                    var defaultMovement = new BotMovementConfig();
                    var resetValue = typeof(BotMovementConfig).GetProperty(property)?.GetValue(defaultMovement);
                    if (resetValue != null)
                    {
                        typeof(BotMovementConfig).GetProperty(property)?.SetValue(config.MovementConfig, resetValue);
                        SaveConfig();
                        player.ChatMessage($"<color={color}>Настройка {configPath} сброшена к значению по умолчанию: {resetValue}</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={color}>Настройка '{property}' не найдена в категории MovementConfig.</color>");
                    }
                    break;
                    
                case "SpawningConfig":
                    var defaultSpawning = new BotSpawningConfig();
                    var resetSpawningValue = typeof(BotSpawningConfig).GetProperty(property)?.GetValue(defaultSpawning);
                    if (resetSpawningValue != null)
                    {
                        typeof(BotSpawningConfig).GetProperty(property)?.SetValue(config.SpawningConfig, resetSpawningValue);
                        SaveConfig();
                        player.ChatMessage($"<color={color}>Настройка {configPath} сброшена к значению по умолчанию: {resetSpawningValue}</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={color}>Настройка '{property}' не найдена в категории SpawningConfig.</color>");
                    }
                    break;

                case "SystemConfig":
                    var defaultSystem = new SystemConfig();
                    var resetSystemValue = typeof(SystemConfig).GetProperty(property)?.GetValue(defaultSystem);
                    if (resetSystemValue != null)
                    {
                        typeof(SystemConfig).GetProperty(property)?.SetValue(config.SystemConfig, resetSystemValue);
                        SaveConfig();
                        player.ChatMessage($"<color={color}>Настройка {configPath} сброшена к значению по умолчанию: {resetSystemValue}</color>");
                        
                        // Если сбросили настройку AllowInfiniteAmmo, применяем её ко всем игрокам
                        if (property == "AllowInfiniteAmmo")
                        {
                            ApplyInfiniteAmmoDefaultToAll();
                        }
                    }
                    else
                    {
                        player.ChatMessage($"<color={color}>Настройка '{property}' не найдена в категории SystemConfig.</color>");
                    }
                    break;

                default:
                    player.ChatMessage($"<color={color}>Категория '{category}' не найдена.</color>");
                    break;
            }
        }
        #endregion

        #region Bot Movement
        private void StartBotMovement(BasePlayer bot)
        {
            if (bot == null || bot.IsDestroyed || !botsMovementEnabled) return;
            
            // Не запускаем движение для раненых ботов
            if (bot.IsWounded() || bot.HasPlayerFlag(BasePlayer.PlayerFlags.Wounded) || !bot.IsAlive())
            {
                return;
            }

            if (botMovementCoroutines.ContainsKey(bot.userID))
            {
                StopBotMovement(bot.userID);
            }

            var coroutine = ServerMgr.Instance.StartCoroutine(BotMovementLoop(bot));
            botMovementCoroutines[bot.userID] = coroutine;
        }

        private void StopBotMovement(ulong botId)
        {
            if (botMovementCoroutines.TryGetValue(botId, out var coroutine))
            {
                if (coroutine != null)
                {
                    ServerMgr.Instance.StopCoroutine(coroutine);
                }
                botMovementCoroutines.Remove(botId);
            }
        }

        private IEnumerator BotMovementLoop(BasePlayer bot)
        {
            if (bot == null || bot.IsDestroyed) yield break;

            bot.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
            bot.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);

            // Сохраняем изначальный поворот при спавне если нужно
            float initialRotationY = bot.viewAngles.y;

            while (bot != null && !bot.IsDestroyed && botsMovementEnabled)
            {
                // Проверяем что бот жив и не ранен
                if (!bot.IsAlive() || bot.IsWounded() || bot.HasPlayerFlag(BasePlayer.PlayerFlags.Wounded))
                {
                    // Если бот ранен или мертв, останавливаем движение и ждем
                    bot.modelState.flags = (int)ModelState.Flag.OnGround;
                    bot.SendNetworkUpdate();
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                // Получаем направление движения на основе настроек
                Vector3 moveDirection = GetMovementDirection(bot, initialRotationY);
                
                // Поворачиваем бота только если не нужно сохранять изначальный поворот
                if (!config.MovementConfig.KeepSpawnRotation)
                {
                    var targetRotation = Quaternion.LookRotation(moveDirection);
                    bot.viewAngles = targetRotation.eulerAngles;
                    bot.SendNetworkUpdate();
                }

                // Устанавливаем флаги движения
                bot.modelState.flags = (int)(ModelState.Flag.OnGround | ModelState.Flag.Sprinting);
                bot.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, false);
                
                // Движение
                yield return SafeMovement(bot, moveDirection, config.MovementConfig.MovementDistance);

                // Пауза между движениями
                if (bot != null && !bot.IsDestroyed)
                {
                    bot.modelState.flags = (int)ModelState.Flag.OnGround;
                    bot.SendNetworkUpdate();
                    yield return new WaitForSeconds(config.MovementConfig.PathDelay);
                }
            }
        }

        private Vector3 GetMovementDirection(BasePlayer bot, float initialRotationY)
        {
            Vector3 direction;
            
            switch (config.MovementConfig.MovementRotationMode.ToLower())
            {
                case "fixed":
                    // Движение всегда в одном направлении (изначальном)
                    direction = Quaternion.Euler(0, initialRotationY, 0) * Vector3.forward;
                    break;
                    
                case "forward":
                    // Движение всегда вперед относительно текущего поворота бота
                    direction = bot.transform.forward;
                    break;
                    
                case "random":
                default:
                    // Случайное направление (текущее поведение)
                    direction = UnityEngine.Random.insideUnitSphere;
                    direction.y = 0;
                    direction = direction.normalized;
                    break;
            }
            
            return direction;
        }

        private IEnumerator SafeMovement(BasePlayer bot, Vector3 direction, float totalDistance)
        {
            float distanceMoved = 0f;
            float stepSize = config.MovementConfig.StepSize; // Маленькие шаги
            
            while (distanceMoved < totalDistance && bot != null && !bot.IsDestroyed && botsMovementEnabled)
            {
                // Проверяем состояние бота перед каждым шагом
                if (!bot.IsAlive() || bot.IsWounded() || bot.HasPlayerFlag(BasePlayer.PlayerFlags.Wounded))
                {
                    yield break;
                }

                Vector3 currentPosition = bot.transform.position;
                Vector3 targetPosition = currentPosition + direction * stepSize;
                
                // Проверяем путь на препятствия
                if (!CheckPathClear(currentPosition, targetPosition))
                {
                    direction = GetNewDirection(currentPosition, direction);
                    continue;
                }
                
                // Привязываем к земле
                Vector3 groundPosition = GetGroundPosition(targetPosition);
                if (groundPosition == Vector3.zero)
                {
                    direction = GetNewDirection(currentPosition, direction);
                    continue;
                }
                
                // Проверяем что новая позиция не слишком далеко от текущей по Y
                if (Mathf.Abs(groundPosition.y - currentPosition.y) > config.MovementConfig.MaxClimbHeight)
                {
                    direction = GetNewDirection(currentPosition, direction);
                    continue;
                }
                
                // Перемещаем бота
                bot.transform.position = groundPosition;
                bot.TransformChanged();
                bot.SendNetworkUpdate();
                
                distanceMoved += stepSize;
                
                // Частое обновление для плавности (как у настоящего игрока)
                yield return new WaitForSeconds(config.MovementConfig.MovementUpdateRate); // 20 FPS обновление
            }
        }

        private bool CheckPathClear(Vector3 from, Vector3 to)
        {
            // Проверяем путь на препятствия
            Vector3 direction = (to - from).normalized;
            float distance = Vector3.Distance(from, to);
            
            // Проверяем на уровне головы и тела
            if (Physics.Raycast(from + Vector3.up * 0.5f, direction, distance, 
                LayerMask.GetMask("Construction", "Deployed")))
            {
                return false;
            }
            
            if (Physics.Raycast(from + Vector3.up * 1.5f, direction, distance, 
                LayerMask.GetMask("Construction", "Deployed")))
            {
                return false;
            }
            
            return true;
        }

        private Vector3 GetNewDirection(Vector3 currentPosition, Vector3 blockedDirection)
        {
            // Пробуем альтернативные направления
            Vector3[] alternatives = {
                Quaternion.Euler(0, 45, 0) * blockedDirection,
                Quaternion.Euler(0, -45, 0) * blockedDirection,
                Quaternion.Euler(0, 90, 0) * blockedDirection,
                Quaternion.Euler(0, -90, 0) * blockedDirection,
                -blockedDirection // Разворот
            };
            
            foreach (var newDirection in alternatives)
            {
                Vector3 testPosition = currentPosition + newDirection.normalized * 0.3f;
                if (CheckPathClear(currentPosition, testPosition))
                {
                    Vector3 groundTest = GetGroundPosition(testPosition);
                    if (groundTest != Vector3.zero && 
                        Mathf.Abs(groundTest.y - currentPosition.y) <= config.MovementConfig.MaxClimbHeight)
                    {
                        return newDirection.normalized;
                    }
                }
            }
            
            // Если ничего не подошло, случайное направление
            var randomDir = UnityEngine.Random.insideUnitSphere;
            randomDir.y = 0;
            return randomDir.normalized;
        }

        private Vector3 GetGroundPosition(Vector3 position)
        {
            Vector3 rayStart = new Vector3(position.x, position.y + 5f, position.z);
            
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 10f, 
                LayerMask.GetMask("Terrain", "World", "Construction")))
            {
                if (Vector3.Angle(hit.normal, Vector3.up) <= 45f)
                {
                    return hit.point + Vector3.up * 0.01f; // Минимальный отступ
                }
            }
            
            return Vector3.zero;
        }
        #endregion

        #region Bot Respawn System
        private void OnEntityDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || !_spawnedBots.Contains(player.userID)) return;
            
            if (!config.SpawningConfig.AutoRespawn)
            {
                _spawnedBots.Remove(player.userID);
                StopBotMovement(player.userID);
                return;
            }

            // Сохраняем позицию смерти
            Vector3 deathPosition = player.transform.position;
            botDeathPositions[player.userID] = deathPosition;
            
            // Останавливаем движение умершего бота
            StopBotMovement(player.userID);
            
            // Планируем возрождение через минимальную задержку
            timer.Once(config.SpawningConfig.RespawnDelay, () => {
                RespawnBotAtPosition(player.userID, deathPosition);
            });
            
            // Удаляем труп через короткое время
            timer.Once(0.2f, () => {
                RemoveBotCorpse(player.userID);
            });
        }

        private void RespawnBotAtPosition(ulong oldBotId, Vector3 position)
        {
            // Удаляем старого бота из списка
            _spawnedBots.Remove(oldBotId);
            
            // Спавним нового бота на том же месте
            var newBot = GameManager.server.CreateEntity(BotPrefab, position, Quaternion.identity)?.ToPlayer();
            if (newBot == null) return;
            
            newBot.Spawn();
            _spawnedBots.Add(newBot.userID);
            
            // Убираем старую позицию смерти и запускаем движение
            botDeathPositions.Remove(oldBotId);
            
            timer.Once(0.2f, () => {
                if (newBot != null && !newBot.IsDestroyed)
                {
                    newBot.inventory.Strip();
                    StartBotMovement(newBot);
                }
            });
        }

        private void RemoveBotCorpse(ulong botId)
        {
            var corpse = FindCorpse(botId);
            if (corpse != null)
            {
                corpse.Kill();
            }
        }
        #endregion

        #region Hitmarker Logic
        [Command("botinfo")]
        private void BotInfoCommand(BasePlayer player, string command, string[] args)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 100f))
            {
                player.ChatMessage("Не удалось найти цель");
                return;
            }

            var entity = hit.GetEntity();
            if (entity is BasePlayer bot && _spawnedBots.Contains(bot.userID))
            {
                player.ChatMessage($"=== Информация о боте {bot.userID} ===");
                player.ChatMessage($"Позиция: {bot.transform.position}");
                player.ChatMessage($"Углы обзора: {bot.viewAngles}");
                player.ChatMessage($"Флаги модели: {bot.modelState.flags}");
                player.ChatMessage($"Флаг сна: {bot.HasPlayerFlag(BasePlayer.PlayerFlags.Sleeping)}");
                player.ChatMessage($"На земле: {bot.IsOnGround()}");
                player.ChatMessage($"Живой: {bot.IsAlive()}");
                player.ChatMessage($"Активен: {!bot.IsDestroyed}");
                player.ChatMessage($"Корутина активна: {botMovementCoroutines.ContainsKey(bot.userID)}");
                
                Puts($"[BOTINFO] Подробная информация о боте {bot.userID}:");
                Puts($"Position: {bot.transform.position}");
                Puts($"ViewAngles: {bot.viewAngles}");
                Puts($"ModelState flags: {bot.modelState.flags}");
                Puts($"IsOnGround: {bot.IsOnGround()}");
                Puts($"IsAlive: {bot.IsAlive()}");
                Puts($"HasPlayerFlag Sleeping: {bot.HasPlayerFlag(BasePlayer.PlayerFlags.Sleeping)}");
                Puts($"HasPlayerFlag ReceivingSnapshot: {bot.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot)}");
            }
            else
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Это не бот или бот не найден в списке</color>");
            }
        }
        #endregion
    }
}