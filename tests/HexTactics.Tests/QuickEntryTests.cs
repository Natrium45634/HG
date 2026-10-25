using System.Linq;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>
/// «Быстрый ввод со связями» (docs/DECISIONS_5.md §77): search over the draft (endings, synonyms, the other keyboard
/// layout, new objects) and rules written in words, linked to what the draft already has.
/// </summary>
public class QuickEntryTests
{
    /// <summary>The shipped content plus what an author has just added to the draft: the holy element and its damage type.</summary>
    private static ContentDatabase WithHoly(out ContentDatabase published)
    {
        published = TestUtil.Shipped();
        var db = published.Clone();
        db.Add(new ElementDef { Id = "holy", Name = "Святость", Aliases = { "святой", "holy" } });
        db.Add(new DamageTypeDef { Id = "holy_dmg", Name = "Святой урон", Element = "holy", Kind = DamageCategory.Magical });
        return db;
    }

    [Fact]
    public void Stemmer_and_normalisation()
    {
        Assert.Equal("свят", RuText.Stem("святой"));
        Assert.Equal("свят", RuText.Stem("святая"));
        Assert.Equal("свят", RuText.Stem("святости"));
        Assert.Equal("свят", RuText.Stem("святость"));
        Assert.Equal("нежит", RuText.Stem("нежити"));
        Assert.Equal("нежит", RuText.Stem("нежить"));
        Assert.Equal("яд", RuText.Stem("яду"));
        Assert.Equal("fire", RuText.Stem("fire"));
        Assert.Equal("еж ежик", RuText.Norm("Ёж — ёжик!"));
        Assert.Equal("свет", RuText.SwapLayout("cdtn"));
        Assert.Equal("svyataya_magiya", RuText.ToId("Святая магия"));
        Assert.Equal(1, RuText.Distance("огон", "огн", 2));
    }

    [Fact]
    public void Search_finds_new_draft_objects_by_any_form_and_marks_them()
    {
        var db = WithHoly(out var pub);
        var index = ContentSearch.Build(db, pub);
        foreach (var q in new[] { "свят", "святой", "святости", "СВЯТОСТЬ", "holy", "cdznjq" })
        {
            var hits = index.Find(q, e => e.Category is DefCategory.Element or DefCategory.DamageType);
            Assert.True(hits.Count > 0, q);
            Assert.Contains(hits.Take(2), h => h.Entry.Value == "holy");
        }
        var holy = index.Get(DefCategory.Element, "holy")!;
        Assert.Equal(SearchState.New, holy.State);
        Assert.Contains("новое", holy.Label);
        // Published and untouched objects are not marked; an edited one is.
        Assert.Equal(SearchState.Published, index.Get(DefCategory.Element, "fire")!.State);
        db.Element("fire")!.Description = "изменено";
        Assert.Equal(SearchState.Changed, ContentSearch.Build(db, pub).Get(DefCategory.Element, "fire")!.State);
    }

    [Fact]
    public void Search_ranks_exact_names_first_and_tolerates_typos()
    {
        var db = TestUtil.Shipped();
        var index = ContentSearch.Build(db);
        Assert.Equal("armor", index.Find("броня", e => e.Category == DefCategory.Stat)[0].Entry.Value);
        Assert.Equal("undead", index.Find("нежити", e => e.Category == DefCategory.Origin)[0].Entry.Value);
        Assert.Equal("evasion", index.Find("уклонене", e => e.Category == DefCategory.Stat)[0].Entry.Value); // a typo
        Assert.Equal("hp_max", index.Find("здоровья", e => e.Category == DefCategory.Stat)[0].Entry.Value);
        Assert.Contains(index.Find("огн"), h => h.Entry.Category == DefCategory.DamageType && h.Entry.Value == "fire");
        // Tags and damage categories are searchable values, not definitions.
        Assert.Contains(index.Entries, e => e.Value == "cat:Magical" && e.Kind == "категория урона");
    }

    [Fact]
    public void Undead_holy_vulnerability_links_the_element_added_to_the_draft()
    {
        // «святой» is also a built-in synonym of «Свет»: the element the author has just added wins.
        var db = WithHoly(out var pub);
        var index = ContentSearch.Build(db, pub);
        foreach (var text in new[] { "+25% входящего урона от святой магии", "уязвимость к святой магии 25%", "получает на 25 % больше урона от святости" })
        {
            var r = Assert.Single(QuickRuleParser.Parse(db, text, index));
            Assert.True(r.Ok, text + ": " + r.Error + string.Join(",", r.Missing.Select(m => m.Term)));
            var m = r.Modifier!;
            Assert.Equal(ModifierTarget.IncomingDamage, m.Target);
            Assert.Equal("el:holy", m.Match);
            Assert.Equal(ModifierMode.Percent, m.Mode);
            Assert.Equal(25, m.Value);
            Assert.Contains(r.Links, l => l.Entry.Value == "holy" && l.Entry.Category == DefCategory.Element);
            Assert.DoesNotContain(r.Links, l => l.Entry.Value == "magic"); // «магии» is not the magic damage type here
        }
        // The damage type of the element is linked when named.
        var t = Assert.Single(QuickRuleParser.Parse(db, "−20% святого урона", index));
        Assert.Equal("holy_dmg", t.Modifier!.Match);
    }

