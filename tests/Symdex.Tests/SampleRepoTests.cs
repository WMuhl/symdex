using Symdex.Core;
using Symdex.Core.Feeding;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Tests;

public sealed class SampleRepoTests : IDisposable
{
    private readonly string _repoRoot;

    public SampleRepoTests()
    {
        _repoRoot = TestUtil.CopyFixture("sample-repo");

        // MSBuildWorkspace requires a project to already be NuGet-restored - it doesn't restore
        // itself, and a fresh clone/copy never has one (obj/ is gitignored). A real target repo
        // would normally already be restored as part of the user's own workflow; tests have to
        // do it explicitly since TestUtil.CopyFixture starts from a clean checkout every time.
        TestUtil.Restore(Path.Combine(_repoRoot, "SampleRepo.sln"));
    }

    public void Dispose()
    {
        Directory.Delete(_repoRoot, recursive: true);
    }

    private static Indexer CreateIndexer() => new([new RoslynFeeder(), new TreeSitterFeeder()]);

    [Fact]
    public void EnsureFresh_indexes_via_roslyn_without_falling_back()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        var result = CreateIndexer().EnsureFresh(_repoRoot, db);

        Assert.True(result.ReindexedFileCount > 0);
        Assert.True(result.Warnings.Count == 0, string.Join("\n", result.Warnings));
    }

    [Fact]
    public void Outline_returns_class_and_method_with_no_bodies()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var outline = db.Outline("csharp/SampleLib/PaymentProcessor.cs");

        Assert.Equal(2, outline.Count);
        Assert.Contains(outline, s => s.Kind == SymbolKinds.Class && s.Name == "PaymentProcessor");
        Assert.Contains(outline, s => s.Kind == SymbolKinds.Method && s.Name == "Charge");
        Assert.All(outline, s => Assert.DoesNotContain("return", s.Signature ?? string.Empty));
    }

    [Fact]
    public void Find_locates_the_interface()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var found = db.Find("IPaymentProcessor", null, 50);

        Assert.Contains(found, s => s.Kind == SymbolKinds.Interface && s.Name == "IPaymentProcessor");
    }

    [Fact]
    public void Refs_finds_implementation_and_usage_sites_across_files()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var candidates = db.ResolveSymbolCandidates("IPaymentProcessor");
        var resolved = Assert.Single(candidates);

        var refs = db.RefsFor(resolved, kindFilter: null, 50);

        Assert.Contains(refs, r => r.Kind == RefKinds.Implementation && r.FilePath.EndsWith("PaymentProcessor.cs"));
        Assert.Contains(refs, r => r.FilePath.EndsWith("CheckoutService.cs"));
    }

    [Fact]
    public void Callers_finds_the_call_site()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var chargeOnInterface = db.Find("Charge", null, 50)
            .Single(s => s.FilePath.EndsWith("IPaymentProcessor.cs"));

        var refs = db.RefsFor(chargeOnInterface.FqName!, [RefKinds.Call, RefKinds.Reference], 50);

        Assert.Contains(refs, r => r.FilePath.EndsWith("CheckoutService.cs") && r.Kind == RefKinds.Call);
    }

    [Fact]
    public void Editing_one_file_reindexes_only_that_file()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var filePath = Path.Combine(_repoRoot, "csharp", "SampleLib", "PaymentProcessor.cs");
        File.AppendAllText(filePath, Environment.NewLine + "// a trivial trailing comment" + Environment.NewLine);

        var result = CreateIndexer().EnsureFresh(_repoRoot, db);

        Assert.Equal(1, result.ReindexedFileCount);
    }

    [Fact]
    public void Deleting_the_solution_falls_back_to_tree_sitter_for_cs_files()
    {
        File.Delete(Path.Combine(_repoRoot, "SampleRepo.sln"));

        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        var result = CreateIndexer().EnsureFresh(_repoRoot, db);

        Assert.NotEmpty(result.Warnings);

        var outline = db.Outline("csharp/SampleLib/IPaymentProcessor.cs");
        Assert.Contains(outline, s => s.Kind == SymbolKinds.Interface && s.Name == "IPaymentProcessor");
    }

    [Fact]
    public void Typescript_import_is_recorded_as_a_reference()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var refs = db.RefsFor("helper", kindFilter: null, 50);

        Assert.Contains(refs, r => r.FilePath.EndsWith("math.ts") && r.Kind == RefKinds.Reference);
    }
}
