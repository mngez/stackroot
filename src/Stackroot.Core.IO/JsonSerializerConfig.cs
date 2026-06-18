using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stackroot.Core.IO;

public static class JsonSerializerConfig
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    static JsonSerializerConfig()
    {
        Default.Converters.Add(new PlatformTypeJsonConverter());
        Default.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }
}
