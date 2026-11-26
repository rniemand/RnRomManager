namespace RnRomManager.Core.Users;

/// <summary>A global hotkey action (B21). <see cref="Role"/> null = every signed-in user.</summary>
public sealed record HotkeyAction(
    string Id,
    string Name,
    IReadOnlyList<string> Defaults,
    bool Editable,
    string? Role)
{
    public bool AllowedFor(IReadOnlySet<string> roles) => Role is null || roles.Contains(Role);
}
