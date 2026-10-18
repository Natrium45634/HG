using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>
/// Definitions the engine relies on that older packs may lack (backward compatibility,
/// docs/DECISIONS_2.md §35): they are added in memory with an informational note.
/// </summary>
public static class ContentDefaults
{
    public static IEnumerable<LayerDef> Layers()
    {
        yield return new LayerDef
        {
            Id = Content.Layers.Ground, Name = "Наземный", Description = "Обычное положение: стоят на клетке, сталкиваются, блокируются", Blockable = true,
        };
        yield return new LayerDef
        {
            Id = Content.Layers.Air, Name = "Воздушный", Description = "Летят над полем: игнорируют стоимость местности, высоту и ямы, пролетают над низкими стенами",
            DisplayHeight = 3.2, IgnoresTerrainCost = true, IgnoresHeight = true, PassesObstaclesUpTo = 4, ImmuneToPits = true, Blockable = false,
        };
        yield return new LayerDef
        {
            Id = Content.Layers.Underground, Name = "Подземный", Description = "Под поверхностью: проходят под препятствиями, видны только с обнаружением слоя или вплотную",
            DisplayHeight = -0.9, Translucent = true, IgnoresTerrainCost = true, IgnoresHeight = true, PassesObstaclesUpTo = 100, ImmuneToPits = true,
            Hidden = true, DetectionStat = "tremor_sense", RevealRange = 1, Blockable = false,
        };
    }

    public static StatDef TremorSense() => new()
    {
        Id = "tremor_sense", Name = "Обнаружение подземных", Description = "> 0 — видит существ подземного слоя в пределах дальности обнаружения", Max = 10, ShowInPanel = false,
    };

    public static StatDef BlockLimit() => new()
    {
        Id = Stats.BlockLimit, Name = "Лимит блокирования", Description = "Сколько слотов врагов юнит может удерживать (0 — не блокирует). Не путать с шансом блока удара",
        Max = 20, ShowInPanel = true,
    };

    public static void Ensure(ContentDatabase db, List<ContentIssue> issues)
    {
        if (db.Table(DefCategory.Layer).Count == 0)
        {
            foreach (var l in Layers()) db.Add(l);
            issues.Add(new ContentIssue(IssueSeverity.Info, "layers.json", "", "Слои не заданы — добавлены встроенные: наземный, воздушный, подземный"));
        }
        if (db.Stat("tremor_sense") == null && db.All<LayerDef>().Any(l => l.DetectionStat == "tremor_sense"))
        {
            db.Add(TremorSense());
            issues.Add(new ContentIssue(IssueSeverity.Info, "stats.json", "", "Добавлена характеристика «Обнаружение подземных» (нужна подземному слою)"));
        }
        if (db.Stat(Stats.BlockLimit) == null)
        {
            db.Add(BlockLimit());
            issues.Add(new ContentIssue(IssueSeverity.Info, "stats.json", "", "Добавлена характеристика «Лимит блокирования» (по умолчанию 0)"));
        }
    }
}
