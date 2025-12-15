using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace LLMBrainMod
{
    internal static class ConfigManager
    {
        private static IModHelper _helper;
        private static IMonitor _monitor;

        public static Dictionary<string,int> FoodSatiety { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string,string> Prompts { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public static Dictionary<string, NpcDefinition> NpcDefinitions { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public static BehaviorRules BehaviorRules { get; private set; } = new();
        public static GenerationRules GenerationRules { get; private set; } = new();

        public static IEnumerable<string> FoodItems => FoodSatiety.Keys;

        /// <summary>
        /// Загружает все конфиги из папки "Configs" внутри папки мода.
        /// Файлы: "Configs/foods.json", "Configs/prompts.json", "Configs/npcs.json",
        /// "Configs/behavior-rules.json", "Configs/generation-rules.json".
        /// При отсутствии файла — соответствующий словарь/объект остаётся пустым
        /// </summary>
        public static void LoadAll(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;

            // foods
            try
            {
                var foods = helper.Data.ReadJsonFile<FoodConfig>("Configs/foods.json");
                if (foods?.Foods != null && foods.Foods.Count > 0)
                    FoodSatiety = new Dictionary<string,int>(foods.Foods, StringComparer.OrdinalIgnoreCase);
                else
                {
                    FoodSatiety = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                    _monitor?.Log("ConfigManager: файл Configs/foods.json пуст или не содержит элементов; FoodSatiety оставлен пустым", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"ConfigManager: ошибка загрузки Configs/foods.json: {ex.Message}", LogLevel.Warn);
                FoodSatiety = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            }

            // prompts
            try
            {
                var prompts = helper.Data.ReadJsonFile<PromptsConfig>("Configs/prompts.json");
                Prompts = (prompts?.Prompts != null)
                    ? new Dictionary<string,string>(prompts.Prompts, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"ConfigManager: ошибка загрузки Configs/prompts.json: {ex.Message}", LogLevel.Warn);
                Prompts = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            }

            // npcs
            try
            {
                var npcs = helper.Data.ReadJsonFile<NpcCollection>("Configs/npcs.json");
                NpcDefinitions = (npcs?.Npcs != null)
                    ? new Dictionary<string,NpcDefinition>(npcs.Npcs, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string,NpcDefinition>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"ConfigManager: ошибка загрузки Configs/npcs.json: {ex.Message}", LogLevel.Warn);
                NpcDefinitions = new Dictionary<string,NpcDefinition>(StringComparer.OrdinalIgnoreCase);
            }

            // behavior rules
            try
            {
                var br = helper.Data.ReadJsonFile<BehaviorRules>("Configs/behavior-rules.json");
                BehaviorRules = br ?? new BehaviorRules();
            }
            catch (Exception ex)
            {
                _monitor?.Log($"ConfigManager: ошибка загрузки Configs/behavior-rules.json: {ex.Message}", LogLevel.Warn);
                BehaviorRules = new BehaviorRules();
            }

            // generation rules
            try
            {
                var gr = helper.Data.ReadJsonFile<GenerationRules>("Configs/generation-rules.json");
                GenerationRules = gr ?? new GenerationRules();
            }
            catch (Exception ex)
            {
                _monitor?.Log($"ConfigManager: ошибка загрузки Configs/generation-rules.json: {ex.Message}", LogLevel.Warn);
                GenerationRules = new GenerationRules();
            }

            _monitor?.Log($"ConfigManager: загружено FoodItems={FoodSatiety.Count}, Prompts={Prompts.Count}, NPCs={NpcDefinitions.Count}", LogLevel.Debug);
        }

        public static bool IsFood(string name) => !string.IsNullOrEmpty(name) && FoodSatiety.ContainsKey(name);
        public static int GetSatiety(string name) => (name != null && FoodSatiety.TryGetValue(name, out var v)) ? v : 0;
        public static string GetPrompt(string key) => (key != null && Prompts.TryGetValue(key, out var p)) ? p : null;
        public static NpcDefinition GetNpcDefinition(string npcName) => (npcName != null && NpcDefinitions.TryGetValue(npcName, out var def)) ? def : null;

        // JSON helper classes
        public class FoodConfig { public Dictionary<string,int> Foods { get; set; } = new(); }
        public class PromptsConfig { public Dictionary<string,string> Prompts { get; set; } = new(); }
        public class NpcCollection { public Dictionary<string, NpcDefinition> Npcs { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
    }

    // Общедоступные DTO из ContextManager
    public class NpcDefinition
    {
        public string Lore { get; set; } = "";
        public Dictionary<string,int> Needs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,int> Inventory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,int> HiddenInventory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string,int> CraftingQueue { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int WorkStaminaCost { get; set; } = 0;
        public int RestRecoveryRate { get; set; } = 0;
        public int HungerRate { get; set; } = 1;
        public int MoodImpact { get; set; } = 0;
        public float MoodRecoveryRate { get; set; } = 1f;
        public float MoodDrainRate { get; set; } = 1f;
        public int SocialEnergy { get; set; } = 0;
        public int SocialEnergyDrain { get; set; } = 0;
        public bool IsAvailableForInteraction { get; set; } = true;
    }

    public class BehaviorRules
    {
        // Пороги, приоритеты и пр.
        public int HungerThreshold { get; set; } = 20;
        public int TiredThreshold { get; set; } = 30;
    }

    public class GenerationRules
    {
        // Правила генерации диалогов/квестов
        public int MaxDailyQuestsPerNpc { get; set; } = 1;
        public int QuestRepeatCooldownDays { get; set; } = 3;
    }
}