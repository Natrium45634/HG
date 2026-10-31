using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

// Part 6 (ТЗ ч. 6, 1–2; docs/DECISIONS_6.md §96): the rules of a game mode — switches of subsystems and references to the
// rules that already exist, not a second engine of modes — and the squad the player takes into a battle.

/// <summary>Who of the chosen squad stands on the field when the battle begins.</summary>
public enum SquadStart
{
    [Ed("Весь отряд на поле", "Свободная RTS: отряд расставляется в зоне расстановки при начале боя")] AllOnField,
    [Ed("Стартовый состав сценария", "Гибрид: на поле — бойцы из «Стартовый состав на поле», остальные ждут вызова")] StartDeployed,
    [Ed("Никто", "Тактический TD: бойцы выходят только расстановкой или вызовом")] NoneOnField,
}

/// <summary>
/// «Правила режима» (ТЗ ч. 6, 1.1–1.5): the switches of the subsystems of part 6 and a reference to a preset of the
/// battle rules. A scenario chooses one and may set its own value of any field (<see cref="GameRulesOverrideDef"/>).
/// </summary>
public sealed class GameRulesDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.GameRules;

    [Ed("Пресет правил боя", "Числа боя: ОД, урон, обзор…; пусто — глобальные правила. Пресет, выбранный в самом сценарии, сильнее", Group = "Правила"),
     EdRef(DefCategory.RulesPreset)]
    public string RulesPreset { get; set; } = "";
    [Ed("Отряд на поле в начале", "Для сценариев с отрядом игрока: кто из выбранных бойцов стоит на поле сразу", Group = "Отряд")]
    public SquadStart SquadStart { get; set; } = SquadStart.AllOnField;
    [Ed("Блокирование", "Удержание врагов бойцами с лимитом блокирования (часть 2); выключено — никто никого не держит", Group = "Системы")]
    public bool Blocking { get; set; } = true;
    [Ed("Урон по союзникам в области", "Не задано — из правил боя («Урон по союзникам»)", Group = "Системы")]
    public bool? FriendlyFire { get; set; }
    [Ed("Дружественный огонь", "Правила по видам воздействия; не задано — из правил боя", Group = "Системы")]
    public FriendlyFireRulesDef? FriendlyFireRules { get; set; }
    [Ed("Развёртывание", "Вызов бойцов отряда во время боя за очки развёртывания (ТЗ ч. 6, 3)", Group = "Развёртывание")]
    public DeploymentRulesDef Deployment { get; set; } = new();
}

/// <summary>A scenario's own values over its rules of the mode: an unset field is inherited from the preset (ТЗ ч. 6, 1.5).</summary>
public sealed class GameRulesOverrideDef : ContentPart
{
    [Ed("Отряд на поле в начале")] public SquadStart? SquadStart { get; set; }
    [Ed("Блокирование")] public bool? Blocking { get; set; }
    [Ed("Урон по союзникам в области")] public bool? FriendlyFire { get; set; }
    [Ed("Дружественный огонь", "Своё правило по видам воздействия; не задано — из правил режима")] public FriendlyFireRulesDef? FriendlyFireRules { get; set; }
}

/// <summary>A saved squad (ТЗ ч. 6, 2.1): templates in order; a «build» is a template or its version (§96).</summary>
public sealed class SquadPresetDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.SquadPreset;

    [Ed("Бойцы", "Шаблоны по порядку; повтор — второй боец того же шаблона"), EdRef(DefCategory.Unit)] public List<string> Units { get; set; } = new();
    [Ed("Повторы разрешены", "Иначе один шаблон — один боец")] public bool DuplicatesAllowed { get; set; }
}

/// <summary>
/// A saved zone of deployment (ТЗ ч. 6, 12.2; §97): its hexes belong to a map, so it is offered to the scenarios of that
/// map; taking it copies the zone into the scenario (later edits of either do not touch the other).
/// </summary>
public sealed class ZonePresetDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.ZonePreset;

    [Ed("Карта", "Гексы зоны — гексы этой карты"), EdRef(DefCategory.Map)] public string Map { get; set; } = "";
    [Ed("Зона")] public DeploymentZoneDef Zone { get; set; } = new();
}

