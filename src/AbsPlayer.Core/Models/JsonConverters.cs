using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbsPlayer.Core.Models;

/// <summary>Shared serializer settings: camelCase, and missing or null required fields fail the element.</summary>
public static class AbsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Reads an array, dropping elements that don't match instead of failing the whole response.</summary>
public sealed class LossyListConverter<T> : JsonConverter<List<T>>
{
    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }
        var list = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            using var element = JsonDocument.ParseValue(ref reader);
            try
            {
                if (element.RootElement.Deserialize<T>(options) is { } item) list.Add(item);
            }
            catch (JsonException) { }
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

/// <summary>Accepts either a single object or an array of them. Anything else reads as null.</summary>
public sealed class OneOrManyConverter<T> : JsonConverter<List<T>>
{
    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        try
        {
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Object => doc.RootElement.Deserialize<T>(options) is { } one ? [one] : null,
                JsonValueKind.Array => doc.RootElement.Deserialize<List<T>>(options),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
