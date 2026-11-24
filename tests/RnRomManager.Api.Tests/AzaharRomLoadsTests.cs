using RnRomManager.Api.Endpoints;

namespace RnRomManager.Api.Tests;

/// <summary>3DS P2: the games list hides quick play for a 3DS file azahar can't load.</summary>
public class AzaharRomLoadsTests
{
    [TestCase("x.3ds", false, true)]
    [TestCase("x.CCI", false, true)]
    [TestCase("x.cia", false, false)]
    [TestCase("x.3ds", true, false)]
    [TestCase("x.cci", true, false)]
    public void AzaharRomLoads_TakesDecryptedCardDumpsOnly(string fileName, bool encrypted, bool loads) =>
        Assert.That(LibraryEndpoints.AzaharRomLoads(fileName, encrypted), Is.EqualTo(loads));
}