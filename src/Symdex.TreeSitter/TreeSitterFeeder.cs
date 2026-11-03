using System.Runtime.InteropServices;
using System.Text;
using Symdex.Core;
using Symdex.Core.Feeding;
using Symdex.TreeSitter.Native;

namespace Symdex.TreeSitter;

/// <summary>
/// Syntax-only feeder backed by the vendored tree-sitter grammars. Used for TypeScript/JavaScript
/// and Python always, and for C# whenever the Roslyn feeder can't load a solution.
/// </summary>
public sealed class TreeSitterFeeder : ISymbolFeeder
{
    private static readonly HashSet<string> ContainerKinds =
    [
        SymbolKinds.Class, SymbolKinds.Interface, SymbolKinds.Struct, SymbolKinds.Enum,
    ];

    // Parents whose `name` field is the binding being declared, not a use of that name.
    private static readonly HashSet<string> DeclarationNameParents =
    [
        "class", "class_declaration", "abstract_class_declaration",
        "interface_declaration", "type_alias_declaration", "enum_declaration",
        "function_declaration", "function_expression", "function_signature",
        "generator_function", "generator_function_declaration",
        "variable_declarator", "required_parameter", "optional_parameter",
        "type_parameter", "mapped_type_clause", "internal_module", "module",
        "method_definition", "public_field_definition", "property_signature",
        "method_signature", "abstract_method_signature", "enum_assignment",
    ];

    private static readonly byte[] NameField = "name"u8.ToArray();
    private static readonly byte[] AliasField = "alias"u8.ToArray();
    private static readonly byte[] ParameterField = "parameter"u8.ToArray();
    private static readonly byte[] PatternField = "pattern"u8.ToArray();
    private static readonly byte[] LeftField = "left"u8.ToArray();

    public string Name => "tree-sitter";

    public bool CanHandle(string language) =>
        language is Languages.CSharp or Languages.TypeScript or Languages.JavaScript or Languages.Python;

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
        var refCandidates = new List<(string PatternKind, string Name, int Line, uint Start, TSNode Node)>();

        foreach (var match in grammar.Query.Execute(root))
        {
            var kind = grammar.PatternKinds[match.PatternIndex];

            if (kind is "import" or "call" or "reference" or "implementation")
            {
                if (match.Captures.TryGetValue("source", out var sourceNode))
                {
                    var text = GetText(sourceBytes, sourceNode);
                    if (kind == "import")
                    {
                        text = text.Trim('"', '\'', '`');
                    }

                    if (text.Length > 0)
                    {
                        var line = (int)NativeMethods.ts_node_start_point(sourceNode).Row + 1;
                        refCandidates.Add((kind, text, line, NativeMethods.ts_node_start_byte(sourceNode), sourceNode));
                    }
                }

                continue;
            }

            if (match.Captures.TryGetValue("def", out var defNode) && match.Captures.TryGetValue("name", out var nameNode))
            {
                if (IsInnerDecoratedDefinition(defNode))
                {
                    continue;
                }

                if (kind == SymbolKinds.Function && IsDirectClassMember(defNode))
                {
                    kind = SymbolKinds.Method;
                }

                symbols.Add((kind, defNode, nameNode));
            }
        }

