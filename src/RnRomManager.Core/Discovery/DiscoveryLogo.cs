namespace RnRomManager.Core.Discovery;

/// <summary>Which discovery list a shared logo belongs to (B13); stored lowercase in <c>discovery_logos.kind</c>.</summary>
public enum LogoKind
{
    Publishers,
    Genres,
    Systems,
}

/// <summary>
/// A shared logo for a publisher, genre or system (B5, B13). <see cref="Key"/> is the 64-character lowercase hexadecimal
/// key: the group key (B2) for publishers and genres, <see cref="DiscoveryKey.ForSystem"/> for systems.
/// <see cref="MediaType"/> is <c>image/png</c> or <c>image/jpeg</c>; <see cref="UpdatedBy"/> is null once that user is deleted.
/// </summary>
public sealed record DiscoveryLogo(LogoKind Kind, string Key, string MediaType, byte[] Bytes, int? UpdatedBy, DateTime UpdatedAt);
