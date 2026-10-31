using System.Linq;
using HexTactics.Core.Content;
using Xunit;

namespace HexTactics.Tests;

/// <summary>Part 5, stage F — the tabs of the unit editor (ТЗ 25.3; docs/DECISIONS_5.md §89).</summary>
public class Part5EditorTests
{
    [Fact]
    public void UnitTabs_EveryFieldOnExactlyOneTab_EveryTabHasFields()
    {
        var fields = UnitEditorTabs.Fields().ToList();
        Assert.Contains(nameof(UnitDef.Stats), fields);
        // Exactly one tab skips nothing of a field: the field is shown once.
        foreach (var f in fields)
            Assert.Single(UnitEditorTabs.All, t => !UnitEditorTabs.SkipFor(t.Tab).Contains(f));
        Assert.All(UnitEditorTabs.All, t => Assert.Contains(fields, f => UnitEditorTabs.Of(f) == t.Tab));
        Assert.Equal(9, UnitEditorTabs.All.Length); // part 6 (§102): «Босс и фазы»
        Assert.Equal(UnitTab.Base, UnitEditorTabs.Of(nameof(UnitDef.Stats)));
        Assert.Equal(UnitTab.Movement, UnitEditorTabs.Of(nameof(UnitDef.MoveCategory)));
        Assert.Equal(UnitTab.Ai, UnitEditorTabs.Of(nameof(UnitDef.AiProfile)));
        Assert.Equal(UnitTab.Tags, UnitEditorTabs.Of(nameof(UnitDef.Origins)));
        Assert.Equal(UnitTab.Passives, UnitEditorTabs.Of(nameof(UnitDef.Passives)));
        Assert.Equal(UnitTab.Skills, UnitEditorTabs.Of(nameof(UnitDef.Skills)));
        Assert.Equal(UnitTab.Visual, UnitEditorTabs.Of(nameof(UnitDef.Visual)));
        Assert.Equal(UnitTab.Rating, UnitEditorTabs.Of(nameof(UnitDef.ThreatCost)));
        // A field nobody placed: on the first tab, never hidden.
        Assert.Equal(UnitTab.Base, UnitEditorTabs.Of("SomeFieldOfTheFuture"));
    }
}
