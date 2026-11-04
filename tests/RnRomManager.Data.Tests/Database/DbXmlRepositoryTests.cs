using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using RnRomManager.Data.Repositories;
using RnRomManager.TestData;

namespace RnRomManager.Data.Tests.Database;

[Category("Integration")]
public class DbXmlRepositoryTests
{
    private DbXmlRepository _repository = null!;

    [SetUp]
    public async Task SetUp()
    {
        var factory = new MySqlConnectionFactory(await MariaDbServer.CreateDatabaseAsync());
        new MySqlDatabaseLifecycle(factory, NullLogger<MySqlDatabaseLifecycle>.Instance, TimeProvider.System).Migrate();
        _repository = new DbXmlRepository(factory, TimeProvider.System);
    }

    [Test]
    public void Empty_ReturnsNoElements() => Assert.That(_repository.GetAllElements(), Is.Empty);

    [Test]
    public void StoredElements_ComeBackInInsertOrder()
    {
        var first = XElement.Parse("<key id=\"1\"><creationDate>2026-11-04</creationDate></key>");
        var second = XElement.Parse("<key id=\"2\"><descriptor>ünïcode &amp; escapes</descriptor></key>");

        _repository.StoreElement(first, "key-1");
        _repository.StoreElement(second, "key-2");

        var all = _repository.GetAllElements().ToList();
        Assert.That(all, Has.Count.EqualTo(2));
        Assert.That(XNode.DeepEquals(all[0], first), Is.True);
        Assert.That(XNode.DeepEquals(all[1], second), Is.True);
    }
}
