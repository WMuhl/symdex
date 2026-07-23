namespace Symdex.Roslyn;

internal static class SolutionResolver
{
    /// <summary>Resolves the .sln/.csproj to load: an explicit --solution path if given, otherwise
    /// the single .sln (or, failing that, single .csproj) found directly under the repo root.
    /// Returns null - and the caller should fall back to tree-sitter - when nothing unambiguous
    /// is found.</summary>
    public static string? Resolve(string repoRoot, string? explicitSolutionPath, out string? warning)
    {
        warning = null;

        if (explicitSolutionPath is not null)
        {
            var resolved = Path.IsPathRooted(explicitSolutionPath)
                ? explicitSolutionPath
                : Path.Combine(repoRoot, explicitSolutionPath);

            if (!File.Exists(resolved))
            {
                warning = $"symdex: --solution path '{explicitSolutionPath}' does not exist.";
                return null;
            }

            return resolved;
        }

        var solutions = Directory.GetFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly);
        if (solutions.Length == 1)
        {
            return solutions[0];
        }

        if (solutions.Length > 1)
        {
            warning = "symdex: multiple .sln files at repo root; pass --solution to disambiguate.";
            return null;
        }

        var projects = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projects.Length == 1)
        {
            return projects[0];
        }

        if (projects.Length > 1)
        {
            warning = "symdex: multiple .csproj files at repo root and no .sln; pass --solution to disambiguate.";
            return null;
        }

        warning = "symdex: no .sln/.csproj found at repo root.";
        return null;
    }
}
