using System.Net;
using System.Net.Sockets;

namespace RnRomManager.Core.Media;

/// <summary>
/// U3: whether an image-URL download may connect to an address. Private, loopback, link-local, multicast, reserved and
/// documentation ranges are refused, and so are IPv6 forms that tunnel to arbitrary IPv4 (6to4, Teredo, local NAT64);
/// IPv4-mapped and well-known NAT64 (<c>64:ff9b::/96</c>) addresses are judged by their embedded IPv4.
/// </summary>
public static class PublicAddress
{
    private static readonly IPNetwork[] BlockedV4 =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24",
            "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        }.Select(IPNetwork.Parse),
    ];

    private static readonly IPNetwork[] BlockedV6 =
    [
        .. new[]
        {
            "::/96", "64:ff9b:1::/48", "100::/64", "fc00::/7", "fe80::/10", "fec0::/10", "ff00::/8", "2001:db8::/32",
            "2001::/32", "2002::/16",
        }.Select(IPNetwork.Parse),
    ];

    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");

    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
                return IsPublicV4(address.MapToIPv4());
            if (Nat64.Contains(address))
                return IsPublicV4(new IPAddress(address.GetAddressBytes()[12..]));
            return !BlockedV6.Any(n => n.Contains(address));
        }
        return address.AddressFamily == AddressFamily.InterNetwork && IsPublicV4(address);
    }

    private static bool IsPublicV4(IPAddress address) => !BlockedV4.Any(n => n.Contains(address));
}
