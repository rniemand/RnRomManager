using System.Net;
using RnRomManager.Core.Media;

namespace RnRomManager.Core.Tests.Media;

/// <summary>U3: every blocked range at its first and last address, and the addresses that must stay reachable.</summary>
public class PublicAddressTests
{
    [TestCase("0.0.0.0"), TestCase("0.255.255.255")]
    [TestCase("10.0.0.0"), TestCase("10.255.255.255")]
    [TestCase("100.64.0.0"), TestCase("100.127.255.255")]
    [TestCase("127.0.0.0"), TestCase("127.255.255.255")]
    [TestCase("169.254.0.0"), TestCase("169.254.255.255")]
    [TestCase("172.16.0.0"), TestCase("172.31.255.255")]
    [TestCase("192.0.0.0"), TestCase("192.0.0.255")]
    [TestCase("192.0.2.0"), TestCase("192.0.2.255")]
    [TestCase("192.88.99.0"), TestCase("192.88.99.255")]
    [TestCase("192.168.0.0"), TestCase("192.168.255.255")]
    [TestCase("198.18.0.0"), TestCase("198.19.255.255")]
    [TestCase("198.51.100.0"), TestCase("198.51.100.255")]
    [TestCase("203.0.113.0"), TestCase("203.0.113.255")]
    [TestCase("224.0.0.0"), TestCase("239.255.255.255")]
    [TestCase("240.0.0.0"), TestCase("255.255.255.255")]
    [TestCase("::"), TestCase("::1"), TestCase("::ffff:ffff")]
    [TestCase("64:ff9b:1::"), TestCase("64:ff9b:1:ffff:ffff:ffff:ffff:ffff")]
    [TestCase("100::"), TestCase("100::ffff:ffff:ffff:ffff")]
    [TestCase("fc00::"), TestCase("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [TestCase("fe80::"), TestCase("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [TestCase("fec0::"), TestCase("feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [TestCase("ff00::"), TestCase("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [TestCase("2001:db8::"), TestCase("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff")]
    [TestCase("2001::"), TestCase("2001:0:ffff:ffff:ffff:ffff:ffff:ffff")]
    [TestCase("2002::"), TestCase("2002:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [TestCase("::ffff:127.0.0.1"), TestCase("::ffff:10.1.2.3")]
    [TestCase("64:ff9b::a00:1"), TestCase("64:ff9b::7f00:1")]
    public void Blocked(string address) => Assert.That(PublicAddress.IsPublic(IPAddress.Parse(address)), Is.False);

    [TestCase("8.8.8.8"), TestCase("1.1.1.1"), TestCase("100.63.255.255"), TestCase("100.128.0.0"), TestCase("172.32.0.0")]
    [TestCase("2606:4700:4700::1111"), TestCase("2a00:1450:4001::200e"), TestCase("2001:1::1")]
    [TestCase("::ffff:8.8.8.8"), TestCase("64:ff9b::808:808")]
    public void Allowed(string address) => Assert.That(PublicAddress.IsPublic(IPAddress.Parse(address)), Is.True);
}
