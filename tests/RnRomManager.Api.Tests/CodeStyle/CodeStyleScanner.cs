using System.Text;
using System.Text.RegularExpressions;

namespace RnRomManager.Api.Tests.CodeStyle;

/// <summary>
/// Line-based checks for the structural rules of <c>docs/style-guide.md</c> (spec S2) and the baseline comparison (S3).
/// Pure functions over file text, so <see cref="CodeStyleScannerTests"/> can feed it samples.
/// </summary>
public static partial class CodeStyleScanner
{
    public const string WebSrc = "src/RnRomManager.Web/src/";

    public const string BaselineHeader =
        "# CodeStyleTests baseline (docs/style-guide.md): existing violations, one \"<rule> <path>\" per line.\n" +
        "# It may only shrink: delete the lines you fix; never add new ones.\n";

    private static readonly HashSet<string> ExcludedDirs = ["bin", "obj", "node_modules", ".svelte-kit", "build", "static"];

    private static readonly HashSet<string> NotDeclarationStarts =
    [
        "return", "await", "throw", "new", "yield", "else", "case", "using", "if", "while", "for", "foreach", "switch",
        "lock", "var", "when", "and", "or", "not", "is", "in", "do", "goto", "select", "where", "from", "let", "orderby",
        "join", "nameof", "typeof", "sizeof", "default", "checked", "unchecked",
    ];

