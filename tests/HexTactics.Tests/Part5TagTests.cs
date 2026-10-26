using System.Linq;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage D — categories as a graph without cycles (ТЗ 13.2, T05; docs/DECISIONS_5.md §87).</summary>
public class Part5TagTests
{
    private static ContentDatabase Tree()
    {
        var db = TestUtil.Minimal();
        db.Add(new OriginDef { Id = "creature", Name = "Существо", Kind = OriginKind.Custom, Rules = { new CategoryRuleDef { Id = "tag_c", Kind = CategoryRuleKind.Tag, Tag = "creature" } } });
        db.Add(new OriginDef { Id = "beast", Name = "Зверь", Kind = OriginKind.Species, Parent = "creature", Rules = { new CategoryRuleDef { Id = "tag_b", Kind = CategoryRuleKind.Tag, Tag = "beast" } } });
        db.Add(new OriginDef
        {
            Id = "wolf", Name = "Волк", Kind = OriginKind.Species, Parent = "beast",
            Rules = { new CategoryRuleDef { Id = "move", Kind = CategoryRuleKind.MoveCategory, MoveCategory = "walk" } },
        });
        db.Add(new OriginDef
        {
            Id = "pack_leader", Name = "Вожак стаи", Kind = OriginKind.Role, Parent = "creature",
            Rules = { new CategoryRuleDef { Id = "tag_l", Kind = CategoryRuleKind.Tag, Tag = "leader" }, new CategoryRuleDef { Id = "move", Kind = CategoryRuleKind.MoveCategory, MoveCategory = "run" } },
        });
        db.Add(new UnitDef { Id = "alpha", Name = "Вожак", Faction = "enemy", MainCategory = "wolf" });
        return db;
    }

    [Fact]
    public void T05_WolfToBeast_SeveralParents_AndACycleIsRejected()
    {
        var db = Tree();
        var wolf = db.Get<OriginDef>("wolf")!;
        // Species/Wolf → CreatureType/Beast → Creature: the chain and the tags of every ancestor.
        Assert.Equal(new[] { "creature", "beast", "wolf" }, CategoryTree.Chain(db, wolf).Select(c => c.Id));
        // A second parent: the role «вожак» — a graph, each ancestor once (the shared «Существо» too).
        Assert.True(CategoryTree.TrySetParents(db, wolf, "beast", new[] { "pack_leader" }, out var why), why);
        var chain = CategoryTree.Chain(db, wolf).Select(c => c.Id).ToList();
        Assert.Equal(new[] { "creature", "beast", "pack_leader", "wolf" }, chain);
        var res = CategoryTree.Resolve(db, db.Unit("alpha")!);
        Assert.Equal(new[] { "beast", "creature", "leader" }, res.Tags.OrderBy(t => t));
        Assert.Single(res.Rules, r => r.Rule.Id == "tag_c" && r.Active);
        // The same rule ID got from the new parent: with «наследовать» the parent's acts and the conflict is reported (13.6)…
        Assert.Contains(res.Problems, p => p.Contains("«move»") && p.Contains("наследовать"));
        Assert.Equal(new[] { "run" }, res.MoveCategories);
        // …«переопределить» — the category's own rule, no conflict.
        wolf.Rules[0].Mode = CategoryInherit.Override;
        res = CategoryTree.Resolve(db, db.Unit("alpha")!);
        Assert.Empty(res.Problems);
        Assert.Equal(new[] { "walk" }, res.MoveCategories);
        Assert.True(CategoryTree.Matches(db, db.Unit("alpha")!, "pack_leader"));

        // The cycle: «Существо» under «Волк» — rejected, nothing changes.
        var creature = db.Get<OriginDef>("creature")!;
        Assert.False(CategoryTree.TrySetParents(db, creature, "wolf", new string[0], out why));
        Assert.Contains("цикл", why);
        Assert.Equal("", creature.Parent);
        Assert.False(CategoryTree.TrySetParents(db, db.Get<OriginDef>("beast")!, "creature", new[] { "wolf" }, out why));
        Assert.Equal("creature", db.Get<OriginDef>("beast")!.Parent);
        Assert.Empty(db.Get<OriginDef>("beast")!.ExtraParents);
        Assert.False(CategoryTree.TrySetParents(db, wolf, "wolf", new string[0], out why));
        Assert.Contains("самой себя", why);
        Assert.DoesNotContain(CategoryTree.ParentChoices(db, creature), o => o.Id is "wolf" or "beast" or "creature" or "pack_leader");
        Assert.Contains(CategoryTree.ParentChoices(db, wolf), o => o.Id == "pack_leader");

        // A cycle that came in a file is an error of the check, and the chain stops at it.
        creature.ExtraParents.Add("wolf");
        Assert.Contains(ContentValidator.Validate(db), i => i.Severity == IssueSeverity.Error && i.Message.Contains("цикл"));
        Assert.Equal(4, CategoryTree.Chain(db, wolf).Count);
    }

    [Fact]
    public void Descendants_ByAnyParent_AreTouchedByAChange()
    {
        var db = Tree();
        db.Get<OriginDef>("wolf")!.ExtraParents.Add("pack_leader");
        var (below, touched) = ChangeImpact.OfCategory(db, "pack_leader");
        Assert.Contains(below, c => c.Id == "wolf");
        Assert.Contains(touched, u => u.Id == "alpha");
        Assert.Equal(new[] { "beast", "creature", "pack_leader" }, CategoryTree.Ancestors(db, "wolf").OrderBy(x => x));
    }
}
