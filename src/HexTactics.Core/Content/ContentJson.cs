using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>Shared JSON settings for all content files.</summary>
public static class ContentJson
{
    public const int CurrentFormatVersion = 2;

    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new HexCoordJsonConverter());
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(JsonNode node) => node.Deserialize<T>(Options);

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    /// <summary>Deep copy through JSON (used for drafts and duplication).</summary>
    public static T Clone<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, value.GetType(), Options);
        return (T)JsonSerializer.Deserialize(json, value.GetType(), Options)!;
    }
}

/// <summary>HexCoord is stored compactly as [q, r].</summary>
public sealed class HexCoordJsonConverter : JsonConverter<HexCoord>
{
    public override HexCoord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            int q = reader.GetInt32();
            reader.Read();
            int r = reader.GetInt32();
            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Координата должна быть [q, r]");
            return new HexCoord(q, r);
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            int q = 0, r = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                if (string.Equals(name, "q", StringComparison.OrdinalIgnoreCase)) q = reader.GetInt32();
                else if (string.Equals(name, "r", StringComparison.OrdinalIgnoreCase)) r = reader.GetInt32();
            }
            return new HexCoord(q, r);
        }
        throw new JsonException("Координата должна быть [q, r]");
    }

    public override void Write(Utf8JsonWriter writer, HexCoord value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Q);
        writer.WriteNumberValue(value.R);
        writer.WriteEndArray();
    }
}
