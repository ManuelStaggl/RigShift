using System.Text;

namespace RigShift.Windows.Games;

/// <summary>
/// A node of Valve's text format (VDF/ACF), as used by <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c>: keys
/// hold either a string or a nested node. Written by hand rather than pulled in as a dependency – the format is a
/// handful of rules and we only ever read it.
/// </summary>
public sealed class ValveNode
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ValveNode> _children = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The nested nodes, in file order of first appearance.</summary>
    public IReadOnlyDictionary<string, ValveNode> Children => _children;

    /// <summary>The plain key/value pairs of this node.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    public string? Value(string key) => _values.GetValueOrDefault(key);

    public ValveNode? Child(string key) => _children.GetValueOrDefault(key);

    internal void SetValue(string key, string value) => _values[key] = value;

    internal ValveNode AddChild(string key)
    {
        var child = new ValveNode();
        _children[key] = child;
        return child;
    }
}

/// <summary>Reader for Valve's text format. Never throws on malformed input – it returns what it could make sense of.</summary>
public static class ValveDataFormat
{
    /// <summary>Parses the text into a root node whose children are the top-level blocks.</summary>
    public static ValveNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = new ValveNode();
        var stack = new Stack<ValveNode>();
        stack.Push(root);

        int index = 0;
        string? pendingKey = null;
        while (index < text.Length)
        {
            char c = text[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
            }
            else if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] is not ('\n' or '\r'))
                {
                    index++;
                }
            }
            else if (c == '{')
            {
                index++;
                // A block without a key is malformed; keep parsing so one bad line does not lose the whole file.
                stack.Push(pendingKey is null ? new ValveNode() : stack.Peek().AddChild(pendingKey));
                pendingKey = null;
            }
            else if (c == '}')
            {
                index++;
                if (stack.Count > 1)
                {
                    stack.Pop();
                }

                pendingKey = null;
            }
            else if (c == '"')
            {
                string token = ReadQuoted(text, ref index);
                if (pendingKey is null)
                {
                    pendingKey = token;
                }
                else
                {
                    stack.Peek().SetValue(pendingKey, token);
                    pendingKey = null;
                }
            }
            else
            {
                // Unquoted token: read to the next whitespace so the position always advances.
                int start = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}'))
                {
                    index++;
                }

                string token = text[start..index];
                if (pendingKey is null)
                {
                    pendingKey = token;
                }
                else
                {
                    stack.Peek().SetValue(pendingKey, token);
                    pendingKey = null;
                }
            }
        }

        return root;
    }

    /// <summary>Reads a quoted token starting at <paramref name="index"/>, resolving the backslash escapes Valve uses.</summary>
    private static string ReadQuoted(string text, ref int index)
    {
        index++; // opening quote
        var token = new StringBuilder();
        while (index < text.Length && text[index] != '"')
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                index++;
                token.Append(text[index] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => text[index], // covers the doubled backslashes in Windows paths
                });
            }
            else
            {
                token.Append(text[index]);
            }

            index++;
        }

        index++; // closing quote
        return token.ToString();
    }
}
