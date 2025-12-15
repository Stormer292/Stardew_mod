using StardewModdingAPI;
using StardewModdingAPI.Events;
using LLMBrainMod.Patches;
using System;
using System.Linq;
using StardewValley;

namespace LLMBrainMod
{
    public class ModEntry : Mod
    {
        private IMonitor ModMonitor;

        public override void Entry(IModHelper helper)
        {
            this.ModMonitor = Monitor;

            // Установка монитора для ContextManager
            ContextManager.SetMonitor(this.ModMonitor);
            ContextManager.Initialize(this.ModMonitor);

            // Загружаем конфигурации до инициализации других компонентов
            ContextManager.LoadConfigs(helper);
            LLMEngine.Initialize(this.ModMonitor);

            try
            {
                HarmonyPatcher.Initialize(this.ModMonitor, this.ModManifest.UniqueID);
            }
            catch (Exception ex)
            {
                this.ModMonitor.Log($"Ошибка при инициализации Harmony: {ex.Message}", LogLevel.Error);
            }

            // Тики для параметров NPC
            helper.Events.GameLoop.UpdateTicked += (sender, e) => {
                ContextManager.UpdateAllContexts((int)e.Ticks);
                
                if (Game1.stats.DaysPlayed % 7 == 0 && Game1.timeOfDay == 600) 
                {
                    ContextManager.CleanupOldQuestRecords();
                }
            };

            // Сохранение/загрузка состояний
            helper.Events.GameLoop.SaveLoaded += (sender, e) => {
                ContextManager.LoadConfigs(helper);
                ContextManager.LoadQuestTracking(helper);
            };
            helper.Events.GameLoop.Saving += (sender, e) => {
                ContextManager.LoadConfigs(helper);
                ContextManager.SaveQuestTracking(helper);
            };

            // ОБРАБОТЧИКИ КЛАВИШ F8/F9 ДЛЯ ТЕСТИРОВАНИЯ + F10 debug
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            this.ModMonitor.Log("LLMBrainMod загружен. Параметры NPC тикают, работает только для Clint.", LogLevel.Info);
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (Game1.player == null)
                return;

            if (e.Button == SButton.F8)
            {
                MaxFriendshipCheat();
                return;
            }

            if (e.Button == SButton.F9)
            {
                GiveQuestItemsCheat();
            }

            if (e.Button == SButton.F10)
            {
                foreach (var ctx in ContextManager.AllContexts)
                {
                    this.ModMonitor.Log($"{ctx.Name}: Mood={ctx.CurrentMood}, Behavior={ctx.CurrentBehavior}, Satiety={ctx.Satiety:F0}%, Stamina={ctx.Stamina:F0}%, Inv={string.Join(", ", ctx.Inventory.Where(kv => kv.Value > 0).Select(kv => kv.Key + "x" + kv.Value))}, Needs={string.Join(", ", ctx.Needs.Select(n => n.Key + "x" + n.Value))}, Quest={ctx.ActiveQuest?.Item ?? "none"}, Schedule={(ctx.ScheduleOverridden ? "OVERRIDE" : "NORMAL")}", LogLevel.Info);
                }
            }
        }

        private void MaxFriendshipCheat()
        {
            foreach (var location in Game1.locations)
            {
                foreach (var character in location.characters)
                {
                    if (!character.IsMonster &&
                        character.Name != null &&
                        character.Name != "Farmer" &&
                        character.Name != "Horse")
                    {
                        if (!Game1.player.friendshipData.ContainsKey(character.Name))
                        {
                            Game1.player.friendshipData[character.Name] = new Friendship();
                        }
                        Game1.player.friendshipData[character.Name].Points = 1000;
                    }
                }
            }

            this.ModMonitor.Log("Дружба со всеми NPC установлена на максимум!", LogLevel.Info);
        }

        private void GiveQuestItemsCheat()
        {
            
            var activeQuest = ContextManager.AllContexts
                .Select(ctx => ctx.ActiveQuest)
                .FirstOrDefault(q => q != null);

            if (activeQuest == null)
            {
                this.ModMonitor.Log("У NPC нет активного квеста — нечего выдавать.", LogLevel.Info);
                return;
            }

            // Исправлено: используем корректный способ выдачи предметов игроку
            int itemId = LLMEngine.GetItemID(activeQuest.Item);
            if (itemId <= 0)
            {
                this.ModMonitor.Log($"Не удалось найти ID для предмета {activeQuest.Item}.", LogLevel.Warn);
                return;
            }

            var item = new StardewValley.Object(itemId.ToString(), activeQuest.Amount, isRecipe: false);
            if (!Game1.player.addItemToInventoryBool(item))
            {
                this.ModMonitor.Log($"Не удалось выдать {activeQuest.Amount}x {activeQuest.Item}. Проверь инвентарь.", LogLevel.Warn);
                return;
            }

            this.ModMonitor.Log($"Выдано {activeQuest.Amount}x {activeQuest.Item} для текущего квеста.", LogLevel.Info);
        }
    }
}