using System.Data.Common;
using MySqlConnector;
using RnRomManager.Core.Database;

namespace RnRomManager.Data;

/// <summary>
/// Forces <c>DateTimeKind=Utc</c> and <c>GuidFormat=Char36</c> on the configured connection string,
/// so users never add them (spec §2).
/// </summary>
public sealed class MySqlConnectionFactory : IDbConnectionFactory
{
    /// <exception cref="ArgumentException">The connection string can't be parsed.</exception>
    public MySqlConnectionFactory(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString)
        {
            DateTimeKind = MySqlDateTimeKind.Utc,
            GuidFormat = MySqlGuidFormat.Char36,
        };
        ConnectionString = builder.ConnectionString;
        Database = builder.Database;
        UserId = builder.UserID;
    }

    public string ConnectionString { get; }

    public string Database { get; }

    public string UserId { get; }

    public DbConnection Create() => new MySqlConnection(ConnectionString);
}
