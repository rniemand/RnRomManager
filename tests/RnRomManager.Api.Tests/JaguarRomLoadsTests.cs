using RnRomManager.Api.Endpoints;

namespace RnRomManager.Api.Tests;

/// <summary>The games list hides quick play for a Jaguar .j64/.rom whose size Virtual Jaguar can't place.</summary>
public class JaguarRomLoadsTests
{
    [TestCase("x.j64", 2097152, true)]
    [TestCase("x.J64", 131072, true)]
    [TestCase("x.rom", 2088960, true)]
    [TestCase("x.ROM", 16769024, true)]
    [TestCase("Jalaga.j64", 902176, false)]
    [TestCase("Jalaga.rom", 893984, false)]
    [TestCase("SuperFly DX.j64", 1572864, false)]
    [TestCase("SuperFly DX.rom", 1564672, false)]
    [TestCase("x.abs", 1832003, true)]
    [TestCase("x.cof", 1006649, true)]
    [TestCase("x.jag", 106258, true)]
    public void JaguarRomLoads_FollowsVirtualJaguarsSizeRules(string fileName, long size, bool loads) =>
        Assert.That(LibraryEndpoints.JaguarRomLoads(fileName, size), Is.EqualTo(loads));
}
