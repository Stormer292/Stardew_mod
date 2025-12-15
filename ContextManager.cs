using StardewValley;
using StardewModdingAPI;
using System.Collections.Generic;
using System.Linq;
using System;

namespace LLMBrainMod
{
    public static class ContextManager
    {
        private static IMonitor Monitor;
        private static Dictionary<string, NpcContext> _contexts = new();

        public static void Initialize(IMonitor monitor)
        {
            Monitor = monitor;
            QuestTrackingModule.SetMonitor(monitor);
        }

        // Загружаем все JSON-конфиги (вызов в ModEntry)
        public static void LoadConfigs(IModHelper helper)
        {
            ConfigManager.LoadAll(helper, Monitor);
        }

        public static void LoadQuestTracking(IModHelper helper)
        {
            QuestTrackingModule.Load(helper);
        }

        public static void SaveQuestTracking(IModHelper helper)
        {
            QuestTrackingModule.Save(helper);
        }

        public static void CleanupOldQuestRecords()
        {
            QuestTrackingModule.CleanupOldRecords();
        }

        public static void SetMonitor(IMonitor monitor)
        {
            Monitor = monitor;
        }

        public static IEnumerable<NpcContext> AllContexts => _contexts.Values;

        private static int GetFriendshipLevel(int points)
        {
            return System.Math.Max(0, System.Math.Min(10, points / 250));
        }

        // Обновляет расписание NPC на основе словаря Schedule (ключи - часы, например 6,12,18)
        private static void UpdateSchedule(NpcContext ctx)
        {
            if (ctx == null) return;
            if (ctx.Schedule == null || ctx.Schedule.Count == 0) return;
            if (ctx.ScheduleOverridden) return;

            // Текущее "часовое" значение (например, 600 -> 6)
            int currentHour = Math.Max(0, ctx.TimeOfDay / 100);

            // Выбираем наиболее близкий часовой ключ, не превышающий текущее время; если нет — берём минимальный
            int selectedKey = ctx.Schedule.Keys.Where(k => k <= currentHour).DefaultIfEmpty(int.MinValue).Max();
            if (selectedKey == int.MinValue) selectedKey = ctx.Schedule.Keys.Min();

            if (ctx.Schedule.TryGetValue(selectedKey, out var scheduledBehavior))
            {
                // Не перетираем срочные состояния питания/отдыха
                if (ctx.CurrentBehavior != NPCBehavior.Eating && ctx.CurrentBehavior != NPCBehavior.Resting)
                {
                    ctx.CurrentBehavior = scheduledBehavior;
                    ctx.OverrideReason = $"Scheduled behavior at {selectedKey}:00";
                }
            }
        }

        // Простая симуляция крафта: если NPC работает и имеет достаточную выносливость,
        // он создаёт 1 единицу из очереди крафта за вызов и тратит стамину.
        private static void SimulateCrafting(NpcContext ctx)
        {
            if (ctx == null) return;
            if (ctx.CraftingQueue == null || ctx.CraftingQueue.Count == 0) return;

            // Требуемая стамина для работы
            int cost = Math.Max(1, ctx.WorkStaminaCost);

            // Выполняем крафт только если NPC работает и достаточно стамины
            if (ctx.CurrentBehavior != NPCBehavior.Working) return;
            if (ctx.Stamina < cost) return;

            var kv = ctx.CraftingQueue.FirstOrDefault(c => c.Value > 0);
            if (kv.Key == null) return;

            // Тратим стамину и создаём 1 элемент
            ctx.Stamina = Math.Max(0f, ctx.Stamina - cost);

            if (ctx.Inventory.ContainsKey(kv.Key)) ctx.Inventory[kv.Key] += 1;
            else ctx.Inventory[kv.Key] = 1;

            ctx.CraftingQueue[kv.Key]--;
            if (ctx.CraftingQueue[kv.Key] <= 0) ctx.CraftingQueue.Remove(kv.Key);

            Monitor?.Log($"{ctx.Name} crafted 1x {kv.Key}. Stamina -> {ctx.Stamina:F0}%", LogLevel.Debug);
        }

