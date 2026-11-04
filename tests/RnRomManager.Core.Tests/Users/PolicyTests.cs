using RnRomManager.Core.Users;

namespace RnRomManager.Core.Tests.Users;

public class PolicyTests
{
    [TestCase("abc")]
    [TestCase("admin")]
    [TestCase("Richard.Niemand_2-x")]
    [TestCase("0123456789012345678901234567890123456789012345678901234567890123")] // 64
    public void Username_Valid(string username) => Assert.That(UsernamePolicy.Validate(username), Is.Null);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ab")]
    [TestCase("01234567890123456789012345678901234567890123456789012345678901234")] // 65
    [TestCase("has space")]
    [TestCase("emoji😀x")]
    [TestCase("ünïcode")]
    [TestCase("a@b.c")]
    [TestCase("abc\n")]
    public void Username_Invalid(string? username) => Assert.That(UsernamePolicy.Validate(username), Is.Not.Null);

    [TestCase(12)]
    [TestCase(128)]
    public void Password_LengthBoundsAreInclusive(int length) =>
        Assert.That(PasswordPolicy.Validate(new string('p', length), "someone"), Is.Null);

    [TestCase(0)]
    [TestCase(11)]
    [TestCase(129)]
    public void Password_OutsideLengthBounds_Fails(int length) =>
        Assert.That(PasswordPolicy.Validate(new string('p', length), "someone"), Does.Contain("12-128"));

    [Test]
    public void Password_Null_Fails() => Assert.That(PasswordPolicy.Validate(null, "someone"), Is.Not.Null);

    [TestCase("longusername1", "longusername1")]
    [TestCase("LongUserName1", "longusername1")]
    public void Password_EqualToUsernameIgnoringCase_Fails(string password, string username) =>
        Assert.That(PasswordPolicy.Validate(password, username), Does.Contain("username"));

    [Test]
    public void Password_NoCompositionRules() =>
        Assert.That(PasswordPolicy.Validate("aaaaaaaaaaaa", "someone"), Is.Null);

    [Test]
    public void Password_MessageNeverContainsThePassword() =>
        Assert.That(PasswordPolicy.Validate("secret-pw", "someone"), Does.Not.Contain("secret-pw"));

    [Test]
    public void NewPassword_Is20UrlSafeCharsAndRandom()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => Secrets.NewPassword()).ToList();

        Assert.That(passwords, Has.All.Matches("^[A-Za-z0-9_-]{20}$"));
        Assert.That(passwords.Distinct().Count(), Is.EqualTo(50));
        Assert.That(passwords, Has.All.Matches<string>(p => PasswordPolicy.Validate(p, "admin") is null));
    }

    [Test]
    public void NewSecurityStamp_Is32LowercaseHexAndRandom()
    {
        var stamps = Enumerable.Range(0, 50).Select(_ => Secrets.NewSecurityStamp()).ToList();

        Assert.That(stamps, Has.All.Matches("^[0-9a-f]{32}$"));
        Assert.That(stamps.Distinct().Count(), Is.EqualTo(50));
    }
}
