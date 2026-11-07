using RnRomManager.Core.Backups;

namespace RnRomManager.Core.Tests.Backups;

public class BackupSettingsTests
{
    [Test]
    public void Defaults_AreEnabledAt0330KeepingThirty() =>
        Assert.That(BackupSettings.Defaults, Is.EqualTo(new BackupSettings(true, "03:30", 30)));

    [TestCase("00:00")]
    [TestCase("03:30")]
    [TestCase("23:59")]
    public void ValidTime_HasNoErrors(string time) =>
        Assert.That(new BackupSettings(true, time, 30).Validate(), Is.Empty);

    [TestCase("24:00")]
    [TestCase("3:30")]
    [TestCase("03:60")]
    [TestCase("03:30:00")]
    [TestCase("")]
    [TestCase("noon")]
    [TestCase("٠٣:٣٠")]
    public void BadTime_IsATimeError(string time) =>
        Assert.That(new BackupSettings(true, time, 30).Validate().Keys, Is.EqualTo(new[] { "time" }));

    [TestCase(1)]
    [TestCase(365)]
    public void RetentionBounds_AreValid(int retention) =>
        Assert.That(new BackupSettings(true, "03:30", retention).Validate(), Is.Empty);

    [TestCase(0)]
    [TestCase(366)]
    [TestCase(-5)]
    public void RetentionOutOfRange_IsARetentionError(int retention) =>
        Assert.That(new BackupSettings(true, "03:30", retention).Validate()["retention"], Is.EqualTo(new[] { "Keep 1–365 backups." }));

    [Test]
    public void FromStored_UsesStoredValues_AndDefaultsForMissingOrBadOnes()
    {
        Assert.That(BackupSettings.FromStored(new Dictionary<string, string>
        {
            ["backup.enabled"] = "false", ["backup.time"] = "01:05", ["backup.retention"] = "7",
        }), Is.EqualTo(new BackupSettings(false, "01:05", 7)));
        Assert.That(BackupSettings.FromStored(new Dictionary<string, string>
        {
            ["backup.enabled"] = "maybe", ["backup.time"] = "25:00", ["backup.retention"] = "0",
        }), Is.EqualTo(BackupSettings.Defaults));
        Assert.That(BackupSettings.FromStored(new Dictionary<string, string>()), Is.EqualTo(BackupSettings.Defaults));
    }

    [Test]
    public void ToStored_RoundTrips()
    {
        var settings = new BackupSettings(false, "22:15", 90);
        Assert.That(BackupSettings.FromStored(settings.ToStored()), Is.EqualTo(settings));
    }
}
