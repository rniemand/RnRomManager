namespace RnRomManager.Core.Discovery;

/// <summary>
/// A shared logo for a publisher, genre or system (B5, B13). <see cref="Key"/> is the 64-character lowercase hexadecimal
/// key: the group key (B2) for publishers, developers and genres, <see cref="DiscoveryKey.ForSystem"/> for systems.
/// <see cref="MediaType"/> is <c>image/png</c> or <c>image/jpeg</c>; <see cref="UpdatedBy"/> is null once that user is deleted.
/// </summary>
public sealed record DiscoveryLogo(
    LogoKind Kind,
    string Key,
    string MediaType,
    byte[] Bytes,
    int? UpdatedBy,
    DateTime UpdatedAt);
