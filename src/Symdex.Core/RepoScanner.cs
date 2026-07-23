using System.Diagnostics;

namespace Symdex.Core;

public sealed record ScannedFile(string RepoRelativePath, string AbsolutePath, string Lang);

/// <summary>Discovers indexable source files under a repo root, respecting .gitignore and skip rules.</summary>
public static class RepoScanner
{
    private const long MaxFileSizeBytes = 1024 * 1024; // 1 MB

    private static readonly string[] SkippedDirNames =
    [
        "node_modules", "bin", "obj", "dist", ".index", ".git", "out", "build", ".vs", ".vscode",
    ];

    private static readonly Dictionary<string, string> ExtensionToLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = Languages.CSharp,
        [".ts"] = Languages.TypeScript,
        [".tsx"] = Languages.TypeScript,
        [".js"] = Languages.JavaScript,
        [".jsx"] = Languages.JavaScript,
    };

    public static IReadOnlyList<ScannedFile> Scan(string repoRoot)
    {
        var candidatePaths = TryGitLsFiles(repoRoot) ?? WalkDirectory(repoRoot);

        var results = new List<ScannedFile>();
        foreach (var relativePath in candidatePaths)
        {
            var normalized = relativePath.Replace('\\', '/');
            if (IsInSkippedDir(normalized))
            {
                continue;
            }

            var ext = Path.GetExtension(normalized);
            if (!ExtensionToLanguage.TryGetValue(ext, out var lang))
            {
                continue;
            }

            var absolutePath = Path.Combine(repoRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolutePath))
            {
                continue;
            }

            var info = new FileInfo(absolutePath);
            if (info.Length > MaxFileSizeBytes)
            {
                continue;
            }

            results.Add(new ScannedFile(normalized, absolutePath, lang));
        }

        return results;
    }

    public static string? LanguageForPath(string repoRelativePath)
    {
        var ext = Path.GetExtension(repoRelativePath);
        return ExtensionToLanguage.TryGetValue(ext, out var lang) ? lang : null;
    }

    private static bool IsInSkippedDir(string normalizedRelativePath)
    {
        var segments = normalizedRelativePath.Split('/');
        // Exclude the file name itself (last segment) from the directory check.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            foreach (var skipped in SkippedDirNames)
            {
                if (string.Equals(segments[i], skipped, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<string>? TryGitLsFiles(string repoRoot)
    {
        if (!Directory.Exists(Path.Combine(repoRoot, ".git")) && !IsInsideGitWorkTree(repoRoot))
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo("git", "ls-files --cached --others --exclude-standard -z")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return null;
            }

            return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsInsideGitWorkTree(string repoRoot)
    {
        var dir = new DirectoryInfo(repoRoot);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return true;
            }

            dir = dir.Parent;
        }

        return false;
    }

    private static IReadOnlyList<string> WalkDirectory(string repoRoot)
    {
        var results = new List<string>();
        WalkDirectoryRecursive(repoRoot, repoRoot, results);
        return results;
    }

    private static void WalkDirectoryRecursive(string repoRoot, string currentDir, List<string> results)
    {
        foreach (var dir in Directory.EnumerateDirectories(currentDir))
        {
            var dirName = Path.GetFileName(dir);
            if (SkippedDirNames.Contains(dirName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            WalkDirectoryRecursive(repoRoot, dir, results);
        }

        foreach (var file in Directory.EnumerateFiles(currentDir))
        {
            results.Add(Path.GetRelativePath(repoRoot, file));
        }
    }
}
