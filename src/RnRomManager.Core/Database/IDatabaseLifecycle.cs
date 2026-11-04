namespace RnRomManager.Core.Database;

/// <summary>Startup steps 5–6 (spec §4.1) and the readiness probe (spec §17.1).</summary>
public interface IDatabaseLifecycle
{
    /// <summary>Retries until the DB answers or the wait times out.</summary>
    /// <exception cref="DatabaseUnavailableException">Timed out, unknown database or access denied.</exception>
    Task WaitUntilAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Applies pending migrations.</summary>
    /// <exception cref="DatabaseMigrationException">A script failed.</exception>
    void Migrate();

    /// <summary><c>SELECT 1</c> within 2 s; never throws.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken);
}
