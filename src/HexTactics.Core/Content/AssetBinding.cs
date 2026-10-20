using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>
/// «Назначить объекту» of the import window (TZ §16, docs/DECISIONS_3.md §47): where a freshly imported resource can
/// go — a terrain's top texture or side material, a unit's model or sounds, a skill's or a status's icon, an effect's
/// texture or model, a stage's projectile. Only references change: the resource stays a separate object.
/// </summary>
public static class AssetBinding
{
    public sealed record Target(string Label, Def Owner, TexturePurpose? Purpose, Func<string> Get, Action<ContentDatabase, AssetDef> Apply)
    {
        /// <summary>The kind the resource takes for this target (a PNG becomes an icon for a skill).</summary>
        public AssetKind Kind { get; init; }
    }

    private static Target Field(string label, Def owner, TexturePurpose? purpose, Func<string> get, Action<string> set) =>
        new(label, owner, purpose, get, (_, a) => set(a.Id));

    /// <summary>
    /// Every place a resource of that kind can be assigned to; for an image file — the places of textures, sprites
    /// and icons alike (the kind follows the chosen target).
    /// </summary>
    public static List<Target> Targets(ContentDatabase db, AssetKind kind)
    {
        if (kind == AssetKind.Texture)
            return KindTargets(db, AssetKind.Texture).Concat(KindTargets(db, AssetKind.Icon)).ToList();
        return KindTargets(db, kind);
    }

    private static List<Target> KindTargets(ContentDatabase db, AssetKind kind)
    {
        var l = new List<Target>();
        switch (kind)
        {
            case AssetKind.Texture or AssetKind.Sprite:
                foreach (var t in db.All<TerrainDef>())
                {
                    var look = t.Look;
                    l.Add(Field($"Местность «{t.DisplayName}» — текстура верха", t, TexturePurpose.HexSurface, () => look.TopTexture, v => look.TopTexture = v));
                    l.Add(new($"Местность «{t.DisplayName}» — материал боковых граней (новый материал)", t, TexturePurpose.Environment, () => look.SideMaterial,
                        (d, a) => look.SideMaterial = MaterialOf(d, a, t.DisplayName + " — грани").Id));
                }
                foreach (var v in db.All<VfxDef>())
                    l.Add(Field($"Эффект «{v.DisplayName}» — текстура", v, TexturePurpose.Other, () => v.Texture, x => v.Texture = x));
                break;
            case AssetKind.Icon:
                foreach (var s in db.All<SkillDef>())
                    l.Add(Field($"Навык «{s.DisplayName}» — иконка", s, TexturePurpose.SkillIcon, () => s.IconAsset, x => s.IconAsset = x));
                foreach (var s in db.All<StatusDef>())
                    l.Add(Field($"Состояние «{s.DisplayName}» — иконка", s, TexturePurpose.StatusIcon, () => s.IconAsset, x => s.IconAsset = x));
                foreach (var u in db.All<UnitDef>())
                    l.Add(Field($"Юнит «{u.DisplayName}» — иконка", u, TexturePurpose.SkillIcon, () => u.Visual.Icon, x => u.Visual.Icon = x));
                break;
            case AssetKind.Model:
                foreach (var u in db.All<UnitDef>())
                    l.Add(Field($"Юнит «{u.DisplayName}» — модель", u, null, () => u.Visual.Model, x => u.Visual.Model = x));
                foreach (var t in db.All<TerrainDef>())
                    l.Add(Field($"Местность «{t.DisplayName}» — модель объекта на клетке", t, null, () => t.Look.PropModel, x => t.Look.PropModel = x));
                foreach (var v in db.All<VfxDef>())
                    l.Add(Field($"Эффект «{v.DisplayName}» — модель", v, null, () => v.Model, x => v.Model = x));
                foreach (var s in db.All<SkillDef>())
                    for (int i = 0; i < s.Stages.Count; i++)
                    {
                        var st = s.Stages[i];
                        l.Add(Field($"Навык «{s.DisplayName}», этап {i + 1} — снаряд", s, null, () => st.Projectile.Model, x =>
                        {
                            st.Projectile.Model = x;
                            st.Projectile.Enabled = true;
                        }));
                    }
                break;
            case AssetKind.Sound:
                foreach (var s in db.All<SkillDef>())
                    l.Add(Field($"Навык «{s.DisplayName}» — звук", s, null, () => s.Sound, x => s.Sound = x));
                foreach (var u in db.All<UnitDef>())
                {
                    var vis = u.Visual;
                    l.Add(Field($"Юнит «{u.DisplayName}» — звук атаки", u, null, () => vis.AttackSound, x => vis.AttackSound = x));
                    l.Add(Field($"Юнит «{u.DisplayName}» — звук получения урона", u, null, () => vis.HitSound, x => vis.HitSound = x));
                    l.Add(Field($"Юнит «{u.DisplayName}» — звук гибели", u, null, () => vis.DeathSound, x => vis.DeathSound = x));
                }
                foreach (var v in db.All<VfxDef>())
                    l.Add(Field($"Эффект «{v.DisplayName}» — звук", v, null, () => v.Sound, x => v.Sound = x));
                break;
        }
        return l.Select(t => t with { Kind = kind == AssetKind.Sprite ? AssetKind.Texture : kind }).ToList();
    }

    /// <summary>A side material needs a «Материал» resource: one is created around the texture.</summary>
    private static AssetDef MaterialOf(ContentDatabase db, AssetDef texture, string name)
    {
        var m = new AssetDef
        {
            Id = ContentIO.UniqueId(db, DefCategory.Asset, texture.Id + "_mat"), Name = name, Kind = AssetKind.Material, Texture = texture.Id,
            Albedo = "#ffffff", Group = "materials",
        };
        db.Add(m);
        return m;
    }
}
