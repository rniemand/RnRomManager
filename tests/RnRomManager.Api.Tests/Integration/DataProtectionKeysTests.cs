using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using NLog;
using RnRomManager.TestData;

namespace RnRomManager.Api.Tests.Integration;

/// <summary>Spec §9.2: keys live in the DB, so a restarted app can read what the old one protected (cookies survive).</summary>
[Category("Integration")]
public class DataProtectionKeysTests
{
    private const string Purpose = "RnRomManager.Tests";

    [Test]
    public async Task PayloadProtectedByOneInstance_IsReadableByANewInstanceOnTheSameDatabase()
    {
        var cs = await MariaDbServer.CreateDatabaseAsync();

        string protectedPayload;
        using (var first = new ApiFactory(cs))
        {
            using var _ = first.CreateClient();
            protectedPayload = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("hello");
        }

        using var second = new ApiFactory(cs);
        using var __ = second.CreateClient();
        var unprotected = second.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(protectedPayload);

        Assert.That(unprotected, Is.EqualTo("hello"));
        Assert.That(await CountKeysAsync(cs), Is.EqualTo(1), "the second instance reused the stored key");
    }

    [Test]
    public async Task DifferentDatabase_CannotReadThePayload()
    {
        string protectedPayload;
        using (var first = new ApiFactory(await MariaDbServer.CreateDatabaseAsync()))
        {
            using var _ = first.CreateClient();
            protectedPayload = first.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("hello");
        }

        using var other = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        using var __ = other.CreateClient();
        var protector = other.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);

        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(protectedPayload));
    }

    [Test]
    public async Task FirstStartOnAnEmptyDatabase_ReadsTheKeyRingOnlyAfterMigrations()
    {
        // Data Protection's own hosted service warms the key ring; before the fix it ran ahead of StartupRunner's DbUp
        // and logged "Table 'data_protection_keys' doesn't exist" at Error on every fresh install.
        using var factory = new ApiFactory(await MariaDbServer.CreateDatabaseAsync());
        using (factory.CreateClient())
            LogManager.Flush();

        var file = Directory.GetFiles(factory.Settings["Paths:Logs"]!, "rnrommanager-*.log").Single();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var errors = new StreamReader(stream).ReadToEnd().Split('\n')
            .Where(line => line.Contains("|ERROR|") || line.Contains("|FATAL|"))
            // §4.1 step 9: no cached DATs on a fresh install is an Error by design.
            .Where(line => !line.Contains("|RnRomManager.Core.Dats.DatCache|"));

        Assert.That(errors, Is.Empty);
    }

    private static async Task<long> CountKeysAsync(string cs)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM data_protection_keys";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
