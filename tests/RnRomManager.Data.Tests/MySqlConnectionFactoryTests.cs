using MySqlConnector;

namespace RnRomManager.Data.Tests;

public class MySqlConnectionFactoryTests
{
    [TestCase("Server=db;Database=rnrommanager;User ID=app;Password=pw")]
    [TestCase("Server=db;Database=rnrommanager;DateTimeKind=Local;GuidFormat=Binary16")]
    [TestCase("Server=db;Database=rnrommanager;DateTimeKind=Unspecified;Guid Format=None")]
    public void ForcesUtcAndChar36_OverridingUserValues(string connectionString)
    {
        var factory = new MySqlConnectionFactory(connectionString);
        var forced = new MySqlConnectionStringBuilder(factory.ConnectionString);

        Assert.That(forced.DateTimeKind, Is.EqualTo(MySqlDateTimeKind.Utc));
        Assert.That(forced.GuidFormat, Is.EqualTo(MySqlGuidFormat.Char36));
    }

    [Test]
    public void KeepsEverythingElse()
    {
        var factory = new MySqlConnectionFactory("Server=db;Port=3307;Database=rnrommanager;User ID=app;Password=pw");
        var forced = new MySqlConnectionStringBuilder(factory.ConnectionString);

        Assert.That(forced.Server, Is.EqualTo("db"));
        Assert.That(forced.Port, Is.EqualTo(3307u));
        Assert.That(forced.Password, Is.EqualTo("pw"));
        Assert.That(factory.Database, Is.EqualTo("rnrommanager"));
        Assert.That(factory.UserId, Is.EqualTo("app"));
    }

    [Test]
    public void CreatesClosedConnectionsWithTheForcedString()
    {
        var factory = new MySqlConnectionFactory("Server=db;Database=rnrommanager");

        using var connection = factory.Create();

        Assert.That(connection, Is.InstanceOf<MySqlConnection>());
        Assert.That(connection.State, Is.EqualTo(System.Data.ConnectionState.Closed));
        Assert.That(new MySqlConnectionStringBuilder(connection.ConnectionString).DateTimeKind, Is.EqualTo(MySqlDateTimeKind.Utc));
    }

    [Test]
    public void UnparseableString_Throws() =>
        Assert.Throws<ArgumentException>(() => new MySqlConnectionFactory("x"));
}
