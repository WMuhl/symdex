using Symdex.Core;

namespace Symdex.Cli.Commands;

internal enum ResolveOutcome
{
    NotFound,
    Resolved,
    Ambiguous,
}

internal readonly record struct ResolveResult(ResolveOutcome Outcome, string? Resolved, IReadOnlyList<string> Candidates);

internal static class SymbolResolution
{
    public static ResolveResult Resolve(IndexDatabase db, string nameOrFqName)
    {
        var candidates = db.ResolveSymbolCandidates(nameOrFqName);
        return candidates.Count switch
        {
            0 => new ResolveResult(ResolveOutcome.NotFound, null, candidates),
            1 => new ResolveResult(ResolveOutcome.Resolved, candidates[0], candidates),
            _ => new ResolveResult(ResolveOutcome.Ambiguous, null, candidates),
        };
    }

    public static int ReportAmbiguous(string query, IReadOnlyList<string> candidates, bool json)
    {
        if (json)
        {
            OutputWriter.WriteLine(string.Empty, json: true, new { ambiguous = true, query, candidates });
        }
        else
        {
            Console.Error.WriteLine($"ambiguous symbol '{query}', candidates:");
            foreach (var candidate in candidates)
            {
                Console.Error.WriteLine($"  {candidate}");
            }
        }

        return 2;
    }

    public static int ReportNotFound(string query)
    {
        Console.Error.WriteLine(
            $"symdex: no symbol matched '{query}'. Try `symdex find {SymbolNames.FindHintSegment(query)}`.");
        return 0;
    }
}
