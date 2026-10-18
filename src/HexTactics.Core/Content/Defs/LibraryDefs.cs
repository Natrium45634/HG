using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>
/// A named set of rules (docs/DECISIONS_2.md §31): a scenario may choose a preset instead of the
/// global rules (for example «Хардкор» with less AP regeneration).
/// </summary>
public sealed class RulesPresetDef : Def
{
    [Ed("Правила", "Полный набор правил; сценарий с этим пресетом использует его вместо глобальных")]
    public RulesDef Rules { get; set; } = new();

    [JsonIgnore] public override DefCategory Category => DefCategory.RulesPreset;
}
