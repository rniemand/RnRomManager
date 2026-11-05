using System.Text.Json;

namespace RnRomManager.Api.Configuration;

/// <summary>
/// One entry of the hand-maintained embedded <c>licenses.json</c> (spec §23 <c>/api/about</c>, §10.4): the app, every
/// direct NuGet/npm dependency and libretro-database. A test fails when a dependency is missing from it.
/// </summary>
public sealed record LicenseInfo(string Name, string License, string Url)
{
    public const string ResourceName = "RnRomManager.Api.licenses.json";

    private static readonly Lazy<IReadOnlyList<LicenseInfo>> Embedded = new(Load);

    public static IReadOnlyList<LicenseInfo> All => Embedded.Value;

    private static IReadOnlyList<LicenseInfo> Load()
    {
        using var stream = typeof(LicenseInfo).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return JsonSerializer.Deserialize<List<LicenseInfo>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
