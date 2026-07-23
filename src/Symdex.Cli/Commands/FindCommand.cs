namespace Symdex.Cli.Commands;

internal static class FindCommand
{
    public static int Run(string repo, bool json, int limit, string query, string? kind, string? solution)
    {
        var repoRoot = Path.GetFullPath(repo);
        using var db = IndexerFactory.EnsureFreshAndOpen(repoRoot, solution);

        var symbols = db.Find(query, kind, OutputWriter.HardCapLimit);

        OutputWriter.WriteRecords(
            symbols,
            limit,
            json,
            s => $"{s.Kind} {s.FqName ?? s.Name} {s.Signature} {s.FilePath}:{s.StartLine}-{s.EndLine}",
            s => new { s.Kind, FqName = s.FqName ?? s.Name, s.Signature, s.FilePath, s.StartLine, s.EndLine });

        return 0;
    }
}
