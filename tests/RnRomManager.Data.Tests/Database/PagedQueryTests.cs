using Dapper;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

/// <summary>D4: the count and the page run on the caller's open connection with the caller's args.</summary>
[Category("Integration")]
public class PagedQueryTests
{
    [Test]
    public async Task Returns_the_requested_page_and_the_full_count()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        await using var connection = factory.Create();
        // A temporary table lives only on this connection, so both queries must use it.
        await connection.OpenAsync();
        await connection.ExecuteAsync("CREATE TEMPORARY TABLE t (id INT PRIMARY KEY, tag VARCHAR(8))");
        await connection.ExecuteAsync("INSERT INTO t VALUES (1, 'a'), (2, 'a'), (3, 'a'), (4, 'b'), (5, 'a')");
        var args = new { tag = "a", pageSize = 2, offset = 2L };

        var page = await connection.QueryPageAsync<int>(
            "SELECT COUNT(*) FROM t WHERE tag = @tag",
            "SELECT id FROM t WHERE tag = @tag ORDER BY id LIMIT @pageSize OFFSET @offset",
            args, 2, 2, CancellationToken.None);

        Assert.That(page.Items, Is.EqualTo(new[] { 3, 5 }));
        Assert.That(page.Total, Is.EqualTo(4));
        Assert.That(page.PageNumber, Is.EqualTo(2));
        Assert.That(page.PageSize, Is.EqualTo(2));
    }

    [TestCase(true, "DESC")]
    [TestCase(false, "")]
    public void Dir_is_desc_or_empty(bool descending, string expected) =>
        Assert.That(SqlOrder.Dir(descending), Is.EqualTo(expected));
}
