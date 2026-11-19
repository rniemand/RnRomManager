namespace RnRomManager.Core.Storage;

/// <summary>ROM Zip Config B7: store <see cref="SystemId"/>'s ROMs inside its game ZIPs (<see cref="Embed"/>) or beside them.</summary>
public sealed record RomStorageChange(string SystemId, bool Embed);
