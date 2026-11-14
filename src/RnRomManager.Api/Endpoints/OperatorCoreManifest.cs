using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Endpoints;

/// <summary>One operator-supplied core file: its exact name, length and lowercase hex SHA-256.</summary>
public sealed record OperatorCoreFile(string Name, long Length, string Sha256, string ContentType);

/// <summary>
/// B1: one EmulatorJS core the operator supplies (never bundled): its id, the name messages use, the option holding its
/// folder, the problem code of a mismatch and the only files <c>/api/emulator/cores/{core}</c> serves.
/// </summary>
public sealed record OperatorCore(string Id, string DisplayName, Func<EmulatorOptions, string> PathOf, string MismatchCode,
    IReadOnlyList<OperatorCoreFile> Files)
{
    /// <summary>The manifest entry with exactly this name (ordinal), or null.</summary>
    public OperatorCoreFile? Find(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// B1/B2: every operator-supplied core, each file pinned to the EmulatorJS 4.2.3 build so a corrupt, other-version or
/// altered file never reaches the browser. Registered as a singleton that tests replace.
/// </summary>
public sealed record OperatorCoreManifest(IReadOnlyList<OperatorCore> Cores)
{
    public const string GenesisPlusGx = "genesis_plus_gx";

    /// <summary>The pinned 4.2.3 files of each core (each report is its package's <c>reports/</c> copy).</summary>
    public static OperatorCoreManifest Pinned { get; } = new(
    [
        // @emulatorjs/core-genesis_plus_gx@4.2.3
        new(GenesisPlusGx, "Genesis Plus GX", o => o.GenesisCorePath, EmulatorEndpoints.GenesisCoreMismatch,
        [
            new("genesis_plus_gx-wasm.data", 1_203_661,
                "190297a6f86757405090f1a2266f67dfe1a570a528c583434ed3641a5664f768", "application/octet-stream"),
            new("genesis_plus_gx-legacy-wasm.data", 1_204_803,
                "2a1edeb68d7ec882149ed3cd5d3aa95a6ac231ebabaf82cdb56175a8b62967cc", "application/octet-stream"),
            new("genesis_plus_gx.json", 129,
                "5936a8ce8d7f010d5bfdd8c3bba2b2414f103b3a703121e56f2724b24dbe7ff3", "application/json"),
        ]),
    ]);

    /// <summary>The core with exactly this id (ordinal), or null.</summary>
    public OperatorCore? Find(string id) => Cores.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
}
