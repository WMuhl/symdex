namespace Symdex.Cli.Commands;

internal static class RefsCommand
{
    public static int Run(string repo, bool json, int limit, string symbol, string? solution)
    {
        var repoRoot = Path.GetFullPath(repo);
        using var db = IndexerFactory.EnsureFreshAndOpen(repoRoot, solution);

        var resolution = SymbolResolution.Resolve(db, symbol);
        switch (resolution.Outcome)
        {
            case ResolveOutcome.NotFound:
                return SymbolResolution.ReportNotFound(symbol);
            case ResolveOutcome.Ambiguous:
                return SymbolResolution.ReportAmbiguous(symbol, resolution.Candidates, json);
        }

        var resolved = resolution.Resolved!;
        var refs = db.RefsFor(resolved, kindFilter: null, OutputWriter.HardCapLimit);

        OutputWriter.WriteLine(resolved, json, new { symbol = resolved });
        OutputWriter.WriteRecords(
            refs,
            limit,
            json,
            r => $"{r.Kind} {r.FilePath}:{r.Line}",
            r => new { r.Kind, r.FilePath, r.Line });

        return 0;
    }
}
