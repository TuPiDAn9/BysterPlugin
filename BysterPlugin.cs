using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Carbon.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins
{
    [Info("BysterPlugin", "TuPiDAn", "3.9.3")]
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
        
        public class BotArmorConfig
        {
            public string CurrentArmorType = "naked";  // hqm, signs, hazmat, naked
            public bool AutoEquipNewBots = true;       // Автоматически экипировать новых ботов
        }
        
        public class SystemConfig
        {
            public string ChatColor = "#B8860B";          // Default chat color
            public bool LogDebugInfo = false;             // Whether to log debug info
            public bool AllowInfiniteAmmo = true;         // Whether infinite ammo is allowed
        }
        
        public class HitmarkerConfig
        {
            public bool EnableHitmarkers = true;
            public bool ShowDamageNumbers = true;
            public float HitmarkerDuration = 1.0f;
            public int TextSize = 20;
            public string NormalHitColor = "1 1 1 1";      // Белый
            public string HeadshotColor = "1 0 0 1";       // Красный
            public string KillColor = "1 0 0 1";           // Красный
            public string TextOutlineColor = "0 0 0 1";    // Черная обводка
            public string TextOutlineDistance = "1 1";
        }
        
        public class ConfigData
        {
            // Bot Movement Configuration
            public BotMovementConfig MovementConfig = new BotMovementConfig();
            
            // Bot Spawning Configuration
            public BotSpawningConfig SpawningConfig = new BotSpawningConfig();
            
            // Bot Armor Configuration
            public BotArmorConfig ArmorConfig = new BotArmorConfig();
            
            // Visual and System Configuration
            public SystemConfig SystemConfig = new SystemConfig();
            
            // Hitmarker Configuration
            public HitmarkerConfig HitmarkerConfig = new HitmarkerConfig();
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

        void OnServerInitialized()
        {
            // Включаем хитмаркеры для всех игроков по умолчанию
            if (config.HitmarkerConfig.EnableHitmarkers)
            {
                ApplyHitmarkersToAll();
            }
            
            // Включаем бесконечные патроны для всех игроков по умолчанию
            if (config.SystemConfig.AllowInfiniteAmmo)
            {
                ApplyInfiniteAmmoDefaultToAll();
                Puts($"[InfiniteAmmo] Бесконечные патроны включены для всех игроков");
            }
        }
        
        private void ApplyHitmarkersToAll()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null)
                {
                    hitmarkerEnabledPlayers.Add(player.userID);
                }
            }
            Puts($"[Hitmarkers] Хитмаркеры включены для {hitmarkerEnabledPlayers.Count} игроков");
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
            
            // Включаем хитмаркеры по умолчанию
            if (config.HitmarkerConfig.EnableHitmarkers)
            {
                hitmarkerEnabledPlayers.Add(player.userID);
            }
            
            // Существующий код для бесконечных патронов
            if (config.SystemConfig.AllowInfiniteAmmo)
                infiniteAmmoPlayers.Add(player.userID);
            else
                infiniteAmmoPlayers.Remove(player.userID);
        }
        
        private void ApplyInfiniteAmmoDefaultToAll()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player != null)
                {
                    if (config.SystemConfig.AllowInfiniteAmmo)
                        infiniteAmmoPlayers.Add(player.userID);
                    else
                        infiniteAmmoPlayers.Remove(player.userID);
                }
            }
            
            if (config.SystemConfig.AllowInfiniteAmmo)
                Puts($"[InfiniteAmmo] Бесконечные патроны включены для {infiniteAmmoPlayers.Count} игроков");
            else
                Puts($"[InfiniteAmmo] Бесконечные патроны отключены для всех игроков");
        }
        #endregion

        #region Simple Hitmarker System
        private readonly HashSet<ulong> hitmarkerEnabledPlayers = new HashSet<ulong>();

        private const string SimpleHitmarkerUI = @"[
            {
                ""name"": ""HitMarker_{id}"",
                ""parent"": ""Overlay"",
                ""components"": [
                    {
                        ""type"": ""UnityEngine.UI.Image"",
                        ""color"": ""0.0 0.0 0.0 0.0""
                    },
                    {
                        ""type"": ""RectTransform"",
                        ""anchormin"": ""{anchormin}"",
                        ""anchormax"": ""{anchormax}""
                    }
                ]
            },
            {
                ""parent"": ""HitMarker_{id}"",
                ""components"": [
                    {
                        ""type"": ""UnityEngine.UI.Text"",
                        ""text"": ""{text}"",
                        ""fontSize"": {fontsize},
                        ""color"": ""{color}"",
                        ""align"": ""MiddleCenter"",
                        ""font"": ""robotocondensed-regular.ttf""
                    },
                    {
                        ""type"": ""UnityEngine.UI.Outline"",
                        ""color"": ""{outlinecolor}"",
                        ""distance"": ""{outlinedistance}""
                    }
                ]
            }
        ]";

        [Command("hitmarker")]
        private void HitmarkerToggleCommand(BasePlayer player, string command, string[] args)
        {
            if (!config.HitmarkerConfig.EnableHitmarkers)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Хитмаркеры отключены в конфигурации.</color>");
                return;
            }

            if (hitmarkerEnabledPlayers.Contains(player.userID))
            {
                hitmarkerEnabledPlayers.Remove(player.userID);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Хитмаркеры выключены.</color>");
            }
            else
            {
                hitmarkerEnabledPlayers.Add(player.userID);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>Хитмаркеры включены.</color>");
            }
        }

        private void OnPlayerAttack(BasePlayer attacker, HitInfo hitInfo)
        {
            if (attacker == null || hitInfo?.HitEntity == null) return;
            if (!config.HitmarkerConfig.EnableHitmarkers) return;
            if (!hitmarkerEnabledPlayers.Contains(attacker.userID)) return;
            if (!config.HitmarkerConfig.ShowDamageNumbers) return;

            var hitEntity = hitInfo.HitEntity;
            
            // Проверяем что попали в цель которая может получать урон
            if (!(hitEntity is BaseCombatEntity)) return;

            // Получаем урон
            float damage = hitInfo.damageTypes.Total();
            if (damage <= 0) return;

            // Определяем тип попадания
            bool isHeadshot = hitInfo.isHeadshot;
            bool isKill = false;
            
            if (hitEntity is BaseCombatEntity combatEntity)
            {
                isKill = combatEntity.health - damage <= 0f;
            }

            // Показываем хитмаркер
            ShowSimpleHitmarker(attacker, damage, isHeadshot, isKill);
        }

        private void ShowSimpleHitmarker(BasePlayer player, float damage, bool isHeadshot, bool isKill)
        {
            string uniqueId = UnityEngine.Random.Range(1000, 9999).ToString();
            
            // Определяем цвет и текст
            string color = config.HitmarkerConfig.NormalHitColor;
            if (isKill)
                color = config.HitmarkerConfig.KillColor;
            else if (isHeadshot)
                color = config.HitmarkerConfig.HeadshotColor;
            
            // Форматируем текст урона
            string damageText = $"-{Mathf.FloorToInt(damage)}";
            
            // Вычисляем позицию (центр экрана с небольшим случайным смещением)
            float offsetX = UnityEngine.Random.Range(-0.05f, 0.05f);
            float offsetY = UnityEngine.Random.Range(-0.05f, 0.05f);
            
            float centerX = 0.5f + offsetX;
            float centerY = 0.5f + offsetY;
            
            string anchorMin = $"{centerX - 0.05f} {centerY - 0.02f}";
            string anchorMax = $"{centerX + 0.05f} {centerY + 0.02f}";

            string ui = SimpleHitmarkerUI
                .Replace("{id}", uniqueId)
                .Replace("{text}", damageText)
                .Replace("{color}", color)
                .Replace("{fontsize}", config.HitmarkerConfig.TextSize.ToString())
                .Replace("{outlinecolor}", config.HitmarkerConfig.TextOutlineColor)
                .Replace("{outlinedistance}", config.HitmarkerConfig.TextOutlineDistance)
                .Replace("{anchormin}", anchorMin)
                .Replace("{anchormax}", anchorMax);

            CommunityEntity.ServerInstance.ClientRPCEx(
                new Network.SendInfo() { connection = player.net.connection }, 
                null, "AddUI", ui);

            // Убираем хитмаркер через заданное время
            timer.Once(config.HitmarkerConfig.HitmarkerDuration, () => {
                CommunityEntity.ServerInstance.ClientRPCEx(
                    new Network.SendInfo() { connection = player.net.connection }, 
                    null, "DestroyUI", $"HitMarker_{uniqueId}");
            });
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
                    
                    // Экипируем броню если включено
                    if (config.ArmorConfig.AutoEquipNewBots)
                    {
                        EquipBotWithArmor(bot);
                    }
                    
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
            
            // Bot Armor
            player.ChatMessage($"<color={color}>=== Настройки брони ботов ===</color>");
            player.ChatMessage($"<color={color}>ArmorConfig.CurrentArmorType: {config.ArmorConfig.CurrentArmorType}</color>");
            player.ChatMessage($"<color={color}>ArmorConfig.AutoEquipNewBots: {config.ArmorConfig.AutoEquipNewBots}</color>");
            
            // System
            player.ChatMessage($"<color={color}>=== Системные настройки ===</color>");
            player.ChatMessage($"<color={color}>SystemConfig.ChatColor: {config.SystemConfig.ChatColor}</color>");
            player.ChatMessage($"<color={color}>SystemConfig.LogDebugInfo: {config.SystemConfig.LogDebugInfo}</color>");
            player.ChatMessage($"<color={color}>SystemConfig.AllowInfiniteAmmo: {config.SystemConfig.AllowInfiniteAmmo}</color>");
            
            // Hitmarker Settings
            player.ChatMessage($"<color={color}>=== Настройки хитмаркеров ===</color>");
            player.ChatMessage($"<color={color}>HitmarkerConfig.EnableHitmarkers: {config.HitmarkerConfig.EnableHitmarkers}</color>");
            player.ChatMessage($"<color={color}>HitmarkerConfig.ShowDamageNumbers: {config.HitmarkerConfig.ShowDamageNumbers}</color>");
            player.ChatMessage($"<color={color}>HitmarkerConfig.HitmarkerDuration: {config.HitmarkerConfig.HitmarkerDuration}</color>");
            player.ChatMessage($"<color={color}>HitmarkerConfig.TextSize: {config.HitmarkerConfig.TextSize}</color>");
            player.ChatMessage($"<color={color}>HitmarkerConfig.NormalHitColor: {config.HitmarkerConfig.NormalHitColor}</color>");
            player.ChatMessage($"<color={color}>HitmarkerConfig.HeadshotColor: {config.HitmarkerConfig.HeadshotColor}</color>");
            player.ChatMessage($"<color={color}>HitmarkerConfig.KillColor: {config.HitmarkerConfig.KillColor}</color>");
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
                case "HitmarkerConfig":
                    return typeof(HitmarkerConfig).GetProperty(property)?.GetValue(config.HitmarkerConfig);
                case "ArmorConfig":
                    return typeof(BotArmorConfig).GetProperty(property)?.GetValue(config.ArmorConfig);
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
                        bool allowInfiniteAmmo = config.SystemConfig.AllowInfiniteAmmo;
                        if (allowInfiniteAmmo)
                        {
                            ApplyInfiniteAmmoDefaultToAll();
                        }
                        else
                        {
                            // Если отключили бесконечные патроны, очищаем список игроков
                            infiniteAmmoPlayers.Clear();
                            Puts("[InfiniteAmmo] Бесконечные патроны отключены для всех игроков");
                        }
                    }
                    break;
                case "HitmarkerConfig":
                    success = SetPropertyValue(config.HitmarkerConfig, property, valueStr, out message);
                    // Если изменили настройку EnableHitmarkers, применяем её ко всем игрокам
                    if (success && property == "EnableHitmarkers")
                    {
                        bool enableHitmarkers = config.HitmarkerConfig.EnableHitmarkers;
                        if (enableHitmarkers)
                        {
                            ApplyHitmarkersToAll();
                        }
                        else
                        {
                            // Если отключили хитмаркеры, очищаем список игроков с хитмаркерами
                            hitmarkerEnabledPlayers.Clear();
                            Puts("[Hitmarkers] Хитмаркеры отключены для всех игроков");
                        }
                    }
                    break;
                case "ArmorConfig":
                    success = SetPropertyValue(config.ArmorConfig, property, valueStr, out message);
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

                case "HitmarkerConfig":
                    var defaultHitmarker = new HitmarkerConfig();
                    var resetHitmarkerValue = typeof(HitmarkerConfig).GetProperty(property)?.GetValue(defaultHitmarker);
                    if (resetHitmarkerValue != null)
                    {
                        typeof(HitmarkerConfig).GetProperty(property)?.SetValue(config.HitmarkerConfig, resetHitmarkerValue);
                        SaveConfig();
                        player.ChatMessage($"<color={color}>Настройка {configPath} сброшена к значению по умолчанию: {resetHitmarkerValue}</color>");
                        
                        // Если сбросили настройку EnableHitmarkers, применяем её ко всем игрокам
                        if (property == "EnableHitmarkers")
                        {
                            ApplyHitmarkersToAll();
                        }
                    }
                    else
                    {
                        player.ChatMessage($"<color={color}>Настройка '{property}' не найдена в категории HitmarkerConfig.</color>");
                    }
                    break;

                case "ArmorConfig":
                    var defaultArmor = new BotArmorConfig();
                    var resetArmorValue = typeof(BotArmorConfig).GetProperty(property)?.GetValue(defaultArmor);
                    if (resetArmorValue != null)
                    {
                        typeof(BotArmorConfig).GetProperty(property)?.SetValue(config.ArmorConfig, resetArmorValue);
                        SaveConfig();
                        player.ChatMessage($"<color={color}>Настройка {configPath} сброшена к значению по умолчанию: {resetArmorValue}</color>");
                    }
                    else
                    {
                        player.ChatMessage($"<color={color}>Настройка '{property}' не найдена в категории ArmorConfig.</color>");
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
            var rotation = GetSpawnRotation();
            var newBot = GameManager.server.CreateEntity(BotPrefab, position, rotation)?.ToPlayer();
            if (newBot == null) return;
            
            newBot.Spawn();
            _spawnedBots.Add(newBot.userID);
            
            // Убираем старую позицию смерти
            botDeathPositions.Remove(oldBotId);
            
            timer.Once(0.2f, () => {
                if (newBot != null && !newBot.IsDestroyed)
                {
                    newBot.inventory.Strip();
                    
                    // ДОБАВЛЯЕМ ЭКИПИРОВКУ БРОНИ ПРИ ВОЗРОЖДЕНИИ
                    if (config.ArmorConfig.AutoEquipNewBots)
                    {
                        EquipBotWithArmor(newBot);
                    }
                    
                    // Устанавливаем углы обзора в соответствии с поворотом
                    newBot.viewAngles = new Vector3(0, rotation.eulerAngles.y, 0);
                    newBot.SendNetworkUpdate();
                    
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
        [Command("binfo")]
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

        #region Console Commands
        [ConsoleCommand("byster.spawn")]
        private void ConsoleSpawnBotCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                Puts("Эта команда может быть выполнена только игроком");
                return;
            }
            
            if (_spawnedBots.Count >= config.SpawningConfig.MaxBotsAllowed)
            {
                string message = $"Достигнут лимит ботов ({config.SpawningConfig.MaxBotsAllowed})";
                Puts(message);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 100f, LayerMask.GetMask("Terrain", "World", "Construction")))
            {
                string message = "Не удалось найти подходящую поверхность";
                Puts(message);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
                return;
            }
            
            SpawnBot(hit.point);
            string successMessage = $"Бот успешно заспавнен в точке: {hit.point}";
            Puts(successMessage);
            player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{successMessage}</color>");
            player.Command($"echo {successMessage}");
        }

        [ConsoleCommand("byster.clear")]
        private void ConsoleClearBotsCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var count = ClearAllBots();
            string message = $"Удалено {count} ботов";
            
            Puts(message);
            if (player != null)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
            }
        }

        [ConsoleCommand("byster.move")]
        private void ConsoleBotMovementToggleCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            string message;
            
            if (botsMovementEnabled)
            {
                foreach (var botId in botMovementCoroutines.Keys.ToList())
                {
                    StopBotMovement(botId);
                }
                botsMovementEnabled = false;
                message = "Движение всех ботов остановлено";
            }
            else
            {
                botsMovementEnabled = true;
                var allPlayers = BaseNetworkable.serverEntities.OfType<BasePlayer>().ToList();
                int startedCount = 0;

                foreach (var bot in allPlayers)
                {
                    if (_spawnedBots.Contains(bot.userID) && bot != null && !bot.IsDestroyed && bot.IsAlive())
                    {
                        if (!bot.IsWounded() && !bot.HasPlayerFlag(BasePlayer.PlayerFlags.Wounded))
                        {
                            StartBotMovement(bot);
                            startedCount++;
                        }
                    }
                }
                message = $"Движение ботов запущено. Активных ботов: {startedCount}";
            }
            
            Puts(message);
            if (player != null)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
            }
        }

        [ConsoleCommand("byster.info")]
        private void ConsoleBotInfoCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                Puts("Эта команда может быть выполнена только игроком");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 100f))
            {
                string message = "Не удалось найти цель";
                Puts(message);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
                return;
            }

            var entity = hit.GetEntity();
            if (entity is BasePlayer bot && _spawnedBots.Contains(bot.userID))
            {
                string[] infoLines = {
                    $"=== Информация о боте {bot.userID} ===",
                    $"Позиция: {bot.transform.position}",
                    $"Углы обзора: {bot.viewAngles}",
                    $"Флаги модели: {bot.modelState.flags}",
                    $"Живой: {bot.IsAlive()}",
                    $"Активен: {!bot.IsDestroyed}",
                    $"Корутина активна: {botMovementCoroutines.ContainsKey(bot.userID)}"
                };
                
                foreach (string line in infoLines)
                {
                    Puts(line);
                    player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{line}</color>");
                    player.Command($"echo {line}");
                }
            }
            else
            {
                string message = "Это не бот или бот не найден в списке";
                Puts(message);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
            }
        }

        [ConsoleCommand("byster.remove")]
        private void ConsoleRemoveCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                Puts("Эта команда может быть выполнена только игроком");
                return;
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 100f))
            {
                string message = "Вы ни на что не смотрите";
                Puts(message);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
                return;
            }

            var entity = hit.GetEntity();
            if (entity == null || entity == player)
            {
                string message = "Неверная цель";
                Puts(message);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
                return;
            }
            
            entity.Kill();
            string successMessage = "Объект удален";
            Puts(successMessage);
            player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{successMessage}</color>");
            player.Command($"echo {successMessage}");
        }

        [ConsoleCommand("byster.infammo")]
        private void ConsoleInfAmmoCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                Puts("Эта команда может быть выполнена только игроком");
                return;
            }
            
            if (!config.SystemConfig.AllowInfiniteAmmo)
            {
                string message = "Команда infammo отключена в конфигурации";
                Puts(message);
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
                return;
            }

            string statusMessage;
            if (infiniteAmmoPlayers.Contains(player.userID))
            {
                infiniteAmmoPlayers.Remove(player.userID);
                statusMessage = $"Бесконечные патроны выключены для игрока {player.displayName}";
            }
            else
            {
                infiniteAmmoPlayers.Add(player.userID);
                statusMessage = $"Бесконечные патроны включены для игрока {player.displayName}";
            }
            
            Puts(statusMessage);
            player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{statusMessage}</color>");
            player.Command($"echo {statusMessage}");
        }

        [ConsoleCommand("byster.config")]
        private void ConsoleConfigCommand(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            
            if (arg.Args.Length == 0)
            {
                string helpMessage = "Использование: byster.config [list|get|set|reset] [параметры]";
                Puts(helpMessage);
                if (player != null)
                {
                    player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{helpMessage}</color>");
                    player.Command($"echo {helpMessage}");
                }
                return;
            }

            string subCommand = arg.Args[0].ToLower();
            
            switch (subCommand)
            {
                case "list":
                    DisplayConsoleConfig(player);
                    break;
                case "get":
                    if (arg.Args.Length < 2)
                    {
                        string usage = "Использование: byster.config get [имя_настройки]";
                        Puts(usage);
                        if (player != null)
                        {
                            player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{usage}</color>");
                            player.Command($"echo {usage}");
                        }
                        return;
                    }
                    GetConsoleConfigValue(arg.Args[1], player);
                    break;
                case "set":
                    if (arg.Args.Length < 3)
                    {
                        string usage = "Использование: byster.config set [имя_настройки] [значение]";
                        Puts(usage);
                        if (player != null)
                        {
                            player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{usage}</color>");
                            player.Command($"echo {usage}");
                        }
                        return;
                    }
                    SetConsoleConfigValue(arg.Args[1], string.Join(" ", arg.Args.Skip(2)), player);
                    break;
                default:
                    string errorMessage = "Неизвестная подкоманда. Доступны: list, get, set";
                    Puts(errorMessage);
                    if (player != null)
                    {
                        player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{errorMessage}</color>");
                        player.Command($"echo {errorMessage}");
                    }
                    break;
            }
        }

        #region Console Config Methods
        private void DisplayConsoleConfig(BasePlayer player)
        {
            string[] configLines = {
                "=== Конфигурация BysterPlugin ===",
                "-- MovementConfig --",
                $"PathDelay: {config.MovementConfig.PathDelay}",
                $"StepSize: {config.MovementConfig.StepSize}",
                $"MaxClimbHeight: {config.MovementConfig.MaxClimbHeight}",
                $"MovementUpdateRate: {config.MovementConfig.MovementUpdateRate}",
                $"MovementDistance: {config.MovementConfig.MovementDistance}",
                $"EnableJumping: {config.MovementConfig.EnableJumping}",
                $"SpawnRotationMode: {config.MovementConfig.SpawnRotationMode}",
                $"SpawnRotationDegrees: {config.MovementConfig.SpawnRotationDegrees}",
                $"SpawnRotationRange: {config.MovementConfig.SpawnRotationRange}",
                $"MovementRotationMode: {config.MovementConfig.MovementRotationMode}",
                $"KeepSpawnRotation: {config.MovementConfig.KeepSpawnRotation}",
                "-- SpawningConfig --",
                $"RespawnDelay: {config.SpawningConfig.RespawnDelay}",
                $"AutoRespawn: {config.SpawningConfig.AutoRespawn}",
                $"MaxBotsAllowed: {config.SpawningConfig.MaxBotsAllowed}",
                $"ClearBotsOnServerShutdown: {config.SpawningConfig.ClearBotsOnServerShutdown}",
                "-- SystemConfig --",
                $"ChatColor: {config.SystemConfig.ChatColor}",
                $"LogDebugInfo: {config.SystemConfig.LogDebugInfo}",
                $"AllowInfiniteAmmo: {config.SystemConfig.AllowInfiniteAmmo}",
                "-- HitmarkerConfig --",
                $"EnableHitmarkers: {config.HitmarkerConfig.EnableHitmarkers}",
                $"ShowDamageNumbers: {config.HitmarkerConfig.ShowDamageNumbers}",
                $"HitmarkerDuration: {config.HitmarkerConfig.HitmarkerDuration}",
                $"TextSize: {config.HitmarkerConfig.TextSize}",
                $"NormalHitColor: {config.HitmarkerConfig.NormalHitColor}",
                $"HeadshotColor: {config.HitmarkerConfig.HeadshotColor}",
                $"KillColor: {config.HitmarkerConfig.KillColor}",
                "-- ArmorConfig --",
                $"CurrentArmorType: {config.ArmorConfig.CurrentArmorType}",
                $"AutoEquipNewBots: {config.ArmorConfig.AutoEquipNewBots}"
            };
            
            foreach (string line in configLines)
            {
                Puts(line);
                if (player != null)
                {
                    player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{line}</color>");
                    player.Command($"echo {line}");
                }
            }
        }

        private void GetConsoleConfigValue(string configPath, BasePlayer player)
        {
            object value = GetConfigValueByPath(configPath);
            string message;
            
            if (value != null)
            {
                message = $"{configPath} = {value}";
            }
            else
            {
                message = $"Настройка '{configPath}' не найдена";
            }
            
            Puts(message);
            if (player != null)
            {
                player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{message}</color>");
                player.Command($"echo {message}");
            }
        }

        private void SetConsoleConfigValue(string configPath, string valueStr, BasePlayer player)
        {
            string[] pathParts = configPath.Split('.');
            if (pathParts.Length != 2)
            {
                string errorMessage = "Неверный формат пути к настройке. Используйте формат 'Секция.Настройка'";
                Puts(errorMessage);
                if (player != null)
                {
                    player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{errorMessage}</color>");
                    player.Command($"echo {errorMessage}");
                }
                return;
            }
            
            string section = pathParts[0];
            string property = pathParts[1];
            object targetObject = null;
            
            switch (section.ToLower())
            {
                case "movementconfig":
                    targetObject = config.MovementConfig;
                    break;
                case "spawningconfig":
                    targetObject = config.SpawningConfig;
                    break;
                case "systemconfig":
                    targetObject = config.SystemConfig;
                    break;
                case "hitmarkerconfig":
                    targetObject = config.HitmarkerConfig;
                    break;
                case "armorconfig":
                    targetObject = config.ArmorConfig;
                    break;
                default:
                    string sectionError = $"Неизвестная секция конфигурации: {section}";
                    Puts(sectionError);
                    if (player != null)
                    {
                        player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{sectionError}</color>");
                        player.Command($"echo {sectionError}");
                    }
                    return;
            }
            
            string errorMsg;
            if (SetPropertyValue(targetObject, property, valueStr, out errorMsg))
            {
                SaveConfig();
                string successMessage = $"Настройка {configPath} успешно установлена в {valueStr}";
                Puts(successMessage);
                if (player != null)
                {
                    player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{successMessage}</color>");
                    player.Command($"echo {successMessage}");
                }
            }
            else
            {
                string errorMessage = $"Ошибка при установке {configPath}: {errorMsg}";
                Puts(errorMessage);
                if (player != null)
                {
                    player.ChatMessage($"<color={config.SystemConfig.ChatColor}>{errorMessage}</color>");
                    player.Command($"echo {errorMessage}");
                }
            }
        }
        #endregion

        #endregion // Console Commands

        // Очищаем данные при отключении игрока
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            hitmarkerEnabledPlayers.Remove(player.userID);
            infiniteAmmoPlayers.Remove(player.userID);
        }

        #region Bot Armor System
        [Command("barmor")]
        private void BotArmorCommand(BasePlayer player, string command, string[] args)
        {
            var chatColor = config.SystemConfig.ChatColor;
            
            if (args.Length == 0)
            {
                player.ChatMessage($"<color={chatColor}>Текущий тип брони: {config.ArmorConfig.CurrentArmorType}</color>");
                player.ChatMessage($"<color={chatColor}>Автоматическая экипировка: {config.ArmorConfig.AutoEquipNewBots}</color>");
                player.ChatMessage($"<color={chatColor}>Использование: /barmor [hqm/signs/hazmat/naked]</color>");
                player.ChatMessage($"<color={chatColor}>  hqm - полный металлический сет</color>");
                player.ChatMessage($"<color={chatColor}>  signs - броня из дорожных знаков</color>");
                player.ChatMessage($"<color={chatColor}>  hazmat - хазмат костюм</color>");
                player.ChatMessage($"<color={chatColor}>  naked - без брони</color>");
                return;
            }

            string armorType = args[0].ToLower();
            
            switch (armorType)
            {
                case "hqm":
                case "signs":
                case "hazmat":
                case "naked":
                    config.ArmorConfig.CurrentArmorType = armorType;
                    SaveConfig();
                    player.ChatMessage($"<color={chatColor}>Тип брони для новых ботов установлен: {armorType}</color>");
                    break;
                    
                case "auto":
                    if (args.Length > 1)
                    {
                        if (bool.TryParse(args[1], out bool autoEquip))
                        {
                            config.ArmorConfig.AutoEquipNewBots = autoEquip;
                            SaveConfig();
                            player.ChatMessage($"<color={chatColor}>Автоматическая экипировка: {autoEquip}</color>");
                        }
                        else
                        {
                            player.ChatMessage($"<color={chatColor}>Введите true или false после auto</color>");
                        }
                    }
                    else
                    {
                        player.ChatMessage($"<color={chatColor}>Использование: /barmor auto [true/false]</color>");
                    }
                    break;
                    
                case "apply":
                    // Применяем броню ко всем существующим ботам
                    ApplyArmorToAllBots();
                    player.ChatMessage($"<color={chatColor}>Броня применена ко всем существующим ботам.</color>");
                    break;
                    
                default:
                    player.ChatMessage($"<color={chatColor}>Неизвестный тип брони. Доступны: hqm, signs, hazmat, naked</color>");
                    break;
            }
        }

        private void EquipBotWithArmor(BasePlayer bot, string armorType = null)
        {
            if (bot == null || bot.IsDestroyed) return;
            
            // Используем переданный тип или текущий из конфига
            string typeToUse = armorType ?? config.ArmorConfig.CurrentArmorType;
            
            // Очищаем инвентарь И экипировку перед экипировкой
            bot.inventory.Strip();
            
            // Небольшая задержка перед экипировкой
            timer.Once(0.1f, () => {
                if (bot != null && !bot.IsDestroyed)
                {
                    switch (typeToUse.ToLower())
                    {
                        case "hqm":
                            EquipHQMArmor(bot);
                            break;
                        case "signs":
                            EquipSignsArmor(bot);
                            break;
                        case "hazmat":
                            EquipHazmatSuit(bot);
                            break;
                        case "naked":
                        default:
                            // Остается голым
                            break;
                    }
                    
                    // Отправляем обновление сети
                    bot.SendNetworkUpdate();
                }
            });
        }

        private void EquipHQMArmor(BasePlayer bot)
        {
            // Полный металлический сет
            GiveItemToBot(bot, "metal.facemask", 1);
            GiveItemToBot(bot, "hoodie", 1);
            GiveItemToBot(bot, "metal.plate.torso", 1);
            GiveItemToBot(bot, "pants", 1);
            GiveItemToBot(bot, "roadsign.kilt", 1);
            GiveItemToBot(bot, "tactical.gloves", 1);
            GiveItemToBot(bot, "shoes.boots", 1);
        }

        private void EquipSignsArmor(BasePlayer bot)
        {
            // Броня из дорожных знаков
            GiveItemToBot(bot, "coffeecan.helmet", 1);
            GiveItemToBot(bot, "hoodie", 1);
            GiveItemToBot(bot, "roadsign.jacket", 1);
            GiveItemToBot(bot, "pants", 1);
            GiveItemToBot(bot, "roadsign.kilt", 1);
            GiveItemToBot(bot, "tactical.gloves", 1);
            GiveItemToBot(bot, "shoes.boots", 1);
        }

        private void EquipHazmatSuit(BasePlayer bot)
        {
            // Хазмат костюм
            GiveItemToBot(bot, "hazmatsuit", 1);
        }

        private void GiveItemToBot(BasePlayer bot, string shortname, int amount = 1)
        {
            var item = ItemManager.CreateByName(shortname, amount);
            if (item == null) return;
            
            item.condition = item.info.condition.max;
            
            // Проверяем можно ли добавить предмет в контейнер экипировки
            var wearContainer = bot.inventory.containerWear;
            var canAcceptResult = wearContainer.CanAcceptItem(item, -1);
            
            if (canAcceptResult != ItemContainer.CanAcceptResult.CanAccept)
            {
                // Если места нет, очищаем подходящий слот
                ClearSlotForItemType(bot, shortname);
            }
            
            // Добавляем предмет
            if (!item.MoveToContainer(wearContainer, -1))
            {
                // Если не получилось в экипировку, добавляем в инвентарь
                bot.inventory.GiveItem(item);
            }
        }

        private void ClearSlotForItemType(BasePlayer bot, string itemType)
        {
            var wearContainer = bot.inventory.containerWear;
            int targetSlot = GetSlotForItemType(itemType);
            
            if (targetSlot >= 0 && targetSlot < wearContainer.capacity)
            {
                var existingItem = wearContainer.GetSlot(targetSlot);
                if (existingItem != null)
                {
                    existingItem.RemoveFromContainer();
                    existingItem.Remove();
                }
            }
        }

        private int GetSlotForItemType(string itemType)
        {
            // Определяем какой слот нужно очистить для данного типа предмета
            if (itemType.Contains("helmet") || itemType.Contains("facemask") || itemType.Contains("bucket"))
                return 0; // Голова
            else if (itemType.Contains("jacket") || itemType.Contains("torso") || itemType.Contains("hazmatsuit"))
                return 1; // Торс
            else if (itemType.Contains("kilt") || itemType.Contains("pants"))
                return 2; // Ноги
            else if (itemType.Contains("boots") || itemType.Contains("shoes"))
                return 3; // Обувь
            
            return -1; // Неизвестный тип
        }

        private void ClearWearSlotForItem(BasePlayer bot, Item newItem)
        {
            var wearContainer = bot.inventory.containerWear;
            
            // Проходим по всем слотам экипировки
            for (int i = 0; i < wearContainer.capacity; i++)
            {
                var existingItem = wearContainer.GetSlot(i);
                if (existingItem != null && CanItemFitInSlot(newItem, i))
                {
                    // Удаляем существующий предмет из слота
                    existingItem.RemoveFromContainer();
                    existingItem.Remove();
                    break;
                }
            }
        }

        private bool CanItemFitInSlot(Item item, int slot)
        {
            // Проверяем совместимость предмета со слотом
            var itemDef = item.info;
            
            // Слот 0 - шлем/голова
            if (slot == 0 && (itemDef.shortname.Contains("helmet") || 
                              itemDef.shortname.Contains("hat") || 
                              itemDef.shortname.Contains("facemask") || 
                              itemDef.shortname.Contains("bucket")))
                return true;
            
            // Слот 1 - торс
            if (slot == 1 && (itemDef.shortname.Contains("torso") || 
                              itemDef.shortname.Contains("jacket") || 
                              itemDef.shortname.Contains("hazmatsuit")))
                return true;
            
            // Слот 2 - ноги
            if (slot == 2 && (itemDef.shortname.Contains("kilt") || 
                              itemDef.shortname.Contains("pants") || 
                              itemDef.shortname.Contains("shorts")))
                return true;
            
            // Слот 3 - обувь
            if (slot == 3 && (itemDef.shortname.Contains("boots") || 
                              itemDef.shortname.Contains("shoes")))
                return true;
            
            return false;
        }

        private void EquipItemFromInventory(BasePlayer bot, Item item)
        {
            if (bot == null || item == null) return;
            
            // Определяем в какой слот экипировки должен попасть предмет
            var wearContainer = bot.inventory.containerWear;
            
            // Находим подходящий слот для данного типа предмета
            for (int i = 0; i < wearContainer.capacity; i++)
            {
                var slot = wearContainer.GetSlot(i);
                if (slot == null && CanItemFitInSlot(item, i))
                {
                    item.MoveToContainer(wearContainer, i);
                    break;
                }
            }
        }

        private void ApplyArmorToAllBots()
        {
            var allPlayers = BaseNetworkable.serverEntities.OfType<BasePlayer>().ToList();
            int equippedCount = 0;

            foreach (var player in allPlayers)
            {
                if (_spawnedBots.Contains(player.userID) && player != null && !player.IsDestroyed)
                {
                    EquipBotWithArmor(player);
                    equippedCount++;
                }
            }
            
            Puts($"[ARMOR] Экипировано ботов: {equippedCount}");
        }
        #endregion
    }
}