        public static void UpdateAllContexts(int gameTicks)
        {
            if (gameTicks % 600 != 0) return;

            foreach (var ctx in _contexts.Values.ToList())
            {
                UpdateNPCParameters(ctx);
                UpdateMood(ctx);
                UpdateBehavior(ctx);
                UpdateSchedule(ctx);
            }
        }

        private static void UpdateNPCParameters(NpcContext ctx)
        {
            int timeOfDay = ctx.TimeOfDay;
            int normalizedTime = timeOfDay < 600 ? timeOfDay + 2400 : timeOfDay;
            float dayProgress = Math.Max(0f, Math.Min(1f, (normalizedTime - 600) / 2400f));

            // Stamina - оставляем логику, но используем конфиг еды для приёма пищи
            float morningSatiety = 80f;
            float eveningSatiety = 20f;
            float expectedSatiety = morningSatiety - (dayProgress * (morningSatiety - eveningSatiety));

            float satietyModifier = 1.0f;
            if (ctx.CurrentBehavior == NPCBehavior.Working) satietyModifier *= 1.3f;
            if (ctx.CurrentBehavior == NPCBehavior.Resting) satietyModifier *= 0.8f;
            if (ctx.CurrentMood == Mood.Stressed || ctx.CurrentMood == Mood.Angry) satietyModifier *= 1.5f;

            float targetSatiety = Math.Max(0f, expectedSatiety * satietyModifier);
            ctx.Satiety = Math.Max(0f, Math.Min(100f, ctx.Satiety + (targetSatiety - ctx.Satiety) * 0.05f));

            float morningStamina = 100f;
            float eveningStamina = 30f;
            float expectedStamina = morningStamina - (dayProgress * (morningStamina - eveningStamina));

            float staminaModifier = 1.0f;
            if (ctx.CurrentBehavior == NPCBehavior.Working) staminaModifier *= 1.5f;
            if (ctx.CurrentBehavior == NPCBehavior.Resting) staminaModifier *= 0.7f;
            if (ctx.CurrentMood == Mood.Tired) staminaModifier *= 0.6f;
            if (ctx.Satiety < 30) staminaModifier *= 0.8f;

            float targetStamina = Math.Max(0f, expectedStamina * staminaModifier);
            ctx.Stamina = Math.Max(0f, Math.Min(100f, ctx.Stamina + (targetStamina - ctx.Stamina) * 0.05f));

            if (ctx.CurrentBehavior == NPCBehavior.Resting)
            {
                float recovery = ctx.RestRecoveryRate * 0.1f;
                float recoveryModifier = 1.0f;
                bool isMorning = timeOfDay >= 600 && timeOfDay < 1200;
                bool isDaytime = timeOfDay >= 1200 && timeOfDay < 1800;
                bool isEvening = timeOfDay >= 1800 && timeOfDay < 2400;
                bool isNight = timeOfDay >= 2400 || timeOfDay < 600;

                if (isMorning) recoveryModifier *= 0.8f;
                else if (isDaytime) recoveryModifier *= 0.9f;
                else if (isEvening) recoveryModifier *= 1.1f;
                else if (isNight) recoveryModifier *= 1.4f;

                if (ctx.CurrentMood == Mood.Content || ctx.CurrentMood == Mood.Happy) recoveryModifier *= 1.5f;
                if (ctx.Satiety > 70) recoveryModifier *= 1.2f;

                ctx.Stamina = Math.Min(100f, ctx.Stamina + (recovery * recoveryModifier));
            }

            // Питание через конфиг: кушаем наиболее сытное, доступное в инвентарях
            if (ctx.CurrentBehavior == NPCBehavior.Eating)
            {
                var foodsOrdered = ConfigManager.FoodItems
                    .OrderByDescending(it => ConfigManager.GetSatiety(it))
                    .ToList();

                foreach (var food in foodsOrdered)
                {
                    if (ctx.HiddenInventory.TryGetValue(food, out int hiddenCount) && hiddenCount > 0)
                    {
                        ctx.HiddenInventory[food]--;
                        if (ctx.HiddenInventory[food] <= 0) ctx.HiddenInventory.Remove(food);
                        ctx.Satiety = Math.Min(100f, ctx.Satiety + ConfigManager.GetSatiety(food));
                        Monitor?.Log($"{ctx.Name} ate {food} from hidden inventory, satiety -> {ctx.Satiety:F0}%", LogLevel.Debug);
                        break;
                    }

                    if (ctx.Inventory.TryGetValue(food, out int visCount) && visCount > 0)
                    {
                        ctx.Inventory[food]--;
                        if (ctx.Inventory[food] <= 0) ctx.Inventory.Remove(food);
                        ctx.Satiety = Math.Min(100f, ctx.Satiety + ConfigManager.GetSatiety(food));
                        Monitor?.Log($"{ctx.Name} ate {food} from visible inventory, satiety -> {ctx.Satiety:F0}%", LogLevel.Debug);
                        break;
                    }
                }
            }

            ContextManager.SimulateCrafting(ctx);

            if (NeedsResourcesForWork(ctx) && ctx.CurrentMood != Mood.Happy) ctx.CurrentMood = Mood.Sad;
        }