    /// <summary>Every violation in the repository at <paramref name="root"/>, sorted ordinally.</summary>
    public static IReadOnlyList<CodeStyleViolation> ScanRepo(string root)
    {
        var found = new List<CodeStyleViolation>();
        foreach (var top in new[] { "src", "tests" })
            foreach (var file in Walk(Path.Combine(root, top)))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.EndsWith(".cs", StringComparison.Ordinal) || IsWebSource(rel))
                    found.AddRange(ScanFile(rel, File.ReadAllText(file)));
            }
        return Sort(found);
    }

    /// <summary>The violations in one file, at most one per rule.</summary>
    public static IReadOnlyList<CodeStyleViolation> ScanFile(string path, string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => IsComment(l) ? "" : l).ToArray();
        var rules = new List<string>();
        if (path.EndsWith(".cs", StringComparison.Ordinal))
        {
            if (HasCs1(lines)) rules.Add("CS1");
            if (HasCs2(lines)) rules.Add("CS2");
        }
        else if (IsWebSource(path))
        {
            var web = path[WebSrc.Length..];
            var svelte = web.EndsWith(".svelte", StringComparison.Ordinal);
            if (svelte && !web.StartsWith("lib/components/modals/", StringComparison.Ordinal) && HasSv1(lines)) rules.Add("SV1");
            if (HasSv2(web, lines)) rules.Add("SV2");
            if (HasSv3(web, lines)) rules.Add("SV3");
            if (svelte && !web.StartsWith("lib/components/forms/", StringComparison.Ordinal) && lines.Any(HasRawControl)) rules.Add("SV4");
            if (lines.Any(l => HasSv5(web, l))) rules.Add("SV5");
            if (svelte && lines.Any(HasStaticStyle)) rules.Add("SV6");
        }
        return rules.Select(r => new CodeStyleViolation(r, path)).ToList();
    }

    /// <summary>Baseline entries from the file text; <c>#</c> comments and blank lines skipped.</summary>
    public static IReadOnlyList<CodeStyleViolation> ParseBaseline(string text) =>
        text.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2))
            .Select(p => new CodeStyleViolation(p[0], p.Length > 1 ? p[1] : ""))
            .ToList();

    /// <summary>The baseline file text for <paramref name="found"/>: header, then sorted lines.</summary>
    public static string FormatBaseline(IEnumerable<CodeStyleViolation> found) =>
        BaselineHeader + string.Concat(Sort(found).Select(v => v + "\n"));

    /// <summary>S3: violations missing from the baseline, and baseline lines no longer found.</summary>
    public static (IReadOnlyList<CodeStyleViolation> New, IReadOnlyList<CodeStyleViolation> Stale) Compare(
        IEnumerable<CodeStyleViolation> found,
        IEnumerable<CodeStyleViolation> baseline)
    {
        var foundSet = found.ToHashSet();
        var baselineSet = baseline.ToHashSet();
        return (Sort(foundSet.Except(baselineSet)), Sort(baselineSet.Except(foundSet)));
    }

    private static List<CodeStyleViolation> Sort(IEnumerable<CodeStyleViolation> violations) =>
        violations.Distinct().OrderBy(v => v.ToString(), StringComparer.Ordinal).ToList();

    private static IEnumerable<string> Walk(string dir)
    {
        if (!Directory.Exists(dir))
            yield break;
        foreach (var file in Directory.EnumerateFiles(dir))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (!ExcludedDirs.Contains(Path.GetFileName(sub)))
                foreach (var file in Walk(sub))
                    yield return file;
    }

    private static bool IsWebSource(string path) =>
        path.StartsWith(WebSrc, StringComparison.Ordinal)
        && (path.EndsWith(".svelte", StringComparison.Ordinal) || path.EndsWith(".ts", StringComparison.Ordinal))
        && !path.EndsWith(".d.ts", StringComparison.Ordinal);

    private static bool IsComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith('*') || t.StartsWith("/*", StringComparison.Ordinal);
    }

    // ---- CS1: one top-level type per file, no nesting ----

    [GeneratedRegex(@"^(\s*)(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe|new|required)\s+)*(?:record\s+(?:class|struct)|class|record|struct|interface|enum)\s+([A-Z]\w*)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"^(\s*)(?:(?:public|internal|private|protected|file|static|unsafe|new)\s+)*delegate\s+.+?\s+([A-Z]\w*)\s*(?:<[^()]*>)?\s*\(")]
    private static partial Regex DelegateDeclaration();

    private static bool HasCs1(string[] lines)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var m = TypeDeclaration().Match(line) is { Success: true } t ? t : DelegateDeclaration().Match(line);
            if (!m.Success)
                continue;
            if (m.Groups[1].Length > 0)
                return true;
            names.Add(m.Groups[2].Value);
        }
        return names.Count > 1;
    }

    // ---- CS2: 4+ parameters must be one per line ----

    [GeneratedRegex(@"^(?<prefix>[A-Za-z_][\w<>\[\],.?:\s]*?)\s+@?[A-Za-z_]\w*(?:<[^()]*>)?$")]
    private static partial Regex DeclarationHead();

    private static bool HasCs2(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            var paren = t.IndexOf('(');
            if (paren <= 0)
                continue;
            var eq = t.IndexOf('=');
            if (eq >= 0 && eq < paren)
                continue;
            var head = t[..paren];
            var first = head.Split(' ', 2)[0];
            if (NotDeclarationStarts.Contains(first) || !DeclarationHead().IsMatch(head))
                continue;
            var (count, crowded) = ReadParameters(lines, i, lines[i].IndexOf('('));
            if (count >= 4 && crowded)
                return true;
        }
        return false;
    }

    /// <summary>Counts the top-level parameters of the list opening at <paramref name="col"/> and whether one line holds two.</summary>
    private static (int Count, bool Crowded) ReadParameters(string[] lines, int row, int col)
    {
        int depth = 0, commas = 0;
        var any = false;
        var crowded = false;
        for (var r = row; r < lines.Length; r++)
        {
            var line = lines[r];
            var commaOnLine = false;
            var lineHasParam = false;
            for (var c = r == row ? col : 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/')
                    break;
                if (ch is '"' or '\'')
                {
                    c = SkipLiteral(line, c);
                    MarkParam();
                    continue;
                }
                switch (ch)
                {
                    case '(' or '<' or '[':
                        depth++;
                        if (depth > 1) MarkParam();
                        break;
                    case ')' or '>' or ']':
                        depth = Math.Max(0, depth - 1);
                        if (depth == 0 && ch == ')')
                            return (any ? commas + 1 : 0, crowded);
                        break;
                    case ',' when depth == 1:
                        commas++;
                        commaOnLine = true;
                        lineHasParam = false;
                        break;
                    default:
                        if (depth >= 1 && !char.IsWhiteSpace(ch)) MarkParam();
                        break;
                }
            }
            continue;

            void MarkParam()
            {
                any = true;
                if (commaOnLine && !lineHasParam)
                    crowded = true;
                lineHasParam = true;
            }
        }
        return (any ? commas + 1 : 0, crowded);
    }

    /// <summary>Index of the closing quote of the string or char literal starting at <paramref name="start"/>.</summary>
    private static int SkipLiteral(string line, int start)
    {
        var quote = line[start];
        var verbatim = start > 0 && line[start - 1] == '@';
        for (var c = start + 1; c < line.Length; c++)
        {
            if (!verbatim && line[c] == '\\')
                c++;
            else if (line[c] == quote)
                return c;
        }
        return line.Length;
    }

    // ---- SV1: modal markup only in lib/components/modals ----

    private static bool HasSv1(string[] lines)
    {
        var text = string.Join('\n', lines);
        if (text.Contains("<BaseModal", StringComparison.Ordinal) || text.Contains("class=\"modal", StringComparison.Ordinal))
            return true;
        for (var at = text.IndexOf("<ConfirmModal", StringComparison.Ordinal); at >= 0; at = text.IndexOf("<ConfirmModal", at + 1, StringComparison.Ordinal))
            if (!IsSelfClosing(text, at))
                return true;
        return false;
    }

    /// <summary>Reads the tag at <paramref name="start"/> to its closing <c>&gt;</c>, skipping <c>{…}</c> expressions and quoted values.</summary>
    private static bool IsSelfClosing(string text, int start)
    {
        var braces = 0;
        char? quote = null;
        for (var c = start + 1; c < text.Length; c++)
        {
            var ch = text[c];
            if (quote is not null)
            {
                if (ch == quote) quote = null;
            }
            else if (ch == '{') braces++;
            else if (ch == '}') braces--;
            else if (braces == 0 && ch is '"' or '\'') quote = ch;
            else if (braces == 0 && ch == '>') return text[c - 1] == '/';
        }
        return false;
    }

    // ---- SV2 / SV3: exported types live in lib/types and lib/enums, one per file ----

    [GeneratedRegex(@"^export\s+(?:interface|type)\b\s*(\w+)?")]
    private static partial Regex TypeExport();

    private static bool IsTypeFolder(string web) =>
        web.StartsWith("lib/types/", StringComparison.Ordinal) || web.StartsWith("lib/enums/", StringComparison.Ordinal);

    private static bool HasSv2(string web, string[] lines) =>
        !IsTypeFolder(web)
        && !web.EndsWith(".test.ts", StringComparison.Ordinal)
        && !web.StartsWith("lib/testing/", StringComparison.Ordinal)
        && lines.Any(l => TypeExport().IsMatch(l.Trim()));

    private static bool HasSv3(string web, string[] lines)
    {
        if (!IsTypeFolder(web) || Path.GetFileName(web) == "index.ts")
            return false;
        var names = lines.Select(l => TypeExport().Match(l.Trim())).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        return names.Count != 1 || names[0] != Path.GetFileNameWithoutExtension(web);
    }

    // ---- SV4: raw form controls only in lib/components/forms ----

    private static bool HasRawControl(string line) =>
        line.Contains("<input", StringComparison.Ordinal) || line.Contains("<select", StringComparison.Ordinal)
        || line.Contains("<textarea", StringComparison.Ordinal) || line.Contains("<button", StringComparison.Ordinal);

    // ---- SV5: no ../ imports into src/lib ----

    [GeneratedRegex(@"(?:from\s+|import\s*\(\s*|vi\.mock\(\s*|import\s+)['""](\.\./[^'""]*)['""]")]
    private static partial Regex ParentImport();

    private static bool HasSv5(string web, string line)
    {
        foreach (Match m in ParentImport().Matches(line))
        {
            var target = Resolve(Path.GetDirectoryName(web)!.Replace('\\', '/'), m.Groups[1].Value);
            if (target is not null && (target == "lib" || target.StartsWith("lib/", StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    /// <summary><paramref name="relative"/> resolved against <paramref name="folder"/> (both under the web <c>src</c>), or null when it leaves it.</summary>
    private static string? Resolve(string folder, string relative)
    {
        var parts = folder.Length == 0 ? new List<string>() : folder.Split('/').ToList();
        foreach (var segment in relative.Split('/'))
        {
            if (segment == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else if (segment is not ("." or ""))
                parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    // ---- SV6: no static inline styles ----

    [GeneratedRegex(@"(?:^|\s)style=(?:""([^""]*)""|'([^']*)')")]
    private static partial Regex StyleAttribute();

    private static bool HasStaticStyle(string line) =>
        StyleAttribute().Matches(line).Any(m => !(m.Groups[1].Value + m.Groups[2].Value).Contains('{'));
}
