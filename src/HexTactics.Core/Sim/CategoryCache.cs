using System.Collections.Generic;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>Resolutions of the category tree per template and scenario tag (docs/DECISIONS_4.md §61).</summary>
public sealed class CategoryCache
{
    private readonly ContentDatabase _db;
    private readonly Dictionary<(UnitDef, string), CategoryResolution> _cache = new();

    public CategoryCache(ContentDatabase db)
    {
        _db = db;
    }

    public CategoryResolution Of(UnitDef def, string tag = "")
    {
        if (!_cache.TryGetValue((def, tag), out var r)) _cache[(def, tag)] = r = CategoryTree.Resolve(_db, def, tag);
        return r;
    }
}