        private static void UpdateMood(NpcContext ctx)
        {
            int timeOfDay = ctx.TimeOfDay;
            bool isMorning = timeOfDay >= 600 && timeOfDay < 1200;
            bool isDaytime = timeOfDay >= 1200 && timeOfDay < 1800;
            bool isEvening = timeOfDay >= 1800 && timeOfDay < 2400;
            bool isNight = timeOfDay >= 2400 || timeOfDay < 600;

            if (ctx.Satiety < 20) ctx.CurrentMood = Mood.Hungry;
            else if (ctx.Stamina < 20) ctx.CurrentMood = Mood.Tired;
            else if (isNight && ctx.CurrentBehavior != NPCBehavior.Resting) ctx.CurrentMood = Mood.Tired;
            else if (ctx.Satiety > 80 && ctx.Stamina > 80 && !NeedsResourcesForWork(ctx))
            {
                ctx.CurrentMood = isMorning ? Mood.Happy : Mood.Content;
            }
            else if (ctx.Satiety < 50 && ctx.Stamina < 50) ctx.CurrentMood = Mood.Stressed;
            else if (NeedsResourcesForWork(ctx)) ctx.CurrentMood = Mood.Sad;
            else if (isEvening && ctx.Stamina < 40) ctx.CurrentMood = Mood.Tired;
            else if (ctx.FriendshipLevel >= 8 && ctx.Inventory.Any(i => i.Value > 0)) ctx.CurrentMood = Mood.Excited;
            else if (ctx.Satiety > 60 && ctx.Stamina > 60) ctx.CurrentMood = Mood.Content;
            else if (isDaytime && ctx.CurrentBehavior == NPCBehavior.Working && ctx.Stamina < 30) ctx.CurrentMood = Mood.Tired;
            else if (isMorning && ctx.Stamina > 70 && ctx.Satiety > 70) ctx.CurrentMood = Mood.Happy;
            else if (isEvening && ctx.Satiety < 40) ctx.CurrentMood = Mood.Neutral;
            else ctx.CurrentMood = Mood.Neutral;
        }

