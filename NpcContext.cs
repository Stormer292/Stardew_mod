using StardewValley;
using System;
using System.Collections.Generic;


namespace LLMBrainMod
{
    public enum Mood
    {
        Happy, Sad, Tired, Hungry, Content, Angry, Neutral, Stressed, Excited
    }

    public enum NPCBehavior
    {
        Working, Resting, Eating, Crafting, Shopping, Socializing, Idle
    }

    public class NpcContext
    {
        public string Name { get; set; }
        public int FriendshipLevel { get; set; }
        public int FriendshipPoints { get; set; }
        public string Season { get; set; }
        public int TimeOfDay { get; set; }
        public string LocationName { get; set; }
        public List<string> KnownTopics { get; set; } = new();
        public Dictionary<string, object> WorldState { get; set; } = new();

        // Новые параметры NPC как живого персонажа
        public Mood CurrentMood { get; set; } = Mood.Neutral;
        public NPCBehavior CurrentBehavior { get; set; } = NPCBehavior.Idle;
        public Dictionary<string, int> Inventory { get; set; } = new(); // Видимый инвентарь
        public Dictionary<string, int> HiddenInventory { get; set; } = new(); // Скрытый инвентарь
        public float Satiety { get; set; } = 10f; // 0-100 сытость
        public float Stamina { get; set; } = 100f; // 0-100 выносливость
        public string Lore { get; set; } = "";
        public Dictionary<string, int> Needs { get; set; } = new(); // Требуемые предметы
        public Dictionary<string, int> CraftingQueue { get; set; } = new(); // Очередь крафта

        // Поведенческие параметры
        public int WorkStaminaCost { get; set; } = 5; // Стамина за час работы
        public int RestRecoveryRate { get; set; } = 15; // Стамина за час отдыха
        public int HungerRate { get; set; } = 3; // Голод за час
        public int MoodImpact { get; set; } = 2; // Влияние настроения на эффективность
        public float MoodRecoveryRate { get; set; } = 1.0f; // Скорость восстановления настроения
        public float MoodDrainRate { get; set; } = 0.5f; // Скорость снижения настроения

        // Система расписания
        public Dictionary<int, NPCBehavior> Schedule { get; set; } = new();
        public bool ScheduleOverridden { get; set; } = false;
        public string OverrideReason { get; set; } = "";
        public string ScheduleOverrideSource { get; set; } = ""; // Источник переопределения расписания

        // Кэш последнего ответа
        public LLMAction CachedAction { get; set; }
        public string CachedActionCode { get; set; }
        public int CachedActionDay { get; set; } = -1;
        public int CachedActionTime { get; set; } = -1;
        public int CachedActionDuration { get; set; }
        public DialogueStage CachedActionStage { get; set; } = DialogueStage.Greeting;

        // Состояние сюжетного диалога
        public DialogueStage DialogueStage { get; set; } = DialogueStage.Greeting;
        public int DialogueStageDay { get; set; } = -1;
        public LLMAction ActiveQuest { get; set; }
        public LLMAction LastCompletedQuest { get; set; }
        
        // Дополнительные параметры для "живого" NPC
        public int SocialEnergy { get; set; } = 10; // Энергия для социальных взаимодействий
        public int SocialEnergyDrain { get; set; } = 5; // Потребление энергии при общении
        public bool IsAvailableForInteraction { get; set; } = true; // Доступен ли для взаимодействия
        
        
        public Dictionary<string, int> DailySatisfiedNeeds { get; set; } = new Dictionary<string, int>();
        
        // Новое: детальная информация о диалоговом состоянии
        public Dictionary<string, int> DiscussedTopics { get; set; } = new(); // Темы, которые уже обсуждались
        public List<string> RecentDialogues { get; set; } = new(); // Последние диалоги за текущую встречу
        public DateTime LastInteractionTime { get; set; } = DateTime.MinValue; // Время последнего взаимодействия
        public bool HasGreetedPlayer { get; set; } = false; // Было ли приветствие в текущем сеансе
        public List<string> SharedStories { get; set; } = new(); // Рассказанные истории за день
        public List<string> GivenTips { get; set; } = new(); // Предоставленные советы за день
        public List<string> SharedRumors { get; set; } = new(); // Разглашенные слухи за день
        public List<string> CompletedQuestsToday { get; set; } = new(); // Завершенные квесты за день
        public Dictionary<string, int> TopicDiscussionCount { get; set; } = new(); // Счетчик обсуждений по темам
        public int ConsecutiveInteractionCount { get; set; } = 0; // Подсчет последовательных взаимодействий
        public string LastDialogueAction { get; set; } = ""; // Последнее выполненное действие в диалоге
        public List<string> RecentlyMentionedItems { get; set; } = new(); // Недавно упомянутые предметы
        
    }

    public enum DialogueStage
    {
        Greeting = 0,
        StoryOrTip = 1,
        QuestOffer = 2,
        WaitingForQuest = 3,
        QuestThanks = 4
    }
}