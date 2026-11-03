using Symdex.Cli.Commands;
using Symdex.Core;

namespace Symdex.Cli.Mcp;

internal sealed record OutlineSymbol(string Kind, string Name, string? Signature, int StartLine, int EndLine);

internal sealed record FindSymbol(
    string Kind,
    string FqName,
    string? Signature,
    string FilePath,
    int StartLine,
    int EndLine);

internal sealed record RefSite(string Kind, string FilePath, int Line);

internal sealed class OutlineToolResult
{
    public required IReadOnlyList<OutlineSymbol> Results { get; init; }
    public required bool Truncated { get; init; }
    public required int More { get; init; }
    public string? Message { get; init; }
}

internal sealed class FindToolResult
{
    public required IReadOnlyList<FindSymbol> Results { get; init; }
    public required bool Truncated { get; init; }
    public required int More { get; init; }
}

internal sealed class RefToolResult
{
    public required IReadOnlyList<RefSite> Results { get; init; }
    public required bool Truncated { get; init; }
    public required int More { get; init; }
    public string? Symbol { get; init; }
    public bool? Ambiguous { get; init; }
    public string? Query { get; init; }
    public IReadOnlyList<string>? Candidates { get; init; }
    public string? Message { get; init; }
}

internal readonly record struct Page<T>(IReadOnlyList<T> Results, bool Truncated, int More);

internal static class IndexQueries
{
    private static readonly string[] CallerKinds = [RefKinds.Call, RefKinds.Reference];

    public static OutlineToolResult Outline(IndexDatabase db, string repoRoot, string path, int? limit)
    {
        var relative = PathUtil.ToRepoRelative(repoRoot, path);
        var page = PageOf(db.Outline(relative), limit);
        var results = page.Results
            .Select(s => new OutlineSymbol(s.Kind, s.Name, s.Signature, s.StartLine, s.EndLine))
            .ToList();
        string? message = results.Count == 0
            ? $"No symbols for '{relative}'. Pass a repo-relative path (forward slashes) or an absolute path."
            : null;
        return new OutlineToolResult
        {
            Results = results,
            Truncated = page.Truncated,
            More = page.More,
            Message = message,
        };
    }

    public static FindToolResult Find(IndexDatabase db, string query, string? kind, int? limit)
    {
        var page = PageOf(db.Find(query, NormalizeKind(kind), OutputWriter.HardCapLimit), limit);
        var results = page.Results
            .Select(s => new FindSymbol(s.Kind, s.FqName ?? s.Name, s.Signature, s.FilePath, s.StartLine, s.EndLine))
            .ToList();
        return new FindToolResult
        {
            Results = results,
            Truncated = page.Truncated,
            More = page.More,
        };
    }

    public static RefToolResult Refs(IndexDatabase db, string symbol, int? limit) =>
        Sites(db, symbol, limit, kindFilter: null);

    public static RefToolResult Callers(IndexDatabase db, string symbol, int? limit) =>
        Sites(db, symbol, limit, CallerKinds);

    private static RefToolResult Sites(IndexDatabase db, string symbol, int? limit, IReadOnlyCollection<string>? kindFilter)
    {
        var resolution = SymbolResolution.Resolve(db, symbol);
        switch (resolution.Outcome)
        {
            case ResolveOutcome.NotFound:
                return new RefToolResult
                {
                    Results = [],
                    Truncated = false,
                    More = 0,
                    Message = $"No symbol matched '{symbol}'. Try symdex find {SymbolNames.FindHintSegment(symbol)}.",
                };
            case ResolveOutcome.Ambiguous:
                return new RefToolResult
                {
                    Results = [],
                    Truncated = false,
                    More = 0,
                    Ambiguous = true,
                    Query = symbol,
                    Candidates = resolution.Candidates,
                    Message = $"'{symbol}' matches more than one symbol. Pass a fully-qualified name.",
                };
        }

        var resolved = resolution.Resolved!;
        var page = PageOf(db.RefsFor(resolved, kindFilter, OutputWriter.HardCapLimit), limit);
        var results = page.Results.Select(r => new RefSite(r.Kind, r.FilePath, r.Line)).ToList();
        return new RefToolResult
        {
            Results = results,
            Truncated = page.Truncated,
            More = page.More,
            Symbol = resolved,
        };
    }

    private static Page<T> PageOf<T>(IReadOnlyList<T> records, int? limit)
    {
        var effective = OutputWriter.EffectiveLimit(limit ?? OutputWriter.DefaultLimit);
        var truncated = records.Count > effective;
        var more = truncated ? records.Count - effective : 0;
        return new Page<T>(records.Take(effective).ToList(), truncated, more);
    }

    private static string? NormalizeKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
}