/// <summary>A limit of one template or tag in a squad.</summary>
public sealed class SquadLimitDef : ContentPart
{
    [Ed("Шаблон, категория или тег", "ID шаблона, категория существ, «tag:x» или просто тег")] public string Match { get; set; } = "";
    [Ed("Не более"), EdRange(0, 100)] public int Max { get; set; } = 1;

    public override string ToString() => $"{Match} ≤ {Max}";
}

/// <summary>
/// The squad of a scenario (ТЗ ч. 6, 2.2–2.5): who may be taken and how many. Every limit is optional. «Размер отряда» is not
/// «одновременно на поле» — the second is the limit of the deployment (§96).
/// </summary>
public sealed class ScenarioSquadDef : ContentPart
{
    [Ed("Отряд игрока", "Выключено — как раньше: только юниты сценария и расстановка")] public bool Enabled { get; set; }
    [Ed("Доступные бойцы", "Шаблоны, из которых набирается отряд; пусто — все шаблоны фракций игрока"), EdRef(DefCategory.Unit),
     EdShowIf(nameof(Enabled), true)]
    public List<string> Pool { get; set; } = new();
    [Ed("Не меньше бойцов"), EdRange(0, 100), EdShowIf(nameof(Enabled), true)] public int MinSize { get; set; }
    [Ed("Не больше бойцов (размер отряда)", "0 — без ограничения"), EdRange(0, 100), EdShowIf(nameof(Enabled), true)] public int MaxSize { get; set; } = 6;
    [Ed("Повторы разрешены", "Два бойца одного шаблона"), EdShowIf(nameof(Enabled), true)] public bool DuplicatesAllowed { get; set; }
    [Ed("Разрешённые теги", "Если заданы — боец должен подходить хотя бы под один (шаблон, категория, «tag:x» или тег)"), EdShowIf(nameof(Enabled), true)]
    public List<string> AllowedTags { get; set; } = new();
    [Ed("Запрещённые теги", "Боец с любым из них недоступен"), EdShowIf(nameof(Enabled), true)] public List<string> ForbiddenTags { get; set; } = new();
    [Ed("Обязательные теги", "Каждый — хотя бы у одного бойца отряда"), EdShowIf(nameof(Enabled), true)] public List<string> RequiredTags { get; set; } = new();
    [Ed("Лимиты", "Не более N бойцов шаблона, категории или тега"), EdShowIf(nameof(Enabled), true)] public List<SquadLimitDef> Limits { get; set; } = new();
    [Ed("Обязательные бойцы", "Всегда в отряде, убрать нельзя"), EdRef(DefCategory.Unit), EdShowIf(nameof(Enabled), true)]
    public List<string> StartingRoster { get; set; } = new();
    [Ed("Стартовый состав на поле", "Для «Отряд на поле в начале: стартовый состав» — кто из отряда стоит на поле сразу"), EdRef(DefCategory.Unit),
     EdShowIf(nameof(Enabled), true)]
    public List<string> StartDeployed { get; set; } = new();
    [Ed("Фиксированный отряд", "Экран выбора не показывается: отряд — «Обязательные бойцы» (или пресет)"), EdShowIf(nameof(Enabled), true)]
    public bool Fixed { get; set; }
    [Ed("Пресет по умолчанию", "Набирается при открытии экрана; для автобоя и фиксированного отряда"), EdRef(DefCategory.SquadPreset), EdShowIf(nameof(Enabled), true)]
    public string DefaultPreset { get; set; } = "";
}

// Part 6, stage 6.2 (ТЗ ч. 6, 3; docs/DECISIONS_6.md §97): the deployment during the battle for a pool of points.

/// <summary>What a fallen fighter of the squad becomes.</summary>
public enum AfterDeathPolicy
{
    [Ed("Выбывает", "Павший боец больше не вызывается")] Gone,
    [Ed("Возвращается после перерыва", "Через «перерыв после гибели» снова ждёт в отряде (это не лечение и не отдых)")] Cooldown,
}

