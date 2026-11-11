using System.Text;
using RnRomManager.Core.Library;
using RnRomManager.Core.Naming;
using RnRomManager.Core.Storage;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Storage;

/// <summary>Spec E1 naming and E2 byte budgets, clash suffixes and reservations.</summary>
public class ExternalRomNameTests
{
    private const string Cjk = "漢"; // 3 UTF-8 bytes, 1 UTF-16 unit
    private const string Astral = "\U0001F600"; // 4 UTF-8 bytes, a surrogate pair

    private static int Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    private static ExternalNameReservations None() => new([]);

    /// <summary>Valid UTF-16 (no lone surrogate) that round-trips through UTF-8 (no cut sequence).</summary>
    private static void AssertWholeCharacters(string name) =>
        Assert.That(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(name)), Is.EqualTo(name).And.Not.Contain('�'));

    // --- E1 / E2 names ---

    [Test]
    public void Build_IsZipBaseDotRomFileName()
    {
        const string zipBase = "Legend of Zelda, The - Tears of the Kingdom";
        const string rom = "The Legend of Zelda - Tears of the Kingdom [0100F2C0115B6000][v0].nsp";

        Assert.That(ExternalRomName.Build(zipBase, rom),
            Is.EqualTo("Legend of Zelda, The - Tears of the Kingdom.The Legend of Zelda - Tears of the Kingdom [0100F2C0115B6000][v0].nsp"));
        Assert.That(ExternalRomName.Build("Orbit", "Orbit [v0].nsp", 3), Is.EqualTo("Orbit.Orbit [v0] (3).nsp"));
    }

    [Test]
    public void Build_Over250Bytes_CutsTheRomStem_KeepsTheExtension()
    {
        var rom = new string('r', 296) + ".nsp"; // 300 bytes on its own

        var name = ExternalRomName.Build("Orbit", rom);

        Assert.That(Bytes(name), Is.EqualTo(250));
        Assert.That(name, Does.StartWith("Orbit.rrr").And.EndWith("r.nsp"));
    }

    [TestCase(Cjk)]
    [TestCase(Astral)]
    public void Build_NeverSplitsACharacter(string character)
    {
        // An odd offset, so the cut can't land on a boundary by luck.
        var rom = "x" + string.Concat(Enumerable.Repeat(character, 100)) + ".nsp";

        var name = ExternalRomName.Build("Orbit", rom);

        Assert.That(Bytes(name), Is.InRange(250 - Bytes(character) + 1, 250));
        Assert.That(name, Does.EndWith(character + ".nsp"));
        AssertWholeCharacters(name);
    }

    [Test]
    public void Build_LongMultiByteZipBase_CutsTheZipBaseOnceTheRomStemIsGone()
    {
        var zipBase = string.Concat(Enumerable.Repeat(Cjk, FileNameSanitizer.MaxLength)); // 180 chars, 540 bytes

        var name = ExternalRomName.Build(zipBase, "Orbit [0100000000010000][v0].nsp");
        var suffixed = ExternalRomName.Build(zipBase, "Orbit [0100000000010000][v0].nsp", 12);

        Assert.That(Bytes(name), Is.InRange(248, 250));
        Assert.That(name, Does.StartWith(Cjk).And.EndWith(Cjk + "..nsp"), "the rom stem is cut to nothing, the extension stays");
        Assert.That(Bytes(suffixed), Is.InRange(248, 250));
        Assert.That(suffixed, Does.EndWith(Cjk + ". (12).nsp"));
        AssertWholeCharacters(name);
        AssertWholeCharacters(suffixed);
    }

    [TestCase("CONIN$", "_CONIN$.Game.nsp")]
    [TestCase("COM¹", "_COM¹.Game.nsp")]
    [TestCase("COM1 ", "_COM1 .Game.nsp")] // Windows ignores the space before the first dot
    [TestCase("NUL .v2", "_NUL .v2.Game.nsp")]
    [TestCase("Console", "Console.Game.nsp")]
    public void Build_PrefixesADeviceAlias(string zipBase, string expected) =>
        Assert.That(ExternalRomName.Build(zipBase, "Game.nsp"), Is.EqualTo(expected));

    [Test]
    public void Build_DeviceAliasAtTheLimit_IsPrefixedAndStillFits()
    {
        var name = ExternalRomName.Build("LPT³ ", new string('r', 300) + ".nsp", 2);

        Assert.That(name, Does.StartWith("_LPT³ .rrr").And.EndWith("r (2).nsp"));
        Assert.That(Bytes(name), Is.EqualTo(250));
        Assert.That(FileNameSanitizer.IsReservedDeviceName(name), Is.False);
    }

    // --- allocation ---

    [Test]
    public void Allocate_Clash_TakesTheNextSuffix_AndReservesFinalAndStagingNames()
    {
        var reservations = new ExternalNameReservations(["orbit.ORBIT [v0].nsp", "Orbit.Orbit [v0] (2).nsp.tmp"]);

        var name = ExternalRomName.Allocate("Orbit", "Orbit [v0].nsp", reservations);

        Assert.That(name, Is.EqualTo("Orbit.Orbit [v0] (3).nsp"), "(1) is taken ignoring case, (2)'s staging name is occupied");
        Assert.That(reservations.IsTaken("Orbit.Orbit [v0] (3).nsp") && reservations.IsTaken("Orbit.Orbit [v0] (3).nsp.tmp"), Is.True);
        Assert.That(ExternalRomName.Allocate("Orbit", "Orbit [v0].nsp", reservations), Is.EqualTo("Orbit.Orbit [v0] (4).nsp"), "this write's own allocations count");
    }

    [Test]
    public void Allocate_ClashOnANameAtTheLimit_StaysWithinTheLimit()
    {
        var rom = new string('r', 300) + ".nsp";
        var reservations = None();

        var first = ExternalRomName.Allocate("Orbit", rom, reservations);
        var second = ExternalRomName.Allocate("Orbit", rom, reservations);

        Assert.That((Bytes(first), Bytes(second)), Is.EqualTo((250, 250)));
        Assert.That(second, Does.EndWith("r (2).nsp"));
        Assert.That(Bytes(second + ExternalRomName.StagingSuffix), Is.LessThanOrEqualTo(ExternalRomName.MaxComponentBytes));
    }

    [Test]
    public void Allocate_TwoLongStemsThatCutToTheSameName_Clash_EvenWhenTheFirstIsOnlyListedByTheDestinationZip()
    {
        var first = new string('r', 300) + " [v0].nsp";
        var second = new string('r', 300) + " [v65536].nsp";
        // The first release's file is missing on disk and the mirror hasn't seen it: only the opened ZIP lists it.
        using var dir = new TempDir();
        var listed = ExternalRomName.Build("Orbit", first);
        var reservations = ExternalNameReservations.ForFolder(dir.Path, "ext/O", [], [listed]);

        var name = ExternalRomName.Allocate("Orbit", second, reservations);

        Assert.That(ExternalRomName.Build("Orbit", second), Is.EqualTo(listed), "both cut to the same candidate");
        Assert.That(name, Is.Not.EqualTo(listed).And.EndWith(" (2).nsp"));
        Assert.That(Bytes(name), Is.EqualTo(250));
    }

    [Test]
    public void ForFolder_CountsDiskEntriesAndMirroredClaimsOfThatFolderOnly()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("On Disk.nsp"), "x");
        Directory.CreateDirectory(dir.Combine("A Folder.nsp"));
        ExternalRomRow Claim(string zipPath, string name) => new(Guid.NewGuid(), zipPath, name, 1, new string('a', 40));

        var reservations = ExternalNameReservations.ForFolder(dir.Path, "ext/O",
        [
            Claim("ext/O/Other Game.zip", "Mr. Driller.nsp"), // another game's file, missing on disk
            Claim("EXT/o/Third.zip", "Case Folder.nsp"),
            Claim("ext/P/Elsewhere.zip", "Elsewhere.nsp"),
            Claim("ext/O/deeper/Nested.zip", "Nested.nsp"),
            Claim("ext/Oh/Prefix.zip", "Prefix.nsp"),
        ]);

        Assert.That(new[] { "on disk.NSP", "a folder.nsp", "MR. DRILLER.nsp", "Case Folder.nsp" }.Select(reservations.IsTaken), Is.All.True);
        Assert.That(new[] { "Elsewhere.nsp", "Nested.nsp", "Prefix.nsp", "Free.nsp" }.Select(reservations.IsTaken), Is.All.False);
    }

    [Test]
    public void Allocate_NameAMirrorRowListsForAnotherGame_WithNoFileOnDisk_TakesASuffix()
    {
        using var dir = new TempDir();
        // "Mr. Driller" + "Encore.nsp" and "Mr" + " Driller.Encore.nsp" are the same file name.
        var claim = new ExternalRomRow(Guid.NewGuid(), "ext/M/Mr. Driller.zip", "Mr. Driller.Encore.nsp", 1, new string('a', 40));
        var reservations = ExternalNameReservations.ForFolder(dir.Path, "ext/M", [claim]);

        Assert.That(ExternalRomName.Allocate("Mr", " Driller.Encore.nsp", reservations), Is.EqualTo("Mr. Driller.Encore (2).nsp"));
    }

    [Test]
    public void Release_FreesANameTheWriteVacates()
    {
        var reservations = new ExternalNameReservations(["Orbit.Orbit [v0].nsp"]);

        reservations.Release("ORBIT.orbit [v0].nsp");

        Assert.That(ExternalRomName.Allocate("Orbit", "Orbit [v0].nsp", reservations), Is.EqualTo("Orbit.Orbit [v0].nsp"));
    }

    [Test]
    public void ForFolder_MissingFolder_HasNoDiskEntries()
    {
        using var dir = new TempDir();

        Assert.That(ExternalNameReservations.ForFolder(dir.Combine("not", "there"), "ext/N", []).IsTaken("x.nsp"), Is.False);
    }

    // --- ZIP names ---

    [Test]
    public void ZipName_ShortTitle_IsTheSanitizedTitle()
    {
        Assert.That(ExternalRomName.ZipName("Legend of Zelda, The - Tears of the Kingdom"), Is.EqualTo("Legend of Zelda, The - Tears of the Kingdom.zip"));
        Assert.That(ExternalRomName.ZipName("What?"), Is.EqualTo("What_.zip"));
        Assert.That(ExternalRomName.ZipName("Orbit", 2), Is.EqualTo("Orbit (2).zip"));
        Assert.That(ExternalRomName.ZipBase("Oh...Sir!!"), Is.EqualTo("Oh...Sir!!"));
    }

    [Test]
    public void ZipName_180CjkCharacters_FitsWithItsTempName_AlsoWithAClashSuffix()
    {
        var title = string.Concat(Enumerable.Repeat(Cjk, FileNameSanitizer.MaxLength));
        var reservations = None();

        var first = ExternalRomName.AllocateZipName(title, reservations);
        var second = ExternalRomName.AllocateZipName(title, reservations);

        Assert.That(first, Is.EqualTo(string.Concat(Enumerable.Repeat(Cjk, 82)) + ".zip"), "246 + 4 bytes");
        Assert.That(second, Is.EqualTo(string.Concat(Enumerable.Repeat(Cjk, 80)) + " (2).zip"), "the suffix costs the base two more characters");
        foreach (var name in new[] { first, second })
        {
            Assert.That(Bytes(name), Is.LessThanOrEqualTo(ExternalRomName.MaxNameBytes));
            Assert.That(Bytes(name + ".tmp"), Is.LessThanOrEqualTo(ExternalRomName.MaxComponentBytes));
            Assert.That(reservations.IsTaken(name + ".tmp"), Is.True);
            AssertWholeCharacters(name);
        }
    }

    [Test]
    public void ZipName_CutThatExposesASpaceOrDot_IsTrimmedLikeSanitize()
    {
        // 81 characters are 243 bytes; the 246-byte budget then ends inside " . ".
        var head = string.Concat(Enumerable.Repeat(Cjk, 81));

        Assert.That(ExternalRomName.ZipName(head + " . " + Cjk + Cjk), Is.EqualTo(head + ".zip"));
    }

    [TestCase("CONIN$", "_CONIN$.zip")]
    [TestCase("COM1 .5", "_COM1 .5.zip")]
    [TestCase("LPT²", "_LPT².zip")]
    [TestCase("CON", "_CON.zip")] // Sanitize's own prefix, not doubled
    public void ZipName_PrefixesADeviceAlias(string title, string expected) =>
        Assert.That(ExternalRomName.ZipName(title), Is.EqualTo(expected));

    [Test]
    public void SameZipBase_IgnoresCaseAndDifferencesPastTheByteCut()
    {
        var common = string.Concat(Enumerable.Repeat(Cjk, 100));

        Assert.That(ExternalRomName.SameZipBase("Orbit", "ORBIT"), Is.True);
        Assert.That(ExternalRomName.SameZipBase(common + "A", common + "B"), Is.True, "both cut to the same 82 characters");
        Assert.That(ExternalRomName.SameZipBase("Orbit", "Orbit 2"), Is.False);
        Assert.That(FileNameSanitizer.Sanitize(common + "A"), Is.Not.EqualTo(FileNameSanitizer.Sanitize(common + "B")), "today's comparison would rename");
    }

    [Test]
    public void IsZipNameFor_AcceptsTheBuilderNameAndCanonicalSuffixes()
    {
        var title = string.Concat(Enumerable.Repeat(Cjk, FileNameSanitizer.MaxLength));

        Assert.That(new[] { "Orbit.zip", "orbit.ZIP", "Orbit (2).zip", "Orbit (10).zip" }.Select(n => ExternalRomName.IsZipNameFor("Orbit", n)), Is.All.True);
        Assert.That(new[] { "Orbit (1).zip", "Orbit (02).zip", "Orbit (+2).zip", "Orbit (99999999999).zip", "Orbit(2).zip", "Orbit 2.zip", "Other.zip" }
            .Select(n => ExternalRomName.IsZipNameFor("Orbit", n)), Is.All.False);
        Assert.That(ExternalRomName.IsZipNameFor(title, ExternalRomName.ZipName(title, 2)), Is.True, "the base is shorter with a suffix");
        Assert.That(ExternalRomName.IsZipNameFor(title, ExternalRomName.ZipBase(title) + " (2).zip"), Is.False, "a name the builder wouldn't produce");
        Assert.That(ExternalRomName.IsZipNameFor("Orbit (2)", "Orbit (2).zip"), Is.True);
    }

    // --- repair backups ---

    [Test]
    public void AllocateRepairBackup_ShortPointer_AppendsTheSuffix_NextCandidateWhenOccupied()
    {
        var reservations = new ExternalNameReservations(["Orbit.Orbit [v0].nsp", "Orbit.Orbit [v0].nsp.repair.tmp"]);

        var backup = ExternalRomName.AllocateRepairBackup("Orbit.Orbit [v0].nsp", reservations);

        Assert.That(backup, Is.EqualTo("Orbit.Orbit [v0].nsp (2).repair.tmp"));
        Assert.That(reservations.IsTaken(backup), Is.True);
    }

    [Test]
    public void AllocateRepairBackup_255BytePointer_FitsTheComponentLimit_AlsoPastAnExistingBackup()
    {
        var pointer = "x" + string.Concat(Enumerable.Repeat(Cjk, 83)) + "y.nsp"; // 255 bytes
        var reservations = None();

        var first = ExternalRomName.AllocateRepairBackup(pointer, reservations);
        var second = ExternalRomName.AllocateRepairBackup(pointer, reservations);

        Assert.That(Bytes(pointer), Is.EqualTo(255));
        Assert.That(first, Does.EndWith(Cjk + ".repair.tmp"));
        Assert.That(second, Does.EndWith(Cjk + " (2).repair.tmp").And.Not.EqualTo(first));
        foreach (var name in new[] { first, second })
        {
            Assert.That(Bytes(name), Is.InRange(253, 255));
            AssertWholeCharacters(name);
        }
    }
}
