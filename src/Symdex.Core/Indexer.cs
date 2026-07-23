using Symdex.Core.Feeding;

namespace Symdex.Core;

public sealed record IndexRunResult(int ReindexedFileCount, IReadOnlyList<string> Warnings);

/// <summary>
/// Ties freshness checking to feeders: hashes candidate files, diffs against the database,
/// and re-runs only the feeders needed to cover added/changed/deleted files.
/// </summary>
public sealed class Indexer(IReadOnlyList<ISymbolFeeder> feeders)
{
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

        foreach (var path in deletedPaths)
        {
            db.DeleteFile(path);
            reindexedCount++;
        }

        var changedByLanguage = changed.GroupBy(f => f.Lang);
        foreach (var group in changedByLanguage)
        {
            var language = group.Key;
            var filesForLanguage = group.ToList();
            var resultsByFile = FeedWithFallback(repoRoot, language, filesForLanguage, solutionPath, warnings);

            foreach (var file in filesForLanguage)
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

                reindexedCount++;
            }
        }

        return new IndexRunResult(reindexedCount, warnings);
    }

    private IReadOnlyDictionary<string, FileFeedResult> FeedWithFallback(
        string repoRoot, string language, IReadOnlyList<ScannedFile> files, string? solutionPath, List<string> warnings)
    {
        var candidates = feeders.Where(f => f.CanHandle(language)).ToList();
        var request = new FeedRequest(repoRoot, files, solutionPath);

        foreach (var feeder in candidates)
        {
            var result = feeder.Feed(request);
            if (result.Success)
            {
                return result.ResultsByFile;
            }

            if (result.Warning is not null)
            {
                warnings.Add(result.Warning);
            }
        }

        return new Dictionary<string, FileFeedResult>();
    }
}
