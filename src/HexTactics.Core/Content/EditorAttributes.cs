using System;

namespace HexTactics.Core.Content;

/// <summary>Label and tooltip of a field in the visual editor (PropertyGrid).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Field)]
public sealed class EdAttribute : Attribute
{
    public string Label { get; }
    public string Tip { get; }
    public string Group { get; set; } = "";
    public int Order { get; set; }

    public EdAttribute(string label, string tip = "")
    {
        Label = label;
        Tip = tip;
    }
}

/// <summary>Numeric range for validation and editor spin boxes.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EdRangeAttribute : Attribute
{
    public double Min { get; }
    public double Max { get; }
    public double Step { get; set; } = 0.0;

    public EdRangeAttribute(double min, double max)
    {
        Min = min;
        Max = max;
    }
}

/// <summary>The string (or list of strings) references a definition of the given category.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EdRefAttribute : Attribute
{
    public DefCategory Category { get; }
    public bool AllowEmpty { get; set; } = true;
    /// <summary>Allows "tag:xxx" entries in addition to ids.</summary>
    public bool AllowTags { get; set; }

    public EdRefAttribute(DefCategory category)
    {
        Category = category;
    }
}

/// <summary>Show the field only when another (enum/bool) field has one of the given values.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class EdShowIfAttribute : Attribute
{
    public string Field { get; }
    public object[] Values { get; }

    public EdShowIfAttribute(string field, params object[] values)
    {
        Field = field;
        Values = values;
    }
}

/// <summary>Hide the field from the generic property grid (edited by a custom tool or internal).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EdHiddenAttribute : Attribute
{
}

/// <summary>String field is a color "#rrggbb".</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EdColorAttribute : Attribute
{
}

/// <summary>Long text (multi-line editor).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EdMultilineAttribute : Attribute
{
}

/// <summary>Dictionary keys reference definitions of the given category (e.g. stats, damage types).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EdKeyRefAttribute : Attribute
{
    public DefCategory Category { get; }

    public EdKeyRefAttribute(DefCategory category)
    {
        Category = category;
    }
}

/// <summary>String field holds a safe formula expression (validated by SafeExpression).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EdFormulaAttribute : Attribute
{
    public string[] Variables { get; }

    public EdFormulaAttribute(params string[] variables)
    {
        Variables = variables;
    }
}

public static class EdUtil
{
    /// <summary>Evaluates [EdShowIf] for a property of the given object (true when no attribute).</summary>
    public static bool IsShown(object obj, System.Reflection.PropertyInfo prop)
    {
        var a = (EdShowIfAttribute?)Attribute.GetCustomAttribute(prop, typeof(EdShowIfAttribute));
        if (a == null) return true;
        var other = obj.GetType().GetProperty(a.Field);
        if (other == null) return true;
        var v = other.GetValue(obj);
        foreach (var x in a.Values)
            if (Equals(x, v))
                return true;
        return false;
    }
}
