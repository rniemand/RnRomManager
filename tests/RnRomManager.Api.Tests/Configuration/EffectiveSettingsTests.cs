using RnRomManager.Api.Configuration;

namespace RnRomManager.Api.Tests.Configuration;

public class EffectiveSettingsTests
{
    [TestCase("Server=db;Database=rnrommanager;User ID=app;Password=hunter2")]
    [TestCase("Server=db;Database=rnrommanager;User ID=app;pwd=hunter2")]
    [TestCase("server=db;database=rnrommanager;uid=app;PASSWORD='hun;ter2'")]
    public void RedactConnectionString_RemovesPassword(string connectionString)
    {
        var redacted = EffectiveSettings.RedactConnectionString(connectionString);

        Assert.That(redacted, Does.Not.Contain("hunter2").And.Not.Contain("hun;ter2").IgnoreCase);
        Assert.That(redacted, Does.Not.Contain("password").IgnoreCase.And.Not.Contain("pwd").IgnoreCase);
        Assert.That(redacted, Does.Contain("Database=rnrommanager"));
    }

    [TestCase("x")]
    [TestCase("not;a=valid;;=connection string;Password=hunter2;bogus")]
    public void RedactConnectionString_Unparseable_NeverEchoesInput(string connectionString) =>
        Assert.That(EffectiveSettings.RedactConnectionString(connectionString), Is.EqualTo("(unparseable)"));
}
