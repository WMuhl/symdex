using System.Text;
using Symdex.Core;
using Symdex.Core.Feeding;
using Symdex.TreeSitter.Native;

namespace Symdex.TreeSitter;

/// <summary>
/// Syntax-only feeder backed by the vendored tree-sitter grammars. Used for TypeScript/JavaScript
/// always, and for C# whenever the Roslyn feeder can't load a solution.
/// </summary>
public sealed class TreeSitterFeeder : ISymbolFeeder
{
    private static readonly HashSet<string> ContainerKinds =
    [
        SymbolKinds.Class, SymbolKinds.Interface, SymbolKinds.Struct, SymbolKinds.Enum,
    ];

    public string Name => "tree-sitter";

    public bool CanHandle(string language) =>
        language is Languages.CSharp or Languages.TypeScript or Languages.JavaScript;

    public FeederResult Feed(FeedRequest request)
    {
        if (!NativeLibraryProbe.IsAvailable)
        {
            return new FeederResult(
                false,
                "symdex: tree-sitter native library not found (no C toolchain at build time?) - skipping syntax-only indexing.",
                new Dictionary<string, FileFeedResult>());
        }

        var results = new Dictionary<string, FileFeedResult>();
        foreach (var file in request.ChangedFiles)
        {
            results[file.RepoRelativePath] = FeedFile(file);
        }

        return new FeederResult(true, null, results);
    }

    private static FileFeedResult FeedFile(ScannedFile file)
    {
        var grammar = GrammarRegistry.ForFile(file.RepoRelativePath);
        if (grammar is null)
        {
            return new FileFeedResult([], []);
        }

        var sourceBytes = File.ReadAllBytes(file.AbsolutePath);
        var parser = NativeMethods.ts_parser_new();
        try
        {
            if (!NativeMethods.ts_parser_set_language(parser, grammar.Language))
            {
                return new FileFeedResult([], []);
            }

            var tree = NativeMethods.ts_parser_parse_string(parser, IntPtr.Zero, sourceBytes, (uint)sourceBytes.Length);
            if (tree == IntPtr.Zero)
            {
                return new FileFeedResult([], []);
            }

            try
            {
                var root = NativeMethods.ts_tree_root_node(tree);
                return ExtractFromTree(sourceBytes, root, grammar);
            }
            finally
            {
                NativeMethods.ts_tree_delete(tree);
            }
        }
        finally
        {
            NativeMethods.ts_parser_delete(parser);
        }
    }

    private static FileFeedResult ExtractFromTree(byte[] sourceBytes, TSNode root, Grammar grammar)
    {
        var symbols = new List<(string Kind, TSNode DefNode, TSNode NameNode)>();
        var refs = new List<RefRow>();

        foreach (var match in grammar.Query.Execute(root))
        {
            var kind = grammar.PatternKinds[match.PatternIndex];

            if (kind == "import")
            {
                if (match.Captures.TryGetValue("source", out var sourceNode))
                {
                    var text = GetText(sourceBytes, sourceNode).Trim('"', '\'', '`');
                    var line = (int)NativeMethods.ts_node_start_point(sourceNode).Row + 1;
                    refs.Add(new RefRow(text, line, RefKinds.Reference));
                }

                continue;
            }

            if (match.Captures.TryGetValue("def", out var defNode) && match.Captures.TryGetValue("name", out var nameNode))
            {
                symbols.Add((kind, defNode, nameNode));
            }
        }

        var rows = BuildSymbolRows(sourceBytes, symbols);
        return new FileFeedResult(rows, refs);
    }

    private static List<SymbolRow> BuildSymbolRows(byte[] sourceBytes, List<(string Kind, TSNode DefNode, TSNode NameNode)> symbols)
    {
        var provisional = new List<(string Kind, string Name, string Signature, string? Doc, int StartLine, int EndLine)>();
        foreach (var (kind, defNode, nameNode) in symbols)
        {
            var name = GetText(sourceBytes, nameNode);
            var startLine = (int)NativeMethods.ts_node_start_point(defNode).Row + 1;
            var endLine = (int)NativeMethods.ts_node_end_point(defNode).Row + 1;
            var signature = BuildSignature(sourceBytes, defNode);
            var doc = ExtractLeadingDoc(sourceBytes, (int)NativeMethods.ts_node_start_byte(defNode));
            provisional.Add((kind, name, signature, doc, startLine, endLine));
        }

        var rows = new List<SymbolRow>(provisional.Count);
        foreach (var (kind, name, signature, doc, startLine, endLine) in provisional)
        {
            string? container = null;
            var bestSpan = int.MaxValue;
            foreach (var candidate in provisional)
            {
                if (!ContainerKinds.Contains(candidate.Kind))
                {
                    continue;
                }

                if (candidate.Name == name && candidate.StartLine == startLine && candidate.EndLine == endLine)
                {
                    continue; // don't treat a symbol as its own container
                }

                if (candidate.StartLine <= startLine && endLine <= candidate.EndLine)
                {
                    var span = candidate.EndLine - candidate.StartLine;
                    if (span < bestSpan)
                    {
                        bestSpan = span;
                        container = candidate.Name;
                    }
                }
            }

            rows.Add(new SymbolRow(name, null, kind, signature, doc, startLine, endLine, container));
        }

        return rows;
    }

    private static string GetText(byte[] sourceBytes, TSNode node)
    {
        var start = (int)NativeMethods.ts_node_start_byte(node);
        var end = (int)NativeMethods.ts_node_end_byte(node);
        return Encoding.UTF8.GetString(sourceBytes, start, end - start);
    }

    private static string BuildSignature(byte[] sourceBytes, TSNode defNode)
    {
        var text = GetText(sourceBytes, defNode);
        var cut = text.IndexOfAny(['{', ';']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim();
    }

    private static string? ExtractLeadingDoc(byte[] sourceBytes, int nodeStartByte)
    {
        var textBefore = Encoding.UTF8.GetString(sourceBytes, 0, nodeStartByte);
        var lines = textBefore.Split('\n');

        var commentLines = new List<string>();
        for (var i = lines.Length - 2; i >= 0; i--) // skip the (partial) current line
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("///", StringComparison.Ordinal) ||
                line.StartsWith("//", StringComparison.Ordinal) ||
                line.StartsWith("*", StringComparison.Ordinal) ||
                line.StartsWith("/**", StringComparison.Ordinal) ||
                line.EndsWith("*/", StringComparison.Ordinal))
            {
                commentLines.Insert(0, line);
                continue;
            }

            break;
        }

        if (commentLines.Count == 0)
        {
            return null;
        }

        foreach (var raw in commentLines)
        {
            var cleaned = CleanCommentLine(raw);
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        return null;
    }

    private static string CleanCommentLine(string line)
    {
        var s = line.Trim();
        s = s.TrimStart('/', '*').TrimEnd('*', '/').Trim();

        // Strip simple XML/JSDoc tags like <summary>, </summary>, @param, etc.
        while (true)
        {
            var start = s.IndexOf('<');
            var end = start >= 0 ? s.IndexOf('>', start) : -1;
            if (start < 0 || end < 0)
            {
                break;
            }

            s = (s[..start] + s[(end + 1)..]).Trim();
        }

        return s;
    }
}
