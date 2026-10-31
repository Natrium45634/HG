using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>
/// The result of a battle for a campaign (ТЗ ч. 6, 10.3; docs/DECISIONS_6.md §103): the numbers the report of the battle
/// already has — no statistics of its own.
/// </summary>
public static class CampaignResults
{
    public static Dictionary<OutcomeMetric, double> Of(Simulation sim, BattleReport filled) => new()
    {
        [OutcomeMetric.Victory] = sim.Outcome == BattleOutcome.Victory ? 1 : 0,
        [OutcomeMetric.BaseHpRemaining] = filled.Bases.Count == 0 ? 1 : filled.Bases.Min(b => b.MaxHp > 0 ? b.Hp / b.MaxHp : 0),
        [OutcomeMetric.Leaks] = filled.EnemiesReachedGoal,
        [OutcomeMetric.TimeToWin] = filled.Seconds,
        [OutcomeMetric.Deaths] = filled.UnitStats.Count(u => u.Side == BattleReport.PlayerSide && u.DeathTick >= 0),
        [OutcomeMetric.UnitsDeployed] = sim.Squad.Enabled ? sim.Squad.Entries.Sum(e => e.Deployments) : 0,
    };
}