/// <summary>
/// «Развёртывание» (ТЗ ч. 6, 3.1): calling fighters of the squad onto the field during the battle for deployment points
/// (ОР). The points are a pool of the side, counted in ticks: not AP, not mana, not resources of skills.
/// </summary>
public sealed class DeploymentRulesDef : ContentPart
{
    [Ed("Вызов за очки развёртывания", "Выключено — бойцы выходят только при старте и расстановкой")] public bool Enabled { get; set; }
    [Ed("Ресурс (вид)", "Имя, цвет и шкала очков; пусто — «Очки развёртывания»"), EdRef(DefCategory.Resource), EdShowIf(nameof(Enabled), true)]
    public string Resource { get; set; } = "dp";
    [Ed("Вызов в подготовке"), EdShowIf(nameof(Enabled), true)] public bool InPreparation { get; set; } = true;
    [Ed("Вызов в бою"), EdShowIf(nameof(Enabled), true)] public bool InCombat { get; set; } = true;
    [Ed("Одновременно на поле", "Бойцов отряда на поле разом; 0 — без ограничения (это не размер отряда)"), EdRange(0, 100), EdShowIf(nameof(Enabled), true)]
    public int MaxDeployed { get; set; } = 6;
    [Ed("Очков в начале"), EdRange(0, 100_000), EdShowIf(nameof(Enabled), true)] public double StartingPoints { get; set; } = 10;
    [Ed("Не больше очков", "0 — без предела"), EdRange(0, 100_000), EdShowIf(nameof(Enabled), true)] public double MaxPoints { get; set; } = 99;
    [Ed("Начисление: очков", "За каждый интервал; 0 — нет периодического начисления"), EdRange(0, 10_000), EdShowIf(nameof(Enabled), true)]
    public double GainAmount { get; set; } = 1;
    [Ed("Начисление: интервал, с", "Игровое время: не зависит от кадров и скорости, на паузе стоит"), EdRange(0.05, 3600), EdShowIf(nameof(Enabled), true)]
    public double GainInterval { get; set; } = 1;
    [Ed("Начислять в подготовке"), EdShowIf(nameof(Enabled), true)] public bool GainInPreparation { get; set; }
    [Ed("Очков за убитого врага"), EdRange(0, 10_000), EdShowIf(nameof(Enabled), true)] public double GainPerKill { get; set; }
    [Ed("Очков за пройденную волну"), EdRange(0, 10_000), EdShowIf(nameof(Enabled), true)] public double GainPerWave { get; set; }
    [Ed("Цена по умолчанию", "Если у юнита не задана: 0 — стоимость угрозы шаблона"), EdRange(0, 100_000), EdShowIf(nameof(Enabled), true)]
    public double DefaultCost { get; set; }
    [Ed("Отзыв с поля"), EdShowIf(nameof(Enabled), true)] public bool AllowRetreat { get; set; } = true;
    [Ed("Возврат при отзыве", "Доля цены"), EdRange(0, 1), EdShowIf(nameof(Enabled), true)] public double RetreatRefund { get; set; } = 0.5;
    [Ed("Отзыв под контролем", "Оглушённого, обездвиженного, под чужим контролем можно отозвать"), EdShowIf(nameof(Enabled), true)]
    public bool RetreatWhileControlled { get; set; }
    [Ed("Подтверждать отзыв", "Окно подтверждения в бою"), EdShowIf(nameof(Enabled), true)] public bool ConfirmRetreat { get; set; } = true;
    [Ed("Повторный вызов", "Отозванного бойца можно вызвать снова"), EdShowIf(nameof(Enabled), true)] public bool AllowRedeploy { get; set; } = true;
    [Ed("Перерыв после отзыва, с", "Если у юнита не задан"), EdRange(0, 3600), EdShowIf(nameof(Enabled), true)] public double RedeployCooldown { get; set; } = 10;
    [Ed("После гибели"), EdShowIf(nameof(Enabled), true)] public AfterDeathPolicy AfterDeath { get; set; } = AfterDeathPolicy.Gone;
    [Ed("Перерыв после гибели, с"), EdRange(0, 3600), EdShowIf(nameof(AfterDeath), AfterDeathPolicy.Cooldown)] public double DeathCooldown { get; set; } = 30;
    [Ed("Не ближе к врагу, гексов", "Нельзя вызвать в N гексах от видимого врага; 0 — можно"), EdRange(0, 20), EdShowIf(nameof(Enabled), true)]
    public int EnemyRadius { get; set; }
    [Ed("Считать невидимых врагов", "Запрет у врага действует и для врагов, которых игрок не видит (иначе — только для видимых)"),
     EdShowIf(nameof(Enabled), true)] public bool EnemyRadiusHidden { get; set; }
    [Ed("Не на опасной клетке", "Огонь, яд и прочие опасные эффекты клетки"), EdShowIf(nameof(Enabled), true)] public bool ForbidHazard { get; set; } = true;
    [Ed("Поводок, гексов", "Вызванный боец не отходит от места вызова дальше N гексов; 0 — ходит по обычным правилам"), EdRange(0, 50),
     EdShowIf(nameof(Enabled), true)]
    public int Leash { get; set; }
}

