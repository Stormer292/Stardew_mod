using StardewValley;
using StardewModdingAPI;
using System.Collections.Generic;
using System.Linq;
using System;

namespace LLMBrainMod
{
    /// <summary>
    /// Отслеживание квестов НПС, для предотвращения повторений
    /// </summary>
    public static class QuestTrackingModule
    {
        private static IMonitor _monitor;
        private static Dictionary<string, List<CompletedQuestInfo>> _completedQuests = new Dictionary<string, List<CompletedQuestInfo>>();
        
        public static void SetMonitor(IMonitor monitor)
        {
            _monitor = monitor;
        }
        
        /// <summary>
        /// Записываем выполненный квест
        /// </summary>
        public static void RecordCompletedQuest(string npcName, LLMAction quest)
        {
            if (string.IsNullOrEmpty(npcName) || quest == null)
                return;
                
            if (!_completedQuests.ContainsKey(npcName))
                _completedQuests[npcName] = new List<CompletedQuestInfo>();
            
            // Проверяем квест на наличие в том же дне
            var today = (int)Game1.stats.DaysPlayed;
            var existingQuest = _completedQuests[npcName].FirstOrDefault(q =>
                q.ItemName.Equals(quest.Item, StringComparison.OrdinalIgnoreCase) &&
                q.Amount == quest.Amount &&
                q.QuestType.Equals(quest.Action, StringComparison.OrdinalIgnoreCase) &&
                q.DayCompleted == today);
                
            if (existingQuest == null)
            {
                var questInfo = new CompletedQuestInfo
                {
                    ItemName = quest.Item,
                    Amount = quest.Amount,
                    Reward = quest.Reward,
                    DayCompleted = today,
                    QuestType = quest.Action
                };
                
                _completedQuests[npcName].Add(questInfo);
                _monitor?.Log($"Записан завершенный квест для {npcName}: {quest.Amount}x {quest.Item}", LogLevel.Debug);
            }
        }
        
       
        public static bool HasRecentlyCompletedQuest(string npcName, string itemName, int amount, int maxDays = 3)
        {
            if (string.IsNullOrEmpty(npcName) || string.IsNullOrEmpty(itemName))
                return false;
                
            if (!_completedQuests.ContainsKey(npcName))
                return false;
                
            var today = (int)Game1.stats.DaysPlayed;
            var recentQuests = _completedQuests[npcName].Where(q =>
                q.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase) &&
                q.Amount >= amount &&  // Проверяем, что выполненный квест был на такое же или большее количество
                (today - q.DayCompleted) <= maxDays);
                
            return recentQuests.Any();
        }
        
        /// <summary>
        /// Проверка на правильность предмета в квесте
        /// </summary>
        public static bool HasCompletedQuestToday(string npcName, string itemName, int amount)
        {
            if (string.IsNullOrEmpty(npcName) || string.IsNullOrEmpty(itemName))
                return false;
                
            if (!_completedQuests.ContainsKey(npcName))
                return false;
                
            var today = (int)Game1.stats.DaysPlayed;
            var todayQuests = _completedQuests[npcName].Where(q =>
                q.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase) &&
                q.Amount >= amount &&  // Проверяем, что выполненный квест был на такое же или большее количество
                q.DayCompleted == today);
                
