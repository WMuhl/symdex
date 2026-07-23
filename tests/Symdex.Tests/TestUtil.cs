using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Symdex.Tests;

internal static class TestUtil
{
    private static readonly string[] SkipDirs = ["bin", "obj", ".index"];

    /// <summary>Copies tests/fixtures/&lt;name&gt; into a fresh temp directory so each test run
    /// gets an isolated copy (indexing writes a .index/code.db next to the source files).</summary>
    public static string CopyFixture(string name, [CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!; // .../tests/Symdex.Tests
        var repoRoot = Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
        var source = Path.Combine(repoRoot, "tests", "fixtures", name);

        var dest = Directory.CreateTempSubdirectory($"symdex-fixture-{name}-").FullName;
        CopyDirectory(source, dest);
        return dest;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(dest, name));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }
    }

    /// <summary>Runs `dotnet restore` against a solution/project, required before MSBuildWorkspace
    /// can open it (it doesn't restore on its own).</summary>
    public static void Restore(string solutionOrProjectPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionOrProjectPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet restore failed for '{solutionOrProjectPath}':\n{stdout}\n{stderr}");
        }
    }
}
