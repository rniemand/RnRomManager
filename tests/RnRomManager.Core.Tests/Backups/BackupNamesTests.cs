using RnRomManager.Core.Backups;

namespace RnRomManager.Core.Tests.Backups;

public class BackupNamesTests
{
    [Test]
    public void For_UsesTheUtcStartTime()
    {
        var local = new DateTimeOffset(2026, 11, 6, 5, 30, 7, TimeSpan.FromHours(2));
        Assert.That(BackupNames.For(local), Is.EqualTo("rnrommanager-20261106-033007.zip"));
        Assert.That(BackupNames.For(local, 2), Is.EqualTo("rnrommanager-20261106-033007 (2).zip"));
    }

    [TestCase("rnrommanager-20261106-033007.zip", 1)]
    [TestCase("rnrommanager-20261106-033007 (2).zip", 2)]
    [TestCase("rnrommanager-20261106-033007 (12).zip", 12)]
    public void Parse_ReadsTimeAndNumber(string name, int n) =>
        Assert.That(BackupNames.Parse(name), Is.EqualTo((new DateTimeOffset(2026, 11, 6, 3, 30, 7, TimeSpan.Zero), n)));

    [TestCase("rnrommanager-20261106-033007.zip.tmp")]
    [TestCase("rnrommanager-20261106-033007.zip.restored")]
    [TestCase("rnrommanager-20261106-033007 (1).zip")]
    [TestCase("rnrommanager-20261106-033007 (02).zip")]
    [TestCase("RNROMMANAGER-20261106-033007.zip")]
    [TestCase("rnrommanager-2026116-033007.zip")]
    [TestCase("x-rnrommanager-20261106-033007.zip")]
    public void Parse_RejectsOtherNames(string name) => Assert.That(BackupNames.Parse(name), Is.Null);

    [Test]
    public void IsTemp_OnlyForABackupNamePlusTmp()
    {
        Assert.That(BackupNames.IsTemp("rnrommanager-20261106-033007.zip.tmp"), Is.True);
        Assert.That(BackupNames.IsTemp("rnrommanager-20261106-033007 (2).zip.tmp"), Is.True);
        Assert.That(BackupNames.IsTemp("other.zip.tmp"), Is.False);
        Assert.That(BackupNames.IsTemp("rnrommanager-20261106-033007.zip"), Is.False);
    }
}
