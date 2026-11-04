namespace RnRomManager.Core.Dats;

/// <summary>
/// Tokenizer + parser for clrmamepro text DATs (spec §10.1): <c>key value</c> pairs where a value is a quoted
/// string, a bare word or a nested <c>( … )</c> block. libretro DATs don't escape quotes, so a string ends at the
/// next <c>"</c>.
/// </summary>
public static class ClrMameProParser
{
    /// <exception cref="DatParseException">Unbalanced parentheses, an unterminated string or a key without a value.</exception>
    public static DatBlock Parse(string text)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var root = ParseItems(tokens, ref position, nested: false);
        return root;
    }

    private static DatBlock ParseItems(List<Token> tokens, ref int position, bool nested)
    {
        var items = new List<KeyValuePair<string, object>>();
        while (position < tokens.Count)
        {
            var token = tokens[position];
            if (token.Kind == TokenKind.Close)
            {
                if (!nested)
                    throw new DatParseException($"Unexpected ')' on line {token.Line}.");
                position++;
                return new DatBlock(items);
            }
            if (token.Kind != TokenKind.Word)
                throw new DatParseException($"Expected a key on line {token.Line}.");

            position++;
            if (position >= tokens.Count)
                throw new DatParseException($"Key '{token.Text}' on line {token.Line} has no value.");

            var value = tokens[position];
            position++;
            switch (value.Kind)
            {
                case TokenKind.Open:
                    items.Add(new(token.Text, ParseItems(tokens, ref position, nested: true)));
                    break;
                case TokenKind.Word or TokenKind.String:
                    items.Add(new(token.Text, value.Text));
                    break;
                default:
                    throw new DatParseException($"Key '{token.Text}' on line {token.Line} has no value.");
            }
        }

        return nested ? throw new DatParseException("Missing ')' at end of file.") : new DatBlock(items);
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var line = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
                line++;
            if (char.IsWhiteSpace(c))
                continue;

            switch (c)
            {
                case '(':
                    tokens.Add(new(TokenKind.Open, "(", line));
                    break;
                case ')':
                    tokens.Add(new(TokenKind.Close, ")", line));
                    break;
                case '"':
                    var end = text.IndexOf('"', i + 1);
                    if (end < 0)
                        throw new DatParseException($"Unterminated string on line {line}.");
                    var value = text[(i + 1)..end];
                    tokens.Add(new(TokenKind.String, value, line));
                    line += value.Count(ch => ch == '\n');
                    i = end;
                    break;
                default:
                    var start = i;
                    while (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] is not ('(' or ')' or '"'))
                        i++;
                    tokens.Add(new(TokenKind.Word, text[start..(i + 1)], line));
                    break;
            }
        }
        return tokens;
    }

    private enum TokenKind
    {
        Open,
        Close,
        String,
        Word,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line);
}
