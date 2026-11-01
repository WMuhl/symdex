using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Symdex.Tests;

internal static class TestUtil
{
    private static readonly string[] FixtureSkipDirs = ["bin", "obj", ".index"];
    private static readonly string[] TreeSkipDirs = ["bin", ".index"];

    // MSBuildLocator publishes these into the process environment when it registers an SDK.
    // A later `dotnet restore` in the same process inherits them and then fails to load the
    // SDK resolver (wrong host / runtime). The child restore must see a clean dotnet environment.
    private static readonly string[] MsBuildLocatorVariables =
    [
        "DOTNET_HOST_PATH",
        "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath",
    ];

    /// <summary>Copies tests/fixtures/&lt;name&gt; into a fresh temp directory so each test run
    /// gets an isolated copy (indexing writes a .index/code.db next to the source files).</summary>
    public static string CopyFixture(string name, [CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!; // .../tests/Symdex.Tests
        var repoRoot = Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
        var source = Path.Combine(repoRoot, "tests", "fixtures", name);

        var dest = Directory.CreateTempSubdirectory($"symdex-fixture-{name}-").FullName;
        CopyDirectory(source, dest, FixtureSkipDirs);
        return dest;
    }

    /// <summary>Copies an already-restored tree, including <c>obj/</c>, so a test can index
    /// without running <c>dotnet restore</c> again. Skips <c>bin/</c> and <c>.index/</c>.</summary>
    public static string CopyTree(string source)
    {
        var dest = Directory.CreateTempSubdirectory("symdex-copy-").FullName;
        CopyDirectory(source, dest, TreeSkipDirs);
        return dest;
    }

    private static void CopyDirectory(string source, string dest, string[] skipDirs)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (skipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(dest, name), skipDirs);
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
        foreach (var name in MsBuildLocatorVariables)
        {
            psi.Environment.Remove(name);
        }

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
