using System.Text.Encodings.Web;
using System.Text.Json;

namespace RnRomManager.Core.Users;

/// <summary>A global hotkey action (B21). <see cref="Role"/> null = every signed-in user.</summary>
public sealed record HotkeyAction(string Id, string Name, IReadOnlyList<string> Defaults, bool Editable, string? Role)
{
    public bool AllowedFor(IReadOnlySet<string> roles) => Role is null || roles.Contains(Role);
}

/// <summary>
/// B21–B23: the hotkey actions, their defaults and the binding rules. Mirrored by <c>src/lib/hotkeys.ts</c> in the SPA;
/// keep both in step. A binding's canonical form is <c>Ctrl+Alt+Shift+Meta+&lt;Key&gt;</c> (modifiers in that order),
/// <c>&lt;Key&gt;</c> being <c>A</c>–<c>Z</c>, <c>0</c>–<c>9</c> or <c>F1</c>–<c>F12</c>.
/// </summary>
public static class Hotkeys
{
    public const string Search = "search";
    public const string NavLibrary = "nav.library";
    public const string NavIngestion = "nav.ingestion";
    public const string NavReview = "nav.review";
    public const string ReviewNext = "review.next";

    public const string FormatError = "Use Ctrl, Alt, Shift or Meta plus one key: A–Z, 0–9 or F1–F12.";
    public const string ModifierError = "Include Ctrl, Alt or Meta.";
    public const string AltGrError = "Ctrl+Alt also needs Shift or Meta: on many keyboards it types characters.";
    public const string ReservedError = "Reserved for global search.";
    public const string NotEditableError = "This shortcut can't be changed.";
    public const string UnknownError = "Unknown action.";

    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Meta"];

    // A database value, never HTML: keep "+" readable instead of +.
    private static readonly JsonSerializerOptions StoredJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>B21's table, in its order (which also decides which of two duplicates fires).</summary>
    public static IReadOnlyList<HotkeyAction> Actions { get; } =
    [
        new(Search, "Open global search", ["Ctrl+K", "Ctrl+Space", "Meta+K"], false, null),
        new(NavLibrary, "Go to Library", ["Alt+Shift+L"], true, null),
        new(NavIngestion, "Go to Ingestion", ["Alt+Shift+I"], true, Roles.Admin),
        new(NavReview, "Go to Review", ["Alt+Shift+F"], true, Roles.Admin),
        new(ReviewNext, "Open the first review item", ["Alt+Shift+G"], true, Roles.Admin),
    ];

    /// <summary>Global search's own bindings: no action may take them.</summary>
    public static IReadOnlyList<string> Reserved => Actions[0].Defaults;

    public static IEnumerable<HotkeyAction> Editable => Actions.Where(a => a.Editable);

    public static HotkeyAction? Find(string id) => Actions.FirstOrDefault(a => a.Id == id);

    /// <summary>The canonical form of <paramref name="combination"/> (any modifier order and letter case), or null when it isn't one.</summary>
    public static string? Normalize(string? combination)
    {
        if (string.IsNullOrWhiteSpace(combination)) return null;
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var raw in combination.Split('+'))
        {
            var token = raw.Trim();
            var modifier = ModifierOrder.FirstOrDefault(m => string.Equals(m, token, StringComparison.OrdinalIgnoreCase));
            if (modifier is not null)
            {
                if (!modifiers.Add(modifier)) return null;
            }
            else if (key is null && NormalizeKey(token) is { } k)
            {
                key = k;
            }
            else
            {
                return null;
            }
        }

