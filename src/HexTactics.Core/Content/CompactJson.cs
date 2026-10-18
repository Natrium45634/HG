using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexTactics.Core.Content;

/// <summary>
/// Human-friendly JSON printer: containers holding only primitives (or tiny primitive arrays)
/// are written on one line when short, everything else is indented. Keeps map files compact
/// (one cell per line) while staying readable and diff-friendly.
/// </summary>
public static class CompactJson
{
    private const int MaxInline = 110;

    public static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteNode(sb, node, 0);
        sb.Append('\n');
        return sb.ToString();
    }

    private static string Primitive(JsonNode? n) =>
        n == null ? "null" : n.ToJsonString(ContentJson.Options);

    private static bool IsInlineable(JsonNode? n, int depth)
    {
        if (n is JsonObject o)
        {
            if (depth > 1) return false;
            foreach (var kv in o)
                if (!IsInlineable(kv.Value, depth + 1))
                    return false;
            return true;
        }
        if (n is JsonArray a)
        {
            if (depth > 1) return false;
            foreach (var x in a)
                if (!IsInlineable(x, depth + 1))
                    return false;
            return true;
        }
        return true;
    }

    private static string Inline(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            if (o.Count == 0) return "{}";
            var parts = new StringBuilder("{ ");
            bool first = true;
            foreach (var kv in o)
            {
                if (!first) parts.Append(", ");
                first = false;
                parts.Append(JsonSerializer.Serialize(kv.Key, ContentJson.Options)).Append(": ").Append(Inline(kv.Value));
            }
            return parts.Append(" }").ToString();
        }
        if (n is JsonArray a)
        {
            if (a.Count == 0) return "[]";
            var parts = new StringBuilder("[");
            for (int i = 0; i < a.Count; i++)
            {
                if (i > 0) parts.Append(", ");
                parts.Append(Inline(a[i]));
            }
            return parts.Append(']').ToString();
        }
        return Primitive(n);
    }

    private static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 2);

    private static void WriteNode(StringBuilder sb, JsonNode? node, int level)
    {
        if (node is JsonObject o)
        {
            if (IsInlineable(o, 0))
            {
                var inl = Inline(o);
                if (inl.Length + level * 2 <= MaxInline)
                {
                    sb.Append(inl);
                    return;
                }
            }
            if (o.Count == 0)
            {
                sb.Append("{}");
                return;
            }
            sb.Append("{\n");
            int i = 0;
            foreach (var kv in o)
            {
                Indent(sb, level + 1);
                sb.Append(JsonSerializer.Serialize(kv.Key, ContentJson.Options)).Append(": ");
                WriteNode(sb, kv.Value, level + 1);
                if (++i < o.Count) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, level);
            sb.Append('}');
            return;
        }
        if (node is JsonArray a)
        {
            if (IsInlineable(a, 0))
            {
                var inl = Inline(a);
                if (inl.Length + level * 2 <= MaxInline)
                {
                    sb.Append(inl);
                    return;
                }
            }
            if (a.Count == 0)
            {
                sb.Append("[]");
                return;
            }
            sb.Append("[\n");
            for (int i = 0; i < a.Count; i++)
            {
                Indent(sb, level + 1);
                WriteNode(sb, a[i], level + 1);
                if (i < a.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, level);
            sb.Append(']');
            return;
        }
        sb.Append(Primitive(node));
    }
}
