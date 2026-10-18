using System.Collections.Generic;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum LosBlock
{
    None,
    Obstacle,
    Elevation,
    Effect,
}

/// <summary>
/// Line of sight with height (docs/DECISIONS.md §9). Endpoints are not checked; for every
/// intermediate cell the ray height is lerp(hA + eye, hB + eye, t). Results are cached per
/// map version.
/// </summary>
public sealed class LineOfSight
{
    private readonly Dictionary<long, LosBlock> _cache = new();
    private int _cacheVersion = -1;

    public long Checks { get; private set; }
    public long CacheHits { get; private set; }

    private static long Key(HexCoord a, HexCoord b)
    {
        unchecked
        {
            long ka = ((long)(a.Q & 0xFFFF) << 16) | (uint)(a.R & 0xFFFF);
            long kb = ((long)(b.Q & 0xFFFF) << 16) | (uint)(b.R & 0xFFFF);
            return (ka << 32) | kb;
        }
    }

    public bool Has(BattleMap map, RulesDef rules, HexCoord a, HexCoord b) => Check(map, rules, a, b, out _) == LosBlock.None;

    public LosBlock Check(BattleMap map, RulesDef rules, HexCoord a, HexCoord b, out HexCoord? blocker)
    {
        blocker = null;
        if (a == b || HexCoord.Distance(a, b) <= 1) return LosBlock.None;
        if (_cacheVersion != map.Version)
        {
            _cache.Clear();
            _cacheVersion = map.Version;
        }
        long key = Key(a, b);
        if (_cache.TryGetValue(key, out var cached) && cached == LosBlock.None)
        {
            CacheHits++;
            return cached;
        }
        Checks++;
        var res = Compute(map, rules, a, b, out blocker);
        _cache[key] = res;
        return res;
    }

    public static LosBlock Compute(BattleMap map, RulesDef rules, HexCoord a, HexCoord b, out HexCoord? blocker)
    {
        blocker = null;
        var ca = map.Get(a);
        var cb = map.Get(b);
        double eye = rules.Height.EyeHeight;
        double ha = (ca?.Height ?? 0) + eye;
        double hb = (cb?.Height ?? 0) + eye;
        var line = HexCoord.Line(a, b);
        int n = line.Count - 1;
        for (int i = 1; i < n; i++)
        {
            var c = map.Get(line[i]);
            if (c == null) continue; // holes do not block sight
            double t = (double)i / n;
            double ray = ha + (hb - ha) * t;
            if (c.TerrainBlocksLoS && c.ObstacleTop > ray - 1e-9)
            {
                blocker = c.Coord;
                return LosBlock.Obstacle;
            }
            if (rules.Height.TerrainBlocksLoS && c.Height > ray + 1e-9)
            {
                blocker = c.Coord;
                return LosBlock.Elevation;
            }
            if (c.EffectBlocksLoS)
            {
                blocker = c.Coord;
                return LosBlock.Effect;
            }
        }
        return LosBlock.None;
    }

    public static string Describe(LosBlock b) => b switch
    {
        LosBlock.Obstacle => "препятствие на линии видимости",
        LosBlock.Elevation => "рельеф закрывает обзор",
        LosBlock.Effect => "дым/туман закрывает обзор",
        _ => "",
    };
}
