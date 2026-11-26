using System.Text;
using RnRomManager.Core.Systems;

namespace RnRomManager.Core.Tests.Systems;

/// <summary>D2: the shared decoder for ROM header text fields.</summary>
public class HeaderTextTests
{
    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    [Test]
    public void ReadAscii_StopsAtTheFirstNul() =>
        Assert.That(HeaderText.ReadAscii([.. Ascii("ZELDA"), 0, .. Ascii("JUNK")], trimStart: true), Is.EqualTo("ZELDA"));

    [Test]
    public void ReadAscii_ReplacesNonPrintableBytes() =>
        Assert.That(HeaderText.ReadAscii([(byte)'A', 0x1F, 0x7F, 0xC3, (byte)'B'], trimStart: true), Is.EqualTo("A???B"));

    [TestCase(true, "MARIO")]
    [TestCase(false, "  MARIO")]
    public void ReadAscii_TrimStartChoosesTheTrim(bool trimStart, string expected) =>
        Assert.That(HeaderText.ReadAscii(Ascii("  MARIO  "), trimStart), Is.EqualTo(expected));

    [Test]
    public void ReadAscii_TrimEndOnlyRemovesSpaces() =>
        Assert.That(HeaderText.ReadAscii(Ascii("MARIO\t "), trimStart: false), Is.EqualTo("MARIO?"));

    [TestCase(true)]
    [TestCase(false)]
    public void ReadAscii_EmptyIsNull(bool trimStart)
    {
        Assert.That(HeaderText.ReadAscii([], trimStart), Is.Null);
        Assert.That(HeaderText.ReadAscii(Ascii("    "), trimStart), Is.Null);
        Assert.That(HeaderText.ReadAscii([0, (byte)'X'], trimStart), Is.Null);
    }

    [Test]
    public void Printable_KeepsNulsAsQuestionMarksAndSpaces() =>
        Assert.That(HeaderText.Printable([(byte)'A', 0, (byte)' ', 0xFF]), Is.EqualTo("A? ?"));
}
