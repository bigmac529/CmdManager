using System.Text.Json;
using System.Text.Json.Serialization;

namespace CmdManager.Core.Http;

/// <summary>JSON settings shared by the API and its clients (camelCase, enums as strings).</summary>
public static class CmdManagerJson
{
    public static JsonSerializerOptions Options { get; } = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
            options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