/// <summary>How the facing of a called fighter is chosen.</summary>
public enum DeployFacing
{
    [Ed("Выбирает игрок")] Manual,
    [Ed("Автоматически — к врагу")] Auto,
    [Ed("Задано")] Fixed,
}

/// <summary>How a template is called onto the field (ТЗ ч. 6, 3.3); absent — the defaults of the rules.</summary>
public sealed class UnitDeploymentDef : ContentPart
{
    [Ed("Цена вызова", "Очков развёртывания; −1 — по правилам (цена по умолчанию или стоимость угрозы)"), EdRange(-1, 100_000)] public double Cost { get; set; } = -1;
    [Ed("Перерыв после отзыва, с", "−1 — по правилам"), EdRange(-1, 3600)] public double RedeployCooldown { get; set; } = -1;
    [Ed("Можно вызвать")] public bool CanDeploy { get; set; } = true;
    [Ed("Можно отозвать")] public bool CanRetreat { get; set; } = true;
    [Ed("Теги зон", "Только в зоны развёртывания с одним из этих тегов; пусто — в любую")] public List<string> ZoneTags { get; set; } = new();
    [Ed("Направление при вызове")] public DeployFacing Facing { get; set; } = DeployFacing.Manual;
    [Ed("Заданное направление"), EdShowIf(nameof(Facing), DeployFacing.Fixed)] public Hex.HexDirection FixedFacing { get; set; } = Hex.HexDirection.E;
}

/// <summary>When a deployment zone is open.</summary>
public enum ZonePhase
{
    [Ed("Всегда")] Any,
    [Ed("Только в подготовке")] Preparation,
    [Ed("Только в бою")] Combat,
}

/// <summary>
/// A zone of deployment (ТЗ ч. 6, 3.4–3.5): hexes where the side may call its fighters. Zones may overlap: on a hex the zone
/// of the highest priority decides (then the first in the list).
/// </summary>
public sealed class DeploymentZoneDef : ContentPart
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Гексы", "Отмечаются на карте"), EdHidden] public List<Hex.HexCoord> Cells { get; set; } = new();
    [Ed("Сторона"), EdRef(DefCategory.Faction)] public string Faction { get; set; } = "player";
    [Ed("Теги зоны", "Для «Теги зон» юнита: ближняя, дальняя, высота…")] public List<string> Tags { get; set; } = new();
    [Ed("Кого можно", "Шаблон, категория или тег; пусто — любого бойца")] public List<string> UnitTags { get; set; } = new();
    [Ed("Фаза", "Когда в зону можно вызывать")] public ZonePhase Phase { get; set; } = ZonePhase.Any;
    [Ed("Приоритет", "На пересечении действуют ограничения всех зон; зона с большим приоритетом проверяется первой и даёт причину отказа")]
    public int Priority { get; set; }
    [Ed("Открыта", "Закрытая зона не принимает бойцов; её открывает и закрывает событие (действия «Открыть/Закрыть зону развёртывания»)")]
    public bool Active { get; set; } = true;

    public override string ToString() => (Name == "" ? Id : Name) + $" ({Cells.Count} гекс.)";
}
