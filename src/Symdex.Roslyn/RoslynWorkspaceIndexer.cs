using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Symdex.Core;
using Symdex.Core.Feeding;

namespace Symdex.Roslyn;

internal readonly record struct OpenResult(MSBuildWorkspace? Workspace, Solution? Solution, FeederResult? Failure);

internal static class RoslynWorkspaceIndexer
{
    public static FeederResult Feed(FeedRequest request)
    {
        var opened = TryOpen(request);
        if (opened.Failure is not null)
        {
            return opened.Failure;
        }

        using (opened.Workspace)
        {
            return Index(opened.Solution!, request);
        }
    }

    internal static OpenResult TryOpen(FeedRequest request)
    {
        var solutionPath = SolutionResolver.Resolve(request.RepoRoot, request.SolutionPath, out var resolveWarning);
        if (solutionPath is null)
        {
            return new OpenResult(null, null, Fail(resolveWarning ?? "symdex: could not resolve a .sln/.csproj."));
        }

        var workspace = MSBuildWorkspace.Create();
        var failures = new List<string>();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                failures.Add(e.Diagnostic.Message);
            }
        });

        Solution solution;
        try
        {
            solution = solutionPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                ? workspace.OpenSolutionAsync(solutionPath).GetAwaiter().GetResult()
                : workspace.OpenProjectAsync(solutionPath).GetAwaiter().GetResult().Solution;
        }
        catch (Exception ex)
        {
            workspace.Dispose();
            return new OpenResult(null, null, Fail($"symdex: failed to open '{Path.GetFileName(solutionPath)}' ({ex.Message})."));
        }

        if (failures.Count > 0)
        {
            workspace.Dispose();
            return new OpenResult(null, null, Fail($"symdex: workspace load reported {failures.Count} failure(s) (e.g. \"{failures[0]}\")."));
        }

        return new OpenResult(workspace, solution, null);
    }

    internal static FeederResult Index(Solution solution, FeedRequest request)
    {
        var documentsByPath = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null)
            .GroupBy(d => NormalizePath(d.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Compilation errors anywhere abort the whole run (graceful degradation is mandatory -
        // the caller falls back to tree-sitter-only indexing for these files).
        foreach (var project in solution.Projects)
        {
            var compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
            if (compilation is null)
            {
                continue;
            }

            var errorCount = compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
            if (errorCount > 0)
            {
                return Fail($"symdex: project '{project.Name}' has {errorCount} compile error(s).");
            }
        }

        var changedByPath = request.ChangedFiles.ToDictionary(
            f => NormalizePath(f.AbsolutePath), f => f.RepoRelativePath, StringComparer.OrdinalIgnoreCase);
        var resultsByFile = new Dictionary<string, (List<SymbolRow> Symbols, List<RefRow> Refs)>();
        foreach (var relativePath in changedByPath.Values)
        {
            resultsByFile[relativePath] = ([], []);
        }

        // Symbols: only for the changed documents.
        var referenceableSymbols = new List<ISymbol>();
        var implementableSymbols = new List<ISymbol>();

        foreach (var (normalizedPath, relativePath) in changedByPath)
        {
            if (!documentsByPath.TryGetValue(normalizedPath, out var document))
            {
                continue;
            }

            var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
            var model = document.GetSemanticModelAsync().GetAwaiter().GetResult();
            if (tree is null || model is null)
            {
                continue;
            }

            var root = tree.GetRoot();
            var symbolRows = resultsByFile[relativePath].Symbols;

            foreach (var node in root.DescendantNodes())
            {
                var symbol = DeclaredSymbolFor(node, model);
                if (symbol is null)
                {
                    continue;
                }

                symbolRows.Add(ToSymbolRow(symbol, node));

                if (symbol is INamedTypeSymbol or IMethodSymbol)
                {
                    referenceableSymbols.Add(symbol);
                }

                if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } ||
                    (symbol is IMethodSymbol { IsAbstract: true }))
                {
                    implementableSymbols.Add(symbol);
                }
            }
        }

        // Refs: search the whole solution (needed for correct cross-file results), but only
        // keep hits that land in one of the changed files.
        foreach (var symbol in referenceableSymbols.Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>())
        {
            var fqName = SymbolFormatting.FqName(symbol);
            var kind = symbol is IMethodSymbol ? RefKinds.Call : RefKinds.Reference;

            var found = SymbolFinder.FindReferencesAsync(symbol, solution).GetAwaiter().GetResult();
            foreach (var referenced in found)
            {
                foreach (var location in referenced.Locations)
                {
                    AddRefIfChanged(location.Location, fqName, kind);
                }
            }
        }

        foreach (var symbol in implementableSymbols.Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>())
        {
            var fqName = SymbolFormatting.FqName(symbol);
            var implementations = SymbolFinder.FindImplementationsAsync(symbol, solution).GetAwaiter().GetResult();
            foreach (var impl in implementations)
            {
                foreach (var location in impl.Locations)
                {
                    AddRefIfChanged(location, fqName, RefKinds.Implementation);
                }
            }
        }

        var finalResults = resultsByFile.ToDictionary(
            kv => kv.Key,
            kv => new FileFeedResult(kv.Value.Symbols, kv.Value.Refs));
        return new FeederResult(true, null, finalResults);

        void AddRefIfChanged(Location location, string fqName, string kind)
        {
            var filePath = location.SourceTree?.FilePath;
            if (filePath is null)
            {
                return;
            }

            if (!changedByPath.TryGetValue(NormalizePath(filePath), out var relativePath))
            {
                return;
            }

            var line = location.GetLineSpan().StartLinePosition.Line + 1;
            resultsByFile[relativePath].Refs.Add(new RefRow(fqName, line, kind));
        }
    }

    internal static FeederResult Fail(string warning) =>
        new(false, warning, new Dictionary<string, FileFeedResult>());

    private static ISymbol? DeclaredSymbolFor(SyntaxNode node, SemanticModel model) => node switch
    {
        BaseTypeDeclarationSyntax t => model.GetDeclaredSymbol(t),
        MethodDeclarationSyntax m => model.GetDeclaredSymbol(m),
        ConstructorDeclarationSyntax c => model.GetDeclaredSymbol(c),
        PropertyDeclarationSyntax p => model.GetDeclaredSymbol(p),
        EventDeclarationSyntax e => model.GetDeclaredSymbol(e),
        VariableDeclaratorSyntax v when v.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax
            => model.GetDeclaredSymbol(v),
        _ => null,
    };

    private static SymbolRow ToSymbolRow(ISymbol symbol, SyntaxNode declaringNode)
    {
        var span = declaringNode.GetLocation().GetLineSpan();
        return new SymbolRow(
            symbol.Name,
            SymbolFormatting.FqName(symbol),
            SymbolFormatting.Kind(symbol),
            SymbolFormatting.Signature(symbol),
            SymbolFormatting.Summary(symbol),
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            symbol.ContainingType?.Name);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);
}
