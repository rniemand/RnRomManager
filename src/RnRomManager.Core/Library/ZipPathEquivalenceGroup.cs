namespace RnRomManager.Core.Library;

/// <summary>Input paths the database treats as one <c>zip_path</c>, and the games already stored under it.</summary>
/// <param name="Paths">The input spellings, in input order; at least one.</param>
/// <param name="Existing">Rows whose <c>zip_path</c> equals them in the database (at most one today: the column is unique).</param>
public sealed record ZipPathEquivalenceGroup(IReadOnlyList<string> Paths, IReadOnlyList<ZipPathOwner> Existing);
