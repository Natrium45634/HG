using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>
/// A saved group of a wave (docs/DECISIONS_4.md §58): template, count, timing, subgroups, limits and local
/// modifiers. Applying a preset copies the group into a wave; the base and the route are chosen in the scene.
/// </summary>
public sealed class GroupPresetDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.GroupPreset;

    [Ed("Группа", "Шаблон группы: база спавна и маршрут задаются в сцене")] public SpawnGroupDef Template { get; set; } = new() { Count = 5, Interval = 1 };

    /// <summary>A copy of the group for a wave with a new ID, bound to a base and a route.</summary>
    public SpawnGroupDef Instantiate(string id, string spawnBase, string route)
    {
        var g = ContentJson.Clone(Template);
        g.Id = id;
        g.SpawnBase = spawnBase;
        g.Route = route;
        g.Preset = Id;
        g.FromBudget = false;
        return g;
    }
}
