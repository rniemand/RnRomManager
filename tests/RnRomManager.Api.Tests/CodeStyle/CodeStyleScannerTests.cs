namespace RnRomManager.Api.Tests.CodeStyle;

/// <summary>S2/S3: the scanner's rules on small samples (single-line literals, so this file never matches a rule itself).</summary>
public class CodeStyleScannerTests
{
    private const string Cs = "src/RnRomManager.Core/Sample.cs";

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static string[] Rules(string path, string text) =>
        CodeStyleScanner.ScanFile(path, text).Select(v => v.Rule).ToArray();

    private static string Web(string relative) => CodeStyleScanner.WebSrc + relative;

    // ---- CS1 ----

    [Test]
    public void Cs1_SecondTopLevelClass_IsFlagged() =>
        Assert.That(Rules(Cs, Lines("namespace X;", "public class A { }", "public sealed class B { }")), Is.EqualTo(new[] { "CS1" }));

    [Test]
    public void Cs1_OneType_IsClean() =>
        Assert.That(Rules(Cs, Lines("namespace X;", "public sealed class A", "{", "    public int B { get; }", "}")), Is.Empty);

    [Test]
    public void Cs1_NestedType_IsFlagged() =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    private sealed record Row(int Id);", "}")), Is.EqualTo(new[] { "CS1" }));

    [Test]
    public void Cs1_GenericArityPair_MayShareAFile() =>
        Assert.That(Rules(Cs, Lines("public class Page { }", "public class Page<T> { }")), Is.Empty);

    [Test]
    public void Cs1_DelegateCountsAsAType() =>
        Assert.That(Rules(Cs, Lines("public class A { }", "public delegate Task<int> Handler(int id);")), Is.EqualTo(new[] { "CS1" }));

    [Test]
    public void Cs1_CommentLinesAreIgnored() =>
        Assert.That(Rules(Cs, Lines("public class A { }", "// public class B { }", "/// public class C", " * public class D")), Is.Empty);

    [Test]
    public void Cs1_EnumAndInterfaceCount() =>
        Assert.That(Rules(Cs, Lines("public interface IA { }", "public enum B { One }")), Is.EqualTo(new[] { "CS1" }));

    // ---- CS2 ----

    [Test]
    public void Cs2_FourParametersOnOneLine_IsFlagged() =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    public void M(int a, int b, int c, int d) { }", "}")), Is.EqualTo(new[] { "CS2" }));

    [Test]
    public void Cs2_OnePerLine_IsClean() =>
        Assert.That(Rules(Cs, Lines("public sealed class A(", "    int a,", "    int b,", "    int c,", "    int d) : IB", "{", "}")), Is.Empty);

    [Test]
    public void Cs2_PartlyWrapped_IsFlagged() =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    public A(", "        int a, int b,", "        int c,", "        int d)", "    { }", "}")), Is.EqualTo(new[] { "CS2" }));

    [Test]
    public void Cs2_ThreeParameters_IsClean() =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    private static int M(int a, int b, int c) => a;", "}")), Is.Empty);

    [Test]
    public void Cs2_PositionalRecord_IsFlagged() =>
        Assert.That(Rules(Cs, Lines("public sealed record A(int B, int C, int D, int E);")), Is.EqualTo(new[] { "CS2" }));

    [Test]
    public void Cs2_LocalFunction_IsFlagged() =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    void M()", "    {", "        int Add(int a, int b, int c, int d) => a;", "    }", "}")), Is.EqualTo(new[] { "CS2" }));

    [Test]
    public void Cs2_CallWithFourArguments_IsClean() =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    void M()", "    {", "        Foo(1, 2, 3, 4);", "        x.Bar(1, 2, 3, 4);", "    }", "}")), Is.Empty);

    [TestCase("return Foo(1, 2, 3, 4);")]
    [TestCase("await Foo(1, 2, 3, 4);")]
    [TestCase("throw new Failure(1, 2, 3, 4);")]
    [TestCase("yield return Foo(1, 2, 3, 4);")]
    [TestCase("else Foo(1, 2, 3, 4);")]
    [TestCase("var x = Foo(1, 2, 3, 4);")]
    [TestCase("&& Matches(1, 2, 3, 4);")]
    public void Cs2_StatementCalls_AreClean(string statement) =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    void M()", "    {", "        " + statement, "    }", "}")), Is.Empty);

    [Test]
    public void Cs2_GenericAndCharCommas_AreNotCounted() =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    void M(Dictionary<string, int> a, char b = ',', string c = \"x, y\") { }", "}")), Is.Empty);

    [Test]
    public void Cs2_TrailingCommentCommas_AreNotCounted() =>
        Assert.That(Rules(Cs, Lines("public class A", "{", "    void M(int a, int b) // c, d, e", "    { }", "}")), Is.Empty);

    // ---- SV1 ----

    [Test]
    public void Sv1_ModalMarkupOutsideModals_IsFlagged() =>
        Assert.That(Rules(Web("routes/x/+page.svelte"), Lines("<div class=\"modal fade\">", "</div>")), Is.EqualTo(new[] { "SV1" }));

    [Test]
    public void Sv1_ModalMarkupInsideModals_IsClean() =>
        Assert.That(Rules(Web("lib/components/modals/XModal.svelte"), Lines("<BaseModal bind:open title=\"X\">", "</BaseModal>")), Is.Empty);

    [Test]
    public void Sv1_SelfClosingConfirmModalOverSeveralLines_IsClean() =>
        Assert.That(Rules(Web("routes/x/+page.svelte"), Lines("<ConfirmModal", "\tbind:open", "\tonconfirm={() => n > 1}", "/>")), Is.Empty);

    [Test]
    public void Sv1_ConfirmModalWithBody_IsFlagged() =>
        Assert.That(Rules(Web("routes/x/+page.svelte"), Lines("<ConfirmModal", "\tbind:open", "\tonconfirm={() => n > 1}", ">", "\t<p>Body</p>", "</ConfirmModal>")), Is.EqualTo(new[] { "SV1" }));

    // ---- SV2 / SV3 ----

    [Test]
    public void Sv2_ExportedTypeOutsideTypes_IsFlagged() =>
        Assert.That(Rules(Web("lib/discovery.ts"), Lines("export type SortDir = 'asc' | 'desc';")), Is.EqualTo(new[] { "SV2" }));

    [Test]
    public void Sv2_LocalTypesAndTests_AreClean()
    {
        Assert.That(Rules(Web("lib/components/X.svelte"), Lines("<script lang=\"ts\">", "\tinterface Props { a: string }", "</script>")), Is.Empty);
        Assert.That(Rules(Web("lib/x.test.ts"), Lines("export interface Fixture { a: string }")), Is.Empty);
    }

    [Test]
    public void Sv3_OneMatchingType_IsClean() =>
        Assert.That(Rules(Web("lib/types/Game.ts"), Lines("export interface Game {", "\tid: number;", "}")), Is.Empty);

    [Test]
    public void Sv3_TwoTypesOrWrongName_IsFlagged()
    {
        Assert.That(Rules(Web("lib/types/Game.ts"), Lines("export interface Game { }", "export interface Release { }")), Is.EqualTo(new[] { "SV3" }));
        Assert.That(Rules(Web("lib/enums/Role.ts"), Lines("export type UserRole = 'Admin' | 'User';")), Is.EqualTo(new[] { "SV3" }));
        Assert.That(Rules(Web("lib/types/index.ts"), Lines("export type { Game } from './Game.js';", "export type { Release } from './Release.js';")), Is.Empty);
    }

    // ---- SV4 ----

    [Test]
    public void Sv4_RawButtonOutsideForms_IsFlagged() =>
        Assert.That(Rules(Web("routes/+page.svelte"), Lines("<button type=\"button\" class=\"btn\">Go</button>")), Is.EqualTo(new[] { "SV4" }));

    [Test]
    public void Sv4_RawInputInsideForms_IsClean() =>
        Assert.That(Rules(Web("lib/components/forms/TextInput.svelte"), Lines("<input class=\"form-control\" {...rest} />")), Is.Empty);

    // ---- SV5 ----

    [Test]
    public void Sv5_ParentImportIntoLib_IsFlagged() =>
        Assert.That(Rules(Web("lib/components/modals/X.svelte"), Lines("import Icon from '../Icon.svelte';")), Is.EqualTo(new[] { "SV5" }));

    [Test]
    public void Sv5_ViMockIntoLib_IsFlagged() =>
        Assert.That(Rules(Web("lib/stores/x.test.ts"), Lines("vi.mock('../x', () => ({}));")), Is.EqualTo(new[] { "SV5" }));

    [Test]
    public void Sv5_ImportsOutsideLib_AreClean()
    {
        Assert.That(Rules(Web("routes/+layout.svelte"), Lines("import '../app.css';")), Is.Empty);
        Assert.That(Rules(Web("lib/x.test.ts"), Lines("import data from '../../static/x.js';")), Is.Empty);
        Assert.That(Rules(Web("lib/x.ts"), Lines("import { y } from './y.js';", "import { z } from '#lib/z.js';")), Is.Empty);
    }

    // ---- SV6 ----

    [Test]
    public void Sv6_StaticStyle_IsFlagged() =>
        Assert.That(Rules(Web("routes/+page.svelte"), Lines("<div style=\"width: 4rem\"></div>")), Is.EqualTo(new[] { "SV6" }));

    [Test]
    public void Sv6_DynamicStyle_IsClean() =>
        Assert.That(Rules(Web("routes/+page.svelte"), Lines("<div style=\"width: {pct}%\"></div>")), Is.Empty);

    // ---- Scope ----

    [Test]
    public void OtherFiles_AreNotScanned() =>
        Assert.That(Rules("src/RnRomManager.Web/static/x.js", Lines("<button>")), Is.Empty);

    // ---- S3 baseline ----

    [Test]
    public void Compare_ReportsNewAndStale()
    {
        var baseline = CodeStyleScanner.ParseBaseline(Lines("# header", "", "CS1 src/A.cs", "CS2 src/B.cs"));
        var found = new[] { new CodeStyleViolation("CS1", "src/A.cs"), new CodeStyleViolation("CS1", "src/C.cs") };

        var (added, stale) = CodeStyleScanner.Compare(found, baseline);

        Assert.That(added.Select(v => v.ToString()), Is.EqualTo(new[] { "CS1 src/C.cs" }));
        Assert.That(stale.Select(v => v.ToString()), Is.EqualTo(new[] { "CS2 src/B.cs" }));
    }

    [Test]
    public void Compare_FileMovedIntoExemptFolder_LeavesItsLineStale()
    {
        var moved = Web("lib/components/forms/CopyButton.svelte");
        var found = CodeStyleScanner.ScanFile(moved, Lines("<button type=\"button\">Copy</button>"));
        var baseline = CodeStyleScanner.ParseBaseline("SV4 " + moved);

        var (added, stale) = CodeStyleScanner.Compare(found, baseline);

        Assert.That(added, Is.Empty);
        Assert.That(stale.Select(v => v.ToString()), Is.EqualTo(new[] { "SV4 " + moved }));
    }

    [Test]
    public void FormatBaseline_SortsOrdinallyAfterTheHeader()
    {
        var text = CodeStyleScanner.FormatBaseline([new("SV4", "b"), new("CS1", "z"), new("CS1", "a")]);

        Assert.That(text, Is.EqualTo(CodeStyleScanner.BaselineHeader + "CS1 a\nCS1 z\nSV4 b\n"));
        Assert.That(CodeStyleScanner.ParseBaseline(text), Has.Count.EqualTo(3));
    }
}
