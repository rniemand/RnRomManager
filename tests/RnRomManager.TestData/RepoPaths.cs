namespace RnRomManager.TestData;

/// <summary>Locates the repository checkout from a test's output folder.</summary>
public static class RepoPaths
{
    /// <summary>The folder holding <c>RnRomManager.slnx</c>.</summary>
    public static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "RnRomManager.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
