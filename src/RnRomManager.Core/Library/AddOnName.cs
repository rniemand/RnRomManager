namespace RnRomManager.Core.Library;

/// <summary>A DLC or update's name (<c>Field</c> <c>dlc</c> or <c>update</c>), matched by <see cref="SearchMatch.Find"/>.</summary>
public sealed record AddOnName(string Field, string Name);
