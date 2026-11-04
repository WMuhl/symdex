namespace Symdex.Roslyn;

internal static class SolutionResolver
{
    internal const int SearchDepth = 3;

    private static readonly string[] SkippedDirNames =
    [
        "node_modules", "bin", "obj", "dist", ".index", ".git", "out", "build", ".vs", ".vscode",
        "__pycache__", ".venv", "venv", ".tox", ".mypy_cache", "site-packages",
    ];

    /// <summary>
    /// Resolves the solution or project to load. An explicit <paramref name="explicitSolutionPath"/>
    /// wins. Otherwise the single solution file at the repo root is used, then the single .csproj
    /// there. If the root has neither, a .sln/.slnx up to <see cref="SearchDepth"/> directories
    /// down is chosen. <paramref name="warning"/> is set when resolution fails and when a nested
    /// solution was chosen.
    /// </summary>
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

        var rootSolutions = SolutionFilesIn(repoRoot);
        if (rootSolutions.Count == 1)
        {
            return rootSolutions[0];
        }

        if (rootSolutions.Count > 1)
        {
            warning = "symdex: multiple .sln/.slnx files at repo root; pass --solution to disambiguate.";
            return null;
        }

        var projects = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projects.Length == 1)
        {
            return projects[0];
        }

        if (projects.Length > 1)
        {
            warning = "symdex: multiple .csproj files at repo root and no .sln/.slnx; pass --solution to disambiguate.";
            return null;
        }

        var nested = FindNestedSolutions(repoRoot);
        if (nested.Count == 0)
        {
            warning = "symdex: no .sln/.slnx/.csproj found at the repo root or within "
                + $"{SearchDepth} directories under it. Pass --solution <path>.";
            return null;
        }

        var chosen = Choose(repoRoot, nested);
        var relative = Path.GetRelativePath(repoRoot, chosen).Replace('\\', '/');
        warning = $"symdex: using '{relative}' (no solution at the repo root; pass --solution to override).";
        return chosen;
    }

    private readonly record struct FoundSolution(string Path, int Depth);

    private static string Choose(string repoRoot, List<FoundSolution> found)
    {
        var repoName = new DirectoryInfo(repoRoot).Name;
        var named = found
            .Where(item => string.Equals(
                Path.GetFileNameWithoutExtension(item.Path),
                repoName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pool = named.Count > 0 ? named : found;
        var depth = pool.Min(item => item.Depth);
        return pool
            .Where(item => item.Depth == depth)
            .Select(item => item.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static List<FoundSolution> FindNestedSolutions(string repoRoot)
    {
        var found = new List<FoundSolution>();
        var pending = new Queue<(string Dir, int Depth)>();
        EnqueueChildren(repoRoot, parentDepth: 0, pending);
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Dequeue();
            foreach (var path in SolutionFilesIn(dir))
            {
                found.Add(new FoundSolution(path, depth));
            }

            if (depth < SearchDepth)
            {
                EnqueueChildren(dir, depth, pending);
            }
        }

        return found;
    }

    private static void EnqueueChildren(string dir, int parentDepth, Queue<(string Dir, int Depth)> pending)
    {
        if (parentDepth >= SearchDepth)
        {
            return;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (SkippedDirNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            pending.Enqueue((child, parentDepth + 1));
        }
    }

    private static List<string> SolutionFilesIn(string dir)
    {
        var found = new List<string>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(file);
            }
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }
}
