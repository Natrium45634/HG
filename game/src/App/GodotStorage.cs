using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;

namespace HexTactics.Game.App;

/// <summary>Read-only content storage on res:// (works inside the exported .pck).</summary>
public sealed class ResStorage : IContentStorage
{
    private readonly string _root;

    public ResStorage(string root)
    {
        _root = root.TrimEnd('/');
    }

    public bool IsReadOnly => true;

    private string Full(string rel) => _root + "/" + rel;

    public bool Exists(string relativePath) => FileAccess.FileExists(Full(relativePath));

    public string? ReadText(string relativePath)
    {
        var path = Full(relativePath);
        if (!FileAccess.FileExists(path)) return null;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return f?.GetAsText();
    }

    public void WriteText(string relativePath, string text) => throw new InvalidOperationException("res:// только для чтения");

    public void Delete(string relativePath) => throw new InvalidOperationException("res:// только для чтения");

    public IEnumerable<string> ListFiles(string relativeDir, string extension)
    {
        var dir = DirAccess.Open(Full(relativeDir));
        if (dir == null) return Enumerable.Empty<string>();
        var names = new List<string>();
        foreach (var f in dir.GetFiles())
        {
            // Exported builds may list "file.json.remap"/".import" variants; normalise them.
            var name = f.EndsWith(".remap", StringComparison.Ordinal) ? f[..^6] : f;
            if (name.EndsWith(extension, StringComparison.Ordinal) && !names.Contains(name)) names.Add(name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public IEnumerable<string> ListDirectories(string relativeDir)
    {
        var dir = DirAccess.Open(Full(relativeDir));
        if (dir == null) return Enumerable.Empty<string>();
        var names = dir.GetDirectories().ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    public void DeleteDirectory(string relativeDir) => throw new InvalidOperationException("res:// только для чтения");

    public string Describe(string relativePath) => Full(relativePath);
}
