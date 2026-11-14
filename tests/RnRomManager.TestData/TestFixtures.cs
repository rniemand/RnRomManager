namespace RnRomManager.TestData;

/// <summary>Committed binary fixtures (spec §15.2) and the builder output they must contain. See <c>Fixtures/README.md</c>.</summary>
public static class TestFixtures
{
    /// <summary>The one entry name inside <c>Fixtures/basic.7z</c>.</summary>
    public const string Basic7zEntry = "Seven Zip Demo (World).gb";

    /// <summary>The ROM inside <c>Fixtures/basic.7z</c>: rebuilt from a fixed seed, so DAT hashes stay valid.</summary>
    public static byte[] Basic7zRom() => new SyntheticRomBuilder { Seed = 7007, Title = "SEVENZIP" }.Build();

    /// <summary>Absolute path of <c>Fixtures/basic.7z</c> (copied next to the test assembly).</summary>
    public static string Basic7zPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "basic.7z");

    /// <summary>The DS ROM entry inside <c>Fixtures/nds.7z</c>.</summary>
    public const string Nds7zEntry = "Archive Tales (Europe).nds";

    /// <summary>The companion file inside <c>Fixtures/nds.7z</c>, which ingest skips.</summary>
    public const string Nds7zCompanion = "readme.txt";

    /// <summary>The bytes of <see cref="Nds7zCompanion"/>.</summary>
    public static byte[] Nds7zCompanionBytes() => "Companion file of the DS 7z fixture; ingest ignores it.\n"u8.ToArray();

    /// <summary>The ROM inside <c>Fixtures/nds.7z</c>: 128 KiB, a valid header and 0xFF after it, so the archive stays tiny.</summary>
    public static byte[] Nds7zRom() =>
        new SyntheticNdsRomBuilder { Seed = 7008, SizeBytes = 128 * 1024, Title = "ARCHIVE TALES", GameCode = "AATP", FillWithFF = true }.Build();

    /// <summary>Absolute path of <c>Fixtures/nds.7z</c> (copied next to the test assembly).</summary>
    public static string Nds7zPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "nds.7z");

    /// <summary>
    /// SHA-1 of <c>Fixtures/nds-marker/nds-marker.nds</c>, the original runnable DS cartridge (built from its source with
    /// <c>build.sh</c>; see <c>Fixtures/README.md</c>). A rebuild with the pinned image reproduces it.
    /// </summary>
    public const string NdsMarkerSha1 = "46e5d09f6629957c04f40f758626d5e97d9ee956";

    /// <summary>The name E2E stages the cartridge under, so the seeded DS DAT matches it as <c>Marker Test (World)</c>.</summary>
    public const string NdsMarkerStagedName = "Marker Test (World).nds";

    /// <summary>Absolute path of <c>Fixtures/nds-marker/nds-marker.nds</c> (copied next to the test assembly).</summary>
    public static string NdsMarkerPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "nds-marker", "nds-marker.nds");
}