    [Fact]
    public void Missing_object_is_reported_and_created_linked()
    {
        var db = TestUtil.Shipped();
        var r = Assert.Single(QuickRuleParser.Parse(db, "+25% входящего урона от лунной магии"));
        Assert.False(r.Ok);
        var miss = Assert.Single(r.Missing);
        Assert.Equal("лунной магии", miss.Term);
        Assert.Equal("Лунная магия", miss.SuggestedName);
        Assert.Equal(DefCategory.Element, miss.CreateAs[0]);
        Assert.Contains(DefCategory.DamageType, miss.CreateAs);

        // «элемент и тип урона»: both created, the type linked to the element.
        var type = (DamageTypeDef)QuickRuleParser.Create(db, DefCategory.DamageType, miss.SuggestedName, withElement: true);
        var el = db.Element(type.Element)!;
        Assert.Equal("Лунная магия", el.Name);
        Assert.Equal("lunnaya_magiya", el.Id);
        var again = Assert.Single(QuickRuleParser.Parse(db, "+25% входящего урона от лунной магии"));
        Assert.True(again.Ok, again.Error);
        Assert.True(again.Modifier!.Match == "el:" + el.Id || again.Modifier.Match == type.Id);
    }

    [Theory]
    [InlineData("броня +10", ModifierTarget.Stat, "armor", ModifierMode.FlatBefore, 10)]
    [InlineData("+10% здоровья", ModifierTarget.Stat, "hp_max", ModifierMode.Percent, 10)]
    [InlineData("скорость -15%", ModifierTarget.Stat, "move_speed", ModifierMode.Percent, -15)]
    [InlineData("уклонение +10%", ModifierTarget.Stat, "evasion", ModifierMode.FlatBefore, 0.1)]
    [InlineData("сопротивление огню +30%", ModifierTarget.Stat, "resist_fire", ModifierMode.FlatBefore, 0.3)]
    [InlineData("максимум маны +20", ModifierTarget.ResourceMax, "mana", ModifierMode.FlatBefore, 20)]
    [InlineData("восстановление энергии +10%", ModifierTarget.ResourceRegen, "energy", ModifierMode.Percent, 10)]
    [InlineData("навыки дешевле на 20%", ModifierTarget.SkillCost, "", ModifierMode.Percent, -20)]
    [InlineData("+2 удара", ModifierTarget.HitCount, "", ModifierMode.Percent, 2)]
    [InlineData("отражает 10% урона", ModifierTarget.Reflection, "*", ModifierMode.Percent, 10)]
    [InlineData("поглощает 30% огненного урона", ModifierTarget.Absorption, "fire", ModifierMode.Percent, 30)]
    [InlineData("на 75 % меньше урона от огня", ModifierTarget.IncomingDamage, "el:fire", ModifierMode.Percent, -75)]
    [InlineData("×1,25 входящего урона от света", ModifierTarget.IncomingDamage, "el:light", ModifierMode.Multiplier, 1.25)]
    [InlineData("наносит +15% урона ядом", ModifierTarget.OutgoingDamage, "el:poison", ModifierMode.Percent, 15)]
    [InlineData("движение по лаве дешевле на 50%", ModifierTarget.TerrainCost, "lava", ModifierMode.Percent, -50)]
    public void Phrases_become_modifiers(string text, ModifierTarget target, string what, ModifierMode mode, double value)
    {
        var db = TestUtil.Shipped();
        var r = Assert.Single(QuickRuleParser.Parse(db, text));
        Assert.True(r.Ok, $"{text}: {r.Error} {string.Join(",", r.Missing.Select(m => m.Term))}");
        var m = r.Modifier!;
        Assert.Equal(target, m.Target);
        string got = target switch
        {
            ModifierTarget.Stat => m.Stat,
            ModifierTarget.ResourceMax or ModifierTarget.ResourceRegen => m.Resource,
            ModifierTarget.TerrainCost => m.Terrain,
            ModifierTarget.SkillCost or ModifierTarget.HitCount => "",
            _ => m.Match,
        };
        Assert.Equal(what, got);
        Assert.Equal(mode, m.Mode);
        Assert.Equal(value, m.Value, 6);
    }

