using Symdex.Core.Feeding;

namespace Symdex.Core;

public sealed record IndexRunResult(int ReindexedFileCount, IReadOnlyList<string> Warnings);

/// <summary>
/// Ties freshness checking to feeders: hashes candidate files, diffs against the database,
/// and re-runs only the feeders needed to cover added/changed/deleted files.
/// </summary>
public sealed class Indexer(IReadOnlyList<ISymbolFeeder> feeders)
{
    private static readonly HashSet<string> EmptyIdentities = new(StringComparer.Ordinal);

    private readonly record struct HashedFile(ScannedFile File, string Hash);

    public IndexRunResult EnsureFresh(string repoRoot, IndexDatabase db, string? solutionPath = null)
    {
        var scanned = RepoScanner.Scan(repoRoot);
        var hashed = scanned
            .Select(f => new HashedFile(f, FileHasher.HashFile(f.AbsolutePath)))
            .ToList();
        var scannedPaths = hashed.Select(h => h.File.RepoRelativePath).ToHashSet();

        var existing = db.GetAllFiles();

        var changed = new List<ScannedFile>();
        foreach (var h in hashed)
        {
            if (!existing.TryGetValue(h.File.RepoRelativePath, out var existingRecord) || existingRecord.Hash != h.Hash)
            {
                changed.Add(h.File);
            }
        }

        var deletedPaths = existing.Keys.Where(p => !scannedPaths.Contains(p)).ToList();

        var warnings = new List<string>();
        var reindexedCount = 0;
        var identities = changed.Count > 0 || deletedPaths.Count > 0
            ? db.GetSymbolIdentities()
            : null;

        var externalRefsStale = false;
        foreach (var path in deletedPaths)
        {
            if (identities is not null
                && identities.TryGetValue(path, out var keys)
                && keys.Count > 0
                && existing.TryGetValue(path, out var record)
                && record.Lang == Languages.CSharp)
            {
                externalRefsStale = true;
            }

            db.DeleteFile(path);
            reindexedCount++;
        }

        var deleted = deletedPaths.ToHashSet(StringComparer.Ordinal);
        var live = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, record) in existing)
        {
            if (!deleted.Contains(path))
            {
                live[path] = record;
            }
        }

        var fedLanguages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in changed.GroupBy(f => f.Lang))
        {
            fedLanguages.Add(group.Key);
            reindexedCount += FeedLanguage(
                repoRoot,
                db,
                solutionPath,
                warnings,
                hashed,
                identities,
                live,
                group.Key,
                group.ToList(),
                externalRefsStale && group.Key == Languages.CSharp);
        }

        if (externalRefsStale && !fedLanguages.Contains(Languages.CSharp))
        {
            FeedLanguage(
                repoRoot,
                db,
                solutionPath,
                warnings,
                hashed,
                identities,
                live,
                Languages.CSharp,
                [],
                externalRefsStale: true);
        }

        return new IndexRunResult(reindexedCount, warnings);
    }

    private int FeedLanguage(
        string repoRoot,
        IndexDatabase db,
        string? solutionPath,
        List<string> warnings,
        List<HashedFile> hashed,
        Dictionary<string, HashSet<string>>? identities,
        Dictionary<string, FileRecord> live,
        string language,
        List<ScannedFile> files,
        bool externalRefsStale)
    {
        var previous = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            previous[file.RepoRelativePath] = identities is not null && identities.TryGetValue(file.RepoRelativePath, out var keys)
                ? keys
                : EmptyIdentities;
        }

        var resultsByFile = FeedWithFallback(
            repoRoot, language, files, solutionPath, warnings, previous, externalRefsStale);

        var changedSet = new HashSet<string>(files.Select(f => f.RepoRelativePath), StringComparer.Ordinal);
        var count = 0;
        foreach (var file in files)
        {
            var hash = hashed.First(h => h.File.RepoRelativePath == file.RepoRelativePath).Hash;
            var fileId = db.UpsertFile(file.RepoRelativePath, hash, language, DateTime.UtcNow.ToString("O"));

            if (resultsByFile.TryGetValue(file.RepoRelativePath, out var feedResult))
            {
                db.ReplaceSymbolsAndRefs(fileId, feedResult.Symbols, feedResult.Refs);
            }
            else
            {
                db.ReplaceSymbolsAndRefs(fileId, [], []);
            }

            count++;
        }

        foreach (var (path, feedResult) in resultsByFile)
        {
            if (changedSet.Contains(path) || !live.TryGetValue(path, out var record))
            {
                continue;
            }

            db.ReplaceRefs(record.Id, feedResult.Refs);
        }

        return count;
    }

    private IReadOnlyDictionary<string, FileFeedResult> FeedWithFallback(
        string repoRoot,
        string language,
        IReadOnlyList<ScannedFile> files,
        string? solutionPath,
        List<string> warnings,
        IReadOnlyDictionary<string, HashSet<string>> previousSymbolIdentities,
        bool externalRefsStale)
    {
        var candidates = feeders.Where(f => f.CanHandle(language)).ToList();
        var request = new FeedRequest(repoRoot, files, solutionPath, previousSymbolIdentities, externalRefsStale);

        foreach (var feeder in candidates)
        {
            var result = feeder.Feed(request);
            if (result.Warning is not null)
            {
                warnings.Add(result.Warning);
            }

            if (result.Success)
            {
                return result.ResultsByFile;
            }
        }

        return new Dictionary<string, FileFeedResult>();
    }
}
