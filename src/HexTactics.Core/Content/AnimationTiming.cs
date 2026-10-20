using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>
/// «Взять тайминг из анимации» (TZ §13.4, docs/DECISIONS_3.md §47): the hit markers of the strike clip become the
/// delays of the skill's hit stages. The first marker is the release moment (the clip is fitted to it in battle), so
/// stage k gets marker[k] − marker[0]. The battle still runs by the delays — the clip only proposed them.
/// </summary>
public static class AnimationTiming
{
    /// <summary>Models that can propose timing for the skill: of the units that know it first, then the rest with markers.</summary>
    public static List<AssetDef> Candidates(ContentDatabase db, SkillDef s)
    {
        var withSkill = db.All<UnitDef>().Where(u => u.Skills.Contains(s.Id) || u.BasicAttack == s.Id || u.DefenseSkill == s.Id)
            .Select(u => db.Get<AssetDef>(u.Visual.Model)).Where(a => a != null).Cast<AssetDef>();
        var withMarkers = db.All<AssetDef>().Where(a => a.Kind == AssetKind.Model && a.Animations.Any(m => m.Markers.Count > 0));
        return withSkill.Concat(withMarkers).Distinct().ToList();
    }

    /// <summary>The moments in the clip of the first hit stage: «проверка попадания», else «выпуск снаряда».</summary>
    public static List<double> Markers(AssetDef model, SkillStageDef stage)
    {
        var map = !string.IsNullOrEmpty(stage.AnimationClip)
            ? model.Animations.FirstOrDefault(m => m.Clip == stage.AnimationClip)
            : model.Animations.FirstOrDefault(m => m.Role == stage.Animation);
        if (map == null) return new List<double>();
        var hits = map.Markers.Where(m => m.Kind == AnimMarker.HitCheck).Select(m => m.Time).OrderBy(t => t).ToList();
        if (hits.Count == 0) hits = map.Markers.Where(m => m.Kind == AnimMarker.ProjectileRelease).Select(m => m.Time).OrderBy(t => t).ToList();
        return hits;
    }

    /// <summary>Applies the markers to the hit stages in order; returns what changed (empty — nothing to take).</summary>
    public static string Apply(SkillDef s, AssetDef model)
    {
        var hitStages = s.Stages.Where(st => st.HasDamage).ToList();
        if (hitStages.Count == 0) return "";
        var marks = Markers(model, hitStages[0]);
        if (marks.Count == 0) return "";
        int n = Math.Min(marks.Count, hitStages.Count);
        double shift = 0;
        var changes = new List<string>();
        for (int i = 0; i < hitStages.Count; i++)
        {
            var st = hitStages[i];
            double old = st.Delay;
            if (i < n) st.Delay = Math.Round(Math.Max(0, marks[i] - marks[0]), 3);
            // Stages beyond the markers keep their spacing behind the last marked one.
            else st.Delay = Math.Round(Math.Max(0, old + shift), 3);
            if (i == n - 1) shift = st.Delay - old;
            if (Math.Abs(old - st.Delay) > 1e-9) changes.Add($"этап «{(st.Name == "" ? (s.Stages.IndexOf(st) + 1).ToString() : st.Name)}»: {old:0.###} → {st.Delay:0.###} с");
        }
        return changes.Count == 0 ? "Задержки уже совпадают с маркерами" : $"Из «{model.DisplayName}» взято маркеров: {n}. " + string.Join("; ", changes);
    }
}