        var rows = BuildSymbolRows(sourceBytes, symbols);
        var refs = BuildRefRows(symbols, refCandidates);
        return new FileFeedResult(rows, refs);
    }

    private static List<RefRow> BuildRefRows(
        List<(string Kind, TSNode DefNode, TSNode NameNode)> symbols,
        List<(string PatternKind, string Name, int Line, uint Start, TSNode Node)> refCandidates)
    {
        var defStarts = new HashSet<uint>(symbols.Select(s => NativeMethods.ts_node_start_byte(s.NameNode)));
        var callStarts = new HashSet<uint>();
        var implementationStarts = new HashSet<uint>();
        foreach (var candidate in refCandidates)
        {
            if (candidate.PatternKind == "call")
            {
                callStarts.Add(candidate.Start);
            }
            else if (candidate.PatternKind == "implementation")
            {
                implementationStarts.Add(candidate.Start);
            }
        }

        var seen = new HashSet<(string Name, int Line, string Kind)>();
        var refs = new List<RefRow>();
        foreach (var candidate in refCandidates)
        {
            var refKind = candidate.PatternKind switch
            {
                "call" => RefKinds.Call,
                "implementation" => RefKinds.Implementation,
                _ => RefKinds.Reference,
            };
            if (candidate.PatternKind == "reference")
            {
                if (defStarts.Contains(candidate.Start) ||
                    callStarts.Contains(candidate.Start) ||
                    implementationStarts.Contains(candidate.Start))
                {
                    continue;
                }

                if (IsNonReferenceIdentifier(candidate.Node))
                {
                    continue;
                }
            }

            if (!seen.Add((candidate.Name, candidate.Line, refKind)))
            {
                continue;
            }

            refs.Add(new RefRow(candidate.Name, candidate.Line, refKind));
        }

        return refs;
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
            var doc = ExtractDoc(sourceBytes, defNode);
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
        var defStart = (int)NativeMethods.ts_node_start_byte(defNode);
        var bodyStart = FindBodyStart(defNode);
        string text;
        if (bodyStart is int cut && cut > defStart)
        {
            text = Encoding.UTF8.GetString(sourceBytes, defStart, cut - defStart);
        }
        else
        {
            text = GetText(sourceBytes, defNode);
            var brace = text.IndexOfAny(['{', ';']);
            if (brace >= 0)
            {
                text = text[..brace];
            }
        }

        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim();
    }

    private static int? FindBodyStart(TSNode node)
    {
        var count = NativeMethods.ts_node_child_count(node);
        for (uint i = 0; i < count; i++)
        {
            var child = NativeMethods.ts_node_child(node, i);
            var type = NodeType(child);
            if (IsBodyNode(type))
            {
                return (int)NativeMethods.ts_node_start_byte(child);
            }

            if (type is "function_definition" or "class_definition")
            {
                var inner = FindBodyStart(child);
                if (inner is not null)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    private static bool IsBodyNode(string type) => type is
        "block" or "statement_block" or "class_body" or "declaration_list" or
        "enum_body" or "enum_member_declaration_list" or "accessor_list" or "interface_body";

    private static string? ExtractDoc(byte[] sourceBytes, TSNode defNode)
    {
        var type = NodeType(defNode);
        if (type is "function_definition" or "class_definition" or "decorated_definition")
        {
            var docstring = ExtractDocstring(sourceBytes, defNode);
            if (docstring is not null)
            {
                return docstring;
            }
        }

        return ExtractLeadingDoc(sourceBytes, (int)NativeMethods.ts_node_start_byte(defNode));
    }

    private static string? ExtractDocstring(byte[] sourceBytes, TSNode defNode)
    {
        var bodyOwner = defNode;
        if (NodeType(defNode) == "decorated_definition")
        {
            var inner = FindChild(defNode, "function_definition", "class_definition");
            if (inner is null)
            {
                return null;
            }

            bodyOwner = inner.Value;
        }

        var body = FindChild(bodyOwner, "block");
        if (body is null || NativeMethods.ts_node_child_count(body.Value) == 0)
        {
            return null;
        }

        var first = NativeMethods.ts_node_child(body.Value, 0);
        if (NodeType(first) != "expression_statement")
        {
            return null;
        }

        var literal = FindChild(first, "string", "concatenated_string");
        if (literal is null)
        {
            return null;
        }

        if (NodeType(literal.Value) == "concatenated_string")
        {
            var part = FindChild(literal.Value, "string");
            if (part is null)
            {
                return null;
            }

            literal = part;
        }

        return StripPythonString(GetText(sourceBytes, literal.Value));
    }

    private static string? StripPythonString(string text)
    {
        text = text.Trim();
        var i = 0;
        while (i < text.Length && "rRuUbBfF".Contains(text[i]))
        {
            i++;
        }

        text = text[i..];
        string[] quotes = ["\"\"\"", "'''", "\"", "'"];
        foreach (var quote in quotes)
        {
            if (text.StartsWith(quote, StringComparison.Ordinal) &&
                text.EndsWith(quote, StringComparison.Ordinal) &&
                text.Length >= quote.Length * 2)
            {
                text = text[quote.Length..^quote.Length];
                break;
            }
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static bool IsInnerDecoratedDefinition(TSNode defNode)
    {
        var type = NodeType(defNode);
        if (type is not ("function_definition" or "class_definition"))
        {
            return false;
        }

        var parent = NativeMethods.ts_node_parent(defNode);
        return !parent.IsNull && NodeType(parent) == "decorated_definition";
    }

    private static bool IsDirectClassMember(TSNode defNode)
    {
        var parent = NativeMethods.ts_node_parent(defNode);
        if (parent.IsNull || NodeType(parent) != "block")
        {
            return false;
        }

        var grand = NativeMethods.ts_node_parent(parent);
        return !grand.IsNull && NodeType(grand) == "class_definition";
    }

    private static bool IsNonReferenceIdentifier(TSNode node)
    {
        var parent = NativeMethods.ts_node_parent(node);
        if (parent.IsNull)
        {
            return false;
        }

        var parentType = NodeType(parent);
        switch (parentType)
        {
            case "parameters":
            case "lambda_parameters":
            case "list_splat_pattern":
            case "dictionary_splat_pattern":
                return true;
            case "typed_parameter":
            case "default_parameter":
            case "typed_default_parameter":
            case "keyword_argument":
                var first = NativeMethods.ts_node_child(parent, 0);
                return first.Id == node.Id;
            // Binding positions. The name being declared is not a use of it.
            case "array_pattern":
            case "rest_pattern":
            case "namespace_import":
            case "import_clause":
            case "import_require_clause":
            case "namespace_export":
            case "pair_pattern":
            case "import_alias":
                return true;
        }

        if (FieldIs(parent, AliasField, node) && parentType is "import_specifier" or "export_specifier")
        {
            return true;
        }

        if (FieldIs(parent, ParameterField, node) && parentType is "arrow_function" or "catch_clause")
        {
            return true;
        }

        if (FieldIs(parent, LeftField, node) && parentType == "assignment_pattern")
        {
            return true;
        }

        if (FieldIs(parent, PatternField, node) && parentType is "required_parameter" or "optional_parameter")
        {
            return true;
        }

        if (FieldIs(parent, NameField, node) && DeclarationNameParents.Contains(parentType))
        {
            return true;
        }

        return false;
    }

    private static bool FieldIs(TSNode parent, byte[] field, TSNode node)
    {
        var child = NativeMethods.ts_node_child_by_field_name(parent, field, (uint)field.Length);
        return !child.IsNull && child.Id == node.Id;
    }

    private static TSNode? FindChild(TSNode node, params string[] types)
    {
        var count = NativeMethods.ts_node_child_count(node);
        for (uint i = 0; i < count; i++)
        {
            var child = NativeMethods.ts_node_child(node, i);
            if (types.Contains(NodeType(child)))
            {
                return child;
            }
        }

        return null;
    }

    private static string NodeType(TSNode node)
    {
        var ptr = NativeMethods.ts_node_type(node);
        return Marshal.PtrToStringUTF8(ptr) ?? "";
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
                line.EndsWith("*/", StringComparison.Ordinal) ||
                line.StartsWith("# ", StringComparison.Ordinal) ||
                line.StartsWith("#\t", StringComparison.Ordinal))
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
        s = s.TrimStart('/', '*', '#').TrimEnd('*', '/').Trim();

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
