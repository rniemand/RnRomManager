namespace RnRomManager.Core.Dats;

/// <summary>A parsed <c>( … )</c> block: key/value pairs in file order; a value is a string or a nested <see cref="DatBlock"/>.</summary>
public sealed record DatBlock(IReadOnlyList<KeyValuePair<string, object>> Items)
{
    /// <summary>First string value for <paramref name="key"/>, or null.</summary>
    public string? Value(string key) => Items.FirstOrDefault(i => i.Key == key && i.Value is string).Value as string;

    /// <summary>Every nested block named <paramref name="key"/>, in order.</summary>
    public IEnumerable<DatBlock> Blocks(string key) => Items.Where(i => i.Key == key).Select(i => i.Value).OfType<DatBlock>();
}