        private static void UpdateBehavior(NpcContext ctx)
        {
            int timeOfDay = ctx.TimeOfDay;
            bool isMorning = timeOfDay >= 600 && timeOfDay < 1200;
            bool isDaytime = timeOfDay >= 1200 && timeOfDay < 1800;
            bool isEvening = timeOfDay >= 1800 && timeOfDay < 2400;
            bool isNight = timeOfDay >= 2400 || timeOfDay < 600;

            // Используем пороги из behavior-rules.json, если присутствуют
            int hungerThreshold = ConfigManager.BehaviorRules?.HungerThreshold ?? 20;
            int tiredThreshold = ConfigManager.BehaviorRules?.TiredThreshold ?? 30;

            if (ctx.Satiety < hungerThreshold)
            {
                ctx.CurrentBehavior = NPCBehavior.Eating;
                ctx.OverrideReason = "Голоден";
            }
            else if (ctx.Stamina < tiredThreshold)
            {
                ctx.CurrentBehavior = NPCBehavior.Resting;
                ctx.OverrideReason = "Устал";
            }
            else if (ctx.CurrentMood == Mood.Hungry && ctx.Satiety < hungerThreshold * 2)
            {
                ctx.CurrentBehavior = NPCBehavior.Eating;
                ctx.OverrideReason = "Голоден";
            }
            else if (ctx.CurrentMood == Mood.Tired && ctx.Stamina < tiredThreshold * 2)
            {
                ctx.CurrentBehavior = NPCBehavior.Resting;
                ctx.OverrideReason = "Устал";
            }
            else if (isNight)
            {
                ctx.CurrentBehavior = NPCBehavior.Resting;
                ctx.OverrideReason = "Поздно, время отдыхать";
            }
            else if (isMorning)
            {
                if (NeedsResourcesForWork(ctx))
                {
                    ctx.CurrentBehavior = NPCBehavior.Shopping;
                    ctx.OverrideReason = "Нуждается в ресурсах для работы";
                }
                else
                {
                    ctx.CurrentBehavior = NPCBehavior.Working;
                    ctx.OverrideReason = "Утренняя работа";
                }
            }
            else if (isDaytime)
            {
                ctx.CurrentBehavior = NPCBehavior.Working;
                ctx.OverrideReason = "Рабочее время";
            }
            else if (isEvening)
            {
                if (ctx.Stamina < 50)
                {
                    ctx.CurrentBehavior = NPCBehavior.Resting;
                    ctx.OverrideReason = "Устал, нужно отдохнуть";
                }
                else if (NeedsResourcesForWork(ctx))
                {
                    ctx.CurrentBehavior = NPCBehavior.Shopping;
                    ctx.OverrideReason = "Нуждается в ресурсах для завтрашней работы";
                }
                else
                {
                    ctx.CurrentBehavior = NPCBehavior.Working;
                    ctx.OverrideReason = "Подготовка к следующему дню";
                }
            }
            else if (NeedsResourcesForWork(ctx))
            {
                ctx.CurrentBehavior = NPCBehavior.Shopping;
                ctx.OverrideReason = "Нуждается в ресурсах для работы";
            }
            else if (ctx.CurrentMood == Mood.Sad || ctx.CurrentMood == Mood.Angry)
            {
                ctx.CurrentBehavior = NPCBehavior.Socializing;
                ctx.OverrideReason = "Плохое настроение";
            }
            else
            {
                ctx.CurrentBehavior = NPCBehavior.Working;
                ctx.OverrideReason = "";
            }

            if (isMorning && ctx.Stamina > 70 && ctx.Satiety > 70 && ctx.CurrentBehavior != NPCBehavior.Eating && ctx.CurrentBehavior != NPCBehavior.Resting)
            {
                ctx.CurrentBehavior = NPCBehavior.Working;
                ctx.OverrideReason = "Хорошее утро, начинаем работать";
            }
            else if (isEvening && ctx.Stamina < 60 && ctx.CurrentBehavior != NPCBehavior.Resting)
            {
                ctx.CurrentBehavior = NPCBehavior.Resting;
                ctx.OverrideReason = "Вечер, нужно отдохнуть";
            }
            else if (isNight && ctx.CurrentBehavior != NPCBehavior.Resting)
            {
                ctx.CurrentBehavior = NPCBehavior.Resting;
                ctx.OverrideReason = "Поздно, время спать";
            }
        }
    
        public static bool NeedsResourcesForWork(NpcContext ctx)
        {
            return ctx.Needs.Any(need =>
            {
                if (ConfigManager.IsFood(need.Key))
                    return false;

                int have = ctx.Inventory.ContainsKey(need.Key) ? ctx.Inventory[need.Key] : 0;
                return have < need.Value;
            });
        }

