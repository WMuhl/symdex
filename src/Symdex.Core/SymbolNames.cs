namespace Symdex.Core;

/// <summary>
/// Matches a C# query against a Roslyn <c>fqname</c> after the return type is removed.
/// Parameters may be omitted. A dotted suffix must fall on a <c>.</c> boundary.
/// </summary>
internal static class SymbolNames
{
    private static readonly string[] ParameterModifiers = ["ref", "out", "in", "params", "scoped", "readonly"];

    private static readonly Dictionary<string, string> KeywordTypes = new(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["char"] = "System.Char",
        ["decimal"] = "System.Decimal",
        ["double"] = "System.Double",
        ["float"] = "System.Single",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["nint"] = "System.IntPtr",
        ["nuint"] = "System.UIntPtr",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["object"] = "System.Object",
        ["string"] = "System.String",
        ["void"] = "System.Void",
    };

    public static bool IsQualifiedQuery(string query)
    {
        var parsed = Parse(query);
        return parsed.Parameters is not null
            || parsed.NamePath.Contains('.')
            || parsed.NamePath.Contains('<')
            || parsed.NamePath.Contains('[');
    }

    public static string SimpleName(string query)
    {
        var path = Parse(query).NamePath;
        var dot = LastIndexOfTopLevel(path, '.');
        var segment = dot >= 0 ? path[(dot + 1)..] : path;
        var angle = IndexOfTopLevel(segment, '<');
        if (angle >= 0)
        {
            segment = segment[..angle];
        }

        var bracket = IndexOfTopLevel(segment, '[');
        if (bracket >= 0)
        {
            segment = segment[..bracket];
        }

        return segment;
    }

    public static string FindHintSegment(string query)
    {
        var segment = SimpleName(query).Trim();
        return segment.Length == 0 ? query : segment;
    }

    public static bool Matches(string query, string? fqname)
    {
        if (string.IsNullOrEmpty(fqname) || !IsQualifiedQuery(query))
        {
            return false;
        }

        var wanted = Parse(query);
        var stored = Parse(fqname);
        if (wanted.NamePath.Length == 0 || !IsDottedSuffix(stored.NamePath, wanted.NamePath))
        {
            return false;
        }

        if (wanted.Parameters is null)
        {
            return true;
        }

        return stored.Parameters is not null && ParametersMatch(wanted.Parameters, stored.Parameters);
    }

    private readonly record struct ParsedMember(string NamePath, string? Parameters);

    private static ParsedMember Parse(string text)
    {
        var body = StripReturnType(text.Trim());
        var paren = IndexOfTopLevel(body, '(');
        if (paren < 0 || !body.EndsWith(')'))
        {
            return new ParsedMember(body, null);
        }

        return new ParsedMember(body[..paren], body[(paren + 1)..^1]);
    }

    private static string StripReturnType(string text)
    {
        var space = IndexOfTopLevel(text, ' ');
        if (space <= 0)
        {
            return text;
        }

        var tail = text[(space + 1)..].TrimStart();
        if (tail.Contains('.') || IndexOfTopLevel(tail, '(') >= 0)
        {
            return tail;
        }

        return text;
    }

    private static bool IsDottedSuffix(string full, string suffix)
    {
        if (suffix.Length == 0 || full.Length < suffix.Length)
        {
            return false;
        }

        if (full.Equals(suffix, StringComparison.Ordinal))
        {
            return true;
        }

        return full.EndsWith(suffix, StringComparison.Ordinal)
            && full[full.Length - suffix.Length - 1] == '.';
    }

    private static bool ParametersMatch(string query, string stored)
    {
        var left = SplitTopLevel(query, ',');
        var right = SplitTopLevel(stored, ',');
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!TypeMatches(NormalizeParameter(left[i]), right[i].Trim()))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeParameter(string raw)
    {
        var text = raw.Trim();
        while (true)
        {
            var space = IndexOfTopLevel(text, ' ');
            if (space <= 0)
            {
                break;
            }

            var word = text[..space];
            if (Array.IndexOf(ParameterModifiers, word) < 0)
            {
                break;
            }

            text = text[(space + 1)..].TrimStart();
        }

        var lastSpace = LastIndexOfTopLevel(text, ' ');
        if (lastSpace > 0)
        {
            var tail = text[(lastSpace + 1)..];
            if (IsIdentifier(tail))
            {
                text = text[..lastSpace].TrimEnd();
            }
        }

        return text;
    }

