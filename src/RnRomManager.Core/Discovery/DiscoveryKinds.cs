namespace RnRomManager.Core.Discovery;

/// <summary>The route names of <see cref="DiscoveryKind"/> (B3): exactly <c>publishers</c>, <c>developers</c> or <c>genres</c>.</summary>
public static class DiscoveryKinds
{
    public static bool TryParse(string? value, out DiscoveryKind kind)
    {
        switch (value)
        {
            case "publishers": kind = DiscoveryKind.Publishers; return true;
            case "genres": kind = DiscoveryKind.Genres; return true;
            case "developers": kind = DiscoveryKind.Developers; return true;
            default: kind = default; return false;
        }
    }

    public static string Name(this DiscoveryKind kind) => kind switch
    {
        DiscoveryKind.Publishers => "publishers",
        DiscoveryKind.Genres => "genres",
        DiscoveryKind.Developers => "developers",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
