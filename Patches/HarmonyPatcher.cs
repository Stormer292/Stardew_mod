using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Threading.Tasks;

namespace LLMBrainMod.Patches
{
    public static class HarmonyPatcher
    {
        private static IMonitor Monitor;
        private static Harmony HarmonyInstance;

        public static void Initialize(IMonitor monitor, string uniqueId)
        {
            Monitor = monitor;
            HarmonyInstance = new Harmony(uniqueId);
            HarmonyInstance.PatchAll();
            Monitor.Log("Harmony патчи применены.", LogLevel.Debug);
        }
    }

    [HarmonyPatch(typeof(NPC), nameof(NPC.checkAction))]
    public class CheckActionPatch
    {
        static bool Prefix(NPC __instance, ref bool __result)
        {
            Farmer who = Game1.player;

            if (Game1.dialogueUp || Game1.activeClickableMenu != null || LLMEngine.IsProcessing)
                return true;

            if (!LLMEngine.IsSupportedNpc(__instance))
                return true;

            var ctx = ContextManager.GetOrCreateContext(__instance, who);

            // Проверяем, не было ли выполнено задание
            // Сначала проверяем через подарок
            if (who.ActiveObject != null)
            {
                // Игрок держит предмет - проверяем, является ли он квестовым
                // Используем ParentSheetIndex, чтобы получить фактический ID предмета, затем сопоставляем с нашим внутренним именем
                string itemName = who.ActiveObject.DisplayName ?? who.ActiveObject.Name;
                int itemId = who.ActiveObject.ParentSheetIndex;
                
                // Пытаемся найти внутреннее имя, сопоставляя ID предмета с нашими предопределенными предметами
                string internalName = null;
                foreach (var name in LLMEngine.GetKnownItemNames()) // используем публичный KnownItemNames
                {
                    if (LLMEngine.GetItemID(name) == itemId)
                    {
                        internalName = name;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(internalName))
                {
                    // Если не удалось найти по ID, возвращаемся к отображаемому имени
                    internalName = itemName;
                }
                
                if (!string.IsNullOrWhiteSpace(internalName))
                {
                    LLMEngine.Monitor.Log($"[DEBUG] Игрок держит предмет: {itemName} (internal: {internalName}, ID: {itemId}), проверяем на квест", LogLevel.Debug);
                    LLMEngine.HandleGiftReceived(__instance, who, internalName, who.ActiveObject.Stack);
                    // Если квест был выполнен, показываем благодарность
                    var newCtx = ContextManager.GetOrCreateContext(__instance, who); // Обновляем контекст
                    if (newCtx.LastCompletedQuest != null)
                    {
                        LLMEngine.Monitor.Log($"[DEBUG] Квест выполнен через HandleGiftReceived, показываем благодарность", LogLevel.Debug);
                        LLMEngine.Monitor.Log($"[DEBUG] Quest completed, but функция TriggerQuestThankYou не реализована.", LogLevel.Debug);
                        
                    }
                    else
                    {
                        // Также пробуем стандартный метод завершения квеста как резервный вариант
                        bool standardCompletion = LLMEngine.TryCompleteQuest(newCtx, who);
                        if (standardCompletion)
                        {
                            LLMEngine.Monitor.Log($"[DEBUG] Квест выполнен через TryCompleteQuest, показываем благодарность", LogLevel.Debug);
                            LLMEngine.Monitor.Log($"[DEBUG] Quest completed, but функция TriggerQuestThankYou не реализована.", LogLevel.Debug);
                            
                        }
                        else
                        {
                            LLMEngine.Monitor.Log($"[DEBUG] Квест не был выполнен - активный квест: {newCtx.ActiveQuest?.Item} x{newCtx.ActiveQuest?.Amount}, стадия: {newCtx.DialogueStage}, количество предмета: {who.ActiveObject.Stack}", LogLevel.Debug);
                        }
                    }
                    return false;
                }
                // Если игрок держит предмет, но не удалось обработать как квест, все равно возвращаем false,
                // чтобы стандартная система не обрабатывала подарок
                return false;
            }

            // Проверяем активный квест - если есть, показываем напоминание
            if (ctx.ActiveQuest != null)
            {
                string reminder = $"{__instance.Name}:\n\nBring me {ctx.ActiveQuest.Amount}x {ctx.ActiveQuest.Item}\nfor {ctx.ActiveQuest.Reward}g!\n\nHold the item and left-click to give.";
                Game1.activeClickableMenu = new DialogueBox(reminder);
                return false;
            }

            // Проверяем кэшированное действие
            if (TryShowCachedAction(__instance, who))
            {
                return false;
            }

            // Обновляем стадию диалога перед каждым взаимодействием
            // Это позволяет избежать застревания на одной стадии
            // Но только если нет активного квеста
            if (ctx.ActiveQuest == null)
            {
                // Если сегодня еще не было приветствия, то устанавливаем стадию на Greeting
                // В противном случае, оставляем текущую стадию для других действий
                if (ctx.DialogueStageDay != (int)Game1.stats.DaysPlayed)
                {
                    // Новый день - сначала приветствие
                    LLMEngine.Monitor.Log($"[DEBUG] Новый день, устанавливаем стадию на Greeting: {ctx.DialogueStage}", LogLevel.Debug);
                    ctx.DialogueStage = DialogueStage.Greeting;
                }
                // Если уже было приветствие в этот день, оставляем текущую стадию
                else
                {
                    LLMEngine.Monitor.Log($"[DEBUG] Уже было приветствие сегодня, стадия остается: {ctx.DialogueStage}", LogLevel.Debug);
                }
            }
            else
            {
                // Если есть активный квест, фокусируемся на нем
                LLMEngine.Monitor.Log($"[DEBUG] Активный квест, фокус на квесте: {ctx.DialogueStage}", LogLevel.Debug);
            }

            Task.Run(async () =>
            {
                LLMEngine.IsProcessing = true;
                string json_response = await LLMEngine.GetLLMResponseAsync(__instance, who);
                await HandleLLMResponseAsync(json_response, __instance, who);
                LLMEngine.IsProcessing = false;
            });

            return false;
        }

        private static bool TryShowCachedAction(NPC npc, Farmer who)
        {
            // Здесь должна быть логика показа кэшированного действия, если она есть.
            // Если такой логики нет, просто возвращайте false.
            return false;
        }

        private static async Task HandleLLMResponseAsync(string json_response, NPC npc, Farmer who)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json_response))
                {
                    LLMEngine.Monitor.Log("[DEBUG] Пустой ответ от LLM, ничего не делаем", LogLevel.Debug);
                    return;
                }

