using Symdex.Core;
using Symdex.Core.Feeding;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Cli;

/// <summary>
/// Wires up the feeders every verb uses to keep the index fresh before answering a query.
/// C# is fed by Roslyn first, falling back to tree-sitter if the workspace can't load;
/// TypeScript, JavaScript, and Python are tree-sitter only.
/// </summary>
internal static class IndexerFactory
{
    public static Indexer Create() => new([new RoslynFeeder(), new TreeSitterFeeder()]);

    /// <summary>
    /// An indexer for a long-lived process. The Roslyn feeder keeps its MSBuild workspace
    /// open and reuses it when a later feed touches the same solution. Dispose the returned
    /// lifetime to close that workspace.
    /// </summary>
    public static (Indexer Indexer, IDisposable Lifetime) CreatePersistent()
    {
        var roslyn = new RoslynFeeder { KeepWorkspaceWarm = true };
        return (new Indexer([roslyn, new TreeSitterFeeder()]), roslyn);
    }

    public static IndexDatabase EnsureFreshAndOpen(string repoRoot, string? solutionPath)
    {
        var db = IndexDatabase.OpenOrCreate(repoRoot);
        RunAndReport(repoRoot, db, solutionPath);
        return db;
    }

    public static void RunAndReport(string repoRoot, IndexDatabase db, string? solutionPath)
    {
        var result = Create().EnsureFresh(repoRoot, db, solutionPath);
        WriteFreshness(result);
    }

    public static void WriteFreshness(IndexRunResult result)
    {
        foreach (var warning in result.Warnings)
        {
            Console.Error.WriteLine(warning);
        }

        if (result.ReindexedFileCount > 0)
        {
            Console.Error.WriteLine($"reindexed {result.ReindexedFileCount} files");
        }
    }
}
