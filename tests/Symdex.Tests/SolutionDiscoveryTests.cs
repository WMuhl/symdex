using Symdex.Core;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Tests;

public sealed class SolutionDiscoveryTests
{
    [Fact]
    public void Explicit_solution_is_used_unchanged()
    {
        var root = TempRepo();
        try
        {
            var sln = Path.Combine(root, "App.sln");
            File.WriteAllText(sln, "sln");
            var chosen = SolutionResolver.Resolve(root, "App.sln", out var warning);
            Assert.Equal(sln, Path.GetFullPath(chosen!));
            Assert.Null(warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_explicit_solution_warns()
    {
        var root = TempRepo();
        try
        {
            var chosen = SolutionResolver.Resolve(root, "missing.sln", out var warning);
            Assert.Null(chosen);
            Assert.Contains("--solution", warning, StringComparison.Ordinal);
            Assert.Contains("does not exist", warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Single_root_solution_needs_no_note()
    {
        var root = TempRepo();
        try
        {
            var sln = Path.Combine(root, "App.slnx");
            File.WriteAllText(sln, "<Solution />");
            var chosen = SolutionResolver.Resolve(root, null, out var warning);
            Assert.Equal(sln, chosen);
            Assert.Null(warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Multiple_root_solutions_ask_for_solution_flag()
    {
        var root = TempRepo();
        try
        {
            File.WriteAllText(Path.Combine(root, "A.sln"), "");
            File.WriteAllText(Path.Combine(root, "B.slnx"), "");
            var chosen = SolutionResolver.Resolve(root, null, out var warning);
            Assert.Null(chosen);
            Assert.Contains("--solution", warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Nested_solution_matching_the_repo_name_wins()
    {
        var root = TempRepo();
        try
        {
            var repoName = new DirectoryInfo(root).Name;
            var match = Path.Combine(root, "deep", "a", repoName + ".sln");
            var other = Path.Combine(root, "other", "Other.slnx");
            Directory.CreateDirectory(Path.GetDirectoryName(match)!);
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllText(match, "");
            File.WriteAllText(other, "");
            var chosen = SolutionResolver.Resolve(root, null, out var warning);
            Assert.Equal(Path.GetFullPath(match), Path.GetFullPath(chosen!));
            Assert.Contains("--solution", warning, StringComparison.Ordinal);
            Assert.Contains($"deep/a/{repoName}.sln", warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Otherwise_the_shallowest_nested_solution_wins()
    {
        var root = TempRepo();
        try
        {
            var shallow = Path.Combine(root, "src", "Zed.sln");
            var deep = Path.Combine(root, "src", "nested", "Aaa.sln");
            Directory.CreateDirectory(Path.GetDirectoryName(shallow)!);
            Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
            File.WriteAllText(shallow, "");
            File.WriteAllText(deep, "");
            var chosen = SolutionResolver.Resolve(root, null, out var warning);
            Assert.Equal(Path.GetFullPath(shallow), Path.GetFullPath(chosen!));
            Assert.Contains("src/Zed.sln", warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Solutions_deeper_than_the_search_or_under_skipped_dirs_are_ignored()
    {
        var root = TempRepo();
        try
        {
            var tooDeep = Path.Combine(root, "a", "b", "c", "d", "Deep.sln");
            var hidden = Path.Combine(root, "obj", "Hidden.sln");
            Directory.CreateDirectory(Path.GetDirectoryName(tooDeep)!);
            Directory.CreateDirectory(Path.GetDirectoryName(hidden)!);
            File.WriteAllText(tooDeep, "");
            File.WriteAllText(hidden, "");
            var chosen = SolutionResolver.Resolve(root, null, out var warning);
            Assert.Null(chosen);
            Assert.Contains("--solution <path>", warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Single_root_project_wins_over_a_nested_solution()
    {
        var root = TempRepo();
        try
        {
            var project = Path.Combine(root, "App.csproj");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(project, "<Project />");
            File.WriteAllText(Path.Combine(root, "src", "App.sln"), "");
            var chosen = SolutionResolver.Resolve(root, null, out var warning);
            Assert.Equal(project, chosen);
            Assert.Null(warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Solution_under_src_is_indexed_by_roslyn()
    {
        var root = TestUtil.CopyFixture("nested-sln");
        try
        {
            TestUtil.Restore(Path.Combine(root, "src", "Nested.sln"));
            using var db = IndexDatabase.OpenOrCreate(root);
            var result = new Indexer([new RoslynFeeder(), new TreeSitterFeeder()]).EnsureFresh(root, db);

            Assert.Contains(result.Warnings, w => w.Contains("src/Nested.sln", StringComparison.Ordinal) && w.Contains("--solution", StringComparison.Ordinal));
            var greeter = Assert.Single(db.Outline("src/App/Greeter.cs"), s => s.Name == "Greeter");
            Assert.Equal("Nested.Greeter", greeter.FqName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Slnx_under_src_is_indexed_by_roslyn()
    {
        var root = TestUtil.CopyFixture("nested-sln");
        try
        {
            File.Delete(Path.Combine(root, "src", "Nested.sln"));
            var slnx = Path.Combine(root, "src", "Nested.slnx");
            File.WriteAllText(slnx, """
                <Solution>
                  <Project Path="App/App.csproj" />
                </Solution>
                """);
            TestUtil.Restore(slnx);
            using var db = IndexDatabase.OpenOrCreate(root);
            var result = new Indexer([new RoslynFeeder(), new TreeSitterFeeder()]).EnsureFresh(root, db);

            Assert.Contains(result.Warnings, w => w.Contains("src/Nested.slnx", StringComparison.Ordinal) && w.Contains("--solution", StringComparison.Ordinal));
            var greeter = Assert.Single(db.Outline("src/App/Greeter.cs"), s => s.Name == "Greeter");
            Assert.Equal("Nested.Greeter", greeter.FqName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempRepo() => Directory.CreateTempSubdirectory("symdex-resolve-").FullName;
}
