namespace RnRomManager.Core.Library;

/// <summary>One image in <c>info.json</c> (spec §6.2 Image).</summary>
public sealed record InfoJsonImage
{
    public const string SourceLibretro = "libretro-thumbnails";
    public const string SourceDefault = "default";

    /// <summary>Entry name under <c>images/</c>.</summary>
    public required string FileName { get; init; }

    public required ImageType Type { get; init; }

    /// <summary><see cref="SourceLibretro"/> or <see cref="SourceDefault"/>.</summary>
    public required string Source { get; init; }

    public string? SourceUrl { get; init; }
    public required bool IsDefault { get; init; }

    /// <summary>Entry size, checked on read (§9.3, §12.1).</summary>
    public required long SizeBytes { get; init; }

    public required string Sha1 { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required DateTimeOffset DateAdded { get; init; }
}
