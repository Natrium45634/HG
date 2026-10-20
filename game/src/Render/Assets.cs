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
        string baseDir = Path.GetDirectoryName(Path.Combine(App.Main.Instance.Content.UserRoot, a.Path)) ?? "";
        var scene = ModelFromBytes(bytes, Path.GetExtension(a.Path), baseDir, out var problem);
        if (scene == null) Fail(a, problem);
        return scene;
    }

    /// <summary>
    /// Builds a model from file bytes — glTF/GLB, FBX through the engine's FBX document (ufbx), OBJ by our parser;
    /// used by the import window before the resource exists.
    /// </summary>
    public static Node3D? ModelFromBytes(byte[] bytes, string ext, string baseDir, out string problem)
    {
        problem = "";
        try
        {
            ext = ext.ToLowerInvariant();
            if (ext == ".obj")
            {
                var obj = ObjModel(bytes);
                if (obj == null) problem = "в OBJ нет граней";
                return obj;
            }
            GltfDocument doc = ext == ".fbx" ? new FbxDocument() : new GltfDocument();
            GltfState state = ext == ".fbx" ? new FbxState() : new GltfState();
            var err = doc.AppendFromBuffer(bytes, baseDir, state);
            if (err != Error.Ok)
            {
                problem = $"модель не читается ({err})";
                return null;
            }
            if (doc.GenerateScene(state) is not Node3D scene)
            {
                problem = "в модели нет сцены";
                return null;
            }
            return scene;
        }
        catch (Exception e)
        {
            problem = "ошибка загрузки модели: " + e.Message;
            return null;
        }
    }

    /// <summary>
    /// OBJ — static geometry (TZ §12.2): positions, normals, UVs, polygons fanned into triangles; materials of the OBJ
    /// are not read (the resource's own material or the unit colour is used).
    /// </summary>
    private static Node3D? ObjModel(byte[] bytes)
    {
        var v = new List<Vector3>();
        var vn = new List<Vector3>();
        var vt = new List<Vector2>();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int tris = 0;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var raw in System.Text.Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var p = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            switch (p[0])
            {
                case "v" when p.Length >= 4:
                    v.Add(new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci)));
                    break;
                case "vn" when p.Length >= 4:
                    vn.Add(new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci)));
                    break;
                case "vt" when p.Length >= 3:
                    vt.Add(new Vector2(float.Parse(p[1], ci), 1 - float.Parse(p[2], ci)));
                    break;
                case "f" when p.Length >= 4:
                {
                    var idx = new List<(int V, int T, int N)>();
                    for (int i = 1; i < p.Length; i++)
                    {
                        var s = p[i].Split('/');
                        int Ix(int k, int count) => k < s.Length && int.TryParse(s[k], out var n) ? (n < 0 ? count + n : n - 1) : -1;
                        idx.Add((Ix(0, v.Count), Ix(1, vt.Count), Ix(2, vn.Count)));
                    }
                    for (int i = 1; i + 1 < idx.Count; i++)
                        foreach (var c in new[] { idx[0], idx[i + 1], idx[i] })
                        {
                            if (c.V < 0 || c.V >= v.Count) continue;
                            if (c.N >= 0 && c.N < vn.Count) st.SetNormal(vn[c.N]);
                            if (c.T >= 0 && c.T < vt.Count) st.SetUV(vt[c.T]);
                            st.AddVertex(v[c.V]);
                        }
                    tris += idx.Count - 2;
                    break;
                }
            }
        }
        if (tris == 0)
        {
            return null;
        }
        if (vn.Count == 0) st.GenerateNormals();
        var root = new Node3D { Name = "Obj" };
        root.AddChild(new MeshInstance3D { Mesh = st.Commit(), Name = "Mesh" });
        return root;
    }

    /// <summary>What the engine sees in a loaded model: triangles, bones, clips with lengths (FBX and the preview).</summary>
    public static (int Triangles, int Bones, List<(string Name, double Seconds)> Clips) EngineStats(Node node)
    {
        int tris = 0, bones = 0;
        var clips = new List<(string, double)>();
        void Walk(Node n)
        {
            if (n is MeshInstance3D mi && mi.Mesh != null)
                for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                {
                    var arr = mi.Mesh.SurfaceGetArrays(s);
                    var ind = arr[(int)Mesh.ArrayType.Index].AsInt32Array();
                    tris += ind.Length > 0 ? ind.Length / 3 : arr[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length / 3;
                }
            if (n is Skeleton3D sk) bones += sk.GetBoneCount();
            if (n is AnimationPlayer ap)
                foreach (var name in ap.GetAnimationList())
                    clips.Add((name, ap.GetAnimation(name)?.Length ?? 0));
            foreach (var c in n.GetChildren()) Walk(c);
        }
        Walk(node);
        return (tris, bones, clips);
    }

    // ------------------------------------------------------------------ background loading

    private static readonly Queue<(AssetDef Asset, Action<Node3D?> Done)> Pending = new();

    /// <summary>
    /// Loads a model in the background (docs/DECISIONS_3.md §47): the caller keeps its base figure until the model is
    /// ready; models come one per frame so the battle never waits for them.
    /// </summary>
    public static void ModelLater(AssetDef a, Action<Node3D?> done) => Pending.Enqueue((a, done));

    /// <summary>Called every frame by the battle screen: loads the next waiting model.</summary>
    public static void ProcessPending(int perFrame = 1)
    {
        for (int i = 0; i < perFrame && Pending.Count > 0; i++)
        {
            var (a, done) = Pending.Dequeue();
            Node3D? m = null;
            try
            {
                m = Model(a);
            }
            catch (Exception e)
            {
                Fail(a, "ошибка загрузки модели: " + e.Message);
            }
            done(m);
        }
    }

    public static int PendingCount => Pending.Count;

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
            var img = LoadImage(bytes, Path.GetExtension(a.Path).ToLowerInvariant(), out var why);
            if (img == null)
            {
                Fail(a, why);
                return null;
            }
            if (a.Filter == TextureFilter.LinearMipmap && !img.HasMipmaps() && !img.IsCompressed()) img.GenerateMipmaps();
            return ImageTexture.CreateFromImage(img);
        });
    }

    /// <summary>
    /// Decodes an image file: PNG, JPEG, WebP, TGA from memory; HDR and EXR through the engine's file loaders — when the
    /// build has no loader for them the reason is said plainly (TZ §11.1).
    /// </summary>
    public static Image? LoadImage(byte[] bytes, string ext, out string problem)
    {
        problem = "";
        var img = new Image();
        Error err;
        switch (ext)
        {
            case ".png": err = img.LoadPngFromBuffer(bytes); break;
            case ".jpg" or ".jpeg": err = img.LoadJpgFromBuffer(bytes); break;
            case ".webp": err = img.LoadWebpFromBuffer(bytes); break;
            case ".tga": err = img.LoadTgaFromBuffer(bytes); break;
            case ".hdr" or ".exr":
            {
                string tmp = Path.Combine(Path.GetTempPath(), "hext_" + Guid.NewGuid().ToString("N") + ext);
                try
                {
                    File.WriteAllBytes(tmp, bytes);
                    var loaded = Image.LoadFromFile(tmp);
                    if (loaded == null || loaded.IsEmpty())
                    {
                        problem = $"формат {ext.TrimStart('.').ToUpperInvariant()} не поддерживается этой сборкой игры (нет загрузчика) — сохраните текстуру в PNG";
                        return null;
                    }
                    return loaded;
                }
                finally
                {
                    try { File.Delete(tmp); } catch (IOException) { }
                }
            }
            default: err = Error.FileUnrecognized; break;
        }
        if (err != Error.Ok)
        {
            problem = $"изображение не читается ({err})";
            return null;
        }
        return img;
    }

    /// <summary>
    /// Parameters of a material the renderer does not use (TZ §11.3: say so instead of pretending): shown by the editor.
    /// </summary>
    public static List<string> Unsupported(AssetDef a)
    {
        var list = new List<string>();
        if (a.Kind == AssetKind.Material)
        {
            if (Math.Abs(a.UvRotation) > 1e-6) list.Add("поворот UV материала не поддерживается рендерером (StandardMaterial3D) — поверните текстуру в файле");
            if (a.LodDistance > 0) list.Add("«дальность детализации» не поддерживается рендерером: детальный слой рисуется на любом расстоянии");
            if (App.Main.Instance.Settings.SimplifiedEffects && (!string.IsNullOrEmpty(a.NormalMap) || !string.IsNullOrEmpty(a.DetailMask)))
                list.Add("включены «упрощённые эффекты»: карты нормалей и детальный слой сейчас не рисуются");
        }
        if (a.Kind == AssetKind.Texture && Math.Abs(a.UvRotation) > 1e-6) list.Add("поворот UV применяется только к текстуре верха гекса");
        return list;
    }

    public static StandardMaterial3D? Material(AssetDef? a, ContentDatabase db)
    {
        // The simplified variant for weak systems (settings «упрощённые эффекты»).
        if (a != null && App.Main.Instance.Settings.SimplifiedEffects && db.Get<AssetDef>(a.SimplifiedMaterial) is { Kind: AssetKind.Material } simple && simple != a) a = simple;
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
            // PBR maps (docs/DECISIONS_3.md §47).
            Texture2D? Map(string id) => string.IsNullOrEmpty(id) ? null : Texture(db.Get<AssetDef>(id));
            bool simplified = App.Main.Instance.Settings.SimplifiedEffects;
            if (Map(a.RoughnessMap) is { } rough) m.RoughnessTexture = rough;
            if (Map(a.MetallicMap) is { } metal) m.MetallicTexture = metal;
            if (Map(a.EmissionMap) is { } emit)
            {
                m.EmissionEnabled = true;
                m.EmissionTexture = emit;
                if (string.IsNullOrEmpty(a.Emission)) m.Emission = Colors.White;
            }
            if (!simplified && Map(a.NormalMap) is { } normal)
            {
                m.NormalEnabled = true;
                m.NormalTexture = normal;
            }
            if (!simplified && Map(a.DetailMask) is { } mask && Map(a.DetailTexture) is { } detail)
            {
                m.DetailEnabled = true;
                m.DetailMask = mask;
                m.DetailAlbedo = detail;
            }
            if (Math.Abs(a.UvScale - 1) > 1e-6 || Math.Abs(a.UvOffsetU) > 1e-6 || Math.Abs(a.UvOffsetV) > 1e-6)
            {
                m.Uv1Scale *= (float)a.UvScale;
                m.Uv1Offset = new Vector3((float)a.UvOffsetU, (float)a.UvOffsetV, 0);
            }
            m.BlendMode = a.Blend switch
            {
                MaterialBlend.Add => BaseMaterial3D.BlendModeEnum.Add,
                MaterialBlend.Multiply => BaseMaterial3D.BlendModeEnum.Mul,
                _ => BaseMaterial3D.BlendModeEnum.Mix,
            };
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
                ".mp3" => AudioStreamMP3.LoadFromBuffer(bytes),
                _ => null,
            };
            if (s == null) Fail(a, "звук не читается");
            return s;
        });
    }

    /// <summary>Plays a sound resource at a point (3D) — silently does nothing when it is missing or damaged.</summary>
    public static void PlayAt(Node parent, AssetDef? a, Vector3 pos)
    {
        // Variants: a random one (the interface's generator — never the battle's).
        if (a != null && a.Variants.Count > 0)
        {
            int r = (int)(GD.Randi() % (uint)(a.Variants.Count + 1));
            if (r < a.Variants.Count && App.Main.Instance.Content.Published.Get<AssetDef>(a.Variants[r]) is { Kind: AssetKind.Sound } v) a = v;
        }
        var stream = Sound(a);
        if (stream == null || !parent.IsInsideTree()) return;
        if (a!.Spatial == false)
        {
            var flat = new AudioStreamPlayer { Stream = stream, VolumeDb = (float)a.VolumeDb };
            parent.AddChild(flat);
            flat.Finished += flat.QueueFree;
            flat.Play();
            flat.GetTree().CreateTimer(Math.Max(1, stream.GetLength() + 0.5)).Timeout += () =>
            {
                if (IsInstanceValid(flat)) flat.QueueFree();
            };
            return;
        }
        var p = new AudioStreamPlayer3D { Stream = stream, VolumeDb = (float)a.VolumeDb, UnitSize = 6, MaxDistance = (float)(a.Radius * Board.HexSize * 1.8) };
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
