# Формат данных

Весь контент игры — набор JSON-файлов в UTF-8. Встроенный пакет лежит в
`game/content/`, пользовательский (после сохранения в редакторе) — в папке
данных пользователя (`%APPDATA%\Godot\app_userdata\HexTactics\content\` на
Windows). Структура обоих одинакова. Поля всех объектов описаны в коде
(`src/HexTactics.Core/Content/Defs/*.cs`): у каждого свойства есть атрибут
`[Ed("Подпись", "Подсказка")]`, по которому редактор строит форму, —
это и есть справочник полей.

## 1. Файлы пакета

| Файл | `kind` | Содержимое |
|---|---|---|
| `rules.json` | `rules` | правила боя (один объект `rules`) |
| `stats.json` | `stats` | характеристики (HP, ОД, точность, броня…) |
| `resources.json` | `resources` | ресурсы (мана, ярость…) |
| `damage_types.json` | `damage_types` | типы урона |
| `factions.json` | `factions` | фракции и отношения |
| `terrains.json` | `terrains` | типы местности и препятствия |
| `cell_effects.json` | `cell_effects` | эффекты клеток |
| `synergies.json` | `synergies` | синергии эффектов клеток |
| `statuses.json` | `statuses` | состояния юнитов |
| `skills.json` | `skills` | навыки |
| `passives.json` | `passives` | пассивные способности |
| `ai_profiles.json` | `ai_profiles` | профили ИИ |
| `units.json` | `units` | юниты и враги (различаются фракцией) |
| `maps/<id>.json` | `map` | одна карта на файл |
| `scenarios/<id>.json` | `scenario` | один сценарий на файл |

Признак пакета — наличие `rules.json`. Отсутствующий файл категории даёт
предупреждение, категория остаётся пустой.

## 2. Обёртка файла

Файл категории:

```json
{
  "formatVersion": 1,
  "kind": "statuses",
  "items": [ { "id": "poison", "name": "Отравление", ... } ]
}
```

Карта / сценарий / правила — один объект под ключом вида:

```json
{ "formatVersion": 1, "kind": "map", "map": { "id": "arena", ... } }
```

Экспорт одного объекта из редактора («Экспорт…», импортируется кнопкой
«Импорт…»):

```json
{ "formatVersion": 1, "kind": "def", "category": "Skill", "item": { ... } }
```

* `formatVersion` — версия формата файла (текущая — **1**).
* `kind` — вид файла; несовпадение с ожидаемым — предупреждение.
* Имена полей — `camelCase`, перечисления — строками (`"Radius"`,
  `"TowardSource"`), координаты гексов — массив `[q, r]` (axial,
  pointy-top), направления — `E, NE, NW, W, SW, SE`, цвета — `"#rrggbb"`.
* Поля, которых нет в файле, получают значения по умолчанию; неизвестные
  поля (например, из более новой версии программы) **сохраняются как
  есть** на любом уровне вложенности и записываются обратно; проверка
  называет каждое такое поле ([DECISIONS_5.md §84](DECISIONS_5.md)).
* Формальные схемы файлов — [docs/schema](schema) (JSON Schema 2020-12,
  генерируются из определений).

## 3. Общие поля объектов

| Поле | Тип | Правило |
|---|---|---|
| `id` | строка | уникален в своей категории, символы `A–Z a–z 0–9 _ . -` |
| `name` | строка | отображаемое имя (если пусто — показывается `id`) |
| `description` | строка | описание/подсказка |
| `tags` | массив строк | теги для синергий, ограничений местности, ИИ |
| `group` | строка | категория в библиотеке (поиск и фильтры) |
| `aliases` | массив строк | необязательно: синонимы для поиска и быстрого ввода (`["святой", "holy"]`); пустой список в файл не пишется, поэтому версия формата не меняется ([DECISIONS_5.md §77](DECISIONS_5.md)) |

Ссылки на другие объекты — по `id` (например, `"damageType": "fire"`,
`"basicAttack": "strike"`). Редактор при переименовании обновляет все
ссылки и показывает зависимости перед удалением.

## 4. Категории (основные поля)

* **Местность** (`terrains`): `moveCost`, `passableFor` /
  `moveCostByCategory` (по категориям передвижения), `defaultHeight`,
  `blocksLoS`, `obstacleHeight`, `cover`, `allowsPlacement`, `hazard`,
  `destructibleHp` → `destroyedTerrain`, `intrinsicEffects` (постоянные
  эффекты, например вода «мокрая»), `color`, `visual`.
* **Эффекты клеток** (`cell_effects`): `duration`, `tickInterval`,
  `stackPolicy` (`Refresh / AddLevel / AddDuration / Ignore / Replace`),
  `maxLevel`, `onTick` и `onEnter` — списки эффектов (как у навыков),
  `allowedTerrainTags` / `forbiddenTerrainTags`, `immuneCreatureCategories`,
  `moveCostAdd`, `blocksMovement`, `blocksLoS`, `noticeModifier`, `spread`
  (интервал, шанс, теги местности, предел поколений), `visual`, `color`.
  Эффект накладывается поверх местности и не заменяет её.
* **Синергии** (`synergies`): операнды `a` и `b` (`kind`: `CellEffect /
  CellEffectTag / TerrainTag`, `value`), `removeA`, `removeB`,
  `addEffects`, `convertTerrain`, `radius`, `unitEffects`,
  `radiusCellEffects`, `cooldownPerCell`, `logText`.
* **Состояния** (`statuses`): `kind` (`Timed / Accumulating`), `duration`,
  `reapply` (`Refresh / Extend / Stack / Intensify / Ignore /
  ReplaceIfStronger`), `maxStacks`, `maxPoints`, `decayPerSecond`,
  `levels[]` (порог, модификаторы характеристик, флаги контроля
  `blocksMovement / blocksSkills / … / interruptsCast`, периодические
  эффекты `onTick`).
* **Навыки** (`skills`): `kind`, `apCost`, `resourceCosts`, `targeting`
  (`Self / Direction / Cell / Unit`), `rangeMin`, `rangeMax`,
  `requiresLoS`, `validTargets`, `hitMode`, `prepTime`, `recoveryTime`,
  `cooldown`, `interrupt` (условия и возврат ОД), `telegraph`,
  `stages[]`.
  * Этап: `delay`, `area`, `effects[]`, `maxTargets`,
    `blockedByObstacles`, `pierce`.
  * Область: `shape` (`Single / Line / Cone / Sector / Radius / Ring /
    Cross / Chain / Mask / Global`), `size`, `innerRadius`, `angle`,
    `chainJumps`, `chainRange`, `includeCenter`, `cells[]` — для маски:
    смещения `{ "offset": [q, r], "effects": [...] }` относительно центра при
    направлении «восток»; при применении маска поворачивается на 60°·k.
  * Эффект: `type` (`Damage, Heal, ApplyStatus, RemoveStatus, Push, Pull,
    ChangeFacing, ModifyResource, ModifyAp, ChangeTerrain, AddCellEffect,
    RemoveCellEffect, DestroyObstacle, Summon, TeleportCaster`), `filter`
    (враги/союзники/нейтралы/сам), `chance`, `requiresHit`, `amount`,
    `damageType`, `scalingStat` × `scalingFactor`, `formula`, `status`,
    `cellEffect`, `distance`, `facing` и т. д. — показываются только
    поля, относящиеся к выбранному типу.
* **Пассивки** (`passives`): `trigger` (`Always, OnDamaged,
  OnAttackNoticed, OnHitEnemy, OnKill, OnAllyDied, OnLowHp, Periodic,
  OnEnemySpotted`), `statMods`, `chance`, `cooldown`, `action`,
  `effects`, `skill`.
* **Юниты** (`units`): `faction`, `creatureCategory`, `moveCategory`,
  `stats` (словарь `id характеристики → значение`), `resistances`
  (`тип урона → доля`, отрицательное — уязвимость), `statusResistances`,
  `immunities`, `resources`, `basicAttack`, `skills`, `passives`,
  `startingStatuses`, `aiProfile`, `visual` (форма фишки, цвет, размер).
* **Профили ИИ** (`ai_profiles`): `aggression`, `targetPriority`,
  `preferredRange`, `keepDistance`, `retreatHpFraction`, `holdPosition`,
  `holdRadius`, `pursue`, `support`, `hazardAvoidance`,
  `decisionInterval`, `faceThreats`, `alert`, `advanceWhenIdle`.
* **Фракции** (`factions`): `color`, `shape` основания фишки,
  `hostileTo`, `alliedWith`.
* **Характеристики / ресурсы / типы урона**: значение по умолчанию,
  пределы, формат; максимум и восстановление ресурса; применимость брони.

## 5. Карты

```json
{
  "id": "arena", "name": "Арена",
  "rules": { "darkness": 0 },
  "cells": [ { "c": [0, 0], "t": "stone", "h": 1 }, ... ],
  "markers": [ { "id": "west", "type": "PlayerStart", "c": [-6, 3], "radius": 2 } ]
}
```

Клетка: `c` — координата, `t` — местность, `h` — уровень высоты. Карта
может иметь любую форму: существуют только перечисленные клетки.
Необязательные переопределения клетки: `moveCost`, `blocked`, `blocksLoS`,
`cover`, `allowPlacement`, `obstacleHp`, `allowedEffects`, `effects`
(стартовые эффекты клеток), `tags`, `name`, `description`. Метки: `PlayerStart, EnemySpawn, NeutralSpawn, Objective,
Trigger, Exit`.

## 6. Сценарии

`map`, `seed`, `intro`, `sides[]` (фракция + `Player`/`Ai`), `units[]`
(юнит, клетка, направление, фракция/профиль ИИ при переопределении,
`tag`), `waves[]` (время начала, метка появления, фракция, юниты, сообщение), `victory[]` и
`defeat[]` (`AllEnemiesDead, AllPlayerUnitsDead, SurviveTime,
ReachMarker, UnitDead, UnitAlive, KillCount, TimeElapsed, WaveSpawned`),
`triggers[]` (условие → `Message, SpawnWave, Autopause, Victory, Defeat,
AddCellEffect`), `limits` (`fogOfWar`, `timeLimit`, `allowStacking`).

Часть 5 ([DECISIONS_5.md §86](DECISIONS_5.md)): `mode` — `Normal,
TdPlacement, FreeBattle, AutoTest, TowerDefense, Assault, Mixed, Escort,
Sandbox`; у маршрута `strictness` (`Corridor, StrictHexes, Waypoints`),
`tolerance`, `onBlocked[]` (`Wait, LocalDetour, Repath, AttackBlocker,
SwitchRoute, ChangeObjective, Abort`), `weight`; у группы `hunt` и
`routeSelect` (`Single, RoundRobin, Random, NearestBase, ShortestRoute,
LowestHp, Weighted`); у профиля ИИ `routeOnly`; в правилах `stuck`
(`seconds`, `yieldSeconds`). Реплей боя (`.hexreplay`) — JSON:
`scenario, seed, simVersion, versions{}, contentHash, content{}?,
startHash, endTick, endHash, entries[]` (тик, действие игрока, его
параметры).

Часть 5, этап D ([§87](DECISIONS_5.md)): у категории `extraParents[]`; у
пассива `stackGroup`, `groupRule` (`Highest, Latest, First, All`),
`stackRank`; у профиля ИИ `temperament` (`Aggressive,
PassiveUntilAttacked`) и `retaliation` (`Attacker, Everyone, Flee, Ignore`);
запрет категории `InitiateAttack`; в правилах `move.knockbackFormula`
(`impulse, mass, reference, resist, cost`), `move.collision` (`Stop,
ChainPush, ImpactDamage, DisplaceIfLegal`), `impactDamage`, `chainDepth`.

Часть 5, этап E ([§88](DECISIONS_5.md)): у шаблона `build` (`mode` —
`Manual, ScaleStats, AutoBuild`; `targetLevel, computedLevel, seed,
profile, profileVersion, ratingHash, choices[]`); у профиля
масштабирования `upgrades[]` (`kind` — `Stat, Skill, Passive`; `stat,
step, maxPicks, skill, passive, weight, minLevel`); у пассива
`auraMaxTargets`.

## 7. Правила боя и формулы

`rules.json` разбит на группы: `time`, `ap`, `turn`, `move`, `height`,
`detection` (секторы: шанс заметить / неожиданности), `combat`, `vision`,
`queue` (политики при неудаче по причинам), `limits`, `autopause`,
`death`. Значения по умолчанию и смысл — в [DECISIONS.md](DECISIONS.md).

Формулы (`combat.hitFormula`, `combat.damageFormula`, `formula` в
эффектах) — безопасные выражения: числа, `+ - * / ^`, скобки, сравнения
`< <= > >= == !=`, функции `min max clamp abs floor ceil round sqrt if`
и переменные только из белого списка поля:

| Поле | Переменные |
|---|---|
| `hitFormula` | `base accuracy evasion height surprise cover distance` |
| `damageFormula` | `raw armor resist crit surprise block` |
| `formula` эффекта | `amount level distance caster_power caster_hp caster_hp_max target_hp target_hp_max target_armor` |

Пустая формула — встроенный расчёт. Неизвестная переменная или функция —
ошибка проверки, код не выполняется.

## 8. Версии и миграции

* При загрузке каждый файл проходит `ContentMigrator`: версия N → N+1 до
  текущей. Файл без `formatVersion` считается версией 0 (прототип):
  поля `hp/speed/ap` юнита переносятся в `stats`, клетки `q/r/terrain/height`
  → `c/t/h`, карты и сценарии без обёртки оборачиваются. Каждое
  преобразование записывается в отчёт загрузки (уровень «инфо»).
* Миграция выполняется в памяти; файл на диске перезаписывается только
  при сохранении из редактора (с резервной копией).
* Файл более новой версии не загружается и не изменяется — ошибка с
  указанием версий.

## 9. Проверка и ошибки

* Объекты читаются по одному: повреждённый элемент даёт ошибку с путём
  (`skills.json items[3]`), остальные загружаются.
* Проверяются типы, диапазоны (`EdRange`), уникальность `id`, ссылки,
  формулы, карты (стартовые точки на проходимых клетках, достижимость
  целей, запертые области) и сценарии.
* Навык с ошибками отключается (виден в редакторе и в отчёте), игра не
  падает. Ничего не исправляется молча: всё попадает в отчёт «Проверка».

## 10. Сохранение

* Редактор работает с черновиком; «Сохранить» публикует его в
  пользовательский пакет, предварительно копируя прежние файлы в
  `content/backups/<yyyyMMdd_HHmmss>/`.
* Черновик автоматически сохраняется в `autosave/` и предлагается к
  восстановлению при следующем входе в редактор.
* Запись — канонический компактный формат (короткие объекты и массивы
  чисел — в одну строку), чтобы файлы было удобно сравнивать в git.
