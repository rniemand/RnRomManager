namespace RnRomManager.Systems.Sega;

internal static class EmbeddedResources
{
    public static byte[] Load(string name)
    {
        using var stream = typeof(EmbeddedResources).Assembly.GetManifestResourceStream($"RnRomManager.Systems.Sega.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
