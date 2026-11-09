using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RnRomManager.Core.Collections;
using RnRomManager.Core.Configuration;
using RnRomManager.TestData;
using static RnRomManager.Core.Tests.Library.ImageFormatTests;

namespace RnRomManager.Core.Tests.Collections;

/// <summary>B2–B5, B4a, B8, B10–B11, B13–B14.</summary>
public class CollectionServiceTests
{
    private const int UserId = 7;
    private static readonly DateTime Created = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private ICollectionRepository _repository = null!;
    private TempDir _dir = null!;
    private CollectionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ICollectionRepository>();
        _repository.UpdateAsync(default!, default).ReturnsForAnyArgs(true);
        _dir = new TempDir();
        var covers = new CollectionCovers(Options.Create(new PathsOptions { Collections = _dir.Path }), NullLogger<CollectionCovers>.Instance);
        _service = new CollectionService(_repository, covers, _time);
    }

    [TearDown]
    public void TearDown() => _dir.Dispose();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private Collection Existing(string? coverFormat = null)
    {
        var collection = new Collection(Guid.NewGuid(), UserId, "Pokemon", "notes", coverFormat,
            coverFormat is null ? null : Created, Created, Created, 3);
        _repository.GetAsync(collection.Id, UserId, Arg.Any<CancellationToken>()).Returns(collection);
        return collection;
    }

    private string[] Files() => Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Order().ToArray()!;

    private void WriteFile(Guid id, string format) => File.WriteAllBytes(Path.Combine(_dir.Path, $"{id:D}.{format}"), [0]);

    // --- validation (B2) ---

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(100, true)]
    [TestCase(101, false)]
    public async Task Create_NameLength(int length, bool valid)
    {
        var result = await _service.CreateAsync(UserId, new string('é', length), null, default);

        Assert.That(result.Error, Is.EqualTo(valid ? CollectionResult.Failure.None : CollectionResult.Failure.ValidationFailed));
        if (!valid)
            Assert.That(result.Errors!.Keys, Is.EquivalentTo(new[] { "name" }));
    }

    [Test]
    public async Task Create_TrimsTheName_CountsCodePoints_AndRejectsNullOrBlank()
    {
        var result = await _service.CreateAsync(UserId, "  " + string.Concat(Enumerable.Repeat("😀", 100)) + " ", null, default);
        Assert.That(result.Collection!.Name, Is.EqualTo(string.Concat(Enumerable.Repeat("😀", 100))));

        Assert.That((await _service.CreateAsync(UserId, null, null, default)).Errors!.Keys, Is.EquivalentTo(new[] { "name" }));
        Assert.That((await _service.CreateAsync(UserId, "   ", null, default)).Errors!.Keys, Is.EquivalentTo(new[] { "name" }));
    }

    [Test]
    public async Task Create_NormalisesNotes()
    {
        Assert.That((await _service.CreateAsync(UserId, "a", " \r\n\t", default)).Collection!.Notes, Is.Null);
        Assert.That((await _service.CreateAsync(UserId, "b", "one\r\ntwo\rthree", default)).Collection!.Notes, Is.EqualTo("one\ntwo\nthree"));
    }

    [TestCase(65536, true)]
    [TestCase(65537, false)]
    public async Task Create_NotesBytes(int bytes, bool valid)
    {
        var notes = new string('é', bytes / 2) + new string('x', bytes % 2);

        var result = await _service.CreateAsync(UserId, "a", notes, default);

        Assert.That(result.Error, Is.EqualTo(valid ? CollectionResult.Failure.None : CollectionResult.Failure.ValidationFailed));
        if (!valid)
            Assert.That(result.Errors!.Keys, Is.EquivalentTo(new[] { "notes" }));
    }

    [Test]
    public async Task Create_InsertsForTheCaller_WithBothTimestampsNow()
    {
        var result = await _service.CreateAsync(UserId, "Pokemon", "n", default);

        var c = result.Collection!;
        Assert.That((c.UserId, c.Name, c.Notes, c.CoverFormat, c.CreatedAt, c.UpdatedAt), Is.EqualTo((UserId, "Pokemon", "n", (string?)null, Now, Now)));
        await _repository.Received(1).InsertAsync(c, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateOrRename_ToATakenName_IsNameTaken()
    {
        _repository.InsertAsync(default!, default).ThrowsAsyncForAnyArgs(new CollectionNameTakenException("x"));
        _repository.UpdateAsync(default!, default).ThrowsAsyncForAnyArgs(new CollectionNameTakenException("x"));
        var existing = Existing();

        Assert.That((await _service.CreateAsync(UserId, "x", null, default)).Error, Is.EqualTo(CollectionResult.Failure.NameTaken));
        Assert.That((await _service.UpdateAsync(existing.Id, UserId, new CollectionPatch("x"), default)).Error,
            Is.EqualTo(CollectionResult.Failure.NameTaken));
    }

    // --- update (B4, B4a) ---

    [Test]
    public async Task Update_AbsentFieldsAreUnchanged_NullNotesClear_AndUpdatedAtMoves()
    {
        var existing = Existing();

        var renamed = (await _service.UpdateAsync(existing.Id, UserId, new CollectionPatch(" New "), default)).Collection!;
        var cleared = (await _service.UpdateAsync(existing.Id, UserId, new CollectionPatch(NotesSet: true, Notes: ""), default)).Collection!;

        Assert.That(renamed, Is.EqualTo(existing with { Name = "New", UpdatedAt = Now }));
        Assert.That(cleared, Is.EqualTo(existing with { Notes = null, UpdatedAt = Now }));
    }

    [Test]
    public async Task Update_ThatChangesNothing_DoesNotWrite()
    {
        var existing = Existing();

        var result = await _service.UpdateAsync(existing.Id, UserId, new CollectionPatch("Pokemon", true, "notes"), default);

        Assert.That(result.Collection, Is.EqualTo(existing));
        await _repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Test]
    public async Task ForeignOrMissingId_IsNotFound_ForEveryCall()
    {
        var id = Guid.NewGuid();

        Assert.That((await _service.UpdateAsync(id, UserId, new CollectionPatch("x"), default)).Error, Is.EqualTo(CollectionResult.Failure.NotFound));
        Assert.That((await _service.DeleteAsync(id, UserId, default)).Error, Is.EqualTo(CollectionResult.Failure.NotFound));
        Assert.That((await _service.SetCoverAsync(id, UserId, Png(1, 1), default)).Error, Is.EqualTo(CollectionResult.Failure.NotFound));
        Assert.That((await _service.RemoveCoverAsync(id, UserId, default)).Error, Is.EqualTo(CollectionResult.Failure.NotFound));
        Assert.That((await _service.AddGamesAsync(id, UserId, [Guid.NewGuid()], default)).Error, Is.EqualTo(CollectionResult.Failure.NotFound));
        Assert.That((await _service.RemoveGameAsync(id, UserId, Guid.NewGuid(), default)).Error, Is.EqualTo(CollectionResult.Failure.NotFound));
        Assert.That(Files(), Is.Empty);
    }

    // --- delete (B5) ---

    [Test]
    public async Task Delete_RemovesTheCoverFiles()
    {
        var id = Guid.NewGuid();
        _repository.DeleteAsync(id, UserId, Arg.Any<CancellationToken>()).Returns(true);
        WriteFile(id, "png");
        WriteFile(id, "jpg");

        Assert.That((await _service.DeleteAsync(id, UserId, default)).Error, Is.EqualTo(CollectionResult.Failure.None));
        Assert.That(Files(), Is.Empty);
    }

    // --- cover (B8, B10, B11) ---

    [Test]
    public async Task SetCover_RejectsNonImagesAndUnreadableDimensions()
    {
        var existing = Existing();

        var notImage = await _service.SetCoverAsync(existing.Id, UserId, [1, 2, 3], default);
        var truncated = await _service.SetCoverAsync(existing.Id, UserId, Png(1, 1)[..12], default);

        Assert.That(notImage.Errors!["file"], Is.EqualTo(new[] { "The image must be a PNG or JPEG." }));
        Assert.That(truncated.Errors!["file"], Is.EqualTo(new[] { "The image's dimensions can't be read." }));
        Assert.That(Files(), Is.Empty);
    }

    [Test]
    public async Task SetCover_WritesTheFile_UpdatesTheRow_AndDropsTheOtherFormat()
    {
        var existing = Existing(coverFormat: "png");
        WriteFile(existing.Id, "png");

        var result = await _service.SetCoverAsync(existing.Id, UserId, Jpeg(0xC0, 2, 2), default);

        Assert.That(result.Collection, Is.EqualTo(existing with { CoverFormat = "jpg", CoverUpdatedAt = Now, UpdatedAt = Now }));
        await _repository.Received(1).UpdateAsync(result.Collection!, Arg.Any<CancellationToken>());
        Assert.That(Files(), Is.EqualTo(new[] { $"{existing.Id:D}.jpg" }));
    }

    [Test]
    public async Task SetCover_RowUpdateFails_DeletesANewDifferentFormatFile()
    {
        var existing = Existing(coverFormat: "png");
        WriteFile(existing.Id, "png");
        _repository.UpdateAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetCoverAsync(existing.Id, UserId, Jpeg(0xC0, 2, 2), default));

        Assert.That(Files(), Is.EqualTo(new[] { $"{existing.Id:D}.png" }));
    }

    [Test]
    public async Task SetCover_RowUpdateFails_KeepsASameFormatReplacement()
    {
        var existing = Existing(coverFormat: "png");
        WriteFile(existing.Id, "png");
        _repository.UpdateAsync(default!, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetCoverAsync(existing.Id, UserId, Png(2, 2), default));

        Assert.That(Files(), Is.EqualTo(new[] { $"{existing.Id:D}.png" }));
        Assert.That(File.ReadAllBytes(Path.Combine(_dir.Path, $"{existing.Id:D}.png")), Is.EqualTo(Png(2, 2)));
    }

    [Test]
    public async Task SetCover_RowGoneMeanwhile_IsNotFound_AndDeletesTheNewFile()
    {
        var existing = Existing();
        _repository.UpdateAsync(default!, default).ReturnsForAnyArgs(false);

        Assert.That((await _service.SetCoverAsync(existing.Id, UserId, Png(1, 1), default)).Error, Is.EqualTo(CollectionResult.Failure.NotFound));
        Assert.That(Files(), Is.Empty);
    }

    [Test]
    public async Task RemoveCover_ClearsTheColumns_AndDeletesBothFiles()
    {
        var existing = Existing(coverFormat: "jpg");
        WriteFile(existing.Id, "png");
        WriteFile(existing.Id, "jpg");

        var result = await _service.RemoveCoverAsync(existing.Id, UserId, default);

        Assert.That(result.Collection, Is.EqualTo(existing with { CoverFormat = null, CoverUpdatedAt = null, UpdatedAt = Now }));
        Assert.That(Files(), Is.Empty);
    }

    [Test]
    public async Task RemoveCover_WithoutACover_DoesNotWrite()
    {
        var existing = Existing();

        Assert.That((await _service.RemoveCoverAsync(existing.Id, UserId, default)).Collection, Is.EqualTo(existing));
        await _repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    // --- membership (B13, B14, B4a) ---

    [TestCase(0)]
    [TestCase(501)]
    public async Task AddGames_OutsideOneTo500Ids_IsInvalid(int count)
    {
        var existing = Existing();

        var result = await _service.AddGamesAsync(existing.Id, UserId, Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList(), default);

        Assert.That(result.Errors!.Keys, Is.EquivalentTo(new[] { "gameIds" }));
        Assert.That((await _service.AddGamesAsync(existing.Id, UserId, null, default)).Errors!.Keys, Is.EquivalentTo(new[] { "gameIds" }));
    }

    [Test]
    public async Task AddGames_ReturnsTheAddedCount_AndTouchesUpdatedAtOnlyWhenSomethingWasAdded()
    {
        var existing = Existing();
        var ids = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToList();
        _repository.AddGamesAsync(existing.Id, default!, default, default).ReturnsForAnyArgs(2, 0);

        Assert.That((await _service.AddGamesAsync(existing.Id, UserId, ids, default)).Added, Is.EqualTo(2));
        Assert.That((await _service.AddGamesAsync(existing.Id, UserId, ids, default)).Added, Is.Zero);

        await _repository.Received(2).AddGamesAsync(existing.Id, Arg.Is<IReadOnlyCollection<Guid>>(g => g.Count == 500), Now, Arg.Any<CancellationToken>());
        await _repository.Received(1).UpdateAsync(existing with { UpdatedAt = Now }, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RemoveGame_TouchesUpdatedAtOnlyWhenARowWasDeleted()
    {
        var existing = Existing();
        var member = Guid.NewGuid();
        _repository.RemoveGameAsync(existing.Id, member, Arg.Any<CancellationToken>()).Returns(true);

        Assert.That((await _service.RemoveGameAsync(existing.Id, UserId, Guid.NewGuid(), default)).Error, Is.EqualTo(CollectionResult.Failure.None));
        await _repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);

        await _service.RemoveGameAsync(existing.Id, UserId, member, default);
        await _repository.Received(1).UpdateAsync(existing with { UpdatedAt = Now }, Arg.Any<CancellationToken>());
    }
}
