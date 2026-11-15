namespace RnRomManager.Core.Discovery;

/// <summary>
/// A discovery group's public key (B2, B3): the lowercase hexadecimal SHA-256 of the normalized name's UTF-8 bytes.
/// MariaDB computes it; the app only checks its shape.
/// </summary>
public static class DiscoveryKey
{
    public const int Length = 64;

    /// <summary>True when <paramref name="value"/> is exactly 64 lowercase hexadecimal characters.</summary>
    public static bool IsValid(string? value) =>
        value is { Length: Length } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
