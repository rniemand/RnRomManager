using RnRomManager.Core.Library;

namespace RnRomManager.Core.Reports;

/// <summary>Image Resolutions R4: one stored image with its pixel size.</summary>
public sealed record ImageResolutionItem(
    long ImageId,
    Guid GameId,
    string DisplayTitle,
    string SystemId,
    ImageType Type,
    int Width,
    int Height,
    long SizeBytes);
