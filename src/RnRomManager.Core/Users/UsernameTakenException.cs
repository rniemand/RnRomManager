namespace RnRomManager.Core.Users;

/// <summary>An insert hit the case-insensitive unique key on <c>users.username</c> (spec §9.1).</summary>
public sealed class UsernameTakenException(string username, Exception? inner = null)
    : Exception($"Username '{username}' is already taken.", inner);
