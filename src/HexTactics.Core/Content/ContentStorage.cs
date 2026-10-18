using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>File access abstraction: the Godot layer provides res:// access, tests use the file system.</summary>
public interface IContentStorage
{
    bool Exists(string relativePath);
    string? ReadText(string relativePath);
    void WriteText(string relativePath, string text);
    void Delete(string relativePath);
    /// <summary>File names (not paths) in a sub directory matching an extension.</summary>
    IEnumerable<string> ListFiles(string relativeDir, string extension);
    /// <summary>Names (not paths) of the sub directories of a directory.</summary>
    IEnumerable<string> ListDirectories(string relativeDir);
    /// <summary>Removes a directory with everything in it.</summary>
    void DeleteDirectory(string relativeDir);
    bool IsReadOnly { get; }
    string Describe(string relativePath);
}

public sealed class FileSystemStorage : IContentStorage
{
    public FileSystemStorage(string root, bool readOnly = false)
    {
        Root = root;
        IsReadOnly = readOnly;
    }

    public string Root { get; }
    public bool IsReadOnly { get; }

    private string Full(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    public bool Exists(string relativePath) => File.Exists(Full(relativePath));

    public string? ReadText(string relativePath)
    {
        var f = Full(relativePath);
        return File.Exists(f) ? File.ReadAllText(f) : null;
    }

    public void WriteText(string relativePath, string text)
    {
        if (IsReadOnly) throw new InvalidOperationException("Хранилище только для чтения");
        var f = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        // Write to a temp file first so a crash never leaves a half-written content file.
        var tmp = f + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(f)) File.Replace(tmp, f, null);
        else File.Move(tmp, f);
    }

    public void Delete(string relativePath)
    {
        if (IsReadOnly) throw new InvalidOperationException("Хранилище только для чтения");
        var f = Full(relativePath);
        if (File.Exists(f)) File.Delete(f);
    }

    public IEnumerable<string> ListFiles(string relativeDir, string extension)
    {
        var d = Full(relativeDir);
        if (!Directory.Exists(d)) return Enumerable.Empty<string>();
        return Directory.GetFiles(d, "*" + extension).Select(Path.GetFileName).Where(n => n != null).Select(n => n!)
            .Where(n => !n.EndsWith(".tmp", StringComparison.Ordinal) || extension.EndsWith(".tmp", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);
    }

    public IEnumerable<string> ListDirectories(string relativeDir)
    {
        var d = Full(relativeDir);
        if (!Directory.Exists(d)) return Enumerable.Empty<string>();
        return Directory.GetDirectories(d).Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal);
    }

    public void DeleteDirectory(string relativeDir)
    {
        if (IsReadOnly) throw new InvalidOperationException("Хранилище только для чтения");
        var d = Full(relativeDir);
        if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    public string Describe(string relativePath) => Full(relativePath);
}

/// <summary>In-memory storage for tests and round-trip checks.</summary>
public sealed class MemoryStorage : IContentStorage
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public bool IsReadOnly => false;

    public bool Exists(string relativePath) => Files.ContainsKey(relativePath);
    public string? ReadText(string relativePath) => Files.TryGetValue(relativePath, out var t) ? t : null;
    public void WriteText(string relativePath, string text) => Files[relativePath] = text;
    public void Delete(string relativePath) => Files.Remove(relativePath);

    public IEnumerable<string> ListFiles(string relativeDir, string extension)
    {
        var prefix = relativeDir.TrimEnd('/') + "/";
        return Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(extension, StringComparison.Ordinal)
                                     && k.IndexOf('/', prefix.Length) < 0)
            .Select(k => k.Substring(prefix.Length)).OrderBy(k => k, StringComparer.Ordinal);
    }

    public IEnumerable<string> ListDirectories(string relativeDir)
    {
        var prefix = relativeDir.TrimEnd('/') + "/";
        return Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) > 0)
            .Select(k => k.Substring(prefix.Length, k.IndexOf('/', prefix.Length) - prefix.Length)).Distinct().OrderBy(k => k, StringComparer.Ordinal);
    }

    public void DeleteDirectory(string relativeDir)
    {
        var prefix = relativeDir.TrimEnd('/') + "/";
        foreach (var k in Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) Files.Remove(k);
    }

    public string Describe(string relativePath) => "mem:" + relativePath;
}
