using RnRomManager.Core.Users;

namespace RnRomManager.Core.Tests.Users;

/// <summary>B21–B23 hotkey rules. The SPA mirror (<c>src/lib/hotkeys.test.ts</c>) runs the same cases.</summary>
public class HotkeysTests
{
    private static readonly IReadOnlySet<string> User = Roles.None;
    private static readonly IReadOnlySet<string> Admin = Roles.Of(Roles.Admin);

    private static Dictionary<string, string?> Map(params (string Id, string? Value)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Value, StringComparer.Ordinal);

    [Test]
    public void Actions_AreTheB21Table()
    {
        Assert.That(Hotkeys.Actions.Select(a => (a.Id, string.Join(", ", a.Defaults), a.Editable, a.Role)), Is.EqualTo(new[]
        {
            ("search", "Ctrl+K, Ctrl+Space, Meta+K", false, (string?)null),
            ("nav.library", "Alt+Shift+L", true, null),
            ("nav.collections", "Alt+Shift+C", true, null),
            ("nav.ingestion", "Alt+Shift+I", true, Roles.Admin),
            ("nav.review", "Alt+Shift+F", true, Roles.Admin),
            ("review.next", "Alt+Shift+G", true, Roles.Admin),
            ("nav.reports", "Alt+Shift+R", true, null),
            ("nav.integrity", "Alt+Shift+T", true, Roles.Admin),
            ("nav.admin", "Alt+Shift+A", true, Roles.Admin),
            ("nav.deleted", "Alt+Shift+D", true, Roles.Admin),
        }));
        Assert.That(Hotkeys.Actions.Skip(1).SelectMany(a => a.Defaults), Has.None.AnyOf(Hotkeys.Reserved.ToArray()));
        Assert.That(Hotkeys.Actions.Skip(1).SelectMany(a => a.Defaults), Is.Unique);
    }

    [Test]
    public void Merge_AStoredOverrideOnANewDefaultKey_IsKept_NewActionsGetTheirDefaults()
    {
        // K1: stored JSON holds overrides only; the table-order duplicate rule decides which of the two fires.
        var merged = Hotkeys.Merge(Map(("nav.library", "Alt+Shift+R")));

        Assert.That((merged["nav.library"], merged["nav.reports"], merged["nav.deleted"]), Is.EqualTo(("Alt+Shift+R", "Alt+Shift+R", "Alt+Shift+D")));
    }

    [TestCase("Alt+Shift+L", "Alt+Shift+L")]
    [TestCase("shift+alt+l", "Alt+Shift+L")]
    [TestCase("Meta+Shift+Ctrl+Alt+9", "Ctrl+Alt+Shift+Meta+9")]
    [TestCase("Ctrl+F12", "Ctrl+F12")]
    [TestCase("ctrl+f1", "Ctrl+F1")]
    [TestCase(" Alt + X ", "Alt+X")]
    public void Normalize_GivesTheCanonicalForm(string input, string expected) =>
        Assert.That(Hotkeys.Normalize(input), Is.EqualTo(expected));

    [TestCase("")]
    [TestCase("Alt+Shift")]
    [TestCase("Alt+Alt+L")]
    [TestCase("Alt+L+M")]
    [TestCase("Alt+F13")]
    [TestCase("Alt+F0")]
    [TestCase("Alt+F01")]
    [TestCase("Alt+Space")]
    [TestCase("Alt+é")]
    [TestCase("Hyper+L")]
    [TestCase("Alt+")]
    public void Normalize_RejectsBadFormats(string input) => Assert.That(Hotkeys.Normalize(input), Is.Null);

    [TestCase("Shift+L", Hotkeys.ModifierError)]
    [TestCase("L", Hotkeys.ModifierError)]
    [TestCase("Ctrl+Alt+Q", Hotkeys.AltGrError)]
    [TestCase("Ctrl+K", Hotkeys.ReservedError)]
    [TestCase("Meta+K", Hotkeys.ReservedError)]
    public void RuleError_Rejects(string canonical, string error) => Assert.That(Hotkeys.RuleError(canonical), Is.EqualTo(error));

    [TestCase("Ctrl+Alt+Shift+Q")]
    [TestCase("Ctrl+Alt+Meta+Q")]
    [TestCase("Alt+Q")]
    [TestCase("Meta+Shift+Q")]
    [TestCase("Ctrl+Shift+K")]
    public void RuleError_Accepts(string canonical) => Assert.That(Hotkeys.RuleError(canonical), Is.Null);

