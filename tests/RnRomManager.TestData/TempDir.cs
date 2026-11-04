namespace RnRomManager.TestData;

/// <summary>A real, empty temp directory for one test; deleted on dispose (plan §1.4: dispose it in TearDown).</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("rnrm-test-").FullName;

    /// <summary>Full path of <paramref name="relative"/> inside this directory (not created).</summary>
    public string Combine(params string[] relative) => System.IO.Path.Combine([Path, .. relative]);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
