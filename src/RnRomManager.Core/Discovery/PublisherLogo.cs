namespace RnRomManager.Core.Discovery;

/// <summary>
/// A publisher's shared logo override (B5). <see cref="Key"/> is the group's public key: the lowercase hexadecimal
/// SHA-256 of the normalized name (B2). <see cref="MediaType"/> is <c>image/png</c> or <c>image/jpeg</c>;
/// <see cref="UpdatedBy"/> is null once that user is deleted.
/// </summary>
public sealed record PublisherLogo(string Key, string MediaType, byte[] Bytes, int? UpdatedBy, DateTime UpdatedAt);
