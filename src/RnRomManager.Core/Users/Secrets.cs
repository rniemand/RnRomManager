using System.Security.Cryptography;

namespace RnRomManager.Core.Users;

/// <summary>Random values for accounts, from the OS CSPRNG.</summary>
public static class Secrets
{
    private const string UrlSafe = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>20 URL-safe chars (~120 bits): generated admin / reset passwords (spec §9.1).</summary>
    public static string NewPassword() => RandomNumberGenerator.GetString(UrlSafe, 20);

    /// <summary>32 lowercase hex chars (128 bits) for <c>users.security_stamp</c> (spec §9.2).</summary>
    public static string NewSecurityStamp() => RandomNumberGenerator.GetHexString(32, lowercase: true);
}