    [Fact]
    public void Shipped_synonyms_link_holy_magic_to_light_and_irregular_forms()
    {
        var db = TestUtil.Shipped();
        var holy = Assert.Single(QuickRuleParser.Parse(db, "+25% входящего урона от святой магии"));
        Assert.True(holy.Ok);
        Assert.Equal("el:light", holy.Modifier!.Match);
        Assert.Equal("el:fire", Assert.Single(QuickRuleParser.Parse(db, "−30% урона от огня")).Modifier!.Match);
        Assert.Equal("el:ice", Assert.Single(QuickRuleParser.Parse(db, "+20% урона от льда")).Modifier!.Match);
        Assert.Equal("hp_max", Assert.Single(QuickRuleParser.Parse(db, "хп +50")).Modifier!.Stat);
    }

    [Fact]
    public void Immunities_and_creature_targets()
    {
        var db = TestUtil.Shipped();
        var poison = Assert.Single(QuickRuleParser.Parse(db, "иммунитет к яду")).Modifier!;
        Assert.Equal(ModifierTarget.Immunity, poison.Target);
        Assert.Equal("el:poison", poison.Match);
        var stun = Assert.Single(QuickRuleParser.Parse(db, "иммунитет к оглушению")).Modifier!;
        Assert.Equal("stunned", stun.Status);
        // A damage type and a status of the same name: an immunity takes the status.
        var bleed = Assert.Single(QuickRuleParser.Parse(db, "невосприимчив к кровотечению")).Modifier!;
        Assert.Equal("bleeding", bleed.Status);

        var vs = Assert.Single(QuickRuleParser.Parse(db, "+15% урона по нежити"));
        Assert.True(vs.Ok);
        Assert.Equal(ModifierTarget.OutgoingDamage, vs.Modifier!.Target);
        Assert.Equal(new[] { "undead" }, vs.Modifier.VsTypes);
        Assert.Contains(vs.Links, l => l.Role == "по существам");
    }

    [Fact]
    public void Several_rules_in_one_line_and_errors()
    {
        var db = TestUtil.Shipped();
        var many = QuickRuleParser.Parse(db, "броня +10 и +5% скорости; иммунитет к яду");
        Assert.Equal(3, many.Count);
        Assert.Equal("armor", many[0].Modifier!.Stat);
        Assert.Equal("move_speed", many[1].Modifier!.Stat);
        Assert.Equal(ModifierTarget.Immunity, many[2].Modifier!.Target);

        var noValue = Assert.Single(QuickRuleParser.Parse(db, "урон от огня"));
        Assert.False(noValue.Ok);
        Assert.Contains("величина", noValue.Error);
        var nonsense = Assert.Single(QuickRuleParser.Parse(db, "+5 шмырдяков"));
        Assert.False(nonsense.Ok);
        Assert.NotNull(nonsense.Error);
    }

    [Fact]
    public void Texts_of_the_form_parse_back_to_the_same_modifiers()
    {
        // What the passive shows the player («Получает на 75 % меньше урона стихии «Вода»») is valid quick input.
        var db = TestUtil.Shipped();
        var mods = new[]
        {
            new ModifierDef { Target = ModifierTarget.IncomingDamage, Match = "el:water", Value = -75 },
            new ModifierDef { Target = ModifierTarget.OutgoingDamage, Match = "fire", Value = 20 },
            new ModifierDef { Target = ModifierTarget.Stat, Stat = "armor", Mode = ModifierMode.FlatBefore, Value = 5 },
            new ModifierDef { Target = ModifierTarget.Stat, Stat = "evasion", Mode = ModifierMode.FlatBefore, Value = 0.1 },
            new ModifierDef { Target = ModifierTarget.Stat, Stat = "hp_max", Value = 15 },
            new ModifierDef { Target = ModifierTarget.Immunity, Match = "el:poison" },
            new ModifierDef { Target = ModifierTarget.Immunity, Status = "stunned" },
        };
        foreach (var m in mods)
        {
            string text = m.Describe(db);
            var r = Assert.Single(QuickRuleParser.Parse(db, text));
            Assert.True(r.Ok, text + ": " + r.Error);
            Assert.Equal(m.Target, r.Modifier!.Target);
            Assert.Equal(m.Match, r.Modifier.Match);
            Assert.Equal(m.Stat, r.Modifier.Stat);
            Assert.Equal(m.Status, r.Modifier.Status);
            if (m.Target != ModifierTarget.Immunity)
            {
                Assert.Equal(m.Mode, r.Modifier.Mode);
                Assert.Equal(m.Value, r.Modifier.Value, 6);
            }
        }
    }

    [Fact]
    public void Empty_aliases_are_not_written_and_aliases_round_trip()
    {
        var e = new ElementDef { Id = "x", Name = "X" };
        Assert.DoesNotContain("aliases", ContentJson.Serialize(e));
        e.Aliases.Add("икс");
        var json = ContentJson.Serialize(e);
        Assert.Contains("aliases", json);
        Assert.Equal(new[] { "икс" }, ContentJson.Deserialize<ElementDef>(json)!.Aliases);
    }
}
