namespace Symdex.Cli;

internal static class PathUtil
{
    /// <summary>Converts a user-supplied path (relative to cwd, or absolute) into a repo-relative,
    /// forward-slash path matching what's stored in the index.</summary>
    public static string ToRepoRelative(string repoRoot, string inputPath)
    {
        var absolute = Path.IsPathRooted(inputPath)
            ? inputPath
            : Path.Combine(Directory.GetCurrentDirectory(), inputPath);

        var relative = Path.GetRelativePath(repoRoot, absolute);
        return relative.Replace('\\', '/');
    }
}
