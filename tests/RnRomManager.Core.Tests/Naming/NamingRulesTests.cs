using RnRomManager.Core.Naming;

namespace RnRomManager.Core.Tests.Naming;

/// <summary>Spec §6.1 displayTitle and Sanitize, §5 bucket.</summary>
public class NamingRulesTests
{
    [TestCase("Legend of Zelda, The - Link's Awakening", "The Legend of Zelda - Link's Awakening")]
    [TestCase("Maus, Die", "Die Maus")]
    [TestCase("Addams Family, The", "The Addams Family")]
    [TestCase("Boy and His Blob, A - Rescue of Princess Blobette", "A Boy and His Blob - Rescue of Princess Blobette")]
    [TestCase("Tetris", "Tetris")]
    [TestCase("Bugs Bunny, Crazy Castle", "Bugs Bunny, Crazy Castle")] // not an article
    [TestCase("Thing, The - Part, The - Two", "The Thing - Part, The - Two")] // first article only (lazy match)
    public void DisplayTitle_MovesTheArticleToTheFront(string title, string expected) =>
        Assert.That(DisplayTitle.From(title), Is.EqualTo(expected));

    [Test]
    public void Sanitize_ReplacesEveryIllegalChar()
    {
        Assert.That(FileNameSanitizer.Sanitize("a\\b/c:d*e?f\"g<h>i|j"), Is.EqualTo("a_b_c_d_e_f_g_h_i_j"));
        Assert.That(FileNameSanitizer.Sanitize("tab\there\u0001\u007F"), Is.EqualTo("tab_here__"));
    }

    [Test]
    public void Sanitize_NormalisesToNfc()
    {
        Assert.That(FileNameSanitizer.Sanitize("Pokémon"), Is.EqualTo("Pokémon"));
    }

    [TestCase("  Tetris  ", "Tetris")]
    [TestCase("Dr. Mario...", "Dr. Mario")]
    [TestCase("Title . .", "Title")]
    [TestCase("...", "")]
    [TestCase("", "")]
    public void Sanitize_TrimsSpacesAndTrailingDots(string input, string expected) =>
        Assert.That(FileNameSanitizer.Sanitize(input), Is.EqualTo(expected));

    [TestCase("CON", "_CON")]
    [TestCase("nul", "_nul")]
    [TestCase("com1", "_com1")]
    [TestCase("LPT9.txt", "_LPT9.txt")]
    [TestCase("CONSOLE", "CONSOLE")]
    [TestCase("COM10", "COM10")]
    public void Sanitize_PrefixesWindowsReservedNames(string input, string expected) =>
        Assert.That(FileNameSanitizer.Sanitize(input), Is.EqualTo(expected));

    [Test]
    public void Sanitize_CutsAt180Chars_ThenTrims()
    {
        Assert.That(FileNameSanitizer.Sanitize(new string('x', 200)), Has.Length.EqualTo(180));
        Assert.That(FileNameSanitizer.Sanitize(new string('x', 179) + " yyyy"), Is.EqualTo(new string('x', 179)));
    }

    [Test]
    public void Sanitize_NeverSplitsASurrogatePair()
    {
        var result = FileNameSanitizer.Sanitize(new string('x', 179) + "\U0001F3AE" + "tail");

        Assert.That(result, Is.EqualTo(new string('x', 179)));
    }

    [TestCase("Tetris.zip", "T")]
    [TestCase("seaQuest DSV.zip", "S")]
    [TestCase("uCity.zip", "U")]
    [TestCase("Legend of Zelda, The - Link's Awakening.zip", "L")]
    [TestCase("4 in 1.zip", "#")]
    [TestCase("Été.zip", "#")]
    [TestCase("[BIOS] x.zip", "#")]
    [TestCase("", "#")]
    public void Bucket_IsTheUppercasedFirstAsciiLetterElseHash(string zipName, string expected) =>
        Assert.That(Bucket.For(zipName), Is.EqualTo(expected));

    [TestCase("NUL", true)]
    [TestCase("nul.txt", true)]
    [TestCase("NUL .nsp", true)]
    [TestCase("COM1 .nsp", true)]
    [TestCase("com\u00B9.nsp", true)]
    [TestCase("LPT\u00B3", true)]
    [TestCase("CONIN$", true)]
    [TestCase("conout$.nsp", true)]
    [TestCase("COM0", false)]
    [TestCase("COM\u2074", false)]
    [TestCase("CONSOLE", false)]
    [TestCase("x.NUL", false)]
    [TestCase("", false)]
    public void IsReservedDeviceName_TestsTheStemBeforeTheFirstDot(string name, bool expected) =>
        Assert.That(FileNameSanitizer.IsReservedDeviceName(name), Is.EqualTo(expected));

    [TestCase("COM\u00B9")]
    [TestCase("CONIN$")]
    [TestCase("LPT\u00B2.txt")]
    public void Sanitize_KeepsItsOwnReservedSet(string name) =>
        Assert.That(FileNameSanitizer.Sanitize(name), Is.EqualTo(name));
}
