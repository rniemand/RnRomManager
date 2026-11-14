namespace RnRomManager.Api.Endpoints;

/// <summary>One operator-supplied Genesis Plus GX file: its exact name, length and lowercase hex SHA-256.</summary>
public sealed record GenesisCoreFile(string Name, long Length, string Sha256, string ContentType);

/// <summary>
/// Genesis B4: the only files <c>/api/emulator/genesis</c> serves, each pinned to the EmulatorJS 4.2.3 build so a
/// corrupt, other-version or altered file never reaches the browser. Registered as a singleton that tests replace.
/// </summary>
public sealed record GenesisCoreManifest(IReadOnlyList<GenesisCoreFile> Files)
{
    /// <summary>The <c>@emulatorjs/core-genesis_plus_gx@4.2.3</c> files (the report is that package's <c>reports/</c> copy).</summary>
    public static GenesisCoreManifest Pinned { get; } = new(
    [
        new("genesis_plus_gx-wasm.data", 1_203_661,
            "190297a6f86757405090f1a2266f67dfe1a570a528c583434ed3641a5664f768", "application/octet-stream"),
        new("genesis_plus_gx-legacy-wasm.data", 1_204_803,
            "2a1edeb68d7ec882149ed3cd5d3aa95a6ac231ebabaf82cdb56175a8b62967cc", "application/octet-stream"),
        new("genesis_plus_gx.json", 129,
            "5936a8ce8d7f010d5bfdd8c3bba2b2414f103b3a703121e56f2724b24dbe7ff3", "application/json"),
    ]);

    /// <summary>The manifest entry with exactly this name (ordinal), or null.</summary>
    public GenesisCoreFile? Find(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
}
