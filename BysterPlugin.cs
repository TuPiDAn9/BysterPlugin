using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Carbon.Plugins;
using UnityEngine;
using UnityEngine.AI;

namespace Carbon.Plugins
{
    [Info("BysterPlugin", "TuPiDAn", "2.0.6")]
    [Description("Adds bots with movement, infinite ammo, and other features.")]
    public class BysterPlugin : CarbonPlugin
    {
        private const string BotPrefab = "assets/prefabs/player/player.prefab";
        private const string ChatColor = "#B8860B"; // Жёлтый
        private readonly HashSet<ulong> _spawnedBots = new HashSet<ulong>();
        private readonly HashSet<ulong> infiniteAmmoPlayers = new HashSet<ulong>();
        private readonly Dictionary<ulong, Coroutine> botMovementCoroutines = new Dictionary<ulong, Coroutine>();
        private readonly Dictionary<ulong, Vector3> botDeathPositions = new Dictionary<ulong, Vector3>();
        private bool _isClearingBots = false;
        private bool botsMovementEnabled = true; // Флаг для управления движением всех ботов

        #region Infinite Ammo
        [Command("infammo")]
        private void InfAmmoCommand(BasePlayer player, string command, string[] args)
        {
            if (infiniteAmmoPlayers.Contains(player.userID))
            {
                infiniteAmmoPlayers.Remove(player.userID);
                player.ChatMessage($"<color={ChatColor}>Бесконечные патроны выключены.</color>");
            }
            else
            {
                infiniteAmmoPlayers.Add(player.userID);
                player.ChatMessage($"<color={ChatColor}>Бесконечные патроны включены.</color>");
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
        #endregion

        #region Bot Management
        [Command("bspawn")]
        private void SpawnBotCommand(BasePlayer player, string command, string[] args)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 100f, LayerMask.GetMask("Terrain", "World", "Construction")))
            {
                player.ChatMessage($"<color={ChatColor}>Не удалось найти подходящую поверхность. Пожалуйста, посмотрите на землю.</color>");
                return;
            }
            SpawnBot(hit.point);
            player.ChatMessage($"<color={ChatColor}>Бот успешно заспавнен в точке: {hit.point}</color>");
        }
        
        [Command("bclear")]
        private void ClearBotsCommand(BasePlayer player, string command, string[] args)
        {
            var count = ClearAllBots();
            player.ChatMessage($"<color={ChatColor}>Удалено {count} ботов.</color>");
        }

        [Command("remove")]
        private void RemoveCommand(BasePlayer player, string command, string[] args)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 100f))
            {
                player.ChatMessage($"<color={ChatColor}>Вы ни на что не смотрите.</color>");
                return;
            }

            var entity = hit.GetEntity();
            if (entity == null || entity == player)
            {
                player.ChatMessage($"<color={ChatColor}>Неверная цель.</color>");
                return;
            }
            
            entity.Kill();
            player.ChatMessage($"<color={ChatColor}>Объект удален.</color>");
        }

        private void OnServerShutdown()
        {
            ClearAllBots();
        }
        
        private void SpawnBot(Vector3 position)
        {
            var bot = GameManager.server.CreateEntity(BotPrefab, position, Quaternion.identity)?.ToPlayer();
            if (bot == null) return;
            
            bot.Spawn();
            _spawnedBots.Add(bot.userID);
            
            timer.Once(0.2f, () => {
                if (bot != null && !bot.IsDestroyed)
                {
                    bot.inventory.Strip();
                    StartBotMovement(bot);
                }
            });
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
                player.ChatMessage($"<color={ChatColor}>Движение всех ботов остановлено.</color>");
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

                player.ChatMessage($"<color={ChatColor}>Движение ботов запущено. Активных ботов: {startedCount}</color>");
                Puts($"[BMOVE] Движение ботов запущено. Активных ботов: {startedCount}");
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

                // Выбираем случайное направление движения
                var randomDirection = UnityEngine.Random.insideUnitSphere;
                randomDirection.y = 0;
                randomDirection.Normalize();

                // Поворачиваем бота в выбранном направлении
                var targetRotation = Quaternion.LookRotation(randomDirection);
                bot.viewAngles = targetRotation.eulerAngles;
                bot.SendNetworkUpdate();

                // Устанавливаем флаги движения
                bot.modelState.flags = (int)(ModelState.Flag.OnGround | ModelState.Flag.Sprinting);
                bot.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, false);
                
                // Движение
                yield return SafeMovement(bot, randomDirection, 15f); // 15 метров за цикл

                // Пауза между движениями
                if (bot != null && !bot.IsDestroyed)
                {
                    bot.modelState.flags = (int)ModelState.Flag.OnGround;
                    bot.SendNetworkUpdate();
                    yield return new WaitForSeconds(0.1f);
                }
            }
        }

        private IEnumerator SafeMovement(BasePlayer bot, Vector3 direction, float totalDistance)
        {
            float distanceMoved = 0f;
            float stepSize = 0.3f; // Маленькие шаги
            
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
                if (Mathf.Abs(groundPosition.y - currentPosition.y) > 2f)
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
                yield return new WaitForSeconds(0.05f); // 20 FPS обновление
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
                        Mathf.Abs(groundTest.y - currentPosition.y) <= 2f)
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
            
            // Сохраняем позицию смерти
            Vector3 deathPosition = player.transform.position;
            botDeathPositions[player.userID] = deathPosition;
            
            // Останавливаем движение умершего бота
            StopBotMovement(player.userID);
            
            // Планируем возрождение через минимальную задержку
            timer.Once(0.1f, () => {
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
                player.ChatMessage($"<color={ChatColor}>Это не бот или бот не найден в списке</color>");
            }
        }
        #endregion
    }
}