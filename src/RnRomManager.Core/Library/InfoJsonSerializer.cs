using System.Text.Json;
using System.Text.Json.Serialization;

namespace RnRomManager.Core.Library;

/// <summary>
/// The one set of JSON options for <c>info.json</c> (spec §6.1/§6.2): camelCase, indented, nulls written, enums as
/// names, UTF-8 without a BOM. Required members, required constructor parameters and nullable annotations are
/// enforced on read, so a missing or null ✓ field is a <see cref="JsonException"/>.
/// </summary>
public static class InfoJsonSerializer
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // The web defaults would accept "1989" for a number; §6.2 wants the right type.
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <returns>UTF-8 bytes, no BOM.</returns>
    public static byte[] Serialize(InfoJsonGame game) => JsonSerializer.SerializeToUtf8Bytes(game, Options);
}
