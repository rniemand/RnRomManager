namespace RnRomManager.Api.Tests.Integration;

internal sealed record LibraryEditorSaveTestGame(
    Guid Id,
    string ZipPath,
    string Zip,
    long[] RomIds,
    string[] FileNames);
