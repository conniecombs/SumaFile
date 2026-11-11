using System.Globalization;

namespace SimpleFile.Core;

/// <summary>
/// Evaluates the <c>when</c> expression of a plugin context-menu item.
/// Grammar: <c>||</c>, <c>&amp;&amp;</c>, <c>!</c>, parentheses; atoms are
/// <c>selectionCount &lt;op&gt; &lt;int&gt;</c>, <c>singleFile</c>, <c>singleFolder</c>,
/// <c>hasExtension(.ext)</c>, <c>hasDecoration</c> and <c>folderFlag(name)</c>.
/// An empty expression is true; an invalid one is false.
/// </summary>
public static class PluginMenuWhen
{
    public static bool Evaluate(string expression, string pluginId, ContextMenuRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var parser = new Parser(expression, pluginId, request);
        return parser.TryParse(out var value, out _) && value;
    }

    public static bool IsValid(string expression, out string error)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "";
            return true;
        }

        var parser = new Parser(expression, "", new ContextMenuRequest());
        return parser.TryParse(out _, out error);
    }

    private const int MaxDepth = 32;

    private enum TokenKind { Ident, Number, Op, And, Or, Not, LParen, RParen, Arg, End }

    private readonly record struct Token(TokenKind Kind, string Text);

    private sealed class SyntaxError(string message) : Exception(message);

    private sealed class Parser
    {
        private readonly string _source;
        private readonly string _pluginId;
        private readonly ContextMenuRequest _request;
        private List<Token> _tokens = [];
        private int _pos;
        private int _depth;

        public Parser(string source, string pluginId, ContextMenuRequest request)
        {
            _source = source;
            _pluginId = pluginId;
            _request = request;
        }

        public bool TryParse(out bool value, out string error)
        {
            value = false;
            try
            {
                _tokens = Tokenize(_source);
                _pos = 0;
                value = ParseOr();
                if (Peek.Kind != TokenKind.End)
                {
                    throw new SyntaxError($"Unexpected '{Peek.Text}'.");
                }

                error = "";
                return true;
            }
            catch (SyntaxError ex)
            {
                value = false;
                error = ex.Message;
                return false;
            }
        }

        private void Enter()
        {
            if (++_depth > MaxDepth)
            {
                throw new SyntaxError($"Expression nesting is deeper than {MaxDepth} levels.");
            }
        }

        private Token Peek => _tokens[_pos];

        private Token Next() => _tokens[_pos++];

        private bool ParseOr()
        {
            var result = ParseAnd();
            while (Peek.Kind == TokenKind.Or)
            {
                Next();
                var right = ParseAnd();
                result = result || right;
            }

            return result;
        }

        private bool ParseAnd()
        {
            var result = ParseNot();
            while (Peek.Kind == TokenKind.And)
            {
                Next();
                var right = ParseNot();
                result = result && right;
            }

            return result;
        }

        private bool ParseNot()
        {
            if (Peek.Kind == TokenKind.Not)
            {
                Next();
                Enter();
                var inner = ParseNot();
                _depth--;
                return !inner;
            }

            return ParsePrimary();
        }

        private bool ParsePrimary()
        {
            var token = Next();
            switch (token.Kind)
            {
                case TokenKind.LParen:
                {
                    Enter();
                    var inner = ParseOr();
                    Expect(TokenKind.RParen, "Expected ')'.");
                    _depth--;
                    return inner;
                }
                case TokenKind.Ident:
                    return ParseAtom(token.Text);
                default:
                    throw new SyntaxError(token.Kind == TokenKind.End ? "Unexpected end of expression." : $"Unexpected '{token.Text}'.");
            }
        }

        private bool ParseAtom(string name)
        {
            switch (name)
            {
                case "selectionCount":
                {
                    var op = Expect(TokenKind.Op, "Expected a comparison operator after 'selectionCount'.").Text;
                    var number = Expect(TokenKind.Number, $"Expected a number after '{op}'.").Text;
                    var rhs = int.Parse(number, CultureInfo.InvariantCulture);
                    var count = _request.SelectionCount;
                    return op switch
                    {
                        "==" => count == rhs,
                        "!=" => count != rhs,
                        "<" => count < rhs,
                        "<=" => count <= rhs,
                        ">" => count > rhs,
                        _ => count >= rhs,
                    };
                }
                case "singleFile":
                    return _request.SelectionCount == 1 && !_request.SelectedIsDirectory;
                case "singleFolder":
                    return _request.SelectionCount == 1 && _request.SelectedIsDirectory;
                case "hasDecoration":
                    return _request.SelectionDecorationPlugins.Contains(_pluginId);
                case "hasExtension":
                {
                    var arg = ParseArgument(name).TrimStart('.');
                    return arg.Length > 0
                        && string.Equals(arg, _request.SelectedExtension?.TrimStart('.'), StringComparison.OrdinalIgnoreCase);
                }
                case "folderFlag":
                {
                    var arg = ParseArgument(name);
                    return arg.Length > 0 && _request.FolderFlags.Contains($"{_pluginId}:{arg}");
                }
                default:
                    throw new SyntaxError($"Unknown term '{name}'.");
            }
        }

        private string ParseArgument(string function)
        {
            Expect(TokenKind.LParen, $"Expected '(' after '{function}'.");
            var arg = Expect(TokenKind.Arg, $"Expected an argument for '{function}'.").Text;
            Expect(TokenKind.RParen, "Expected ')'.");
            return arg;
        }

        private Token Expect(TokenKind kind, string message)
        {
            if (Peek.Kind != kind)
            {
                throw new SyntaxError(message);
            }

            return Next();
        }

        private static List<Token> Tokenize(string source)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < source.Length)
            {
                var c = source[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsAsciiLetter(c))
                {
                    var start = i;
                    while (i < source.Length && char.IsAsciiLetterOrDigit(source[i]))
                    {
                        i++;
                    }

                    var ident = source[start..i];
                    tokens.Add(new Token(TokenKind.Ident, ident));
                    if (ident is "hasExtension" or "folderFlag" && NextNonSpace(source, i) == '(')
                    {
                        // Function arguments are raw text up to the closing parenthesis.
                        while (source[i] != '(')
                        {
                            i++;
                        }

                        tokens.Add(new Token(TokenKind.LParen, "("));
                        i++;
                        var argStart = i;
                        while (i < source.Length && source[i] != ')')
                        {
                            i++;
                        }

                        tokens.Add(new Token(TokenKind.Arg, source[argStart..i].Trim()));
                    }

                    continue;
                }

                if (char.IsAsciiDigit(c))
                {
                    var start = i;
                    while (i < source.Length && char.IsAsciiDigit(source[i]))
                    {
                        i++;
                    }

                    var text = source[start..i];
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    {
                        throw new SyntaxError($"Number '{text}' is too large.");
                    }

                    tokens.Add(new Token(TokenKind.Number, text));
                    continue;
                }

                var two = i + 1 < source.Length ? source.Substring(i, 2) : "";
                if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=")
                {
                    tokens.Add(new Token(two switch { "&&" => TokenKind.And, "||" => TokenKind.Or, _ => TokenKind.Op }, two));
                    i += 2;
                    continue;
                }

                switch (c)
                {
                    case '<' or '>':
                        tokens.Add(new Token(TokenKind.Op, c.ToString()));
                        break;
                    case '!':
                        tokens.Add(new Token(TokenKind.Not, "!"));
                        break;
                    case '(':
                        tokens.Add(new Token(TokenKind.LParen, "("));
                        break;
                    case ')':
                        tokens.Add(new Token(TokenKind.RParen, ")"));
                        break;
                    default:
                        throw new SyntaxError($"Unexpected character '{c}'.");
                }

                i++;
            }

            tokens.Add(new Token(TokenKind.End, "end of expression"));
            return tokens;
        }

        private static char NextNonSpace(string source, int index)
        {
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            return index < source.Length ? source[index] : '\0';
        }
    }
}
