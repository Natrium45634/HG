using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;
using FileAccess = Godot.FileAccess;

namespace HexTactics.Game.Render;

/// <summary>
/// Runtime loading of imported resources (docs/DECISIONS_2.md §34): models (glTF/GLB), textures,
/// sounds and materials. Files are read from the user pack first, then from the built-in pack,
/// checked by <see cref="AssetValidator"/> (signature, checksum, safe references) before Godot sees
/// them, and cached. Any problem is recorded in <see cref="Problems"/> and the caller gets null —
/// it then uses the base geometry / silence; the game never stops because of a resource.
/// </summary>
public static class Assets
{
    /// <summary>Resource ID → what is wrong with it (shown in the editor «Ресурсы» and in diagnostics).</summary>
    public static Dictionary<string, string> Problems { get; } = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, object?> Cache = new(StringComparer.Ordinal);

    /// <summary>Clears caches and problems (after publication or a content reload).</summary>
    public static void Reset()
    {
        Cache.Clear();
        Problems.Clear();
    }

    /// <summary>Reads a pack-relative file: user pack, then the built-in pack (res://content). Null if absent.</summary>
    public static byte[]? Read(string rel)
    {
        if (!ContentPackage.IsSafePath(rel)) return null;
        try
        {
            var user = Path.Combine(App.Main.Instance.Content.UserRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(user)) return File.ReadAllBytes(user);
        }
        catch (Exception)
        {
            // Fall through to the built-in pack.
        }
        string res = App.ContentManager.BuiltInRoot + "/" + rel;
        return FileAccess.FileExists(res) ? FileAccess.GetFileAsBytes(res) : null;
    }

    private static void Fail(AssetDef a, string message)
    {
        if (Problems.TryAdd(a.Id, message)) GD.PushWarning($"Ресурс «{a.DisplayName}» [{a.Id}]: {message} — используется базовое представление");
    }

    /// <summary>The checked bytes of a resource file, or null (problem recorded).</summary>
    private static byte[]? Checked(AssetDef a)
    {
        var bytes = Read(a.Path);
        var problem = AssetValidator.FileProblem(a, bytes);
        if (problem != null)
        {
            Fail(a, problem);
            return null;
        }
        return bytes;
    }

    private static T? Cached<T>(string key, Func<T?> load) where T : class
    {
        if (Cache.TryGetValue(key, out var v)) return v as T;
        T? r;
        try
        {
            r = load();
        }
        catch (Exception)
        {
            r = null;
        }
        Cache[key] = r;
        return r;
    }

    private static string Key(AssetDef a) => $"{a.Kind}|{a.Path}|{a.Sha256}";

    // ------------------------------------------------------------------ models

