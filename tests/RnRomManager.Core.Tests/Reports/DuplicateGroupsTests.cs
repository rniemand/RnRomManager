using System.Security.Cryptography;
using System.Text;
using RnRomManager.Core.Reports;

namespace RnRomManager.Core.Tests.Reports;

public class DuplicateGroupsTests
{
    private static readonly Guid A = Guid.Parse("0a000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("1c000000-0000-0000-0000-000000000003");

    [Test]
    public void GamesKey_IsIndependentOfInputOrder()
    {
        Assert.That(DuplicateGroups.GamesKey([B, C, A]), Is.EqualTo(DuplicateGroups.GamesKey([A, B, C])));
    }

    [Test]
    public void GamesKey_IsLowerHexSha256OfTheOrdinallySortedLowerCaseIds()
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "0a000000-0000-0000-0000-000000000001,1c000000-0000-0000-0000-000000000003,b0000000-0000-0000-0000-000000000002")));

        Assert.That(DuplicateGroups.GamesKey([C, B, A]), Is.EqualTo(expected));
        Assert.That(expected, Does.Match("^[0-9a-f]{64}$"));
    }
}
