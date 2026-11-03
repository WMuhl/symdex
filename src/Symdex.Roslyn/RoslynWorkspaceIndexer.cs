using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Symdex.Core;
using Symdex.Core.Feeding;

namespace Symdex.Roslyn;

internal readonly record struct OpenResult(
    MSBuildWorkspace? Workspace,
    Solution? Solution,
    FeederResult? Failure,
    string? Note);

internal static class RoslynWorkspaceIndexer
{
    private static readonly string[] SkippedDirNames =
    [
        "node_modules", "bin", "obj", "dist", ".index", ".git", "out", "build", ".vs", ".vscode",
        "__pycache__", ".venv", "venv", ".tox", ".mypy_cache", "site-packages",
    ];

    public static FeederResult Feed(FeedRequest request)
    {
        var opened = TryOpen(request);
        if (opened.Failure is not null)
        {
            return opened.Failure;
        }

        using (opened.Workspace)
        {
            return Index(opened.Solution!, request, opened.Note);
        }
    }

    internal static OpenResult TryOpen(FeedRequest request)
    {
        var solutionPath = SolutionResolver.Resolve(request.RepoRoot, request.SolutionPath, out var resolveWarning);
        if (solutionPath is null)
        {
            return new OpenResult(null, null, Fail(resolveWarning ?? "symdex: could not resolve a .sln/.csproj."), null);
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
            solution = IsSolutionFile(solutionPath)
                ? workspace.OpenSolutionAsync(solutionPath).GetAwaiter().GetResult()
                : workspace.OpenProjectAsync(solutionPath).GetAwaiter().GetResult().Solution;
        }
        catch (Exception ex)
        {
            workspace.Dispose();
            return new OpenResult(null, null, Fail($"symdex: failed to open '{Path.GetFileName(solutionPath)}' ({ex.Message})."), null);
        }

        if (failures.Count > 0)
        {
            workspace.Dispose();
            return new OpenResult(null, null, Fail($"symdex: workspace load reported {failures.Count} failure(s) (e.g. \"{failures[0]}\")."), null);
        }

        return new OpenResult(workspace, solution, null, resolveWarning);
    }