        return key is null ? null : string.Join('+', ModifierOrder.Where(modifiers.Contains).Append(key));
    }

    private static string? NormalizeKey(string token)
    {
        if (token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]))
            return token.ToUpperInvariant();
        if (token.Length is 2 or 3 && token[0] is 'F' or 'f'
            && int.TryParse(token.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && n is >= 1 and <= 12 && token[1] != '0')
            return "F" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>B22's rules for one canonical binding (duplicates are checked across actions by <see cref="Apply"/>).</summary>
    public static string? RuleError(string canonical)
    {
        var parts = canonical.Split('+');
        var has = (string m) => parts.Contains(m, StringComparer.Ordinal);
        if (!has("Ctrl") && !has("Alt") && !has("Meta")) return ModifierError;
        if (has("Ctrl") && has("Alt") && !has("Shift") && !has("Meta")) return AltGrError;
        if (Reserved.Contains(canonical, StringComparer.Ordinal)) return ReservedError;
        return null;
    }

    /// <summary>The stored overrides (action id → binding or null); unreadable JSON or unknown ids count as none.</summary>
    public static Dictionary<string, string?> ParseStored(string? json)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return overrides;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
            foreach (var (id, value) in stored ?? [])
            {
                if (Find(id) is { Editable: true })
                    overrides[id] = value is null ? null : Normalize(value);
            }
        }
        catch (JsonException)
        {
            // A broken value falls back to the defaults rather than locking the user out of the modal.
        }

        return overrides;
    }

    /// <summary>The column value: null when nothing differs from the defaults.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, string?> overrides) =>
        overrides.Count == 0
            ? null
            : JsonSerializer.Serialize(Editable.Where(a => overrides.ContainsKey(a.Id)).ToDictionary(a => a.Id, a => overrides[a.Id]), StoredJson);

    /// <summary>Every editable action's binding: its override, else its default (B23 GET).</summary>
    public static Dictionary<string, string?> Merge(IReadOnlyDictionary<string, string?> overrides) =>
        Editable.ToDictionary(a => a.Id, a => overrides.TryGetValue(a.Id, out var value) ? value : a.Defaults[0]);

    /// <summary>
    /// B23 PUT: <paramref name="requested"/> replaces the overrides of every action the user's roles allow (missing = its
    /// default); overrides of other actions are kept unchanged and ignored for conflicts (B22). Overrides equal to the
    /// default aren't stored.
    /// </summary>
    /// <returns>The new overrides, or null with field errors keyed <c>bindings.&lt;id&gt;</c>.</returns>
    public static (Dictionary<string, string?>? Overrides, Dictionary<string, string[]> Errors) Apply(
        IReadOnlyDictionary<string, string?> stored, IReadOnlyDictionary<string, string?> requested, IReadOnlySet<string> roles)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var overrides = stored.Where(o => Find(o.Key) is { } a && !a.AllowedFor(roles))
            .ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal);

        foreach (var (id, value) in requested)
        {
            var key = $"bindings.{id}";
            var action = Find(id);
            if (action is null)
            {
                errors[key] = [UnknownError];
                continue;
            }

            if (!action.Editable)
            {
                errors[key] = [NotEditableError];
                continue;
            }

            if (!action.AllowedFor(roles))
                continue;
            if (value is null)
            {
                overrides[id] = null;
                continue;
            }

            var canonical = Normalize(value);
            if (canonical is null)
            {
                errors[key] = [FormatError];
                continue;
            }

            if (RuleError(canonical) is { } rule)
            {
                errors[key] = [rule];
                continue;
            }

            if (canonical != action.Defaults[0])
                overrides[id] = canonical;
        }

        // Duplicates among the actions this user can use; the later one in table order gets the error.
        var merged = Merge(overrides);
        var taken = new Dictionary<string, HotkeyAction>(StringComparer.Ordinal);
        foreach (var action in Editable.Where(a => a.AllowedFor(roles)))
        {
            if (merged[action.Id] is not { } binding || errors.ContainsKey($"bindings.{action.Id}")) continue;
            if (taken.TryGetValue(binding, out var first))
                errors[$"bindings.{action.Id}"] = [$"Already used by {first.Name}."];
            else
                taken[binding] = action;
        }

        return errors.Count > 0 ? (null, errors) : (overrides, errors);
    }
}