    /// <summary>A new instance of a model (each call — a separate node tree), or null.</summary>
    public static Node3D? Model(AssetDef? a)
    {
        if (a == null || a.Kind != AssetKind.Model) return null;
        var bytes = Cached(Key(a) + "|bytes", () => Checked(a));
        if (bytes == null) return null;
        try
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            string baseDir = Path.GetDirectoryName(Path.Combine(App.Main.Instance.Content.UserRoot, a.Path)) ?? "";
            var err = doc.AppendFromBuffer(bytes, baseDir, state);
            if (err != Error.Ok)
            {
                Fail(a, $"модель не читается ({err})");
                return null;
            }
            if (doc.GenerateScene(state) is not Node3D scene)
            {
                Fail(a, "в модели нет сцены");
                return null;
            }
            return scene;
        }
        catch (Exception e)
        {
            Fail(a, "ошибка загрузки модели: " + e.Message);
            return null;
        }
    }

    /// <summary>The AnimationPlayer of a model instance (glTF clips), if any.</summary>
    public static AnimationPlayer? PlayerOf(Node node)
    {
        if (node is AnimationPlayer p) return p;
        foreach (var c in node.GetChildren())
            if (PlayerOf(c) is { } found)
                return found;
        return null;
    }

    // ------------------------------------------------------------------ textures, materials, sounds

    public static Texture2D? Texture(AssetDef? a)
    {
        if (a == null || a.Kind is not (AssetKind.Texture or AssetKind.Sprite or AssetKind.Icon)) return null;
        return Cached<Texture2D>(Key(a), () =>
        {
            var bytes = Checked(a);
            if (bytes == null) return null;
            var img = new Image();
            string ext = Path.GetExtension(a.Path).ToLowerInvariant();
            var err = ext switch
            {
                ".png" => img.LoadPngFromBuffer(bytes),
                ".jpg" or ".jpeg" => img.LoadJpgFromBuffer(bytes),
                ".webp" => img.LoadWebpFromBuffer(bytes),
                _ => Error.FileUnrecognized,
            };
            if (err != Error.Ok)
            {
                Fail(a, $"изображение не читается ({err})");
                return null;
            }
            return ImageTexture.CreateFromImage(img);
        });
    }

    public static StandardMaterial3D? Material(AssetDef? a, ContentDatabase db)
    {
        if (a == null || a.Kind != AssetKind.Material) return null;
        return Cached<StandardMaterial3D>(Key(a) + "|" + a.Texture, () =>
        {
            var m = new StandardMaterial3D
            {
                AlbedoColor = Ui.Parse(a.Albedo, Colors.White),
                Metallic = (float)a.Metallic,
                Roughness = (float)a.Roughness,
            };
            if (!string.IsNullOrEmpty(a.Texture))
            {
                var tex = Texture(db.Get<AssetDef>(a.Texture));
                if (tex != null)
                {
                    m.AlbedoTexture = tex;
                    m.Uv1Triplanar = true;
                    m.Uv1Scale = new Vector3(0.9f, 0.9f, 0.9f);
                }
            }
            if (!string.IsNullOrEmpty(a.Emission))
            {
                m.EmissionEnabled = true;
                m.Emission = Ui.Parse(a.Emission, Colors.Black);
                m.EmissionEnergyMultiplier = (float)a.EmissionEnergy;
            }
            if (a.Transparency > 0)
            {
                m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                m.AlbedoColor = m.AlbedoColor with { A = 1 - (float)a.Transparency };
            }
            return m;
        });
    }

    public static AudioStream? Sound(AssetDef? a)
    {
        if (a == null || a.Kind != AssetKind.Sound) return null;
        return Cached<AudioStream>(Key(a), () =>
        {
            var bytes = Checked(a);
            if (bytes == null) return null;
            AudioStream? s = Path.GetExtension(a.Path).ToLowerInvariant() switch
            {
                ".wav" => AudioStreamWav.LoadFromBuffer(bytes),
                ".ogg" => AudioStreamOggVorbis.LoadFromBuffer(bytes),
                _ => null,
            };
            if (s == null) Fail(a, "звук не читается");
            return s;
        });
    }

    /// <summary>Plays a sound resource at a point (3D) — silently does nothing when it is missing or damaged.</summary>
    public static void PlayAt(Node parent, AssetDef? a, Vector3 pos)
    {
        var stream = Sound(a);
        if (stream == null || !parent.IsInsideTree()) return;
        var p = new AudioStreamPlayer3D { Stream = stream, VolumeDb = (float)a!.VolumeDb, UnitSize = 6 };
        parent.AddChild(p);
        p.GlobalPosition = pos;
        p.Finished += p.QueueFree;
        p.Play();
        // Safety net when no audio output finishes the stream (headless runs).
        p.GetTree().CreateTimer(Math.Max(1, stream.GetLength() + 0.5)).Timeout += () =>
        {
            if (IsInstanceValid(p)) p.QueueFree();
        };
    }

    private static bool IsInstanceValid(GodotObject o) => GodotObject.IsInstanceValid(o);
}
