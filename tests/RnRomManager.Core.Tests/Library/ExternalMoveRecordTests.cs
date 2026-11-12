using System.Security.Cryptography;
using System.Text.Json;
using RnRomManager.Core.Library;
using RnRomManager.Core.Storage;
using RnRomManager.Core.Systems;
using RnRomManager.TestData;

namespace RnRomManager.Core.Tests.Library;

/// <summary>Spec W3/W7: the companion record that ties a move backup to its games, paths and release.</summary>
public class ExternalMoveRecordTests
{
    private const string SourceZip = "ext/S/Space Game.zip";
    private const string UsaPointer = "Space Game.Space Game (USA).nsp";
    private const string EuropePointer = "Space Game.Space Game (Europe).nsp";
    private static readonly byte[] Usa = RandomNumberGenerator.GetBytes(64);
    private static readonly byte[] Europe = RandomNumberGenerator.GetBytes(64);

    private TempDir _dir = null!;
    private string _roms = null!;
    private InfoJsonReader _reader = null!;
    private InfoJsonGame _game = null!;
    private ZipBackup _backup = null!;

    private static string Sha1(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    [SetUp]
    public async Task SetUp()
    {
        _dir = new TempDir();
        _roms = Directory.CreateDirectory(_dir.Combine("roms")).FullName;
        _reader = new InfoJsonReader([], new SystemRegistry([new ExternalTestHandler()]));
        static InfoJsonRom Release(string region, byte[] bytes, string pointer) =>
            TestLibrary.Rom($"Space Game ({region}).nsp", bytes) with { ExternalFileName = pointer };
        _game = TestLibrary.Game("Space Game", Release("USA", Usa, UsaPointer), Release("Europe", Europe, EuropePointer) with { IsPrimary = false })
            with { System = ExternalTestHandler.SystemId };
        var zip = Path.Combine(_roms, "ext", "S", "Space Game.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        await new LibraryZipWriter(_reader).WriteAsync(zip, null, new LibraryZipChanges(_game, new Dictionary<string, Func<Stream>>()));
        _backup = await ExternalZipBackup.CreateAsync(_roms, zip, ExternalRomPath.ClassifyFile(_roms, zip).Snapshot!);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private MoveRecord Record() => new(MoveRecord.SingleFileVersion, _backup.Sha256, _game.GameId, SourceZip, Guid.NewGuid(), "ext/T/Target.zip",
        Sha1(Usa), UsaPointer, "Target.Space Game (USA).nsp");

    [Test]
    public async Task Record_RoundTrips_AndStillIdentifiesTheMove_WhenBothZipsWereReplaced()
    {
        var record = Record();
        await ExternalMoveRecord.WriteAsync(_roms, _backup, record, _reader);
        File.WriteAllText(Path.Combine(_roms, "ext", "S", "Space Game.zip"), "someone else's ZIP");

        var read = await ExternalMoveRecord.ReadAsync(_roms, _backup.Path, _reader);

        Assert.That(read, Is.EqualTo(record));
        Assert.That((read.SourceZipPath, read.TargetZipPath, read.RomSha1), Is.EqualTo((SourceZip, "ext/T/Target.zip", Sha1(Usa))));
        Assert.That(File.Exists(ExternalMoveRecord.PathOf(_backup) + ExternalRomName.PartialSuffix), Is.False);
    }

    [TestCase("version", TestName = "Record_ThatDoesntMatchItsBackup_IsRejected unsupported version")]
    [TestCase("digest", TestName = "Record_ThatDoesntMatchItsBackup_IsRejected wrong backup digest")]
    [TestCase("pointer", TestName = "Record_ThatDoesntMatchItsBackup_IsRejected another release's pointer")]
    public async Task Record_ThatDoesntMatchItsBackup_IsRejected(string mismatch)
    {
        var record = mismatch switch
        {
            "version" => Record() with { Version = 2 },
            "digest" => Record() with { BackupSha256 = new string('0', 64) },
            _ => Record() with { SourceExternalFileName = EuropePointer }, // valid shape, but the USA SHA1's pointer is UsaPointer
        };
        // Written as a stray file would be, bypassing the writer's own check.
        File.WriteAllBytes(ExternalMoveRecord.PathOf(_backup),
            JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        Assert.That(() => ExternalMoveRecord.ReadAsync(_roms, _backup.Path, _reader), Throws.InstanceOf<InvalidDataException>());
        File.Delete(ExternalMoveRecord.PathOf(_backup));
        Assert.That(() => ExternalMoveRecord.WriteAsync(_roms, _backup, record, _reader), Throws.InstanceOf<IOException>());
        Assert.That(File.Exists(ExternalMoveRecord.PathOf(_backup)), Is.False, "nothing is published for a record that doesn't match");
    }

    [Test]
    public void OccupiedRecordName_KeepsTheOccupant_AndCleansUpItsOwnStaging()
    {
        var final = ExternalMoveRecord.PathOf(_backup);
        var hooks = new ExternalZipBackupHooks { BeforePromotion = _ => File.WriteAllText(final, "an occupant") };

        Assert.That(() => ExternalMoveRecord.WriteAsync(_roms, _backup, Record(), _reader, hooks), Throws.InstanceOf<IOException>());

        Assert.That(File.ReadAllText(final), Is.EqualTo("an occupant"));
        Assert.That(File.Exists(final + ExternalRomName.PartialSuffix), Is.False);
    }

    // --- B18: v2 records for releases with set files ---

    /// <summary>A FileSets game whose one release has <paramref name="files"/>, written and backed up; with its reader.</summary>
    private async Task<(ZipBackup Backup, InfoJsonGame Game, InfoJsonReader Reader)> SetBackupAsync(IReadOnlyList<string> files)
    {
        var reader = new InfoJsonReader([], new SystemRegistry([new ExternalTestHandler(), new ExternalSetTestHandler()]));
        const string main = "Disc (USA) (Track 1).bin";
        var rom = TestLibrary.Rom(main, Usa) with
        {
            ExternalFileName = main,
            Files = [.. files.Select((name, i) => new InfoJsonRomFile(name, name, name, 1, "00000000", new string('0', 32), new string((char)('a' + i % 6), 40)))],
        };
        var game = TestLibrary.Game("Disc", rom) with { System = ExternalSetTestHandler.SystemId };
        var zip = Path.Combine(_roms, "extset", "D", "Disc.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        await new LibraryZipWriter(reader).WriteAsync(zip, null, new LibraryZipChanges(game, new Dictionary<string, Func<Stream>>()));
        return (await ExternalZipBackup.CreateAsync(_roms, zip, ExternalRomPath.ClassifyFile(_roms, zip).Snapshot!), game, reader);
    }

    private static MoveRecord SetRecord(ZipBackup backup, InfoJsonGame game, IReadOnlyList<MoveRecordFile> files) =>
        new(MoveRecord.CurrentVersion, backup.Sha256, game.GameId, "extset/D/Disc.zip", Guid.NewGuid(), "extset/T/Target.zip",
            game.Roms[0].Metadata.Sha1, "Disc (USA) (Track 1).bin", "Disc (USA) (Track 1).bin", files);

    [Test]
    public async Task V2_With99PairsOfEscapedNames_FitsTheCap_AndRoundTrips()
    {
        // Each ' is written as a 6-byte ' escape: 99 × 2 × ~1,500 bytes, far over v1's 16 KiB.
        var names = Enumerable.Range(0, 99).Select(i => new string('\'', 244) + $"{i:00}.bin").ToList();
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(names[0]), Is.EqualTo(250));
        var (backup, game, reader) = await SetBackupAsync(names);
        var record = SetRecord(backup, game, [.. names.Select(n => new MoveRecordFile(n, n))]);

        await ExternalMoveRecord.WriteAsync(_roms, backup, record, reader);
        var read = await ExternalMoveRecord.ReadAsync(_roms, backup.Path, reader);

        Assert.That(new FileInfo(ExternalMoveRecord.PathOf(backup)).Length, Is.GreaterThan(16 * 1024).And.LessThanOrEqualTo(ExternalMoveRecord.MaxBytes));
        Assert.That(read with { Files = null }, Is.EqualTo(record with { Files = null }));
        Assert.That(read.Files, Is.EqualTo(record.Files));
    }

    [Test]
    public async Task V2_WhoseFilesAreNotTheBackedUpReleasesFiles_IsRefused()
    {
        var (backup, game, reader) = await SetBackupAsync(["Disc (USA).cue", "Disc (USA) (Track 2).bin"]);
        var record = SetRecord(backup, game, [new("Disc (USA).cue", "Disc (USA).cue"), new("Other (Track 2).bin", "Other (Track 2).bin")]);
        File.WriteAllBytes(ExternalMoveRecord.PathOf(backup),
            JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        Assert.That(async () => await ExternalMoveRecord.ReadAsync(_roms, backup.Path, reader), Throws.InstanceOf<InvalidDataException>());
        Assert.That(async () => await ExternalMoveRecord.WriteAsync(_roms, backup, record with { Files = [new("Disc (USA).cue", "x.cue")] }, reader),
            Throws.InstanceOf<IOException>(), "one pair for two files");
    }

    [TestCase(MoveRecord.SingleFileVersion, true)]
    [TestCase(MoveRecord.CurrentVersion, false)]
    public async Task VersionAndFiles_MustAgree(int version, bool withFiles)
    {
        var (backup, game, reader) = await SetBackupAsync(["Disc (USA).cue"]);
        var record = SetRecord(backup, game, [new("Disc (USA).cue", "Disc (USA).cue")]) with
        {
            Version = version, Files = withFiles ? [new("Disc (USA).cue", "Disc (USA).cue")] : null,
        };

        Assert.That(async () => await ExternalMoveRecord.WriteAsync(_roms, backup, record, reader), Throws.InstanceOf<IOException>());
    }

    [Test]
    public void V1_IsWrittenWithoutAFilesProperty()
    {
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(Record(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        Assert.That(json, Does.Not.Contain("files"));
        Assert.That(MoveRecord.VersionFor(null), Is.EqualTo(1));
    }
}