    [Test]
    public void Apply_StoresOnlyNonDefaultOverrides_Canonically()
    {
        var (overrides, errors) = Hotkeys.Apply(Map(), Map(("nav.library", "Alt+Shift+L"), ("nav.review", "shift+alt+q")), Admin);

        Assert.That(errors, Is.Empty);
        Assert.That(overrides, Is.EqualTo(Map(("nav.review", "Alt+Shift+Q"))));
    }

    [Test]
    public void Apply_NullUnassigns()
    {
        var (overrides, _) = Hotkeys.Apply(Map(), Map(("nav.library", null)), User);

        Assert.That(overrides, Is.EqualTo(Map(("nav.library", null))));
        Assert.That(Hotkeys.Merge(overrides!)["nav.library"], Is.Null);
    }

    [Test]
    public void Apply_EmptyResetsEverythingTheRoleAllows()
    {
        var (overrides, errors) = Hotkeys.Apply(Map(("nav.library", "Alt+Q"), ("nav.review", "Alt+R")), Map(), Admin);

        Assert.That(errors, Is.Empty);
        Assert.That(overrides, Is.Empty);
        Assert.That(Hotkeys.Serialize(overrides!), Is.Null);
    }

    [Test]
    public void Apply_DuplicateAmongAllowedActions_ErrorsOnTheLaterOne()
    {
        var (overrides, errors) = Hotkeys.Apply(Map(), Map(("nav.review", "Alt+Shift+L")), Admin);

        Assert.That(overrides, Is.Null);
        Assert.That(errors, Is.EqualTo(new Dictionary<string, string[]> { ["bindings.nav.review"] = ["Already used by Go to Library."] }));
    }

    [Test]
    public void Apply_AUserMayTakeAnAdminActionsKey_AndAdminOverridesAreKept()
    {
        var stored = Map(("nav.review", "Alt+Shift+Z"));

        var (overrides, errors) = Hotkeys.Apply(stored, Map(("nav.library", "Alt+Shift+I"), ("nav.review", "Alt+Shift+Y")), User);

        Assert.That(errors, Is.Empty);
        Assert.That(overrides, Is.EqualTo(Map(("nav.review", "Alt+Shift+Z"), ("nav.library", "Alt+Shift+I"))));
    }

    [Test]
    public void Apply_ReservedAltGrAndFormat_AreFieldErrors()
    {
        var (_, errors) = Hotkeys.Apply(Map(),
            Map(("nav.library", "Ctrl+K"), ("nav.ingestion", "Ctrl+Alt+I"), ("nav.review", "Shift+"), ("review.next", "Shift+N")), Admin);

        Assert.That(errors, Is.EqualTo(new Dictionary<string, string[]>
        {
            ["bindings.nav.library"] = [Hotkeys.ReservedError],
            ["bindings.nav.ingestion"] = [Hotkeys.AltGrError],
            ["bindings.nav.review"] = [Hotkeys.FormatError],
            ["bindings.review.next"] = [Hotkeys.ModifierError],
        }));
    }

    [Test]
    public void Apply_UnknownAndNotEditableIds_AreFieldErrors()
    {
        var (_, errors) = Hotkeys.Apply(Map(), Map(("nope", "Alt+Q"), ("search", "Alt+S")), User);

        Assert.That(errors, Is.EqualTo(new Dictionary<string, string[]>
        {
            ["bindings.nope"] = [Hotkeys.UnknownError],
            ["bindings.search"] = [Hotkeys.NotEditableError],
        }));
    }

    [Test]
    public void Merge_FillsDefaults_AndKeepsAStoredDuplicate()
    {
        var merged = Hotkeys.Merge(Hotkeys.ParseStored("""{"nav.review":"Alt+Shift+L"}"""));

        Assert.That(merged, Is.EqualTo(Map(
            ("nav.library", "Alt+Shift+L"), ("nav.collections", "Alt+Shift+C"), ("nav.ingestion", "Alt+Shift+I"), ("nav.review", "Alt+Shift+L"),
            ("review.next", "Alt+Shift+G"), ("nav.reports", "Alt+Shift+R"), ("nav.integrity", "Alt+Shift+T"), ("nav.admin", "Alt+Shift+A"),
            ("nav.deleted", "Alt+Shift+D"))));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("""{"search":"Alt+S","unknown":"Alt+U"}""")]
    public void ParseStored_IgnoresWhatItCantUse(string? json) => Assert.That(Hotkeys.ParseStored(json), Is.Empty);

    [Test]
    public void Serialize_RoundTripsInTableOrder()
    {
        var overrides = Map(("review.next", null), ("nav.library", "Alt+Q"));

        var json = Hotkeys.Serialize(overrides);

        Assert.That(json, Is.EqualTo("""{"nav.library":"Alt+Q","review.next":null}"""));
        Assert.That(Hotkeys.ParseStored(json), Is.EqualTo(overrides));
    }
}
