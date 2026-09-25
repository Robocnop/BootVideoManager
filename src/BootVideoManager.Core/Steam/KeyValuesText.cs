using System.Text;

namespace BootVideoManager.Core.Steam;

/// <summary>
/// Minimal reader/editor for Valve's text KeyValues files (<c>config.vdf</c>). Edits are surgical: only the
/// targeted value (or the inserted lines) change, every other byte of the file is kept as is.
/// </summary>
internal static class KeyValuesText
{
    private enum TokenKind
    {
        String,
        Open,
        Close,
    }

    /// <param name="Start">Index of the token's first character (the opening quote for a quoted string).</param>
    /// <param name="End">Index just past the token.</param>
    private readonly record struct Token(TokenKind Kind, string Text, int Start, int End);

    private sealed class Node(string key, int depth)
    {
        public string Key { get; } = key;

        public int Depth { get; } = depth;

        /// <summary>Value token for a key/value pair; <c>null</c> for a block.</summary>
        public Token? Value { get; set; }

        public List<Node> Children { get; } = [];

        /// <summary>Index of the block's closing brace; -1 until it is read.</summary>
        public int CloseIndex { get; set; } = -1;
    }

    /// <summary>Reads a string value; <c>false</c> when a key on the path is missing or is a block.</summary>
    /// <exception cref="InvalidDataException">The text is not well-formed KeyValues.</exception>
    public static bool TryGetValue(string text, IReadOnlyList<string> path, out string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);
        value = string.Empty;
        var node = Find(Parse(text), path);
        if (node?.Value is not { } token)
        {
            return false;
        }

        value = token.Text;
        return true;
    }

    /// <summary>Returns <paramref name="text"/> with the value at <paramref name="path"/> set, creating missing keys below the root.</summary>
    /// <exception cref="InvalidDataException">Malformed text, missing root block, or a key on the path has the wrong kind.</exception>
    public static string SetValue(string text, IReadOnlyList<string> path, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        if (path.Count < 2)
        {
            throw new ArgumentException("The path needs a root block and a key.", nameof(path));
        }

        var current = Parse(text).Find(n => Matches(n, path[0]));
        if (current is null || current.Value is not null)
        {
            throw new InvalidDataException($"Root block \"{path[0]}\" not found.");
        }

        for (var i = 1; i < path.Count; i++)
        {
            var child = current.Children.Find(n => Matches(n, path[i]));
            if (child is null)
            {
                return Insert(text, current, path.Skip(i).ToList(), value);
            }

            if (i == path.Count - 1)
            {
                if (child.Value is not { } token)
                {
                    throw new InvalidDataException($"\"{path[i]}\" is a block, not a value.");
                }

                return string.Concat(text.AsSpan(0, token.Start), Quote(value), text.AsSpan(token.End));
            }

            if (child.Value is not null)
            {
                throw new InvalidDataException($"\"{path[i]}\" is a value, not a block.");
            }

            current = child;
        }

        throw new InvalidOperationException("Unreachable: the loop always returns on the last key.");
    }

    private static bool Matches(Node node, string key) => string.Equals(node.Key, key, StringComparison.OrdinalIgnoreCase);

    private static Node? Find(List<Node> roots, IReadOnlyList<string> path)
    {
        Node? node = null;
        var level = roots;
        foreach (var key in path)
        {
            node = level.Find(n => Matches(n, key));
            if (node is null)
            {
                return null;
            }

            level = node.Children;
        }

        return node;
    }

    /// <summary>Inserts the missing keys as new lines just before the closing brace of <paramref name="parent"/>.</summary>
    private static string Insert(string text, Node parent, List<string> keys, string value)
    {
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var depth = parent.Depth + 1;
        var builder = new StringBuilder();
        for (var i = 0; i < keys.Count - 1; i++)
        {
            builder.Append('\t', depth + i).Append(Quote(keys[i])).Append(newLine);
            builder.Append('\t', depth + i).Append('{').Append(newLine);
        }

        builder.Append('\t', depth + keys.Count - 1).Append(Quote(keys[^1])).Append("\t\t").Append(Quote(value)).Append(newLine);
        for (var i = keys.Count - 2; i >= 0; i--)
        {
            builder.Append('\t', depth + i).Append('}').Append(newLine);
        }

        // Keep the closing brace's own line intact: insert at the start of that line when the brace stands alone.
        var close = parent.CloseIndex;
        var lineStart = close == 0 ? 0 : text.LastIndexOf('\n', close - 1) + 1;
        return string.IsNullOrWhiteSpace(text[lineStart..close])
            ? string.Concat(text.AsSpan(0, lineStart), builder.ToString(), text.AsSpan(lineStart))
            : string.Concat(text.AsSpan(0, close), newLine, builder.ToString(), text.AsSpan(close));
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static List<Node> Parse(string text)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var roots = ParseBlock(tokens, ref position, depth: 0, parent: null);
        if (position != tokens.Count)
        {
            throw new InvalidDataException("Unexpected closing brace at the top level.");
        }

        return roots;
    }

    private static List<Node> ParseBlock(List<Token> tokens, ref int position, int depth, Node? parent)
    {
        var nodes = new List<Node>();
        while (position < tokens.Count)
        {
            var key = tokens[position];
            if (key.Kind == TokenKind.Close)
            {
                if (parent is not null)
                {
                    parent.CloseIndex = key.Start;
                    position++;
                }

                return nodes;
            }

            if (key.Kind != TokenKind.String)
            {
                throw new InvalidDataException($"Expected a key at offset {key.Start}.");
            }

            if (++position >= tokens.Count)
            {
                throw new InvalidDataException($"Key \"{key.Text}\" has no value.");
            }

            var node = new Node(key.Text, depth);
            var next = tokens[position++];
            if (next.Kind == TokenKind.String)
            {
                node.Value = next;
            }
            else if (next.Kind == TokenKind.Open)
            {
                node.Children.AddRange(ParseBlock(tokens, ref position, depth + 1, node));
                if (node.CloseIndex < 0)
                {
                    throw new InvalidDataException($"Block \"{key.Text}\" is not closed.");
                }
            }
            else
            {
                throw new InvalidDataException($"Unexpected closing brace after \"{key.Text}\".");
            }

            nodes.Add(node);
        }

        if (parent is not null)
        {
            throw new InvalidDataException($"Block \"{parent.Key}\" is not closed.");
        }

        return nodes;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c is '{' or '}')
            {
                tokens.Add(new Token(c == '{' ? TokenKind.Open : TokenKind.Close, c.ToString(), i, i + 1));
                i++;
            }
            else if (c == '"')
            {
                var start = i++;
                var builder = new StringBuilder();
                while (true)
                {
                    if (i >= text.Length)
                    {
                        throw new InvalidDataException($"Unterminated string at offset {start}.");
                    }

                    var d = text[i++];
                    if (d == '"')
                    {
                        break;
                    }

                    if (d == '\\' && i < text.Length)
                    {
                        var escaped = text[i++];
                        builder.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            _ => escaped,
                        });
                    }
                    else
                    {
                        builder.Append(d);
                    }
                }

                tokens.Add(new Token(TokenKind.String, builder.ToString(), start, i));
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('"' or '{' or '}'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.String, text[start..i], start, i));
            }
        }

        return tokens;
    }
}
