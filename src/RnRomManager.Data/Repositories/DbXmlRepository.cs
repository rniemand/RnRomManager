using System.Xml.Linq;
using Dapper;
using Microsoft.AspNetCore.DataProtection.Repositories;
using RnRomManager.Core.Database;

namespace RnRomManager.Data.Repositories;

/// <summary>
/// Data Protection key ring in <c>data_protection_keys</c> (spec §9.2), so auth and antiforgery cookies
/// survive container restarts. Keys aren't encrypted at rest: the DB is trusted.
/// The interface is synchronous, so these calls are too (they run rarely: key ring load/rotation).
/// </summary>
public sealed class DbXmlRepository(IDbConnectionFactory connections, TimeProvider time) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = connections.Create();
        return connection.Query<string>("SELECT xml FROM data_protection_keys ORDER BY id")
            .Select(XElement.Parse)
            .ToList();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = connections.Create();
        connection.Execute(
            "INSERT INTO data_protection_keys (friendly_name, xml, created_at) VALUES (@friendlyName, @xml, @createdAt)",
            new { friendlyName, xml = element.ToString(SaveOptions.DisableFormatting), createdAt = time.GetUtcNow().UtcDateTime });
    }
}
