using Microsoft.Extensions.Options;
using MySqlConnector;
using RnRomManager.Core.Configuration;

namespace RnRomManager.Api.Configuration;

/// <summary>The effective configuration with secrets removed: logged at startup, later shown to Admins (spec §4.1, §13).</summary>
public sealed record EffectiveSettings(
    string ConnectionString,
    PathsOptions Paths,
    string AdminInitialPassword,
    IntegrityOptions Integrity,
    LoggingOptions Logging,
    SourcesOptions Sources)
{
    public const string Masked = "***";

    public static EffectiveSettings From(IServiceProvider services) => new(
        RedactConnectionString(services.GetRequiredService<IOptions<ConnectionStringsOptions>>().Value.Default),
        services.GetRequiredService<IOptions<PathsOptions>>().Value,
        services.GetRequiredService<IOptions<AdminOptions>>().Value.InitialPassword.Length == 0 ? "" : Masked,
        services.GetRequiredService<IOptions<IntegrityOptions>>().Value,
        services.GetRequiredService<IOptions<LoggingOptions>>().Value,
        services.GetRequiredService<IOptions<SourcesOptions>>().Value);

    /// <summary>Only via <see cref="MySqlConnectionStringBuilder"/> with <c>Password</c> removed (spec §13).</summary>
    public static string RedactConnectionString(string connectionString)
    {
        try
        {
            var builder = new MySqlConnectionStringBuilder(connectionString);
            builder.Remove("Password");
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            // Never echo a string we can't parse: it may hold a password in an unknown form.
            return "(unparseable)";
        }
    }
}
