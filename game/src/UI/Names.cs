using System;
using System.Collections.Generic;

namespace HexTactics.Game.UI;

/// <summary>Russian display names of enum values used by the editor and HUD.</summary>
public static class Names
{
    private static readonly Dictionary<string, string> Map = new()
    {
        ["TerrainVisual.Ground"] = "Земля", ["TerrainVisual.Grass"] = "Трава", ["TerrainVisual.Sand"] = "Песок", ["TerrainVisual.Stone"] = "Камень",
        ["TerrainVisual.Water"] = "Вода", ["TerrainVisual.Pit"] = "Пропасть", ["TerrainVisual.Wall"] = "Стена", ["TerrainVisual.Rock"] = "Скала",
        ["TerrainVisual.Crate"] = "Ящики", ["TerrainVisual.Vegetation"] = "Растительность", ["TerrainVisual.Hazard"] = "Опасная зона",
        ["TerrainVisual.Special"] = "Особая", ["TerrainVisual.Ice"] = "Лёд", ["TerrainVisual.Rubble"] = "Обломки",
        ["StatFormat.Number"] = "Число", ["StatFormat.Percent"] = "Процент",
        ["FactionShape.Circle"] = "Круг", ["FactionShape.Diamond"] = "Ромб", ["FactionShape.Square"] = "Квадрат", ["FactionShape.Triangle"] = "Треугольник",
        ["UnitShape.Pawn"] = "Пешка", ["UnitShape.Soldier"] = "Воин", ["UnitShape.Archer"] = "Стрелок", ["UnitShape.Mage"] = "Маг",
        ["UnitShape.Beast"] = "Зверь", ["UnitShape.Brute"] = "Громила", ["UnitShape.Construct"] = "Конструкт", ["UnitShape.Orb"] = "Сфера", ["UnitShape.Totem"] = "Тотем",
        ["PassiveTrigger.Always"] = "Всегда", ["PassiveTrigger.OnDamaged"] = "При получении урона", ["PassiveTrigger.OnAttackNoticed"] = "Заметил атаку",
        ["PassiveTrigger.OnHitEnemy"] = "При попадании", ["PassiveTrigger.OnKill"] = "При убийстве", ["PassiveTrigger.OnAllyDied"] = "Гибель союзника",
        ["PassiveTrigger.OnLowHp"] = "Низкое HP", ["PassiveTrigger.Periodic"] = "Периодически", ["PassiveTrigger.OnEnemySpotted"] = "Обнаружен враг",
        ["PassiveAction.None"] = "Нет", ["PassiveAction.EffectsOnSelf"] = "Эффекты на себя", ["PassiveAction.EffectsOnSource"] = "Эффекты на источник",
        ["PassiveAction.CounterAttack"] = "Контратака", ["PassiveAction.UseSkillOnSource"] = "Навык по источнику",
        ["AiTargetPriority.Nearest"] = "Ближайшая", ["AiTargetPriority.Weakest"] = "Слабейшая", ["AiTargetPriority.MostDangerous"] = "Опаснейшая",
        ["AiTargetPriority.LowestHpFraction"] = "Наименьший % HP",
        ["SkillKind.Attack"] = "Атака", ["SkillKind.Spell"] = "Заклинание", ["SkillKind.Support"] = "Поддержка", ["SkillKind.Movement"] = "Перемещение", ["SkillKind.Utility"] = "Утилита",
        ["TargetingMode.Self"] = "На себя", ["TargetingMode.Direction"] = "Направление", ["TargetingMode.Cell"] = "Клетка", ["TargetingMode.Unit"] = "Юнит",
        ["HitMode.Roll"] = "Бросок точности", ["HitMode.AlwaysHit"] = "Всегда попадает",
        ["SkillVfx.Slash"] = "Удар", ["SkillVfx.Projectile"] = "Снаряд", ["SkillVfx.Burst"] = "Вспышка", ["SkillVfx.Beam"] = "Луч", ["SkillVfx.Glow"] = "Свечение",
        ["AreaShape.Single"] = "Одна клетка", ["AreaShape.Line"] = "Линия", ["AreaShape.Cone"] = "Конус", ["AreaShape.Sector"] = "Сектор",
        ["AreaShape.Radius"] = "Радиус", ["AreaShape.Ring"] = "Кольцо", ["AreaShape.Cross"] = "Крест", ["AreaShape.Chain"] = "Цепь",
        ["AreaShape.Mask"] = "Маска (вручную)", ["AreaShape.Global"] = "Глобально",
        ["EffectType.Damage"] = "Урон", ["EffectType.Heal"] = "Лечение", ["EffectType.ApplyStatus"] = "Наложить состояние", ["EffectType.RemoveStatus"] = "Снять состояние",
        ["EffectType.Push"] = "Оттолкнуть", ["EffectType.Pull"] = "Притянуть", ["EffectType.ChangeFacing"] = "Изменить направление",
        ["EffectType.ModifyResource"] = "Изменить ресурс", ["EffectType.ModifyAp"] = "Изменить ОД", ["EffectType.ChangeTerrain"] = "Изменить местность",
        ["EffectType.AddCellEffect"] = "Эффект клетки", ["EffectType.RemoveCellEffect"] = "Убрать эффект клетки", ["EffectType.DestroyObstacle"] = "Разрушить препятствие",
        ["EffectType.Summon"] = "Призыв", ["EffectType.TeleportCaster"] = "Переместить исполнителя",
        ["FacingMode.TowardSource"] = "К источнику", ["FacingMode.AwayFromSource"] = "От источника", ["FacingMode.Opposite"] = "Развернуть",
        ["FacingMode.Random"] = "Случайно", ["FacingMode.Set"] = "Задать",
        ["HexDirection.E"] = "Восток (В)", ["HexDirection.NE"] = "Северо-восток (СВ)", ["HexDirection.NW"] = "Северо-запад (СЗ)",
        ["HexDirection.W"] = "Запад (З)", ["HexDirection.SW"] = "Юго-запад (ЮЗ)", ["HexDirection.SE"] = "Юго-восток (ЮВ)",
        ["CellEffectVisual.Glow"] = "Свечение", ["CellEffectVisual.Flames"] = "Пламя", ["CellEffectVisual.Gas"] = "Газ", ["CellEffectVisual.Ice"] = "Лёд",
        ["CellEffectVisual.Sparks"] = "Искры", ["CellEffectVisual.Liquid"] = "Жидкость", ["CellEffectVisual.Smoke"] = "Дым", ["CellEffectVisual.Runes"] = "Руны",
        ["CellEffectVisual.Barrier"] = "Барьер",
        ["CellStackPolicy.Refresh"] = "Обновить длительность", ["CellStackPolicy.AddLevel"] = "Повысить уровень", ["CellStackPolicy.AddDuration"] = "Сложить длительность",
        ["CellStackPolicy.Ignore"] = "Игнорировать", ["CellStackPolicy.Replace"] = "Заменить",
        ["SynergyOperandKind.CellEffect"] = "Эффект клетки", ["SynergyOperandKind.CellEffectTag"] = "Тег эффекта", ["SynergyOperandKind.TerrainTag"] = "Тег местности",
        ["StatusKind.Timed"] = "Временное", ["StatusKind.Accumulating"] = "Накопительное",
        ["ReapplyPolicy.Refresh"] = "Обновить длительность", ["ReapplyPolicy.Extend"] = "Продлить", ["ReapplyPolicy.Stack"] = "Складывать экземпляры",
        ["ReapplyPolicy.Intensify"] = "Усилить до лимита", ["ReapplyPolicy.Ignore"] = "Игнорировать", ["ReapplyPolicy.ReplaceIfStronger"] = "Заменить более слабое",
        ["AttackSector.Front"] = "Фронт", ["AttackSector.NearFlank"] = "Ближний фланг", ["AttackSector.FarFlank"] = "Дальний фланг", ["AttackSector.Rear"] = "Тыл",
        ["FailureAction.Skip"] = "Пропустить", ["FailureAction.CancelQueue"] = "Отменить очередь", ["FailureAction.Wait"] = "Ждать", ["FailureAction.Repath"] = "Пересчитать путь",
        ["MarkerType.PlayerStart"] = "Старт игрока", ["MarkerType.EnemySpawn"] = "Появление врагов", ["MarkerType.NeutralSpawn"] = "Появление нейтралов",
        ["MarkerType.Objective"] = "Цель", ["MarkerType.Trigger"] = "Триггер", ["MarkerType.Exit"] = "Выход",
        ["SideController.Player"] = "Игрок", ["SideController.Ai"] = "ИИ",
        ["ConditionType.AllEnemiesDead"] = "Все враги уничтожены", ["ConditionType.AllPlayerUnitsDead"] = "Все юниты игрока погибли",
        ["ConditionType.SurviveTime"] = "Продержаться", ["ConditionType.ReachMarker"] = "Дойти до метки", ["ConditionType.UnitDead"] = "Юнит погиб",
        ["ConditionType.UnitAlive"] = "Юнит жив", ["ConditionType.KillCount"] = "Число убийств", ["ConditionType.TimeElapsed"] = "Прошло время",
        ["ConditionType.WaveSpawned"] = "Началась волна",
        ["TriggerActionType.Message"] = "Сообщение", ["TriggerActionType.SpawnWave"] = "Запустить волну", ["TriggerActionType.Autopause"] = "Автопауза",
        ["TriggerActionType.Victory"] = "Победа", ["TriggerActionType.Defeat"] = "Поражение", ["TriggerActionType.AddCellEffect"] = "Эффект клетки у метки",
        ["Relation.Ally"] = "Союзник", ["Relation.Enemy"] = "Враг", ["Relation.Neutral"] = "Нейтрал",
        ["CommandStatus.Pending"] = "в очереди", ["CommandStatus.Active"] = "выполняется", ["CommandStatus.Waiting"] = "ждёт",
        ["CommandStatus.Done"] = "выполнено", ["CommandStatus.Skipped"] = "пропущено", ["CommandStatus.Failed"] = "не выполнено", ["CommandStatus.Cancelled"] = "отменено",
    };

    public static string Of(Enum e) => Of(e.GetType(), e.ToString());

    /// <summary>Display name: the table above, else the [Ed] label on the enum member (core enums are self-describing).</summary>
    public static string Of(Type enumType, string member)
    {
        var key = enumType.Name + "." + member;
        if (Map.TryGetValue(key, out var v)) return v;
        var field = enumType.GetField(member);
        if (field != null && Attribute.GetCustomAttribute(field, typeof(HexTactics.Core.Content.EdAttribute)) is HexTactics.Core.Content.EdAttribute ed) return ed.Label;
        return member;
    }
}
