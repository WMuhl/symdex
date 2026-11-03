namespace Symdex.Cli;

internal static class PathUtil
{
    /// <summary>Converts a user-supplied path (relative to <paramref name="baseDirectory"/>, or
    /// absolute) into a repo-relative, forward-slash path matching what's stored in the index.
    /// <paramref name="baseDirectory"/> defaults to the current directory.</summary>
    public static string ToRepoRelative(string repoRoot, string inputPath, string? baseDirectory = null)
    {
        var rooted = Path.IsPathRooted(inputPath);
        var absolute = rooted
            ? inputPath
            : Path.Combine(baseDirectory ?? Directory.GetCurrentDirectory(), inputPath);

        var relative = Path.GetRelativePath(repoRoot, absolute);

        // The current directory can come back with symlinks resolved (macOS reports /var/... as
        // /private/var/...) while repoRoot does not, which makes an in-repo path look outside it.
        if (!rooted && IsOutsideRepo(relative) && File.Exists(Path.Combine(repoRoot, inputPath)))
        {
            relative = Path.GetRelativePath(repoRoot, Path.Combine(repoRoot, inputPath));
        }

        return relative.Replace('\\', '/');
    }

    private static bool IsOutsideRepo(string relative) =>
        relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
        || relative.StartsWith("..\\", StringComparison.Ordinal) || Path.IsPathRooted(relative);
}
