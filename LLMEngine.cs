using StardewModdingAPI;
using StardewValley;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewValley.Menus;                                                      
using System.IO;

namespace LLMBrainMod
{
    public class LLMAction
    {
        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("topic")]
        public string Topic { get; set; }

        [JsonProperty("mood")]
        public string Mood { get; set; }

        [JsonProperty("item")]
        public string Item { get; set; }

        [JsonIgnore]
        public int ItemId { get; set; } = -1;

        [JsonProperty("amount")]
        public int Amount { get; set; } = 1;

        [JsonProperty("reward")]
        public int Reward { get; set; } = 100;

        [JsonIgnore]
        public bool IsReplay { get; set; }
    }

    public static class LLMEngine
    {
        public sealed class ActionHandler
        {
            public string Code { get; init; }
            public string PromptDescription { get; init; }
            public string JsonExample { get; init; }
            public Func<LLMAction, bool> Validator { get; set; }
            public Action<LLMAction, NPC, Farmer> Executor { get; set; }
            public int CacheDuration { get; init; } = 200;
            public bool CacheResponse { get; init; } = false;
        }

        // Загружаемые коллекции (инициализируются в статическом конструкторе)
        private static List<ActionHandler> ActionBlueprints;
        private static readonly Dictionary<string, ActionHandler> ActionHandlers = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, int> PredefinedQuestItems = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> QuestItemWhitelist = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> ItemNameToId = new(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> KnownItemNames;

        public static IMonitor Monitor;
        private static readonly HttpClient client = new HttpClient();
        private const string OLLAMA_URL = "http://localhost:11434/api/generate";
        private static readonly string[] SupportedNpcNames = { "Clint" };

        public static bool IsProcessing { get; set; } = false;

        static LLMEngine()
        {
            // Попытка загрузки конфигов — если ConfigManager уже загружен, он обеспечит foods/npcs/behavior
            // Загружаем action handlers и quest items из файлов в Configs
            ActionBlueprints = LoadActionHandlersFromConfig();
            ActionHandlers.Clear();
            foreach (var handler in ActionBlueprints)
                ActionHandlers[handler.Code] = handler;

            PredefinedQuestItems = LoadQuestItemsFromConfig();
            QuestItemWhitelist.Clear();
            foreach (var item in PredefinedQuestItems.Keys) QuestItemWhitelist.Add(item);

            InitializeItemCache();
        }

        public static void Initialize(IMonitor monitor)
        {
            Monitor = monitor;
            Monitor?.Log("Инициализация LLMEngine.", LogLevel.Info);
        }

        public static void Shutdown()
        {
            Monitor?.Log("LLMEngine остановлен.", LogLevel.Info);
        }

        private static bool MatchesItem(Item item, int itemId)
        {
            if (item == null)
                return false;
            // Сравниваем по ParentSheetIndex 
            return item.ParentSheetIndex == itemId;
        }

        public static bool IsSupportedNpc(NPC npc)
        {
            return npc?.Name != null && SupportedNpcNames.Any(n => n.Equals(npc.Name, StringComparison.OrdinalIgnoreCase));
        }

        public static bool TryCompleteQuest(NpcContext ctx, Farmer who)
        {
            if (ctx.ActiveQuest == null) return false;

            string questItemName = ctx.ActiveQuest.Item;
            int questItemId = GetItemID(questItemName);
            int needed = ctx.ActiveQuest.Amount;

            int totalAvailable = 0;
            List<int> itemSlots = new List<int>();

            for (int i = 0; i < who.Items.Count; i++)
            {
                Item item = who.Items[i];
                if (item != null && MatchesItem(item, questItemId))
                {
                    totalAvailable += item.Stack;
                    itemSlots.Add(i);
                    if (totalAvailable >= needed) break;
                }
            }

            if (totalAvailable >= needed)
            {
                int remainingToTake = needed;
                foreach (int slotIndex in itemSlots)
                {
                    Item item = who.Items[slotIndex];
                    if (item != null)
                    {
                        int takeAmount = Math.Min(item.Stack, remainingToTake);
                        item.Stack -= takeAmount;
                        remainingToTake -= takeAmount;

                        if (item.Stack <= 0)
                        {
                            who.Items[slotIndex] = null;
                        }

                        if (remainingToTake <= 0) break;
                    }
                }

                int current = ctx.Inventory.TryGetValue(questItemName, out int cur) ? cur : 0;
                ctx.Inventory[questItemName] = current + needed;

                if (ctx.ActiveQuest.Action == "FOOD_QUEST_REQUEST")
                {
                    ctx.Satiety = Math.Min(100f, ctx.Satiety + 40f);
                }
                else
                {
                    ctx.Satiety = Math.Min(100f, ctx.Satiety + 25f);
                }

                ctx.Stamina = Math.Min(100f, ctx.Stamina + 20f);
                ctx.CurrentMood = Mood.Happy;

                if (ctx.Needs.ContainsKey(questItemName))
                {
                    int currentNeed = ctx.Needs[questItemName];
                    int newNeed = Math.Max(0, currentNeed - needed);
                    if (newNeed == 0) ctx.Needs.Remove(questItemName);
                    else ctx.Needs[questItemName] = newNeed;
                }

                ctx.LastCompletedQuest = ctx.ActiveQuest;
                ctx.ActiveQuest = null;
                ctx.DialogueStage = DialogueStage.Greeting;
                ctx.DialogueStageDay = (int)Game1.stats.DaysPlayed;

                who.Money += ctx.LastCompletedQuest.Reward;

                QuestTrackingModule.RecordCompletedQuest(ctx.Name, ctx.LastCompletedQuest);
                QuestTrackingModule.UpdateNeedsAfterCompletion(ctx, ctx.LastCompletedQuest.Item, ctx.LastCompletedQuest.Amount);

                if (ctx.CompletedQuestsToday == null) ctx.CompletedQuestsToday = new List<string>();
                ctx.CompletedQuestsToday.Add($"{ctx.LastCompletedQuest.Item}_{ctx.LastCompletedQuest.Amount}");

                Monitor.Log($"Квест завершен для {ctx.Name}: {needed}x {questItemName} (ID: {questItemId})", LogLevel.Info);
                return true;
            }

            return false;
        }

        public static async Task<string> GetLLMResponseAsync(NPC npc, Farmer who)
        {
            try
            {
                var ctx = ContextManager.GetOrCreateContext(npc, who);

                if (TryCompleteQuest(ctx, who))
                {
                    return "";
                }

                var allowedHandlers = GetAllowedHandlers(ctx);
                if (allowedHandlers.Count == 0)
                    throw new InvalidOperationException("Нет доступных действий для текущей стадии диалога.");

                string prompt = BuildPrompt(ctx, who, allowedHandlers);

                var requestBody = new
                {
                    model = "phi3:mini",
                    prompt = prompt,
                    stream = false,
                    options = new
                    {
                        temperature = 0.7f,
                        num_predict = 256
                    }
                };

                var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(OLLAMA_URL, content);
                response.EnsureSuccessStatusCode();

                var jsonResponse = await response.Content.ReadAsStringAsync();
                dynamic result = JsonConvert.DeserializeObject(jsonResponse);
                return result.response;
            }
            catch (Exception ex)
            {
                Monitor.Log($"ОШИБКА OLLAMA: {ex.Message}.", LogLevel.Error);
                return "[ОШИБКА: Ollama не отвечает. Проверьте консоль Ollama.]";
            }
        }

        private static string BuildPrompt(NpcContext ctx, Farmer who, IReadOnlyList<ActionHandler> allowedHandlers)
        {
            // Получаем шаблон из конфига
            string template = ConfigManager.GetPrompt("main_prompt");
            if (string.IsNullOrWhiteSpace(template))
            {
                // Фоллбек (минимальный) — если шаблон не найден в JSON
                template = "<|system|>\nYou are NPC '{NPC_NAME}'. Background: {LORE}\n\n**STATE**:\n- Time: {TIME} ({TIME_DESC})\n- Mood: {MOOD}\n- Satiety: {SATIETY}%\n- Stamina: {STAMINA}%\n- Inventory: {INVENTORY}\n- Hidden: {HIDDEN_STORAGE}\n- Needs: {NEEDS}\n- Crafting: {CRAFTING}\n\nChoose ONE action from the list below and output only a single JSON object.\n{HANDLERS}\n<|end|>\n<|user|>Player {PLAYER_NAME} approaches you. {PLAYER_NAME}.\n<|end|>";
            }

            // Сформировать блок handlers
            var sbHandlers = new StringBuilder();
            var sortedHandlers = allowedHandlers.OrderByDescending(h => GetActionPriority(h.Code, ctx));
            foreach (var h in sortedHandlers)
            {
                sbHandlers.AppendLine($"- {h.Code}: {h.PromptDescription}");
                if (!string.IsNullOrWhiteSpace(h.JsonExample))
                    sbHandlers.AppendLine($"  Example: {h.JsonExample}");
            }
            string handlersText = sbHandlers.ToString().TrimEnd();

            // Инвентарь и скрытое хранилище
            string inventoryText = ctx.Inventory != null && ctx.Inventory.Any()
                ? string.Join(", ", ctx.Inventory.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}x{kv.Value}"))
                : "None";
            string hiddenText = ctx.HiddenInventory != null && ctx.HiddenInventory.Any(kv => kv.Value > 0)
                ? string.Join(", ", ctx.HiddenInventory.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}x{kv.Value}"))
                : "None";

            string needsText = ctx.Needs != null && ctx.Needs.Any()
                ? string.Join(", ", ctx.Needs.Select(n => $"{n.Key}x{n.Value}"))
                : "None";
            string craftingText = ctx.CraftingQueue != null && ctx.CraftingQueue.Any()
                ? string.Join(", ", ctx.CraftingQueue.Select(c => $"{c.Key}x{c.Value}"))
                : "None";

            string scheduleDesc = GetScheduleDescription(ctx);
            string timeDesc = GetTimeDescription(ctx.TimeOfDay);

            string activeQuest = ctx.ActiveQuest != null
                ? $"waiting for {ctx.ActiveQuest.Amount}x {ctx.ActiveQuest.Item} (reward {ctx.ActiveQuest.Reward}g)"
                : "None";
            string lastCompleted = ctx.LastCompletedQuest != null
                ? $"{ctx.LastCompletedQuest.Amount}x {ctx.LastCompletedQuest.Item}"
                : "None";

            // Diversity list и строгие правила хранятся в конфигах, но даём разумный дефолт, если нет
            string diversityList = ConfigManager.GetPrompt("diversity_list") ?? "Iron Bar, Copper Bar, Gold Bar, Coal, Wood, Hardwood, Stone, Clay, Fiber, Maple Syrup, Oak Resin, Pine Tar, Battery Pack, Cloth, Wool, Cheese, Goat Cheese, Honey, Jelly, Pickles, Copper Ore, Iron Ore, Gold Ore";
            string strictRules = ConfigManager.GetPrompt("strict_rules") ?? "- Output ONLY valid JSON. No prose before/after JSON.\n- Items must match whitelist.\n- amount 1-20; reward 10-1000.";

            // Подстановка значений в шаблон
            string result = template
                .Replace("{NPC_NAME}", ctx.Name ?? "NPC")
                .Replace("{LORE}", ctx.Lore ?? "")
                .Replace("{SCHEDULE_DESC}", scheduleDesc ?? "")
                .Replace("{TIME}", ctx.TimeOfDay.ToString())
                .Replace("{TIME_DESC}", timeDesc)
                .Replace("{MOOD}", ctx.CurrentMood.ToString())
                .Replace("{SATIETY}", $"{ctx.Satiety:F0}")
                .Replace("{STAMINA}", $"{ctx.Stamina:F0}")
                .Replace("{INVENTORY}", inventoryText)
                .Replace("{HIDDEN_STORAGE}", hiddenText)
                .Replace("{NEEDS}", needsText)
                .Replace("{CRAFTING}", craftingText)
                .Replace("{DIALOGUE_STAGE}", ctx.DialogueStage.ToString())
                .Replace("{HANDLERS}", handlersText)
                .Replace("{PLAYER_NAME}", who?.Name ?? "Player")
                .Replace("{FRIENDSHIP}", ctx.FriendshipLevel.ToString())
                .Replace("{FRIENDSHIP_POINTS}", ctx.FriendshipPoints.ToString())
                .Replace("{SEASON}", ctx.Season ?? "")
                .Replace("{LOCATION}", ctx.LocationName ?? "")
                .Replace("{ACTIVE_QUEST}", activeQuest)
                .Replace("{LAST_COMPLETED_QUEST}", lastCompleted)
                .Replace("{DIVERSITY_LIST}", diversityList)
                .Replace("{STRICT_RULES}", strictRules);

            return result;
        }

        private static int GetActionPriority(string actionCode, NpcContext ctx)
        {
            // Приоритеты действий в зависимости от состояния NPC
            switch (actionCode)
            {
                // Высокий приоритет при низкой сытости
                case "HUNGRY_EAT_FROM_INV":
                case "HUNGRY_REQUEST_ITEM":
                case "FOOD_QUEST_REQUEST":
                    if (ctx.Satiety < 20) return 100;
                    if (ctx.Satiety < 30) return 80;
                    return 20;

                // Высокий приоритет при низкой выносливости
                case "TIRED_REST_ACTION":
                    if (ctx.Stamina < 20) return 95;
                    if (ctx.Stamina < 30) return 75;
                    return 25;

                // Средний приоритет для квестов
                case "START_FETCH_QUEST":
                case "CRAFT_QUEST":
                    return 55;

                // Средний приоритет для общения
                case "TELL_STORY":
                case "GIVE_TIP":
                case "SHARE_RUMOR":
                    return 40;

                // Низкий приоритет для приветствия и отказа
                case "GREET_PLAYER":
                    return 10;

                case "REFUSAL":
                    return 15;

                // Прочие действия - средний приоритет
                default:
                    return 30;
            }
        }

        private static string GetTimeDescription(int timeOfDay)
        {
            if (timeOfDay < 600) return "very early morning";
            if (timeOfDay < 1200) return "morning";
            if (timeOfDay < 1800) return "afternoon";
            if (timeOfDay < 2200) return "evening";
            return "night";
        }

        private static string GetScheduleDescription(NpcContext ctx)
        {
            if (ctx.Name.Equals("Clint", StringComparison.OrdinalIgnoreCase))
            {
                return "6:00 AM - Start work at the blacksmith shop, 12:00 PM - Lunch break, 2:00 PM - Continue work, 6:00 PM - Rest, 8:00 PM - Shop for materials, 10:00 PM - Sleep";
            }
            return "Typical villager schedule: Work during day, rest in evening, sleep at night";
        }

        private static string ExtractJson(string input)
        {
            int braceCount = 0;
            int start = -1;
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '{')
                {
                    if (braceCount == 0) start = i;
                    braceCount++;
                }
                else if (input[i] == '}')
                {
                    braceCount--;
                    if (braceCount == 0 && start != -1)
                    {
                        return input.Substring(start, i - start + 1);
                    }
                }
            }
            return null;
        }

        public static bool TryValidateAction(LLMAction action, out ActionHandler handler)
        {
            handler = null;
            if (action?.Action == null)
            {
                return false;
            }

            if (!ActionHandlers.TryGetValue(action.Action, out handler))
            {
                return false;
            }

            // Дополнительная проверка: если это квест на предмет, убедиться, что предмет в белом списке
            if (action.Action == "START_FETCH_QUEST" || action.Action == "CRAFT_QUEST")
            {
                if (!string.IsNullOrWhiteSpace(action.Item) && !QuestItemWhitelist.Contains(action.Item))
                {
                    Monitor.Log($"Предупреждение: LLM попытался создать квест на недопустимый предмет '{action.Item}'", LogLevel.Warn);
                    return false;
                }
            }

            return handler.Validator?.Invoke(action) ?? true;
        }

        private static IReadOnlyList<ActionHandler> GetAllowedHandlers(NpcContext ctx)
        {
            var list = new List<ActionHandler>();

            void Add(string code)
            {
                if (ActionHandlers.TryGetValue(code, out var handler))
                {
                    // Проверяем, не выполнялось ли это действие недавно в текущей сессии
                    if (ShouldSkipAction(ctx, code))
                    {
                        Monitor.Log($"[DEBUG] Действие {code} пропущено - уже выполнялось недавно", LogLevel.Debug);
                        return;
                    }
                    list.Add(handler);
                    Monitor.Log($"[DEBUG] Добавлено действие: {code}", LogLevel.Debug);
                }
            }

            void AddQuest(string code, string itemName, int amount)
            {
                // Проверяем, подходит ли этот квест для предложения на основе истории завершения
                if (QuestTrackingModule.IsQuestEligible(ctx, itemName, amount))
                {
                    if (ActionHandlers.TryGetValue(code, out var handler))
                    {

                        string questKey = $"{code}_{itemName}_{amount}";
                        if (ShouldSkipAction(ctx, questKey))
                        {
                            Monitor.Log($"[DEBUG] Квест {questKey} пропущен - уже предлагался недавно", LogLevel.Debug);
                            return;
                        }
                        list.Add(handler);
                        Monitor.Log($"[DEBUG] Добавлен квест: {code} для {itemName} x{amount}", LogLevel.Debug);
                    }
                }
                else
                {
                    Monitor.Log($"[DEBUG] Квест {code} для {itemName} x{amount} не подходит - уже был недавно", LogLevel.Debug);
                }
            }

            Monitor.Log($"[DEBUG] Текущая стадия диалога: {ctx.DialogueStage}", LogLevel.Debug);
            Monitor.Log($"[DEBUG] Сытость: {ctx.Satiety}, Выносливость: {ctx.Stamina}, Настроение: {ctx.CurrentMood}", LogLevel.Debug);
            Monitor.Log($"[DEBUG] Скрытое хранилище: {string.Join(", ", ctx.HiddenInventory.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}x{kv.Value}"))}", LogLevel.Debug);

            // Основной подход: приоритет действий на основе текущего состояния NPC
            // Голод имеет высокий приоритет
            if (ctx.Satiety < 20)
            {
                Monitor.Log($"[DEBUG] Сытость < 20, добавляем HUNGRY_EAT_FROM_INV", LogLevel.Debug);
                Add("HUNGRY_EAT_FROM_INV");
            }

            // Если голоден и нет еды, предлагает квест на еду
            if (ctx.Satiety < 30 && ctx.HiddenInventory.All(kv => !IsFoodItem(kv.Key) || kv.Value <= 0))
            {
                Monitor.Log($"[DEBUG] Голоден и нет еды, добавляем FOOD_QUEST_REQUEST", LogLevel.Debug);
                Add("FOOD_QUEST_REQUEST");
            }
            // Только если не добавили квест на еду, добавляем простой запрос
            else if (ctx.Satiety < 30)
            {
                Monitor.Log($"[DEBUG] Голоден, добавляем HUNGRY_REQUEST_ITEM", LogLevel.Debug);
                Add("HUNGRY_REQUEST_ITEM");
            }
            // Проверка, было ли уже совершено приветствие в этой сессии.
            if (!ctx.HasGreetedPlayer)
            {
                Add("GREET_PLAYER");
            }
            Add("TELL_STORY");
            Add("GIVE_TIP");
            Add("SHARE_RUMOR");
            Add("REFUSAL");


            // Добавляем квесты и потребности, если не было ограничения
            if (ctx.DialogueStage == DialogueStage.QuestOffer || ctx.DialogueStage == DialogueStage.Greeting)
            {
                // Check for potential START_FETCH_QUEST
                foreach (var need in ctx.Needs)
                {
                    if (!IsFoodItem(need.Key)) // При выполнении заданий по поиску предметов учитываем только несъедобные предметы.
                    {
                        AddQuest("START_FETCH_QUEST", need.Key, need.Value);
                    }
                }

                // Проверка на возможность CRAFT_QUEST
                foreach (var craft in ctx.CraftingQueue)
                {
                    AddQuest("CRAFT_QUEST", craft.Key, craft.Value);
                }
            }


            // Усталость - средний приоритет
            if (ctx.Stamina < 30)
            {
                Monitor.Log($"[DEBUG] Выносливость < 30, добавляем TIRED_REST_ACTION и TIRED_CONTINUE_WORKING", LogLevel.Debug);
                Add("TIRED_REST_ACTION");
                Add("TIRED_CONTINUE_WORKING");
            }

            // Настроение - влияет на социальные действия
            if (ctx.CurrentMood == Mood.Sad || ctx.CurrentMood == Mood.Angry)
            {
                Monitor.Log($"[DEBUG] Плохое настроение, добавляем LOW_MOOD_SEEK_COMPANY", LogLevel.Debug);
                Add("LOW_MOOD_SEEK_COMPANY");
            }

            if (ctx.CurrentMood == Mood.Happy || ctx.CurrentMood == Mood.Excited)
            {
                Monitor.Log($"[DEBUG] Хорошее настроение, добавляем HAPPY_SHARE_ENERGY", LogLevel.Debug);
                Add("HAPPY_SHARE_ENERGY");
            }

            // Если есть активный квест, добавляем благодарность
            if (ctx.DialogueStage == DialogueStage.QuestThanks || ctx.LastCompletedQuest != null)
            {
                Monitor.Log($"[DEBUG] Есть активный квест или выполненный, добавляем QUEST_THANKS", LogLevel.Debug);
                Add("QUEST_THANKS");
            }




            // Если ждет выполнения квеста
            if (ctx.DialogueStage == DialogueStage.WaitingForQuest && ctx.ActiveQuest != null)
            {
                Monitor.Log($"[DEBUG] Ожидание выполнения квеста, добавляем другие действия", LogLevel.Debug);
                // Все равно можем добавить другие действия, но с акцентом на квест
            }

            // Всегда добавляем хотя бы базовые действия, если список пуст
            if (list.Count == 0)
            {
                Monitor.Log($"[DEBUG] Нет доступных действий, добавляем базовые", LogLevel.Debug);
                // Временно отключаем проверку пропуска для гарантии добавления базовых действий
                // Добавляем базовые действия напрямую в список
                if (ActionHandlers.TryGetValue("REFUSAL", out var refusalHandler))
                    list.Add(refusalHandler);
                if (ActionHandlers.TryGetValue("GREET_PLAYER", out var greetHandler) && !ctx.HasGreetedPlayer)
                    list.Add(greetHandler);
                if (ActionHandlers.TryGetValue("TELL_STORY", out var storyHandler))
                    list.Add(storyHandler);
                if (ActionHandlers.TryGetValue("GIVE_TIP", out var tipHandler))
                    list.Add(tipHandler);
                if (ActionHandlers.TryGetValue("SHARE_RUMOR", out var rumorHandler))
                    list.Add(rumorHandler);
            }
            
            Monitor.Log($"[DEBUG] Всего доступных действий: {list.Count}", LogLevel.Debug);
            return list;
        }

        // Проверка и установка питания/ресурсов — заменяет старые хардкод-методы
        private static bool CheckAndConsumeResource(NpcContext ctx, string resourceName, int amount)
        {
            if (ctx.Inventory.ContainsKey(resourceName) && ctx.Inventory[resourceName] >= amount)
            {
                ctx.Inventory[resourceName] -= amount;
                if (ctx.Inventory[resourceName] <= 0) ctx.Inventory.Remove(resourceName);
                return true;
            }
            return false;
        }


        private static bool ShouldSkipAction(NpcContext ctx, string actionKey)
        {
            // Примерная логика: если действие уже было выполнено сегодня, пропускаем его
            if (ctx == null || string.IsNullOrEmpty(actionKey))
                return false;

            // Проверяем, есть ли в списке выполненных квестов сегодня
            if (ctx.CompletedQuestsToday != null && ctx.CompletedQuestsToday.Any(q => q.Equals(actionKey, StringComparison.OrdinalIgnoreCase)))
                return true;


            return false;
        }
        private static bool CheckAndRecoverSatiety(NpcContext ctx, string foodName)
        {
            if (ConfigManager.IsFood(foodName))
            {
                ctx.Satiety = Math.Min(100f, ctx.Satiety + ConfigManager.GetSatiety(foodName));
                return true;
            }
            return false;
        }

        private static bool CheckAndRecoverStamina(NpcContext ctx)
        {
            ctx.Stamina = Math.Min(100f, ctx.Stamina + 20f);
            return true;
        }

        private static bool UpdateNpcMood(NpcContext ctx, Mood newMood)
        {
            ctx.CurrentMood = newMood;
            return true;
        }

        // Кэширование действий NPC
        public static void CacheAction(NpcContext ctx, LLMAction action, ActionHandler handler, DialogueStage stageForCache)
        {
            if (ctx == null || action == null || handler == null)
            {
                return;
            }

            ctx.CachedAction = CloneAction(action);
            ctx.CachedActionCode = handler.Code;
            ctx.CachedActionDay = (int)Game1.stats.DaysPlayed;
            ctx.CachedActionTime = Game1.timeOfDay;
            ctx.CachedActionDuration = handler.CacheDuration;
            ctx.CachedActionStage = stageForCache;
        }

        private static bool IsCacheValid(NpcContext ctx)
        {
            if (ctx?.CachedAction == null)
            {
                return false;
            }

            if (ctx.CachedActionDay != (int)Game1.stats.DaysPlayed)
            {
                return false;
            }

            if (ctx.CachedActionDuration <= 0)
            {
                return true;
            }

            int elapsed = Game1.timeOfDay - ctx.CachedActionTime;
            return elapsed <= ctx.CachedActionDuration;
        }

        public static void ClearCachedAction(NpcContext ctx)
        {
            if (ctx == null)
            {
                return;
            }

            ctx.CachedAction = null;
            ctx.CachedActionCode = null;
            ctx.CachedActionDuration = 0;
            ctx.CachedActionDay = -1;
            ctx.CachedActionTime = -1;
            ctx.CachedActionStage = DialogueStage.Greeting;
        }

        private static LLMAction CloneAction(LLMAction action)
        {
            if (action == null)
            {
                return null;
            }

            return new LLMAction
            {
                Action = action.Action,
                Text = action.Text,
                Title = action.Title,
                Topic = action.Topic,
                Mood = action.Mood,
                Item = action.Item,
                ItemId = action.ItemId,
                Amount = action.Amount,
                Reward = action.Reward,
                IsReplay = false
            };
        }

        public static LLMAction ParseAction(string jsonBlock)
        {
            var jObject = JObject.Parse(jsonBlock);

            var action = new LLMAction
            {
                Action = jObject.Value<string>("action"),
                Text = jObject.Value<string>("text"),
                Title = jObject.Value<string>("title"),
                Topic = jObject.Value<string>("topic"),
                Mood = jObject.Value<string>("mood"),
                Reward = jObject.Value<int?>("reward") ?? 100,
                Amount = jObject.Value<int?>("amount") ?? 1
            };

            var itemToken = jObject["item"];
            if (itemToken != null)
            {
                if (itemToken.Type == JTokenType.String)
                {
                    action.Item = itemToken.Value<string>();

                    // Проверка и исправление формата, если предмет и количество объединены (например, "Iron Barx3")
                    if (action.Item.Contains("x") && int.TryParse(action.Item.Split('x')[^1], out int parsedAmount))
                    {
                        // Извлекаем название предмета и количество
                        string[] parts = action.Item.Split('x');
                        if (parts.Length >= 2)
                        {
                            // Восстанавливаем название предмета, объединив все части кроме количества
                            string extractedItem = string.Join("x", parts.Take(parts.Length - 1));
                            action.Item = extractedItem;
                            action.Amount = parsedAmount;

                            Monitor?.Log($"[LLM] Исправлен формат предмета: '{extractedItem}' x{parsedAmount}", LogLevel.Trace);
                        }
                    }

                    if (PredefinedQuestItems.TryGetValue(action.Item, out int id))
                    {
                        action.ItemId = id;
                    }
                    // Дополнительная проверка: убедиться, что предмет в белом списке для квестов
                    else if (!string.IsNullOrEmpty(action.Item) &&
                             (action.Action == "START_FETCH_QUEST" || action.Action == "CRAFT_QUEST") &&
                             !QuestItemWhitelist.Contains(action.Item))
                    {
                        Monitor?.Log($"[LLM] Предупреждение: предмет '{action.Item}' не находится в белом списке для квестов", LogLevel.Warn);
                    }
                }
                else if (itemToken.Type == JTokenType.Array)
                {
                    var first = itemToken.First;
                    if (first != null)
                    {
                        action.Item = first.Value<string>();

                        // Проверка и исправление формата, если предмет и количество объединены (например, "Iron Barx3")
                        if (action.Item.Contains("x") && int.TryParse(action.Item.Split('x')[^1], out int parsedAmount))
                        {
                            // Извлекаем название предмета и количество
                            string[] parts = action.Item.Split('x');
                            if (parts.Length >= 2)
                            {
                                // Восстанавливаем название предмета, объединив все части кроме последней (количества)
                                string extractedItem = string.Join("x", parts.Take(parts.Length - 1));
                                action.Item = extractedItem;
                                action.Amount = parsedAmount;

                                Monitor?.Log($"[LLM] Исправлен формат предмета: '{extractedItem}' x{parsedAmount}", LogLevel.Trace);
                            }
                        }

                        if (PredefinedQuestItems.TryGetValue(action.Item, out int id))
                        {
                            action.ItemId = id;
                        }
                        // Дополнительная проверка: убедиться, что предмет в белом списке для квестов
                        else if (!string.IsNullOrEmpty(action.Item) &&
                                 (action.Action == "START_FETCH_QUEST" || action.Action == "CRAFT_QUEST") &&
                                 !QuestItemWhitelist.Contains(action.Item))
                        {
                            Monitor?.Log($"[LLM] Предупреждение: предмет '{action.Item}' не находится в белом списке для квестов", LogLevel.Warn);
                        }
                        Monitor?.Log($"[LLM] Получен массив предметов, используем первый элемент '{action.Item}'.", LogLevel.Trace);
                    }
                }
            }

            var amountToken = jObject["amount"];
            if (amountToken is JArray amountArray)
            {
                var firstAmount = amountArray.First?.Value<int?>();
                if (firstAmount.HasValue)
                {
                    action.Amount = firstAmount.Value;
                    Monitor?.Log("[LLM] Получен массив количеств, используем первое значение.", LogLevel.Trace);
                }
            }

            var rewardToken = jObject["reward"];
            if (rewardToken is JArray rewardArray)
            {
                var firstReward = rewardArray.First?.Value<int?>();
                if (firstReward.HasValue)
                {
                    action.Reward = firstReward.Value;
                    Monitor?.Log("[LLM] Получен массив наград, используем первое значение.", LogLevel.Trace);
                }
            }

            return action;
        }

        public static bool UpdateDialogueStage(NpcContext ctx, ActionHandler handler, LLMAction actionData)
        {
            // Реализуем базовую логику обновления стадии диалога
            switch (handler.Code)
            {
                case "GREET_PLAYER":
                    if (ctx.DialogueStage == DialogueStage.Greeting)
                        ctx.DialogueStage = DialogueStage.StoryOrTip;
                    return true;
                case "TELL_STORY":
                case "GIVE_TIP":
                case "SHARE_RUMOR":
                    if (ctx.DialogueStage == DialogueStage.StoryOrTip)
                        ctx.DialogueStage = DialogueStage.QuestOffer;
                    return true;
                case "START_FETCH_QUEST":
                case "CRAFT_QUEST":
                case "FOOD_QUEST_REQUEST":
                    if (ctx.DialogueStage == DialogueStage.QuestOffer)
                    {
                        ctx.DialogueStage = DialogueStage.WaitingForQuest;
                        ctx.ActiveQuest = actionData;
                    }
                    return true;
                case "QUEST_THANKS":
                    if (ctx.DialogueStage == DialogueStage.QuestThanks)
                        ctx.DialogueStage = DialogueStage.Greeting;
                    return false; // Не разрешаем повторное воспроизведение благодарности
                default:
                    return true;
            }
        }

        public static void HandleGiftReceived(NPC npc, Farmer who, string giftedItemName, int giftedAmount)
        {
            if (!IsSupportedNpc(npc) || string.IsNullOrWhiteSpace(giftedItemName) || giftedAmount <= 0)
            {
                return;
            }

            var ctx = ContextManager.GetOrCreateContext(npc, who);
            if (ctx.DialogueStage != DialogueStage.WaitingForQuest || ctx.ActiveQuest == null)
            {
                return;
            }

            // Нормализуем имя подаренного предмета для сравнения
            string normalizedGifted = giftedItemName.Trim();

            // Проверяем, является ли это активным предметом квеста
            if (ctx.ActiveQuest != null)
            {
                string normalizedQuest = ctx.ActiveQuest.Item.Trim();

                // Сначала проверяем, совпадают ли имена (без учета регистра)
                bool nameMatches = normalizedGifted.Equals(normalizedQuest, StringComparison.OrdinalIgnoreCase);

                // Если имена не совпадают, пробуем сопоставить по ID предмета, чтобы обработать различия в локализации
                if (!nameMatches)
                {
                    int giftedItemId = GetItemID(normalizedGifted);
                    int questItemId = GetItemID(normalizedQuest);

                    // Сопоставление предметов по ID
                    nameMatches = giftedItemId == questItemId && giftedItemId != -1;


                    if (!nameMatches && giftedItemId != -1 && questItemId != -1)
                    {
                        nameMatches = giftedItemId == questItemId;
                    }
                }

                if (!nameMatches)
                {
                    // Проверяем, является ли подаренный предмет едой - если да, NPC может съесть его даже без активного квеста
                    if (IsFoodItem(normalizedGifted) && ctx.ActiveQuest.Action != "FOOD_QUEST_REQUEST")
                    {

                        int currentSatiety = (int)ctx.Satiety;
                        int foodValue = GetFoodValue(normalizedGifted);
                        ctx.Satiety = Math.Min(100f, ctx.Satiety + foodValue);

                        // Добавляем в инвентарь, чтобы отслеживать использование
                        if (ctx.Inventory.ContainsKey(normalizedGifted))
                            ctx.Inventory[normalizedGifted]++;
                        else
                            ctx.Inventory[normalizedGifted] = 1;

                        Monitor.Log($"{ctx.Name} ate {giftedAmount}x {giftedItemName}, satiety increased from {currentSatiety:F0}% to {ctx.Satiety:F0}%", LogLevel.Debug);
                        return;
                    }
                    else if (IsFoodItem(normalizedGifted) && ctx.ActiveQuest.Action == "FOOD_QUEST_REQUEST")
                    {

                        string questFoodItem = ctx.ActiveQuest.Item.Trim();
                        int questFoodId = GetItemID(questFoodItem);
                        int giftedFoodId = GetItemID(normalizedGifted);


                        if (questFoodId == giftedFoodId && giftedFoodId != -1)
                        {

                        }
                        else
                        {
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
            }
            else
            {
                // Проверка подаренного предмет на то, является ли это едой
                if (IsFoodItem(normalizedGifted))
                {

                    int currentSatiety = (int)ctx.Satiety;
                    int foodValue = GetFoodValue(normalizedGifted);
                    ctx.Satiety = Math.Min(100f, ctx.Satiety + foodValue);


                    if (ctx.Inventory.ContainsKey(normalizedGifted))
                        ctx.Inventory[normalizedGifted]++;
                    else
                        ctx.Inventory[normalizedGifted] = 1;

                    Monitor.Log($"{ctx.Name} ate {giftedAmount}x {giftedItemName}, satiety increased from {currentSatiety:F0}% to {ctx.Satiety:F0}%", LogLevel.Debug);
                    return;
                }
                else
                {
                    return;
                }
            }

            if (giftedAmount < ctx.ActiveQuest.Amount)
            {
                return;
            }

            // Завершение квеста
            int current = ctx.Inventory.TryGetValue(ctx.ActiveQuest.Item, out int cur) ? cur : 0;
            ctx.Inventory[ctx.ActiveQuest.Item] = current + ctx.ActiveQuest.Amount;

            // Обновление параметров НПС
            if (ctx.ActiveQuest.Action == "FOOD_QUEST_REQUEST")
            {
                ctx.Satiety = Math.Min(100f, ctx.Satiety + 40f);
            }
            else
            {
                ctx.Satiety = Math.Min(100f, ctx.Satiety + 25f);
            }

            ctx.Stamina = Math.Min(100f, ctx.Stamina + 20f);
            ctx.CurrentMood = Mood.Happy;

            // Уменьшаем потребности NPC, если предмет квеста соответствует нужному предмету
            string questItemName = ctx.ActiveQuest.Item;
            int neededAmount = ctx.ActiveQuest.Amount;
            if (ctx.Needs.ContainsKey(questItemName))
            {
                int currentNeed = ctx.Needs[questItemName];
                int newNeed = Math.Max(0, currentNeed - neededAmount);
                if (newNeed == 0) ctx.Needs.Remove(questItemName);
                else ctx.Needs[questItemName] = newNeed;
            }

            // Отслеживаем, какие потребности были Satisfied today, чтобы предотвратить повторение одних и тех же квестов
            if (ctx.DailySatisfiedNeeds == null)
                ctx.DailySatisfiedNeeds = new Dictionary<string, int>();

            if (ctx.DailySatisfiedNeeds.ContainsKey(questItemName))
                ctx.DailySatisfiedNeeds[questItemName] += neededAmount;
            else
                ctx.DailySatisfiedNeeds[questItemName] = neededAmount;

            ctx.LastCompletedQuest = ctx.ActiveQuest;
            ctx.ActiveQuest = null;
            ctx.DialogueStage = DialogueStage.Greeting;
            ctx.DialogueStageDay = (int)Game1.stats.DaysPlayed;
            ClearCachedAction(ctx);

            who.Money += ctx.LastCompletedQuest.Reward;
            // Показываем благодарность напрямую, без изменения стадии диалога
            if (ctx.LastCompletedQuest != null)
            {
                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: Спасибо за {ctx.LastCompletedQuest.Amount}x {ctx.LastCompletedQuest.Item}! Это очень помогло.");
            }

            // Записываем завершенный квест, чтобы предотвратить повторение
            QuestTrackingModule.RecordCompletedQuest(ctx.Name, ctx.LastCompletedQuest);
            // Обновляем потребности NPC на основе завершенного квеста, чтобы предотвратить повторение
            QuestTrackingModule.UpdateNeedsAfterCompletion(ctx, ctx.LastCompletedQuest.Item, ctx.LastCompletedQuest.Amount);

            // Отслеживаем, что квест был завершен сегодня, чтобы предотвратить предложение другого квеста сегодня
            if (ctx.CompletedQuestsToday == null)
                ctx.CompletedQuestsToday = new List<string>();
            ctx.CompletedQuestsToday.Add($"{ctx.LastCompletedQuest.Item}_{ctx.LastCompletedQuest.Amount}");

            Monitor.Log($"Квест завершен через подарок: {ctx.Name} получил {giftedAmount}x {giftedItemName} (нужно было {ctx.LastCompletedQuest.Amount}x {ctx.LastCompletedQuest.Item})", LogLevel.Info);
        }

        private static List<ActionHandler> LoadActionHandlersFromConfig()
        {
            var handlers = new List<ActionHandler>();
            try
            {
                string configPath = Path.Combine(Constants.ContentPath, "Configs", "action-handlers.json");
                if (!File.Exists(configPath))
                {
                    Monitor?.Log($"Action handlers config not found: {configPath}", LogLevel.Warn);
                    return handlers;
                }

                string jsonContent = File.ReadAllText(configPath);
                var root = JObject.Parse(jsonContent);
                var arr = root["ActionHandlers"] as JArray;
                if (arr == null) return handlers;

                foreach (var node in arr)
                {
                    string code = node.Value<string>("Code");
                    string prompt = node.Value<string>("PromptDescription") ?? string.Empty;
                    string example = node.Value<string>("JsonExample") ?? "{}";
                    int cacheDuration = node.Value<int?>("CacheDuration") ?? 200;
                    bool cacheResponse = node.Value<bool?>("CacheResponse") ?? false;

                    var handler = new ActionHandler
                    {
                        Code = code,
                        PromptDescription = prompt,
                        JsonExample = example,
                        CacheDuration = cacheDuration,
                        CacheResponse = code?.ToUpperInvariant() switch
                        {
                            "START_FETCH_QUEST" => true,
                            "CRAFT_QUEST" => true,
                            "HUNGRY_REQUEST_HELP" => true,
                            "HUNGRY_REQUEST_ITEM" => true,
                            "FOOD_QUEST_REQUEST" => true,
                            _ => cacheResponse
                        },
                        Validator = a => !string.IsNullOrWhiteSpace(a.Text) // Стандартный валидатор
                    };

                    // Сопоставляем известные коды с соответствующими исполнителями/валидаторами (сохраняет исходное поведение).
                    switch (code?.ToUpperInvariant())
                    {
                        case "GREET_PLAYER":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text) && action.Text.Length <= 200;
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                            };
                            break;
                        case "REFUSAL":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text) && action.Text.Length <= 220;
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                            };
                            break;
                        case "START_FETCH_QUEST":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                if (!string.IsNullOrWhiteSpace(action.Item) && action.Amount > 0 && action.Reward > 0 && !action.IsReplay)
                                {
                                    Monitor?.Log($"Новый квест: {action.Item} x{action.Amount} за {action.Reward}g.", LogLevel.Info);
                                }
                                if (npc.Name.Equals("Clint", StringComparison.OrdinalIgnoreCase))
                                {
                                    var ctx = ContextManager.GetOrCreateContext(npc, Game1.player);
                                    ctx.ActiveQuest = action;
                                    int amount = action.Amount > 0 ? action.Amount : 1;
                                    int reward = action.Reward > 0 ? action.Reward : 100;
                                    Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}\n(Quest: collect {amount}x {action.Item} for {reward}g)");
                                }
                            };
                            break;
                        case "TELL_STORY":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text) && action.Text.Length <= 240;
                            handler.Executor = (action, npc, _) =>
                            {
                                string title = string.IsNullOrWhiteSpace(action.Title) ? string.Empty : $"{action.Title}\n";
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {title}{action.Text}");
                            };
                            break;
                        case "SHARE_RUMOR":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text) && action.Text.Length <= 240;
                            handler.Executor = (action, npc, _) =>
                            {
                                string topic = string.IsNullOrWhiteSpace(action.Topic) ? string.Empty : $"[{action.Topic}] ";
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {topic}{action.Text}");
                            };
                            break;
                        case "GIVE_TIP":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text) && action.Text.Length <= 240;
                            handler.Executor = (action, npc, _) =>
                            {
                                string topic = string.IsNullOrWhiteSpace(action.Topic) ? string.Empty : $"({action.Topic}) ";
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {topic}{action.Text}");
                            };
                            break;
                        case "QUEST_THANKS":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text) && action.Text.Length <= 260;
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                            };
                            break;
                        case "CRAFT_QUEST":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                var ctx = ContextManager.GetOrCreateContext(npc, Game1.player);
                                if (npc.Name.Equals("Clint", StringComparison.OrdinalIgnoreCase))
                                {
                                    ctx.ActiveQuest = action;
                                    int amount = action.Amount > 0 ? action.Amount : 1;
                                    int reward = action.Reward > 0 ? action.Reward : 100;
                                    Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}\n(Quest: {amount}x {action.Item} for {reward}g craft)");
                                }
                            };
                            break;
                        case "EAT":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                            };
                            break;
                        case "HUNGRY_EAT_FROM_INV":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                var ctx = ContextManager.GetOrCreateContext(npc, Game1.player);
                                if (!string.IsNullOrWhiteSpace(action.Item) && ctx.Inventory.ContainsKey(action.Item) && ctx.Inventory[action.Item] > 0)
                                {
                                    ctx.Inventory[action.Item]--;
                                    if (ctx.Inventory[action.Item] <= 0) ctx.Inventory.Remove(action.Item);
                                    ctx.Satiety = Math.Min(100f, ctx.Satiety + ConfigManager.GetSatiety(action.Item));
                                    Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                                }
                                else
                                {
                                    Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: I was about to eat, but I don't have any food on me.");
                                }
                            };
                            break;
                        case "HUNGRY_REQUEST_HELP":
                        case "HUNGRY_REQUEST_ITEM":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                            };
                            break;
                        case "TIRED_REST_ACTION":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                                var ctx = ContextManager.GetOrCreateContext(npc, Game1.player);
                                ctx.WorldState["needsRest"] = true;
                            };
                            break;
                        case "TIRED_CONTINUE_WORKING":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                            };
                            break;
                        case "LOW_MOOD_SEEK_COMPANY":
                        case "HAPPY_SHARE_ENERGY":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                            };
                            break;
                        case "FOOD_QUEST_REQUEST":
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                var ctx = ContextManager.GetOrCreateContext(npc, Game1.player);
                                ctx.ActiveQuest = action;
                                int amount = action.Amount > 0 ? action.Amount : 1;
                                int reward = action.Reward > 0 ? action.Reward : 100;
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}\n(Food Quest: bring {amount}x {action.Item} for {reward}g - I'll eat it)");
                            };
                            break;
                        default:
                            // Default executor: просто показать текст
                            handler.Validator = action => !string.IsNullOrWhiteSpace(action.Text);
                            handler.Executor = (action, npc, _) =>
                            {
                                Game1.activeClickableMenu = new DialogueBox($"{npc.Name}: {action.Text}");
                            };
                            break;
                    }

                    handlers.Add(handler);
                }
            }
            catch (Exception ex)
            {
                Monitor?.Log($"Ошибка загрузки ActionHandlers из конфига: {ex.Message}", LogLevel.Error);
            }
            return handlers;
        }

        private static Dictionary<string, int> LoadQuestItemsFromConfig()
        {
            var questItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string configPath = Path.Combine(Constants.ContentPath, "Configs", "quest-items.json");
                if (!File.Exists(configPath))
                {
                    Monitor?.Log($"Quest items config not found: {configPath}", LogLevel.Warn);
                    return questItems;
                }

                string jsonContent = File.ReadAllText(configPath);
                var root = JObject.Parse(jsonContent);
                var obj = root["PredefinedQuestItems"] as JObject;
                if (obj != null)
                {
                    foreach (var prop in obj.Properties())
                    {
                        int id = prop.Value.Value<int>();
                        questItems[prop.Name] = id;
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Ошибка загрузки PredefinedQuestItems из конфига: {ex.Message}", LogLevel.Error);
            }
            return questItems;
        }

        private static void InitializeItemCache()
        {
            // Пример инициализации кэша предметов, если требуется
            // Если у вас уже есть логика для заполнения ItemNameToId и KnownItemNames, реализуйте её здесь
            ItemNameToId.Clear();
            KnownItemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Пример: заполняем из PredefinedQuestItems
            foreach (var kvp in PredefinedQuestItems)
            {
                ItemNameToId[kvp.Key] = kvp.Value;
                KnownItemNames.Add(kvp.Key);
            }
        }

        public static int GetItemID(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return -1;
            if (ItemNameToId.TryGetValue(itemName, out int id))
                return id;
            return -1;
        }

        private static bool IsFoodItem(string itemName)
        {
            // Предполагаем, что ConfigManager предоставляет информацию о еде
            return ConfigManager.IsFood(itemName);
        }

        private static int GetFoodValue(string itemName)
        {
            // Предполагаем, что ConfigManager предоставляет значение сытости для еды
            return ConfigManager.GetSatiety(itemName);
        }

        public static IEnumerable<string> GetKnownItemNames()
        {
            return KnownItemNames;
        }
    }
}