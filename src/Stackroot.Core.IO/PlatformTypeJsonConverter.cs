using System.Text.Json;
using System.Text.Json.Serialization;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.IO;

public sealed class PlatformTypeJsonConverter : JsonConverter<PlatformType>
{
    public override PlatformType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return PlatformType.WinX64;
            }

            var normalized = raw.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (Enum.TryParse<PlatformType>(normalized, ignoreCase: true, out var parsed))
            {
                return parsed;
            }
        }

        return PlatformType.WinX64;
    }

    public override void Write(Utf8JsonWriter writer, PlatformType value, JsonSerializerOptions options)
    {
        var text = value switch
        {
            PlatformType.WinArm64 => "win-arm64",
            _ => "win-x64"
        };
        writer.WriteStringValue(text);
    }
}
