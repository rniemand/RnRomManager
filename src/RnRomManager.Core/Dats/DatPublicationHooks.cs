namespace RnRomManager.Core.Dats;

/// <summary>Test seams of <see cref="DatPublication"/>: the directory renames and deletes it makes.</summary>
public sealed class DatPublicationHooks
{
    public Action<string, string> Move { get; init; } = Directory.Move;
    public Action<string> Delete { get; init; } = path => Directory.Delete(path, recursive: true);
}
