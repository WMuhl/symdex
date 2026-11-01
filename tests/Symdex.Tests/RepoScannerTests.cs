using System.Diagnostics;
using Symdex.Core;

namespace Symdex.Tests;

public sealed class RepoScannerTests
{
    [Fact]
    public void Scan_respects_gitignore_in_a_git_repo()
    {
        var root = Directory.CreateTempSubdirectory("symdex-git-scan-").FullName;
        try
        {
            RunGit(root, "init -q");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "secret/\n*.ignored.cs\n");
            File.WriteAllText(Path.Combine(root, "kept.cs"), "class Kept {}\n");
            File.WriteAllText(Path.Combine(root, "noise.ignored.cs"), "class Noise {}\n");
            Directory.CreateDirectory(Path.Combine(root, "secret"));
            File.WriteAllText(Path.Combine(root, "secret", "hidden.cs"), "class Hidden {}\n");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "ok.py"), "def ok():\n    return 1\n");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
            File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "index.js"), "export const x = 1;\n");

            var scanned = RepoScanner.Scan(root).Select(f => f.RepoRelativePath).ToHashSet(StringComparer.Ordinal);

            Assert.Contains("kept.cs", scanned);
            Assert.Contains("nested/ok.py", scanned);
            Assert.DoesNotContain("noise.ignored.cs", scanned);
            Assert.DoesNotContain("secret/hidden.cs", scanned);
            Assert.DoesNotContain(scanned, path => path.StartsWith("node_modules/", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scan_skips_known_directories_and_maps_extensions_outside_git()
    {
        var root = Directory.CreateTempSubdirectory("symdex-walk-scan-").FullName;
        try
        {
            var skipped =
                new[]
                {
                    "node_modules", "bin", "obj", "dist", ".index", "out", "build", ".vs", ".vscode",
                    "__pycache__", ".venv", "venv", ".tox", ".mypy_cache", "site-packages",
                };
            foreach (var name in skipped)
            {
                var dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "skip.cs"), "class Skip {}\n");
            }

            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            Write(src, "a.cs", "class A {}\n");
            Write(src, "a.ts", "export const a = 1;\n");
            Write(src, "a.tsx", "export const A = () => null;\n");
            Write(src, "a.js", "export const a = 1;\n");
            Write(src, "a.jsx", "export const A = () => null;\n");
            Write(src, "a.py", "def a():\n    return 1\n");
            Write(src, "a.pyi", "def a() -> int: ...\n");
            Write(src, "Upper.CS", "class Upper {}\n");
            Write(src, "notes.txt", "ignore me\n");
            File.WriteAllText(Path.Combine(root, "bin.cs"), "class BinFile {}\n");

            Assert.Equal(Languages.CSharp, RepoScanner.LanguageForPath("a.cs"));
            Assert.Equal(Languages.CSharp, RepoScanner.LanguageForPath("Upper.CS"));
            Assert.Equal(Languages.TypeScript, RepoScanner.LanguageForPath("a.ts"));
            Assert.Equal(Languages.TypeScript, RepoScanner.LanguageForPath("a.tsx"));
            Assert.Equal(Languages.JavaScript, RepoScanner.LanguageForPath("a.js"));
            Assert.Equal(Languages.JavaScript, RepoScanner.LanguageForPath("a.jsx"));
            Assert.Equal(Languages.Python, RepoScanner.LanguageForPath("a.py"));
            Assert.Equal(Languages.Python, RepoScanner.LanguageForPath("a.pyi"));
            Assert.Null(RepoScanner.LanguageForPath("notes.txt"));

            var scanned = RepoScanner.Scan(root).ToDictionary(f => f.RepoRelativePath, f => f.Lang, StringComparer.Ordinal);
            Assert.Equal(Languages.CSharp, scanned["src/a.cs"]);
            Assert.Equal(Languages.TypeScript, scanned["src/a.ts"]);
            Assert.Equal(Languages.TypeScript, scanned["src/a.tsx"]);
            Assert.Equal(Languages.JavaScript, scanned["src/a.js"]);
            Assert.Equal(Languages.JavaScript, scanned["src/a.jsx"]);
            Assert.Equal(Languages.Python, scanned["src/a.py"]);
            Assert.Equal(Languages.Python, scanned["src/a.pyi"]);
            Assert.Equal(Languages.CSharp, scanned["src/Upper.CS"]);
            Assert.Equal(Languages.CSharp, scanned["bin.cs"]);
            Assert.DoesNotContain(scanned.Keys, path => path.EndsWith("skip.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(scanned.Keys, path => path.EndsWith("notes.txt", StringComparison.Ordinal));
            Assert.Equal(9, scanned.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string dir, string name, string contents) =>
        File.WriteAllText(Path.Combine(dir, name), contents);

    private static void RunGit(string root, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args} failed:\n{stdout}\n{stderr}");
        }
    }
}
