using Symdex.Core;
using Symdex.Core.Feeding;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Cli;

/// <summary>
/// Wires up the feeders every verb uses to keep the index fresh before answering a query.
/// C# is fed by Roslyn first, falling back to tree-sitter if the workspace can't load;
/// TypeScript/JavaScript are tree-sitter only.
/// </summary>
internal static class IndexerFactory
{
    public static Indexer Create() => new([new RoslynFeeder(), new TreeSitterFeeder()]);

    public static IndexDatabase EnsureFreshAndOpen(string repoRoot, string? solutionPath)
    {
        var db = IndexDatabase.OpenOrCreate(repoRoot);
        RunAndReport(repoRoot, db, solutionPath);
        return db;
    }

    public static void RunAndReport(string repoRoot, IndexDatabase db, string? solutionPath)
    {
        var result = Create().EnsureFresh(repoRoot, db, solutionPath);

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
