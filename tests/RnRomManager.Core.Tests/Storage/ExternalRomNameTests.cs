using System.Text;
using NSubstitute;
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
        Assert.That(ExternalRomName.Build("Orbit", "Orbit [v0].nsp", 3), Is.EqualTo("Orbit.[v0] (3).nsp"));
    }

    [TestCase("Death's Door", "Death's Door [0100B31015AF8000][v0].nsp", "Death's Door.[0100B31015AF8000][v0].nsp")]
    [TestCase("Death's Door", "Death's Door [0100B31015AF8800][v0][1.0.1].nsp", "Death's Door.[0100B31015AF8800][v0][1.0.1].nsp")]
    [TestCase("DEATH'S door", "Death's Door [0100B31015AF8000][v0].nsp", "DEATH'S door.[0100B31015AF8000][v0].nsp")]
    [TestCase("Orbit", "Orbit   [v0].nsp", "Orbit.[v0].nsp")]
    [TestCase("Orbit", "Orbit.nsp", "Orbit.Orbit.nsp")]
    [TestCase("Orbit", "Orbit .nsp", "Orbit.Orbit .nsp")]
    [TestCase("Orbit", "Orbitals [v0].nsp", "Orbit.Orbitals [v0].nsp")]
    [TestCase("Orbit (2)", "Orbit [v0].nsp", "Orbit (2).Orbit [v0].nsp")]
    [TestCase("Wait…", "Wait... [v0].nsp", "Wait….Wait... [v0].nsp")]
    public void Build_DropsTheTitleTheRomNameRepeats(string zipBase, string rom, string expected) =>
        Assert.That(ExternalRomName.Build(zipBase, rom), Is.EqualTo(expected));

    [Test]
    public void Build_StrippedName_StillTakesTheSuffixAndTheBudgetCut()
    {
        var rest = "[" + new string('r', 300) + "]";

        var name = ExternalRomName.Build("Orbit", $"Orbit {rest}.nsp", 2);

        Assert.That(name, Does.StartWith("Orbit.[rrr").And.EndWith("r (2).nsp"));
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(name), Is.EqualTo(ExternalRomName.MaxNameBytes));
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
        var reservations = new ExternalNameReservations(["orbit.[V0].nsp", "Orbit.[v0] (2).nsp.tmp"]);

        var name = ExternalRomName.Allocate("Orbit", "Orbit [v0].nsp", reservations);

        Assert.That(name, Is.EqualTo("Orbit.[v0] (3).nsp"), "(1) is taken ignoring case, (2)'s staging name is occupied");
        Assert.That(reservations.IsTaken("Orbit.[v0] (3).nsp") && reservations.IsTaken("Orbit.[v0] (3).nsp.tmp"), Is.True);
        Assert.That(ExternalRomName.Allocate("Orbit", "Orbit [v0].nsp", reservations), Is.EqualTo("Orbit.[v0] (4).nsp"), "this write's own allocations count");
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
        var reservations = new ExternalNameReservations(["Orbit.[v0].nsp"]);

        reservations.Release("ORBIT.[V0].nsp");

        Assert.That(ExternalRomName.Allocate("Orbit", "Orbit [v0].nsp", reservations), Is.EqualTo("Orbit.[v0].nsp"));
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

    // --- logical rom names (E3) ---

    private static int Scalars(string value) => value.EnumerateRunes().Count();

    [Test]
    public void LogicalName_ValidAndFree_IsKeptVerbatim_AndReserved()
    {
        var taken = None();
        var atLimit = new string('a', 251) + ".nsp";

        Assert.That(ExternalRomName.AllocateLogicalName("Orbit Test [0100000000010000][v0].nsp", taken), Is.EqualTo("Orbit Test [0100000000010000][v0].nsp"));
        Assert.That(ExternalRomName.AllocateLogicalName(atLimit, taken), Is.EqualTo(atLimit));
        Assert.That(taken.IsTaken("orbit test [0100000000010000][V0].NSP"), Is.True);
    }

    [Test]
    public void LogicalName_Collision_AddsASuffixBeforeTheExtension()
    {
        var taken = new ExternalNameReservations(["orbit.NSP"]);

        Assert.That(ExternalRomName.AllocateLogicalName("Orbit.nsp", taken), Is.EqualTo("Orbit (2).nsp"));
        Assert.That(ExternalRomName.AllocateLogicalName("Orbit.nsp", taken), Is.EqualTo("Orbit (3).nsp"));
    }

    [Test]
    public void LogicalName_255Scalars_Colliding_IsShortenedKeepingItsExtension()
    {
        var name = new string('a', 251) + ".nsp";
        var taken = new ExternalNameReservations([name]);

        var allocated = ExternalRomName.AllocateLogicalName(name, taken);

        Assert.That(allocated, Is.EqualTo(new string('a', 247) + " (2).nsp"));
        Assert.That(Scalars(allocated), Is.EqualTo(255));
    }

    [Test]
    public void LogicalName_CollisionAfterTruncation_UsesTheNextSuffix()
    {
        // Two different names over the limit that are identical once cut.
        var taken = None();
        var first = ExternalRomName.AllocateLogicalName(new string('a', 300) + "-one.nsp", taken);
        var second = ExternalRomName.AllocateLogicalName(new string('a', 300) + "-two.nsp", taken);
        var third = ExternalRomName.AllocateLogicalName(new string('a', 300) + "-three.nsp", taken);

        Assert.That(first, Is.EqualTo(new string('a', 251) + ".nsp"));
        Assert.That(second, Is.EqualTo(new string('a', 247) + " (2).nsp"));
        Assert.That(third, Is.EqualTo(new string('a', 247) + " (3).nsp"));
        Assert.That(new[] { first, second, third }.Select(Scalars), Is.All.EqualTo(255));
    }

    [Test]
    public void LogicalName_SupplementaryCharacters_CountOnceAndStayWhole()
    {
        // 252 scalars but 503 UTF-16 units: within the limit, so kept; one more scalar is cut as a whole pair.
        var fits = "x" + string.Concat(Enumerable.Repeat(Astral, 250)) + ".nsp";
        var tooLong = "x" + string.Concat(Enumerable.Repeat(Astral, 260)) + ".nsp";
        var taken = new ExternalNameReservations([fits]);

        var suffixed = ExternalRomName.AllocateLogicalName(fits, taken);
        var cut = ExternalRomName.AllocateLogicalName(tooLong, None());

        Assert.That(suffixed, Is.EqualTo("x" + string.Concat(Enumerable.Repeat(Astral, 246)) + " (2).nsp"));
        Assert.That(cut, Is.EqualTo("x" + string.Concat(Enumerable.Repeat(Astral, 250)) + ".nsp"));
        Assert.That((Scalars(suffixed), Scalars(cut)), Is.EqualTo((255, 255)));
        AssertWholeCharacters(suffixed);
        AssertWholeCharacters(cut);
    }

    [Test]
    public void LogicalName_GeneratedTitleIdAndVersionTags_SurviveTruncationAndSuffixes()
    {
        const string tags = " [0100F2C0115B6000][v65536]";
        var name = new string('n', 300) + tags + ".xci";
        var taken = None();

        var first = ExternalRomName.AllocateLogicalName(name, taken, tags);
        var second = ExternalRomName.AllocateLogicalName(name, taken, tags);

        Assert.That(first, Is.EqualTo(new string('n', 255 - tags.Length - 4) + tags + ".xci"));
        Assert.That(second, Is.EqualTo(new string('n', 255 - tags.Length - 8) + tags + " (2).xci"));
        Assert.That((Scalars(first), Scalars(second)), Is.EqualTo((255, 255)));
    }

    [Test]
    public void LogicalName_KeepTailThatIsNotTheEndOfTheStem_IsIgnored()
    {
        var name = new string('n', 300) + ".nsp";

        Assert.That(ExternalRomName.AllocateLogicalName(name, None(), " [v0]"), Is.EqualTo(new string('n', 251) + ".nsp"));
    }

    [TestCase("NUL.nsp", "_NUL.nsp")]
    [TestCase("COM1 .nsp", "_COM1 .nsp")]
    [TestCase("CONOUT$.xci", "_CONOUT$.xci")]
    [TestCase("Console.nsp", "Console.nsp")]
    public void LogicalName_DeviceName_IsPrefixed(string name, string expected) =>
        Assert.That(ExternalRomName.AllocateLogicalName(name, None()), Is.EqualTo(expected));

    [Test]
    public void LogicalName_DeviceNameAtTheLimit_ReservesThePrefixInTheBudget()
    {
        var name = "NUL." + new string('a', 247) + ".nsp"; // 255 scalars, a device name by its first part
        var taken = None();

        var first = ExternalRomName.AllocateLogicalName(name, taken);
        var second = ExternalRomName.AllocateLogicalName(name, taken);

        Assert.That(first, Is.EqualTo("_NUL." + new string('a', 246) + ".nsp"));
        Assert.That(second, Is.EqualTo("_NUL." + new string('a', 242) + " (2).nsp"));
        Assert.That((Scalars(first), Scalars(second)), Is.EqualTo((255, 255)));
    }

    // --- ZIP path allocation (E2 + E4) ---

    /// <summary>A repository where only the listed paths are equal to something: an existing game, or another input.</summary>
    private static IGameRepository Games(Guid? owner = null, params string[] equivalentToAnother)
    {
        var games = Substitute.For<IGameRepository>();
        games.ListZipPathEquivalenceAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var paths = call.Arg<IReadOnlyList<string>>();
            var clashing = paths.Where(equivalentToAnother.Contains).ToList();
            IReadOnlyList<ZipPathEquivalenceGroup> groups =
            [
                .. paths.Where(p => !clashing.Contains(p)).Select(p => new ZipPathEquivalenceGroup([p], [])),
                .. clashing.Count == 0 ? [] : new[]
                {
                    new ZipPathEquivalenceGroup(clashing, owner is { } id ? [new ZipPathOwner(id, "stored spelling")] : []),
                },
            ];
            return groups.OrderBy(g => paths.ToList().IndexOf(g.Paths[0])).ToList();
        });
        return games;
    }

    [Test]
    public async Task AllocateZipPath_FreeName_IsSystemBucketName()
    {
        using var dir = new TempDir();

        var path = await ExternalRomName.AllocateZipPathAsync(dir.Path, "ext", "Legend of Zelda, The", [], Games(), default);

        Assert.That(path, Is.EqualTo("ext/L/Legend of Zelda, The.zip"));
        Assert.That(await ExternalRomName.AllocateZipPathAsync(dir.Path, "ext", "3D Game", [], Games(), default), Is.EqualTo("ext/#/3D Game.zip"));
    }

    [Test]
    public async Task AllocateZipPath_NameOrTempNameOnDisk_TakesTheNextSuffix_WithoutAskingTheDatabase()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("ext", "O"));
        File.WriteAllText(dir.Combine("ext", "O", "ORBIT.ZIP"), "taken ignoring case");
        File.WriteAllText(dir.Combine("ext", "O", "Orbit (2).zip.tmp"), "a temp file occupies the staging name");
        var games = Games();

        var path = await ExternalRomName.AllocateZipPathAsync(dir.Path, "ext", "Orbit", [], games, default);

        Assert.That(path, Is.EqualTo("ext/O/Orbit (3).zip"));
        await games.Received(1).ListZipPathEquivalenceAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AllocateZipPath_PathEqualToAnotherGamesRow_TakesTheNextSuffix_ButNotForItsOwnRow()
    {
        using var dir = new TempDir();
        var owner = Guid.NewGuid();

        var other = await ExternalRomName.AllocateZipPathAsync(dir.Path, "ext", "Orbit", [], Games(owner, "ext/O/Orbit.zip"), default);
        var own = await ExternalRomName.AllocateZipPathAsync(dir.Path, "ext", "Orbit", [], Games(owner, "ext/O/Orbit.zip"), default, ownGameId: owner);

        Assert.That((other, own), Is.EqualTo(("ext/O/Orbit (2).zip", "ext/O/Orbit.zip")));
    }

    [Test]
    public async Task AllocateZipPath_PathEqualToAnotherZipOnDisk_TakesTheNextSuffix_AndItsOwnZipIsNotAsked()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("ext", "O"));
        File.WriteAllText(dir.Combine("ext", "O", "Other spelling.zip"), "equal to Orbit.zip for the database");
        File.WriteAllText(dir.Combine("ext", "O", "notes.txt"), "not a ZIP");
        var games = Games(null, "ext/O/Orbit.zip", "ext/O/Other spelling.zip");

        var path = await ExternalRomName.AllocateZipPathAsync(dir.Path, "ext", "Orbit", [], games, default);
        var renamed = await ExternalRomName.AllocateZipPathAsync(dir.Path, "ext", "Orbit", [], games, default, ownZipPath: "ext/O/Other spelling.zip");

        Assert.That((path, renamed), Is.EqualTo(("ext/O/Orbit (2).zip", "ext/O/Orbit.zip")));
        await games.Received().ListZipPathEquivalenceAsync(
            Arg.Is<IReadOnlyList<string>>(p => p.SequenceEqual(new[] { "ext/O/Orbit.zip", "ext/O/Other spelling.zip" })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AllocateZipPath_NameAnotherGamesExternalFileClaims_TakesTheNextSuffix()
    {
        using var dir = new TempDir();
        // Unlikely but possible: a hand-edited pointer named like a ZIP candidate would be overwritten by it.
        var claim = new ExternalRomRow(Guid.NewGuid(), "ext/O/Other.zip", "Orbit.zip", 1, new string('a', 40));

        Assert.That(await ExternalRomName.AllocateZipPathAsync(dir.Path, "ext", "Orbit", [claim], Games(), default), Is.EqualTo("ext/O/Orbit (2).zip"));
    }
}
