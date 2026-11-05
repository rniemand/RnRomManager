namespace RnRomManager.Systems.GameBoy;

/// <summary>The one way this project reads its embedded <c>Resources/</c> files (box art, header tables).</summary>
internal static class EmbeddedResources
{
    /// <param name="name">Relative to <c>Resources/</c>, dot-separated: <c>boxart.gb.png</c>.</param>
    public static byte[] Load(string name)
    {
        using var stream = typeof(EmbeddedResources).Assembly.GetManifestResourceStream($"RnRomManager.Systems.GameBoy.Resources.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
