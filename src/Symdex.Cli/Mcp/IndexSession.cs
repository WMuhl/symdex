using Symdex.Core;

namespace Symdex.Cli.Mcp;

/// <summary>
/// One database and indexer for the life of the MCP process. Tool calls share it and
/// take the gate so a reindex never overlaps another feed or query.
/// </summary>
internal sealed class IndexSession : IDisposable
{
    private readonly Indexer _indexer;
    private readonly IDisposable _lifetime;
    private readonly IndexDatabase _database;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public IndexSession(string repoRoot, string? solutionPath)
    {
        RepoRoot = Path.GetFullPath(repoRoot);
        SolutionPath = solutionPath;
        (_indexer, _lifetime) = IndexerFactory.CreatePersistent();
        _database = IndexDatabase.OpenOrCreate(RepoRoot);
    }

    public string RepoRoot { get; }

    public string? SolutionPath { get; }

    public async Task<T> QueryAsync<T>(Func<IndexDatabase, T> query, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var freshness = _indexer.EnsureFresh(RepoRoot, _database, SolutionPath);
            IndexerFactory.WriteFreshness(freshness);
            return query(_database);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _database.Dispose();
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
