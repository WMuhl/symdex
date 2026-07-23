namespace Symdex.Cli.Commands;

internal static class OutlineCommand
{
    public static int Run(string repo, bool json, int limit, string file)
    {
        var repoRoot = Path.GetFullPath(repo);
        using var db = IndexerFactory.EnsureFreshAndOpen(repoRoot);

        var relativePath = PathUtil.ToRepoRelative(repoRoot, file);
        var symbols = db.Outline(relativePath);

        OutputWriter.WriteRecords(
            symbols,
            limit,
            json,
            s => $"{s.Kind} {s.Name} {s.Signature} :{s.StartLine}-{s.EndLine}",
            s => new { s.Kind, s.Name, s.Signature, s.StartLine, s.EndLine });

        return 0;
    }
}
