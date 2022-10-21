using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RajceDownloader.Main.Json;

internal sealed class RajceDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string dateStrValue = reader.GetString();
        if (string.IsNullOrWhiteSpace(dateStrValue))
        {
            return DateTime.UtcNow;
        }

        try
        {
            return DateTime.ParseExact(reader.GetString() ?? string.Empty, "yyyy-MM-dd HH:mm:ss", null, DateTimeStyles.RoundtripKind);
        }
        catch (FormatException)
        {
            return DateTime.UtcNow;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }
}