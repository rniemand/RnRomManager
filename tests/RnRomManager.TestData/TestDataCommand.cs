namespace RnRomManager.TestData;

public static class TestDataCommand
{
    public const string Usage = "Usage: --ingest <dir> --cache <dir> --set Basic|Full";

    public static int Run(string[] args, TextWriter output, TextWriter error, TimeProvider time)
    {
        if (args is ["--help"]) { output.WriteLine(Usage); return 0; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--ingest" or "--cache" or "--set") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i], args[i + 1]))
            { error.WriteLine(Usage); return 1; }
        }
        if (!values.TryGetValue("--ingest", out var ingest) || !values.TryGetValue("--cache", out var cache) ||
            !values.TryGetValue("--set", out var name) || name is not ("Basic" or "Full"))
        { error.WriteLine(Usage); return 1; }
        try
        {
            IngestSeeder.Seed(ingest, cache, name == "Basic" ? SeedSet.Basic : SeedSet.Full, time);
            output.WriteLine($"Seeded {name} into {Path.GetFullPath(ingest)}; fixture DATs: {Path.Combine(Path.GetFullPath(cache), "dats")}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { error.WriteLine($"Seeding failed: {ex.Message}"); return 1; }
    }
}