    private static bool IsIdentifier(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (text[0] == '@')
        {
            text = text[1..];
        }

        if (text.Length == 0 || !(char.IsAsciiLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (!(char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TypeMatches(string query, string stored)
    {
        query = query.Trim();
        stored = stored.Trim();
        if (query.Length == 0)
        {
            return stored.Length == 0;
        }

        while (true)
        {
            var querySuffix = TrailingSuffix(query);
            var storedSuffix = TrailingSuffix(stored);
            if (querySuffix is null && storedSuffix is null)
            {
                break;
            }

            if (!string.Equals(querySuffix, storedSuffix, StringComparison.Ordinal))
            {
                return false;
            }

            query = query[..^querySuffix!.Length];
            stored = stored[..^storedSuffix!.Length];
        }

        if (TrySplitGeneric(query, out var queryName, out var queryArgs)
            && TrySplitGeneric(stored, out var storedName, out var storedArgs))
        {
            if (queryArgs.Count != storedArgs.Count || !SimpleTypeMatches(queryName, storedName))
            {
                return false;
            }

            for (var i = 0; i < queryArgs.Count; i++)
            {
                if (!TypeMatches(queryArgs[i], storedArgs[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return SimpleTypeMatches(query, stored);
    }

    private static bool SimpleTypeMatches(string query, string stored)
    {
        if (query == stored)
        {
            return true;
        }

        if (KeywordTypes.TryGetValue(query, out var expanded)
            && (expanded == stored || IsDottedSuffix(stored, expanded)))
        {
            return true;
        }

        return IsDottedSuffix(stored, query);
    }

    private static string? TrailingSuffix(string type)
    {
        if (type.EndsWith('?'))
        {
            return "?";
        }

        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            return "[]";
        }

        if (type.EndsWith('*'))
        {
            return "*";
        }

        return null;
    }

    private static bool TrySplitGeneric(string type, out string name, out List<string> args)
    {
        name = type;
        args = [];
        var open = IndexOfTopLevel(type, '<');
        if (open <= 0 || !type.EndsWith('>'))
        {
            return false;
        }

        var depth = 0;
        var close = -1;
        for (var i = open; i < type.Length; i++)
        {
            if (type[i] == '<')
            {
                depth++;
            }
            else if (type[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        if (close != type.Length - 1)
        {
            return false;
        }

        name = type[..open];
        args = SplitTopLevel(type[(open + 1)..^1], ',');
        return true;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        while (true)
        {
            var index = IndexOfTopLevel(text, separator, start);
            if (index < 0)
            {
                parts.Add(text[start..].Trim());
                return parts;
            }

            parts.Add(text[start..index].Trim());
            start = index + 1;
        }
    }

    private static int LastIndexOfTopLevel(string text, char target)
    {
        var last = -1;
        var angle = 0;
        var paren = 0;
        var bracket = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == target && angle == 0 && paren == 0 && bracket == 0)
            {
                last = i;
            }

            switch (text[i])
            {
                case '<':
                    angle++;
                    break;
                case '>':
                    if (angle > 0)
                    {
                        angle--;
                    }

                    break;
                case '(':
                    paren++;
                    break;
                case ')':
                    if (paren > 0)
                    {
                        paren--;
                    }

                    break;
                case '[':
                    bracket++;
                    break;
                case ']':
                    if (bracket > 0)
                    {
                        bracket--;
                    }

                    break;
            }
        }

        return last;
    }

    private static int IndexOfTopLevel(string text, char target, int start = 0)
    {
        var angle = 0;
        var paren = 0;
        var bracket = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == target && angle == 0 && paren == 0 && bracket == 0)
            {
                return i;
            }

            switch (text[i])
            {
                case '<':
                    angle++;
                    break;
                case '>':
                    if (angle > 0)
                    {
                        angle--;
                    }

                    break;
                case '(':
                    paren++;
                    break;
                case ')':
                    if (paren > 0)
                    {
                        paren--;
                    }

                    break;
                case '[':
                    bracket++;
                    break;
                case ']':
                    if (bracket > 0)
                    {
                        bracket--;
                    }

                    break;
            }
        }

        return -1;
    }
}
