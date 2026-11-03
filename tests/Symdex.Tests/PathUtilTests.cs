using Symdex.Cli;

namespace Symdex.Tests;

public class PathUtilTests
{
    [Fact]
    public void Relative_path_resolves_against_the_base_directory()
    {
        var repo = Path.Combine(Path.GetTempPath(), "symdex-pathutil-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("src/Foo.cs", PathUtil.ToRepoRelative(repo, "Foo.cs", Path.Combine(repo, "src")));
    }

    [Fact]
    public void Absolute_path_inside_the_repo_is_made_repo_relative()
    {
        var repo = Path.Combine(Path.GetTempPath(), "symdex-pathutil-" + Guid.NewGuid().ToString("N"));
        Assert.Equal("src/Foo.cs", PathUtil.ToRepoRelative(repo, Path.Combine(repo, "src", "Foo.cs")));
    }

    [Fact]
    public void Repo_relative_file_wins_when_the_base_directory_is_spelled_differently()
    {
        var repo = Path.Combine(Path.GetTempPath(), "symdex-pathutil-" + Guid.NewGuid().ToString("N"));
        var otherSpelling = Path.Combine(Path.GetTempPath(), "symdex-pathutil-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        try
        {
            File.WriteAllText(Path.Combine(repo, "src", "Foo.cs"), "class Foo {}");
            Assert.Equal("src/Foo.cs", PathUtil.ToRepoRelative(repo, Path.Combine("src", "Foo.cs"), otherSpelling));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Path_outside_the_repo_is_left_outside_when_no_such_repo_file_exists()
    {
        var repo = Path.Combine(Path.GetTempPath(), "symdex-pathutil-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(Path.GetTempPath(), "symdex-pathutil-other-" + Guid.NewGuid().ToString("N"));
        Assert.StartsWith("../", PathUtil.ToRepoRelative(repo, "Foo.cs", elsewhere));
    }
}