                var action = LLMEngine.ParseAction(json_response);
                if (action == null)
                {
                    LLMEngine.Monitor.Log($"[DEBUG] Не удалось распарсить JSON: {json_response}", LogLevel.Debug);
                    return;
                }

                var ctx = ContextManager.GetOrCreateContext(npc, who);

                // Пытаемся получить обработчик действия
                if (LLMEngine.TryValidateAction(action, out var handler))
                {
                    // Обновляем стадию диалога на основе действия
                    bool shouldUpdateStage = LLMEngine.UpdateDialogueStage(ctx, handler, action);

                    // Выполняем действие
                    handler.Executor?.Invoke(action, npc, who);

                    // Кэшируем действие, если это необходимо
                    if (handler.CacheResponse)
                    {
                        LLMEngine.CacheAction(ctx, action, handler, ctx.DialogueStage);
                    }
                }
                else
                {
                    LLMEngine.Monitor.Log($"[DEBUG] Недопустимое действие от LLM: {json_response}", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                LLMEngine.Monitor.Log($"Ошибка при обработке ответа LLM: {ex.Message}", LogLevel.Error);
            }
        }
    }
    [HarmonyPatch(typeof(NPC), nameof(NPC.receiveGift))]
    public class ReceiveGiftPatch
    {
   
        static void Postfix(NPC __instance, StardewValley.Object o, Farmer giver, bool updateGiftLimitInfo, float friendshipChangeMultiplier, bool showResponse)
        {
            if (o == null || o.Stack <= 0)
                return;

            if (!LLMEngine.IsSupportedNpc(__instance))
                return;

            // Используем ParentSheetIndex, чтобы получить фактический ID предмета, затем сопоставляем с нашим внутренним именем
            string itemName = o.DisplayName ?? o.Name;
            int itemId = o.ParentSheetIndex;
            
            // Пытаемся найти внутреннее имя, сопоставляя ID предмета с нашими предопределенными предметами
            string internalName = null;
                foreach (var name in LLMEngine.GetKnownItemNames()) // используем публичный KnownItemNames
                {
                    if (LLMEngine.GetItemID(name) == itemId)
                    {
                        internalName = name;
                        break;
                    }
                }
            if (string.IsNullOrEmpty(internalName))
            {
                // Если не удалось найти по ID, возвращаемся к отображаемому имени
                internalName = itemName;
            }
            
            if (string.IsNullOrWhiteSpace(internalName))
                return;

            // Передаём имя и количество в LLMEngine, он уже сверит с активным квестом
            LLMEngine.Monitor.Log($"[DEBUG] Получен подарок: {itemName} (internal: {internalName}, ID: {itemId})", LogLevel.Debug);
            LLMEngine.HandleGiftReceived(__instance, giver, internalName, o.Stack);
        }
    }
}