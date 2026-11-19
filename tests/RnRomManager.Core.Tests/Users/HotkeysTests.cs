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
            ("reports.missingImage.next", "Alt+Shift+M", true, null),
            ("reports.missingMetadata.next", "", true, Roles.Admin),
        }));
        Assert.That(Hotkeys.Actions.Skip(1).SelectMany(a => a.Defaults), Has.None.AnyOf(Hotkeys.Reserved.ToArray()));
        Assert.That(Hotkeys.Actions.Skip(1).SelectMany(a => a.Defaults), Is.Unique);
    }

    [Test]
    public void Merge_AStoredOverrideOnANewDefaultKey_IsKept_NewActionsGetTheirDefaults()
    {
        // K1: stored JSON holds overrides only; the table-order duplicate rule decides which of the two fires.
        var merged = Hotkeys.Merge(Map(("nav.library", "Alt+Shift+R")), User);

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

    private const string Next = Hotkeys.MissingImageNext;

    [Test]
    public void Merge_AnOlderOverrideOnTheNextMissingImageDefault_LeavesItImplicitlyUnassigned()
    {
        var stored = Map(("nav.library", "Alt+Shift+M"));

        var merged = Hotkeys.Merge(stored, User);

        Assert.That((merged["nav.library"], merged[Next]), Is.EqualTo(("Alt+Shift+M", (string?)null)));
        Assert.That(Hotkeys.ImplicitUnassigned(stored, User), Is.EqualTo(new[] { Next }));
    }

    [Test]
    public void Merge_OwnOverride_OrNoConflict_IsNotImplicit()
    {
        Assert.That(Hotkeys.ImplicitUnassigned(Map(("nav.library", "Alt+Shift+M"), (Next, "Alt+Shift+N")), User), Is.Empty);
        Assert.That(Hotkeys.ImplicitUnassigned(Map(("nav.library", "Alt+Shift+M"), (Next, null)), User), Is.Empty, "a deliberate disable");
        Assert.That(Hotkeys.ImplicitUnassigned(Map(("nav.library", "Alt+Shift+Q")), User), Is.Empty);
        Assert.That(Hotkeys.Merge(Map(), User)[Next], Is.EqualTo("Alt+Shift+M"));
    }

    [Test]
    public void Merge_ADisallowedRolesOverride_DoesNotTakeTheDefault()
    {
        var stored = Map(("nav.review", "Alt+Shift+M"));

        Assert.That(Hotkeys.ImplicitUnassigned(stored, User), Is.Empty, "a User's stored Admin override is ignored for conflicts");
        Assert.That(Hotkeys.Merge(stored, User)[Next], Is.EqualTo("Alt+Shift+M"));
        Assert.That(Hotkeys.ImplicitUnassigned(stored, Admin), Is.EqualTo(new[] { Next }));
    }

    [Test]
    public void Merge_OlderActionsKeepK1_TheyNeverYield() =>
        Assert.That(Hotkeys.ImplicitUnassigned(Map(("nav.library", "Alt+Shift+R")), User), Is.Empty);

    [Test]
    public void Apply_UnrelatedSave_KeepsTheOlderOverride_AndTheFallback()
    {
        var stored = Map(("nav.library", "Alt+Shift+M"));

        var (overrides, errors) = Hotkeys.Apply(stored,
            Map(("nav.library", "Alt+Shift+M"), ("nav.collections", "Alt+Shift+Q"), ("nav.reports", "Alt+Shift+R")), User);

        Assert.That(errors, Is.Empty);
        Assert.That(overrides, Is.EqualTo(Map(("nav.library", "Alt+Shift+M"), ("nav.collections", "Alt+Shift+Q"))));
        Assert.That(Hotkeys.ImplicitUnassigned(overrides!, User), Is.EqualTo(new[] { Next }));
    }

    [Test]
    public void Apply_ANewOverrideOnTheDefault_WithTheActionOmitted_FallsBack()
    {
        var (overrides, errors) = Hotkeys.Apply(Map(), Map(("nav.reports", "Alt+Shift+M")), User);

        Assert.That(errors, Is.Empty);
        Assert.That(Hotkeys.Merge(overrides!, User)[Next], Is.Null);
    }

    [TestCase("Alt+Shift+M")]
    [TestCase("shift+alt+m")]
    public void Apply_ExplicitlyNamingTheActionOnAConflict_IsTheNormalDuplicateError(string binding)
    {
        var (overrides, errors) = Hotkeys.Apply(Map(("nav.library", "Alt+Shift+M")),
            Map(("nav.library", "Alt+Shift+M"), (Next, binding)), User);

        Assert.That(overrides, Is.Null);
        Assert.That(errors, Is.EqualTo(new Dictionary<string, string[]> { [$"bindings.{Next}"] = ["Already used by Go to Library."] }));
    }

    [Test]
    public void Apply_DeliberateDisable_StoresNull_AndResetAllFreesTheDefault()
    {
        var stored = Map(("nav.library", "Alt+Shift+M"));

        var (disabled, _) = Hotkeys.Apply(stored, Map(("nav.library", "Alt+Shift+M"), (Next, null)), User);
        Assert.That(disabled, Is.EqualTo(Map(("nav.library", "Alt+Shift+M"), (Next, null))));
        Assert.That(Hotkeys.ImplicitUnassigned(disabled!, User), Is.Empty);

        // Reset all sends every default, the older one included.
        var defaults = Hotkeys.Editable.ToDictionary(a => a.Id, a => (string?)a.Defaults.FirstOrDefault());
        var (reset, errors) = Hotkeys.Apply(stored, defaults, User);
        Assert.That(errors, Is.Empty);
        Assert.That(reset, Is.Empty);
        Assert.That(Hotkeys.Merge(reset!, User)[Next], Is.EqualTo("Alt+Shift+M"));
    }

    [Test]
    public void Apply_NullUnassigns()
    {
        var (overrides, _) = Hotkeys.Apply(Map(), Map(("nav.library", null)), User);

        Assert.That(overrides, Is.EqualTo(Map(("nav.library", null))));
        Assert.That(Hotkeys.Merge(overrides!, User)["nav.library"], Is.Null);
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
        var merged = Hotkeys.Merge(Hotkeys.ParseStored("""{"nav.review":"Alt+Shift+L"}"""), Admin);

        Assert.That(merged, Is.EqualTo(Map(
            ("nav.library", "Alt+Shift+L"), ("nav.collections", "Alt+Shift+C"), ("nav.ingestion", "Alt+Shift+I"), ("nav.review", "Alt+Shift+L"),
            ("review.next", "Alt+Shift+G"), ("nav.reports", "Alt+Shift+R"), ("nav.integrity", "Alt+Shift+T"), ("nav.admin", "Alt+Shift+A"),
            ("nav.deleted", "Alt+Shift+D"), ("reports.missingImage.next", "Alt+Shift+M"), ("reports.missingMetadata.next", null))));
    }

    [Test]
    public void MissingMetadataNext_HasNoDefault_IsUnbound_AndAcceptsABinding()
    {
        Assert.That(Hotkeys.Merge(Map(), Admin)[Hotkeys.MissingMetadataNext], Is.Null);
        Assert.That(Hotkeys.ImplicitUnassigned(Map(("nav.library", "Alt+Shift+M")), Admin), Is.EqualTo(new[] { Hotkeys.MissingImageNext }));

        var (empty, noErrors) = Hotkeys.Apply(Map(), Map((Hotkeys.MissingMetadataNext, null)), Admin);
        Assert.That((noErrors, empty![Hotkeys.MissingMetadataNext]), Is.EqualTo((new Dictionary<string, string[]>(), (string?)null)));

        var (overrides, errors) = Hotkeys.Apply(Map(), Map((Hotkeys.MissingMetadataNext, "alt+shift+n")), Admin);
        Assert.That(errors, Is.Empty);
        Assert.That(overrides![Hotkeys.MissingMetadataNext], Is.EqualTo("Alt+Shift+N"));
        Assert.That(Hotkeys.Merge(overrides, Admin)[Hotkeys.MissingMetadataNext], Is.EqualTo("Alt+Shift+N"));
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
