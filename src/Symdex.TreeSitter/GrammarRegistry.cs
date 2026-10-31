using System.Reflection;
using Symdex.Core;
using Symdex.TreeSitter.Native;

namespace Symdex.TreeSitter;

internal sealed record Grammar(IntPtr Language, TsQuery Query, IReadOnlyList<string> PatternKinds);

/// <summary>
/// Maps a file extension to the compiled tree-sitter grammar (language + query + per-pattern kind
/// list) used to index it. ".js"/".jsx" reuse the typescript/tsx grammars since no separate
/// tree-sitter-javascript grammar is vendored (see README "Design decisions"). ".py"/".pyi"
/// share the python grammar.
/// </summary>
internal static class GrammarRegistry
{
    // Must stay positionally in sync with queries/csharp.scm.
    private static readonly string[] CSharpPatternKinds =
    [
        SymbolKinds.Class, SymbolKinds.Interface, SymbolKinds.Struct, SymbolKinds.Enum,
        SymbolKinds.Method, SymbolKinds.Method, SymbolKinds.Property, SymbolKinds.Field, "import",
    ];

    // Must stay positionally in sync with queries/typescript.scm.
    private static readonly string[] TypeScriptPatternKinds =
    [
        SymbolKinds.Class, SymbolKinds.Interface, SymbolKinds.Type, SymbolKinds.Function,
        SymbolKinds.Method, SymbolKinds.Field, SymbolKinds.Export, "import", "import",
    ];

    // Must stay positionally in sync with queries/python.scm.
    // function_definition is both functions and methods; TreeSitterFeeder reclassifies a
    // function that sits directly in a class body as a method.
    private static readonly string[] PythonPatternKinds =
    [
        SymbolKinds.Class, SymbolKinds.Class, SymbolKinds.Function, SymbolKinds.Function,
        SymbolKinds.Export, SymbolKinds.Field, "import", "import", "import", "import",
        "call", "call", "reference",
    ];

    private static readonly Lazy<string> CSharpQuerySource = new(() => ReadEmbeddedQuery("csharp.scm"));
    private static readonly Lazy<string> TypeScriptQuerySource = new(() => ReadEmbeddedQuery("typescript.scm"));
    private static readonly Lazy<string> PythonQuerySource = new(() => ReadEmbeddedQuery("python.scm"));

    private static Grammar? _csharp;
    private static Grammar? _typescript;
    private static Grammar? _tsx;
    private static Grammar? _python;

    public static Grammar? ForFile(string repoRelativePath)
    {
        var ext = Path.GetExtension(repoRelativePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => CSharp(),
            ".ts" => TypeScript(),
            ".tsx" or ".jsx" => Tsx(),
            ".js" => TypeScript(),
            ".py" or ".pyi" => Python(),
            _ => null,
        };
    }

    private static Grammar? CSharp()
    {
        if (_csharp is not null)
        {
            return _csharp;
        }

        var language = NativeMethods.tree_sitter_c_sharp();
        if (language == IntPtr.Zero)
        {
            return null;
        }

        var query = TsQuery.Compile(language, CSharpQuerySource.Value);
        return _csharp = new Grammar(language, query, CSharpPatternKinds);
    }

    private static Grammar? TypeScript()
    {
        if (_typescript is not null)
        {
            return _typescript;
        }

        var language = NativeMethods.tree_sitter_typescript();
        if (language == IntPtr.Zero)
        {
            return null;
        }

        var query = TsQuery.Compile(language, TypeScriptQuerySource.Value);
        return _typescript = new Grammar(language, query, TypeScriptPatternKinds);
    }

    private static Grammar? Tsx()
    {
        if (_tsx is not null)
        {
            return _tsx;
        }

        var language = NativeMethods.tree_sitter_tsx();
        if (language == IntPtr.Zero)
        {
            return null;
        }

        // The tsx grammar exposes the same declaration node types as typescript.
        var query = TsQuery.Compile(language, TypeScriptQuerySource.Value);
        return _tsx = new Grammar(language, query, TypeScriptPatternKinds);
    }

    private static Grammar? Python()
    {
        if (_python is not null)
        {
            return _python;
        }

        var language = NativeMethods.tree_sitter_python();
        if (language == IntPtr.Zero)
        {
            return null;
        }

        var query = TsQuery.Compile(language, PythonQuerySource.Value);
        return _python = new Grammar(language, query, PythonPatternKinds);
    }

    private static string ReadEmbeddedQuery(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
