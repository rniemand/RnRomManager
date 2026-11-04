using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using RnRomManager.Core.Users;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

[Category("Integration")]
public class UserRepositoryTests
{
    private UserRepository _repository = null!;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _repository = new UserRepository(factory);
    }

    private static User NewUser(string username = "richard") => new()
    {
        Username = username,
        PasswordHash = "hash",
        Role = UserRole.User,
        SecurityStamp = new string('a', 32),
        CreatedAt = new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234560),
        UpdatedAt = new DateTime(2026, 11, 4, 11, 0, 0, DateTimeKind.Utc),
    };

    [Test]
    public async Task Insert_ThenGet_RoundTripsEveryField()
    {
        var user = NewUser() with
        {
            Role = UserRole.Admin,
            IsDisabled = true,
            MustChangePassword = true,
            FailedLoginCount = 3,
            LockoutUntil = new DateTime(2026, 11, 4, 12, 15, 0, DateTimeKind.Utc),
            LastLoginAt = new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc),
        };

        var id = await _repository.InsertAsync(user, CancellationToken.None);
        var loaded = await _repository.GetByUsernameAsync("richard", CancellationToken.None);

        Assert.That(id, Is.GreaterThan(0));
        Assert.That(loaded, Is.EqualTo(user with { Id = id }));
        Assert.That(loaded!.CreatedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public async Task NullableFields_RoundTripAsNull()
    {
        await _repository.InsertAsync(NewUser(), CancellationToken.None);

        var loaded = await _repository.GetByUsernameAsync("richard", CancellationToken.None);

        Assert.That(loaded!.LockoutUntil, Is.Null);
        Assert.That(loaded.LastLoginAt, Is.Null);
        Assert.That(loaded.Role, Is.EqualTo(UserRole.User));
    }

    [Test]
    public async Task GetByUsername_IgnoresCase_AndReturnsNullWhenMissing()
    {
        await _repository.InsertAsync(NewUser("Richard"), CancellationToken.None);

        Assert.That((await _repository.GetByUsernameAsync("rICHARD", CancellationToken.None))?.Username, Is.EqualTo("Richard"));
        Assert.That(await _repository.GetByUsernameAsync("nobody", CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Count_CountsRows()
    {
        Assert.That(await _repository.CountAsync(CancellationToken.None), Is.Zero);
        await _repository.InsertAsync(NewUser("one"), CancellationToken.None);
        await _repository.InsertAsync(NewUser("two"), CancellationToken.None);

        Assert.That(await _repository.CountAsync(CancellationToken.None), Is.EqualTo(2));
    }

    [Test]
    public async Task Insert_DuplicateUsernameIgnoringCase_Throws()
    {
        await _repository.InsertAsync(NewUser("admin"), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UsernameTakenException>(() => _repository.InsertAsync(NewUser("ADMIN"), CancellationToken.None));

        Assert.That(ex!.InnerException, Is.InstanceOf<MySqlException>());
    }

    [Test]
    public async Task GetById_ReturnsTheRowOrNull()
    {
        var id = await _repository.InsertAsync(NewUser(), CancellationToken.None);

        Assert.That((await _repository.GetByIdAsync(id, CancellationToken.None))?.Username, Is.EqualTo("richard"));
        Assert.That(await _repository.GetByIdAsync(id + 1, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Update_WritesEveryMutableField()
    {
        var id = await _repository.InsertAsync(NewUser(), CancellationToken.None);
        var changed = NewUser() with
        {
            Id = id,
            PasswordHash = "new-hash",
            Role = UserRole.Admin,
            IsDisabled = true,
            MustChangePassword = true,
            FailedLoginCount = 2,
            LockoutUntil = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc),
            SecurityStamp = new string('b', 32),
            UpdatedAt = new DateTime(2026, 11, 5, 1, 0, 0, DateTimeKind.Utc),
            LastLoginAt = new DateTime(2026, 11, 5, 2, 0, 0, DateTimeKind.Utc),
        };

        await _repository.UpdateAsync(changed, CancellationToken.None);

        Assert.That(await _repository.GetByIdAsync(id, CancellationToken.None), Is.EqualTo(changed));
    }

    [Test]
    public async Task FailedLogins_IncrementLockAndResetOnSuccess()
    {
        var id = await _repository.InsertAsync(NewUser(), CancellationToken.None);
        var until = new DateTime(2026, 11, 4, 12, 15, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        var counts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _repository.IncrementFailedLoginCountAsync(id, CancellationToken.None)));
        Assert.That(counts, Is.EquivalentTo(new[] { 1, 2, 3, 4, 5 }), "parallel increments are all counted");

        await _repository.LockOutAsync(id, until, now, CancellationToken.None);
        var locked = await _repository.GetByIdAsync(id, CancellationToken.None);
        Assert.That((locked!.FailedLoginCount, locked.LockoutUntil, locked.UpdatedAt), Is.EqualTo((0, (DateTime?)until, now)));

        await _repository.IncrementFailedLoginCountAsync(id, CancellationToken.None);
        await _repository.RecordSuccessfulLoginAsync(id, now, CancellationToken.None);
        var loggedIn = await _repository.GetByIdAsync(id, CancellationToken.None);
        Assert.That((loggedIn!.FailedLoginCount, loggedIn.LockoutUntil, loggedIn.LastLoginAt), Is.EqualTo((0, (DateTime?)null, (DateTime?)now)));
    }

    [Test]
    public async Task List_ReturnsEveryUserOrderedByName()
    {
        await _repository.InsertAsync(NewUser("zed"), CancellationToken.None);
        await _repository.InsertAsync(NewUser("amy"), CancellationToken.None);

        var users = await _repository.ListAsync(CancellationToken.None);

        Assert.That(users.Select(u => u.Username), Is.EqualTo(new[] { "amy", "zed" }));
    }

    [Test]
    public async Task UpdateUnlessLastAdmin_RefusesToRemoveTheLastEnabledAdmin()
    {
        var one = NewUser("one") with { Role = UserRole.Admin };
        one = one with { Id = await _repository.InsertAsync(one, CancellationToken.None) };
        var two = NewUser("two") with { Role = UserRole.Admin };
        two = two with { Id = await _repository.InsertAsync(two, CancellationToken.None) };

        Assert.That(await _repository.UpdateUnlessLastAdminAsync(one with { IsDisabled = true }, CancellationToken.None), Is.True);
        Assert.That(await _repository.UpdateUnlessLastAdminAsync(two with { Role = UserRole.User }, CancellationToken.None), Is.False);
        Assert.That(await _repository.UpdateUnlessLastAdminAsync(two with { IsDisabled = true }, CancellationToken.None), Is.False);
        Assert.That(await _repository.UpdateUnlessLastAdminAsync(two with { PasswordHash = "changed" }, CancellationToken.None), Is.True);

        var stored = await _repository.GetByIdAsync(two.Id, CancellationToken.None);
        Assert.That((stored!.Role, stored.IsDisabled, stored.PasswordHash), Is.EqualTo((UserRole.Admin, false, "changed")));
    }

    [Test]
    public async Task UpdateUnlessLastAdmin_ParallelDemotions_LeaveOneAdmin()
    {
        var one = NewUser("one") with { Role = UserRole.Admin };
        one = one with { Id = await _repository.InsertAsync(one, CancellationToken.None) };
        var two = NewUser("two") with { Role = UserRole.Admin };
        two = two with { Id = await _repository.InsertAsync(two, CancellationToken.None) };

        var results = await Task.WhenAll(
            _repository.UpdateUnlessLastAdminAsync(one with { Role = UserRole.User }, CancellationToken.None),
            _repository.UpdateUnlessLastAdminAsync(two with { Role = UserRole.User }, CancellationToken.None));

        Assert.That(results.Count(r => r), Is.EqualTo(1));
        Assert.That((await _repository.ListAsync(CancellationToken.None)).Count(u => u.Role == UserRole.Admin), Is.EqualTo(1));
    }
}
