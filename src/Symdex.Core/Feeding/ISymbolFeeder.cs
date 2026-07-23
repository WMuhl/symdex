namespace Symdex.Core.Feeding;

/// <summary>
/// Produces symbols and references for a set of changed files. A feeder may need to load
/// broader context (e.g. a whole compilation) to resolve cross-file references correctly,
/// but it only reports rows for the files listed in <see cref="FeedRequest.ChangedFiles"/>.
/// </summary>
public interface ISymbolFeeder
{
    string Name { get; }

    bool CanHandle(string language);

    FeederResult Feed(FeedRequest request);
}

public sealed record FeedRequest(string RepoRoot, IReadOnlyList<ScannedFile> ChangedFiles, string? SolutionPath = null);

/// <summary>
/// Result of a feed attempt. When <see cref="Success"/> is false the caller should fall back
/// to another feeder (if one is registered) for the same files; <see cref="Warning"/> is a
/// single human-readable line to print to stderr in that case.
/// </summary>
public sealed record FeederResult(
    bool Success,
    string? Warning,
    IReadOnlyDictionary<string, FileFeedResult> ResultsByFile);

public sealed record FileFeedResult(
    IReadOnlyList<SymbolRow> Symbols,
    IReadOnlyList<RefRow> Refs);