        // В GetOrCreateContext используем конфиг по NPC (например, Клинт)
        public static NpcContext GetOrCreateContext(NPC npc, Farmer player)
        {
            if (_contexts.TryGetValue(npc.Name, out var ctx))
            {
                RefreshDayState(ctx);
                int points = player.friendshipData.TryGetValue(npc.Name, out var friendship) ? friendship.Points : 0;
                ctx.FriendshipPoints = points;
                ctx.FriendshipLevel = GetFriendshipLevel(points);
                ctx.Season = Game1.currentSeason;
                ctx.TimeOfDay = Game1.timeOfDay;
                ctx.LocationName = npc.currentLocation?.Name ?? "Unknown";
                return ctx;
            }

            int initialPoints = player.friendshipData.TryGetValue(npc.Name, out var f) ? f.Points : 0;

            var newCtx = new NpcContext
            {
                Name = npc.Name,
                FriendshipPoints = initialPoints,
                FriendshipLevel = GetFriendshipLevel(initialPoints),
                Season = Game1.currentSeason,
                TimeOfDay = Game1.timeOfDay,
                LocationName = npc.currentLocation?.Name ?? "Unknown",
                DialogueStageDay = (int)Game1.stats.DaysPlayed
            };

            // Попробуем применить конфиг NPC
            var def = ConfigManager.GetNpcDefinition(npc.Name);
            if (def != null)
            {
                newCtx.Lore = def.Lore ?? "";
                foreach (var kv in def.Needs) newCtx.Needs[kv.Key] = kv.Value;
                foreach (var kv in def.Inventory) newCtx.Inventory[kv.Key] = kv.Value;
                foreach (var kv in def.HiddenInventory) newCtx.HiddenInventory[kv.Key] = kv.Value;
                foreach (var kv in def.CraftingQueue) newCtx.CraftingQueue[kv.Key] = kv.Value;

                newCtx.WorkStaminaCost = def.WorkStaminaCost;
                newCtx.RestRecoveryRate = def.RestRecoveryRate;
                newCtx.HungerRate = def.HungerRate;
                newCtx.MoodImpact = def.MoodImpact;
                newCtx.MoodRecoveryRate = def.MoodRecoveryRate;
                newCtx.MoodDrainRate = def.MoodDrainRate;
                newCtx.SocialEnergy = def.SocialEnergy;
                newCtx.SocialEnergyDrain = def.SocialEnergyDrain;
                newCtx.IsAvailableForInteraction = def.IsAvailableForInteraction;
            }
            else if (npc.Name.Equals("Clint", StringComparison.OrdinalIgnoreCase))
            {
                // Фоллбек — минимальный хардкод для совместимости
                newCtx.Lore = "Кузнец по имени Клинт...";
                newCtx.Needs["Iron Bar"] = 3;
                newCtx.Needs["Coal"] = 2;
                newCtx.Satiety = 80f;
                newCtx.Stamina = 90f;
                newCtx.CurrentMood = Mood.Content;
                newCtx.Inventory["Iron Bar"] = 2;
                newCtx.HiddenInventory["Cheese"] = 2;
                newCtx.WorkStaminaCost = 6;
                newCtx.RestRecoveryRate = 15;
            }

            _contexts[npc.Name] = newCtx;
            return newCtx;
        }

        // InvalidateContext, RefreshDayState, Save/Load
        public static void InvalidateContext(string npcName) => _contexts.Remove(npcName);
        public static void InvalidateAll() => _contexts.Clear();

        private static void RefreshDayState(NpcContext ctx)
        {
            int currentDay = (int)Game1.stats.DaysPlayed;
            if (ctx.DialogueStageDay == currentDay) return;

            if (ctx.DialogueStageDay < currentDay && ctx.ActiveQuest != null)
            {
                Monitor?.Log($"{ctx.Name} has given up on the previous quest request (expired).", LogLevel.Debug);
                ctx.ActiveQuest = null;
            }

            ctx.DialogueStageDay = currentDay;
            ctx.DialogueStage = DialogueStage.Greeting;
            ctx.ActiveQuest = null;
            ctx.LastCompletedQuest = null;
            ctx.CachedAction = null;
            ctx.CachedActionCode = null;
            ctx.CachedActionDuration = 0;
            ctx.CachedActionDay = -1;
            ctx.CachedActionTime = -1;
            ctx.HasGreetedPlayer = false;
            ctx.ConsecutiveInteractionCount = 0;
            ctx.SharedStories.Clear();
            ctx.GivenTips.Clear();
            ctx.SharedRumors.Clear();
            ctx.CompletedQuestsToday.Clear();
            ctx.RecentDialogues.Clear(); // Clear recent dialogues at the start of each new day
            ctx.DiscussedTopics.Clear();
            ctx.TopicDiscussionCount.Clear();
            ctx.RecentlyMentionedItems.Clear();
            ctx.DailySatisfiedNeeds.Clear();
        }
    }
}