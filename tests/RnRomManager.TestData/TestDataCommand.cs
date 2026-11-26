using System.Globalization;

namespace RnRomManager.TestData;

public static class TestDataCommand
{
    public const string Usage = """
        Usage: --ingest <dir> --cache <dir> --set Basic|Full [--nds-dat <file>]...
               --nds-generate <file> --size <bytes> --seed <n>
          --nds-dat       list this ROM file in the seeded DS No-Intro DAT instead of the empty catalog (repeatable;
                          game name: the file name without its extension, ROM name: the file name)
          --nds-generate  write one synthetic DS ROM with a valid header CRC, streamed to disk
        """;

    public static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        TimeProvider time)
    {
        if (args is ["--help"]) { output.WriteLine(Usage); return 0; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ndsDat = new List<string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--ingest" or "--cache" or "--set" or "--nds-dat" or "--nds-generate" or "--size" or "--seed") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            { error.WriteLine(Usage); return 1; }
            if (args[i] == "--nds-dat")
                ndsDat.Add(args[i + 1]);
            else if (!values.TryAdd(args[i], args[i + 1]))
            { error.WriteLine(Usage); return 1; }
        }
        if (values.TryGetValue("--nds-generate", out var generate))
            return Generate(generate, values, ndsDat, output, error);
        if (values.ContainsKey("--size") || values.ContainsKey("--seed") ||
            !values.TryGetValue("--ingest", out var ingest) || !values.TryGetValue("--cache", out var cache) ||
            !values.TryGetValue("--set", out var name) || name is not ("Basic" or "Full"))
        { error.WriteLine(Usage); return 1; }
        try
        {
            IngestSeeder.Seed(ingest, cache, name == "Basic" ? SeedSet.Basic : SeedSet.Full, time, ndsDat);
            output.WriteLine($"Seeded {name} into {Path.GetFullPath(ingest)}; fixture DATs: {Path.Combine(Path.GetFullPath(cache), "dats")}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { error.WriteLine($"Seeding failed: {ex.Message}"); return 1; }
    }

    /// <summary><c>--nds-generate &lt;file&gt; --size &lt;bytes&gt; --seed &lt;n&gt;</c>, and nothing else.</summary>
    private static int Generate(
        string path,
        Dictionary<string, string> values,
        List<string> ndsDat,
        TextWriter output,
        TextWriter error)
    {
        if (values.Count != 3 || ndsDat.Count > 0 ||
            !values.TryGetValue("--size", out var sizeText) || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size < SyntheticNdsRomBuilder.HeaderEnd ||
            !values.TryGetValue("--seed", out var seedText) || !int.TryParse(seedText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seed))
        { error.WriteLine(Usage); return 1; }
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
                new SyntheticNdsRomBuilder { Seed = seed, SizeBytes = size, Title = $"SYNTH {seed}" }.WriteTo(file);
            output.WriteLine($"Wrote {size} bytes to {Path.GetFullPath(path)}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { error.WriteLine($"Generating failed: {ex.Message}"); return 1; }
    }
}
