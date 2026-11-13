using RnRomManager.Core.Jobs;
using RnRomManager.Core.Library;
using RnRomManager.TestData;
using static RnRomManager.Core.Integrity.IntegrityIssueType;
using static RnRomManager.TestData.TestLibrary;

namespace RnRomManager.Data.Tests.Database;

/// <summary>Disc systems B19: each set file is checked like an external main file, keyed by its name.</summary>
public partial class IntegrityJobTests
{
    private const string SetZip = "extset/S/Set.zip";
    private const string SetMain = "Set (Track 1).bin";
    private const string SetCueFile = "Set.cue";
    private const string SetTrack2File = "Set (Track 2).bin";

    /// <summary>A FileSets game whose release is a data track plus a cue and an audio track; returns the folder.</summary>
    private async Task<string> WriteSet()
    {
        var path = Abs(SetZip);
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var parts = new (string Name, byte[] Bytes)[]
        {
            (SetMain, RomBytes(++_seed)),
            (SetCueFile, "FILE \"Set (Track 1).bin\" BINARY\nFILE \"Set (Track 2).bin\" BINARY\n"u8.ToArray()),
            (SetTrack2File, RomBytes(++_seed)),
        };
        foreach (var (name, bytes) in parts)
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
        var files = parts.Skip(1).Select(p => Rom(p.Name, p.Bytes).Metadata)
            .Zip(parts.Skip(1), (m, p) => new InfoJsonRomFile(p.Name, p.Name, p.Name, m.SizeBytes, m.Crc32, m.Md5, m.Sha1));
        var game = Game("Set", Rom(SetMain, parts[0].Bytes) with { ExternalFileName = SetMain, Files = [.. files] })
            with { System = ExternalSetTestHandler.SystemId };
        await new LibraryZipWriter(_reader).WriteAsync(path, null, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>()));
        Touch(SetZip);
        return folder;
    }

    [Test]
    public async Task Set_MissingTrack_IsEntryMissingByItsName_AndResolvesOnceItIsBack()
    {
        var folder = await WriteSet();
        var track2 = Path.Combine(folder, SetTrack2File);
        var bytes = File.ReadAllBytes(track2);
        File.Delete(track2);

        await Run();
        Assert.That(await OpenExternal(), Is.EqualTo(new[] { (EntryMissing, (string?)SetZip, (string?)SetTrack2File) }));

        File.WriteAllBytes(track2, bytes);
        await Run();
        Assert.That(await OpenExternal(), Is.Empty);
    }

    [Test]
    public async Task Set_TruncatedTrack_IsEntrySizeMismatch()
    {
        var folder = await WriteSet();
        var track2 = Path.Combine(folder, SetTrack2File);
        File.WriteAllBytes(track2, File.ReadAllBytes(track2)[..^1]);

        await Run();

        Assert.That(await OpenExternal(), Is.EqualTo(new[] { (EntrySizeMismatch, (string?)SetZip, (string?)SetTrack2File) }));
    }

    [Test]
    public async Task Set_DeepRunOnAChangedTrack_IsHashMismatch()
    {
        var folder = await WriteSet();
        ReplaceSameSize(Path.Combine(folder, SetCueFile));

        await Run();
        Assert.That(await OpenExternal(), Is.Empty, "a quick run doesn't hash");
        await Run(JobMode.Deep);

        Assert.That(await OpenExternal(), Is.EqualTo(new[] { (HashMismatch, (string?)SetZip, (string?)SetCueFile) }));
    }

    [Test]
    public async Task Set_FilesAreNotOrphans()
    {
        var folder = await WriteSet();
        File.WriteAllText(Path.Combine(folder, "Set (Track 3).bin"), "?");

        await Run(JobMode.Deep);

        Assert.That((await OpenExternal()).Select(i => (i.Type, i.ZipPath)), Is.EqualTo(new[] { (OrphanFile, (string?)"extset/S/Set (Track 3).bin") }));
    }
}