    internal static FeederResult Index(Solution solution, FeedRequest request, string? note = null)
    {
        var projects = SelectProjects(solution);

        foreach (var project in projects)
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

        var documentsByPath = projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null)
            .GroupBy(d => NormalizePath(d.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var changedByPath = request.ChangedFiles.ToDictionary(
            f => NormalizePath(f.AbsolutePath), f => f.RepoRelativePath, StringComparer.OrdinalIgnoreCase);
        var resultsByFile = new Dictionary<string, FileFeedResult>(StringComparer.Ordinal);
        var symbolKeysChanged = request.ExternalRefsStale;

        foreach (var (normalizedPath, relativePath) in changedByPath)
        {
            var previous = PreviousKeys(request, relativePath);
            if (!documentsByPath.TryGetValue(normalizedPath, out var document))
            {
                resultsByFile[relativePath] = new FileFeedResult([], []);
                if (previous.Count > 0)
                {
                    symbolKeysChanged = true;
                }

                continue;
            }

            var (symbols, refs) = Extract(document);
            resultsByFile[relativePath] = new FileFeedResult(symbols, refs);
            if (!SameIdentities(symbols, previous))
            {
                symbolKeysChanged = true;
            }
        }

        if (symbolKeysChanged)
        {
            foreach (var (normalizedPath, document) in documentsByPath)
            {
            var absolutePath = document.FilePath;
            if (changedByPath.ContainsKey(normalizedPath) || absolutePath is null)
            {
                continue;
            }

            var relativePath = RepoRelative(request.RepoRoot, absolutePath);
                if (relativePath is null || IsInSkippedDir(relativePath))
                {
                    continue;
                }

                var (_, refs) = Extract(document);
                resultsByFile[relativePath] = new FileFeedResult([], refs);
            }
        }

        return new FeederResult(true, note, resultsByFile);
    }

    internal static FeederResult Fail(string warning) =>
        new(false, warning, new Dictionary<string, FileFeedResult>());

    internal static bool IsSolutionFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Project> SelectProjects(Solution solution)
    {
        var groups = new Dictionary<string, List<Project>>(StringComparer.OrdinalIgnoreCase);
        var withoutPath = new List<Project>();
        foreach (var project in solution.Projects)
        {
            if (string.IsNullOrEmpty(project.FilePath))
            {
                withoutPath.Add(project);
                continue;
            }

            var key = NormalizePath(project.FilePath);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(project);
        }

        var selected = new List<Project>(groups.Count + withoutPath.Count);
        foreach (var list in groups.Values)
        {
            selected.Add(list
                .Select((project, index) => (project, Rank: FrameworkRank(project), index))
                .OrderByDescending(item => item.Rank)
                .ThenBy(item => item.index)
                .First()
                .project);
        }

        selected.AddRange(withoutPath);
        return selected;
    }

    private static int FrameworkRank(Project project)
    {
        if (project.ParseOptions is not Microsoft.CodeAnalysis.CSharp.CSharpParseOptions options)
        {
            return 0;
        }

        var best = 0;
        foreach (var name in options.PreprocessorSymbolNames)
        {
            if (name.EndsWith("_OR_GREATER", StringComparison.Ordinal))
            {
                continue;
            }

            best = Math.Max(best, ScoreFrameworkSymbol(name));
        }

        return best;
    }

    private static int ScoreFrameworkSymbol(string name)
    {
        if (TryVersionPair(name, "NETCOREAPP", out var major, out var minor))
        {
            return 2_000_000 + major * 100 + minor;
        }

        if (TryVersionPair(name, "NETSTANDARD", out major, out minor))
        {
            return 1_000_000 + major * 100 + minor;
        }

        if (TryVersionPair(name, "NET", out major, out minor))
        {
            return 3_000_000 + major * 100 + minor;
        }

        if (name.StartsWith("NET", StringComparison.Ordinal) && int.TryParse(name[3..], out var framework))
        {
            return 100_000 + framework;
        }

        return 0;
    }

    private static bool TryVersionPair(string name, string prefix, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = name[prefix.Length..].Split('_');
        return parts.Length == 2
            && int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor);
    }

    private static (List<SymbolRow> Symbols, List<RefRow> Refs) Extract(Document document)
    {
        var tree = document.GetSyntaxTreeAsync().GetAwaiter().GetResult();
        var model = document.GetSemanticModelAsync().GetAwaiter().GetResult();
        if (tree is null || model is null)
        {
            return ([], []);
        }

        var root = tree.GetRoot();
        var symbols = new List<SymbolRow>();
        var refs = new List<RefRow>();
        var seen = new HashSet<(string FqName, int Line, string Kind)>();

        foreach (var node in root.DescendantNodes())
        {
            var symbol = DeclaredSymbolFor(node, model);
            if (symbol is null)
            {
                continue;
            }

            symbols.Add(ToSymbolRow(symbol, node));
        }

        foreach (var node in root.DescendantNodes(descendIntoTrivia: true))
        {
            var info = SymbolInfoFor(node, model);
            if (info.Symbol is null)
            {
                continue;
            }

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            AddRef(info.Symbol, line, kind: null);
        }

        foreach (var node in root.DescendantNodes())
        {
            var symbol = DeclaredSymbolFor(node, model);
            if (symbol is null)
            {
                continue;
            }

            AddImplementations(symbol, tree, AddRef);
        }

        return (symbols, refs);

        void AddRef(ISymbol? symbol, int line, string? kind)
        {
            var canonical = Canonical(symbol);
            if (canonical is null)
            {
                return;
            }

            var fqName = SymbolFormatting.FqName(canonical);
            if (fqName.Length == 0)
            {
                return;
            }

            var refKind = kind ?? (canonical is IMethodSymbol ? RefKinds.Call : RefKinds.Reference);
            if (seen.Add((fqName, line, refKind)))
            {
                refs.Add(new RefRow(fqName, line, refKind));
            }
        }
    }

