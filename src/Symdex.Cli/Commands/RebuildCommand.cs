using Symdex.Core;

namespace Symdex.Cli.Commands;

internal static class RebuildCommand
{
    public static int Run(string repo, bool json)
    {
        var repoRoot = Path.GetFullPath(repo);
        using var db = IndexDatabase.OpenOrCreate(repoRoot);
        db.Rebuild();

        var result = IndexerFactory.Create().EnsureFresh(repoRoot, db);

        foreach (var warning in result.Warnings)
        {
            Console.Error.WriteLine(warning);
        }

        OutputWriter.WriteLine(
            $"rebuilt index: {result.ReindexedFileCount} files indexed",
            json,
            new { reindexed = result.ReindexedFileCount });

        return 0;
    }
}
