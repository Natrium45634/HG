using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>
/// What a unit is doing, as an animation role (TZ §3.5, docs/DECISIONS_2.md §34). The renderer plays
/// the mapped model clip or the procedural animation of the base figure; the simulation never
/// depends on it — visuals are a replaceable representation.
/// </summary>
public static class VisualState
{
    public const double SpawnSeconds = 0.6;
    public const double HitSeconds = 0.35;

    public static AnimRole RoleOf(Simulation sim, Unit u)
    {
        if (!u.Alive) return AnimRole.Death;
        long t = sim.Tick;
        if (u.SpawnTick >= 0 && t - u.SpawnTick < sim.TicksFor(SpawnSeconds)) return AnimRole.Spawn;
        if (u.Flags.BlocksActions || u.Flags.MindControl) return AnimRole.Control;
        if (u.Casting != null)
            return u.Casting.Skill == u.BasicAttack || u.Casting.Skill.Kind == SkillKind.Attack ? AnimRole.Attack : AnimRole.Skill;
        if (u.LastDamagedTick > 0 && t - u.LastDamagedTick < sim.TicksFor(HitSeconds)) return AnimRole.Hit;
        if (u.StepTo.HasValue) return AnimRole.Move;
        if (u.Defending || sim.Blocking.Load(u) > 0 || sim.Blocking.BlockerOf(u) != null) return AnimRole.Block;
        return AnimRole.Idle;
    }

    /// <summary>Roles that repeat while they last (the others play once).</summary>
    public static bool Loops(AnimRole r) => r is AnimRole.Idle or AnimRole.Move or AnimRole.Control or AnimRole.Block;
}