    private static SymbolInfo SymbolInfoFor(SyntaxNode node, SemanticModel model) => node switch
    {
        IdentifierNameSyntax or GenericNameSyntax or MemberBindingExpressionSyntax
            or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
            or ElementAccessExpressionSyntax or AliasQualifiedNameSyntax
            => model.GetSymbolInfo((ExpressionSyntax)node),
        ConstructorInitializerSyntax initializer => model.GetSymbolInfo(initializer),
        AttributeSyntax attribute => model.GetSymbolInfo(attribute),
        CrefSyntax cref => model.GetSymbolInfo(cref),
        _ => default,
    };

    private static void AddImplementations(ISymbol declared, SyntaxTree tree, Action<ISymbol?, int, string?> add)
    {
        var line = LineInTree(declared, tree);
        if (line is null)
        {
            return;
        }

        if (declared is INamedTypeSymbol type)
        {
            foreach (var iface in type.AllInterfaces)
            {
                add(iface, line.Value, RefKinds.Implementation);
            }
        }

        if (declared is not IMethodSymbol method || method.ContainingType is null)
        {
            return;
        }

        foreach (var explicitImpl in method.ExplicitInterfaceImplementations)
        {
            add(explicitImpl, line.Value, RefKinds.Implementation);
        }

        for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
        {
            if (overridden.IsAbstract)
            {
                add(overridden, line.Value, RefKinds.Implementation);
            }
        }

        foreach (var iface in method.ContainingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                var implementation = method.ContainingType.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(implementation?.OriginalDefinition, method.OriginalDefinition))
                {
                    add(member, line.Value, RefKinds.Implementation);
                }
            }
        }
    }

    private static int? LineInTree(ISymbol symbol, SyntaxTree tree)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.SourceTree == tree)
            {
                return location.GetLineSpan().StartLinePosition.Line + 1;
            }
        }

        return null;
    }

    private static ISymbol? Canonical(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
        {
            symbol = reduced;
        }

        symbol = symbol.OriginalDefinition;
        return IsIndexable(symbol) ? symbol : null;
    }

    private static bool IsIndexable(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
        {
            return false;
        }

        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Error or TypeKind.Unknown or TypeKind.Submission }:
                return false;
            case INamedTypeSymbol:
                return HasSourceLocation(symbol);
            case IMethodSymbol method when method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor
                or MethodKind.StaticConstructor or MethodKind.ExplicitInterfaceImplementation:
                return HasSourceLocation(method);
            case IPropertySymbol or IFieldSymbol or IEventSymbol:
                return HasSourceLocation(symbol);
            default:
                return false;
        }
    }

    private static bool HasSourceLocation(ISymbol symbol) =>
        symbol.Locations.Any(static location => location.IsInSource);

    private static HashSet<string> PreviousKeys(FeedRequest request, string relativePath)
    {
        if (request.PreviousSymbolIdentities is not null
            && request.PreviousSymbolIdentities.TryGetValue(relativePath, out var keys))
        {
            return keys;
        }

        return [];
    }

    private static bool SameIdentities(List<SymbolRow> symbols, HashSet<string> previous)
    {
        var keys = new HashSet<string>(symbols.Select(Identity), StringComparer.Ordinal);
        return keys.Count == previous.Count && keys.All(previous.Contains);
    }

    private static string Identity(SymbolRow symbol) =>
        SymbolIdentity.Key(symbol.Kind, symbol.FqName, symbol.Name);

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

    private static string? RepoRelative(string repoRoot, string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        var root = Path.GetFullPath(repoRoot);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return full[prefix.Length..].Replace('\\', '/');
    }

    private static bool IsInSkippedDir(string repoRelativePath)
    {
        var segments = repoRelativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            foreach (var skipped in SkippedDirNames)
            {
                if (string.Equals(segments[i], skipped, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);
}