            return todayQuests.Any();
        }
        
        /// <summary>
        /// Проверяет, можно ли предложить квест на основе истории завершения
        /// </summary>
        public static bool IsQuestEligible(NpcContext ctx, string itemName, int amount)
        {
            // Не предлагать квест, если NPC уже выполнил один и тот же квест сегодня
            if (HasCompletedQuestToday(ctx.Name, itemName, amount))
            {
                _monitor?.Log($"{ctx.Name} already completed quest for {amount}x {itemName} today", LogLevel.Debug);
                return false;
            }
            
            // Не предлагать квест, если NPC недавно выполнил один и тот же квест
            if (HasRecentlyCompletedQuest(ctx.Name, itemName, amount))
            {
                _monitor?.Log($"{ctx.Name} recently completed quest for {amount}x {itemName}", LogLevel.Debug);
                return false;
            }
            
            // Не предлагать квест, если у NPC уже достаточно этого предмета
            int currentAmount = ctx.Inventory.ContainsKey(itemName) ? ctx.Inventory[itemName] : 0;
            if (currentAmount >= amount)
            {
                _monitor?.Log($"{ctx.Name} already has enough {itemName} ({currentAmount} >= {amount})", LogLevel.Debug);
                return false;
            }
            
            // Не предлагать квест, если потребность NPC в этом предмете уже была удовлетворена сегодня
            if (ctx.DailySatisfiedNeeds != null && ctx.DailySatisfiedNeeds.ContainsKey(itemName))
            {
                int satisfiedAmount = ctx.DailySatisfiedNeeds[itemName];
                if (satisfiedAmount >= amount)
                {
                    _monitor?.Log($"{ctx.Name}'s need for {amount}x {itemName} was already satisfied today ({satisfiedAmount}x satisfied)", LogLevel.Debug);
                    return false;
                }
            }
            
            // Не предлагать никакой квест, если NPC уже выполнил квест сегодня
            if (ctx.CompletedQuestsToday != null && ctx.CompletedQuestsToday.Count > 0)
            {
                _monitor?.Log($"{ctx.Name} already completed a quest today, won't offer another one", LogLevel.Debug);
                return false;
            }
            
            // Дополнительная проверка: предлагать квесты только для предметов, которые действительно нужны NPC
            // Это предотвращает предложение квестов для предметов, которые NPC на самом деле не нуждаются
            if (ctx.Needs.ContainsKey(itemName))
            {
                // Проверяем, все еще ли нуждается NPC в этом предмете (с учетом текущего инвентаря)
                int neededAmount = ctx.Needs[itemName];
                int alreadyHave = ctx.Inventory.ContainsKey(itemName) ? ctx.Inventory[itemName] : 0;
                int stillNeeded = Math.Max(0, neededAmount - alreadyHave);
                
                if (amount > stillNeeded)
                {
                    _monitor?.Log($"{ctx.Name} doesn't need {amount}x {itemName}, only needs {stillNeeded} more", LogLevel.Debug);
                    return false;
                }
            }
            // Если предмета нет в потребностях NPC, разрешаем только если это для целей крафта
            else
            {
                // Пока мы разрешаем квесты для предметов, которых нет в потребностях, но в полной реализации
                // вы можете захотеть ограничить это дальше
                _monitor?.Log($"{ctx.Name} doesn't have {itemName} in their needs, but allowing quest", LogLevel.Debug);
            }
            
            return true;
        }
        
        /// <summary>
        /// Получает все предметы, для которых NPC недавно выполнил квесты
        /// </summary>
        public static HashSet<string> GetRecentlyCompletedItems(string npcName, int maxDays = 3)
        {
            if (string.IsNullOrEmpty(npcName) || !_completedQuests.ContainsKey(npcName))
                return new HashSet<string>();
                
            var today = (int)Game1.stats.DaysPlayed;
            var recentItems = new HashSet<string>();
            
            foreach (var quest in _completedQuests[npcName])
            {
                if ((today - quest.DayCompleted) <= maxDays)
                {
                    recentItems.Add(quest.ItemName);
                }
            }
            
            return recentItems;
        }
        
        /// <summary>
        /// Обновляет потребности NPC на основе выполненных квестов, чтобы предотвратить повторение
        /// </summary>
        public static void UpdateNeedsAfterCompletion(NpcContext ctx, string completedItemName, int completedAmount)
        {
            // Уменьшаем потребность NPC в этом предмете на основе выполненного квеста
            if (ctx.Needs.ContainsKey(completedItemName))
            {
                int currentNeed = ctx.Needs[completedItemName];
                int newNeed = Math.Max(0, currentNeed - completedAmount);
                
                if (newNeed == 0)
                {
                    ctx.Needs.Remove(completedItemName);
                }
                else
                {
                    ctx.Needs[completedItemName] = newNeed;
                }
                
                _monitor?.Log($"Обновлена потребность {ctx.Name} в {completedItemName}: была {currentNeed}, стала {newNeed}", LogLevel.Debug);
            }
            
            // Также обновляем DailySatisfiedNeeds, чтобы отслеживать предметы, которые были удовлетворены сегодня
            if (ctx.DailySatisfiedNeeds == null)
                ctx.DailySatisfiedNeeds = new Dictionary<string, int>();
                
            if (ctx.DailySatisfiedNeeds.ContainsKey(completedItemName))
                ctx.DailySatisfiedNeeds[completedItemName] += completedAmount;
            else
                ctx.DailySatisfiedNeeds[completedItemName] = completedAmount;
        }
        
        /// <summary>
        /// Очищает старые записи квестов, которые больше не актуальны
        /// </summary>
        public static void CleanupOldRecords(int maxDays = 7)
        {
            var today = (int)Game1.stats.DaysPlayed;
            
            foreach (var npcRecord in _completedQuests)
            {
                var npcName = npcRecord.Key;
                var quests = npcRecord.Value;
                
                // Удаляем квесты старше maxDays
                var questsToRemove = quests.Where(q => (today - q.DayCompleted) > maxDays).ToList();
                
                foreach (var quest in questsToRemove)
                {
                    quests.Remove(quest);
                }
            }
        }
        
        /// <summary>
        /// Сохраняет данные отслеживания квестов
        /// </summary>
        public static void Save(IModHelper helper)
        {
            var data = new Dictionary<string, List<Dictionary<string, object>>>();
            
            foreach (var npcRecord in _completedQuests)
            {
                var npcName = npcRecord.Key;
                var quests = npcRecord.Value;
                
                var questList = new List<Dictionary<string, object>>();
                foreach (var quest in quests)
                {
                    questList.Add(new Dictionary<string, object>
                    {
                        ["ItemName"] = quest.ItemName,
                        ["Amount"] = quest.Amount,
                        ["Reward"] = quest.Reward,
                        ["DayCompleted"] = quest.DayCompleted,
                        ["QuestType"] = quest.QuestType
                    });
                }
                
                data[npcRecord.Key] = questList;
            }
            
            helper.Data.WriteJsonFile("quest-tracking.json", data);
        }
        
        /// <summary>
        /// Загружает данные отслеживания квестов
        /// </summary>
        public static void Load(IModHelper helper)
        {
            try
            {
                var data = helper.Data.ReadJsonFile<Dictionary<string, List<Dictionary<string, object>>>>("quest-tracking.json") ?? new Dictionary<string, List<Dictionary<string, object>>>();
                
                _completedQuests.Clear();
                
                foreach (var npcRecord in data)
                {
                    var npcName = npcRecord.Key;
                    var quests = npcRecord.Value;
                    
                    var questList = new List<CompletedQuestInfo>();
                    foreach (var quest in quests)
                    {
                        questList.Add(new CompletedQuestInfo
                        {
                            ItemName = quest["ItemName"].ToString(),
                            Amount = Convert.ToInt32(quest["Amount"]),
                            Reward = Convert.ToInt32(quest["Reward"]),
                            DayCompleted = Convert.ToInt32(quest["DayCompleted"]),
                            QuestType = quest["QuestType"].ToString()
                        });
                    }
                    
                    _completedQuests[npcRecord.Key] = questList;
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"Ошибка загрузки данных отслеживания квестов: {ex.Message}", LogLevel.Warn);
            }
        }
    }
    
    /// <summary>
    /// Информация о выполненном квесте
    /// </summary>
    public class CompletedQuestInfo
    {
        public string ItemName { get; set; }
        public int Amount { get; set; }
        public int Reward { get; set; }
        public int DayCompleted { get; set; }
        public string QuestType { get; set; }
    }
}