using System.Text.Json;
using System.Text.Json.Serialization;

namespace FieldTrack.Api;

/// <summary>
/// Reads and writes every API enum as exactly one member name (any case on read), so a request cannot carry a
/// number or a comma-joined list. <see cref="JsonStringEnumConverter"/> reads "A, B" as both values OR-ed together,
/// which is either no member at all or silently another one.
/// </summary>
public sealed class EnumNameConverter : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(NameConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class NameConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return Enum.GetNames<TEnum>().FirstOrDefault(member => string.Equals(member, name, StringComparison.OrdinalIgnoreCase)) is { } match
                ? Enum.Parse<TEnum>(match)
                : throw new JsonException($"Expected one of the {typeof(TEnum).Name} names.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
