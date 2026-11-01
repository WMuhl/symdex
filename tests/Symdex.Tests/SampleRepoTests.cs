using Symdex.Core;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Tests;

[Collection(SampleRepoCollection.Name)]
public sealed class SampleRepoTests(RestoredSampleRepoFixture fixture)
{
    private static Indexer CreateIndexer() => new([new RoslynFeeder(), new TreeSitterFeeder()]);

    [Fact]
    public void EnsureFresh_indexes_via_roslyn_without_falling_back()
    {
        var root = fixture.CreateIsolatedCopy();
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var result = CreateIndexer().EnsureFresh(root, db);

            Assert.True(result.ReindexedFileCount > 0);
            Assert.True(result.Warnings.Count == 0, string.Join("\n", result.Warnings));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Outline_returns_class_and_method_with_no_bodies()
    {
        using var db = IndexDatabase.OpenOrCreate(fixture.Root);
        CreateIndexer().EnsureFresh(fixture.Root, db);

        var outline = db.Outline("csharp/SampleLib/PaymentProcessor.cs");

        Assert.Equal(2, outline.Count);
        Assert.Contains(outline, s => s.Kind == SymbolKinds.Class && s.Name == "PaymentProcessor");
        Assert.Contains(outline, s => s.Kind == SymbolKinds.Method && s.Name == "Charge");
        Assert.All(outline, s => Assert.DoesNotContain("return", s.Signature ?? string.Empty));
    }

    [Fact]
    public void Find_locates_the_interface()
    {
        using var db = IndexDatabase.OpenOrCreate(fixture.Root);
        CreateIndexer().EnsureFresh(fixture.Root, db);

        var found = db.Find("IPaymentProcessor", null, 50);

        Assert.Contains(found, s => s.Kind == SymbolKinds.Interface && s.Name == "IPaymentProcessor");
    }

    [Fact]
    public void Refs_finds_implementation_and_usage_sites_across_files()
    {
        using var db = IndexDatabase.OpenOrCreate(fixture.Root);
        CreateIndexer().EnsureFresh(fixture.Root, db);

        var candidates = db.ResolveSymbolCandidates("IPaymentProcessor");
        var resolved = Assert.Single(candidates);

        var refs = db.RefsFor(resolved, kindFilter: null, 50);

        Assert.Contains(refs, r => r.Kind == RefKinds.Implementation && r.FilePath.EndsWith("PaymentProcessor.cs"));
        Assert.Contains(refs, r => r.FilePath.EndsWith("CheckoutService.cs"));
    }

    [Fact]
    public void Callers_finds_the_call_site()
    {
        using var db = IndexDatabase.OpenOrCreate(fixture.Root);
        CreateIndexer().EnsureFresh(fixture.Root, db);

        var chargeOnInterface = db.Find("Charge", null, 50)
            .Single(s => s.FilePath.EndsWith("IPaymentProcessor.cs"));

        var refs = db.RefsFor(chargeOnInterface.FqName!, [RefKinds.Call, RefKinds.Reference], 50);

        Assert.Contains(refs, r => r.FilePath.EndsWith("CheckoutService.cs") && r.Kind == RefKinds.Call);
    }

    [Fact]
    public void Editing_one_file_reindexes_only_that_file()
    {
        var root = fixture.CreateIsolatedCopy();
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            CreateIndexer().EnsureFresh(root, db);

            var filePath = Path.Combine(root, "csharp", "SampleLib", "PaymentProcessor.cs");
            File.AppendAllText(filePath, Environment.NewLine + "// a trivial trailing comment" + Environment.NewLine);

            var result = CreateIndexer().EnsureFresh(root, db);

            Assert.Equal(1, result.ReindexedFileCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Deleting_the_solution_falls_back_to_tree_sitter_for_cs_files()
    {
        var root = fixture.CreateIsolatedCopy();
        try
        {
            File.Delete(Path.Combine(root, "SampleRepo.sln"));

            using var db = IndexDatabase.OpenOrCreate(root);
            var result = CreateIndexer().EnsureFresh(root, db);

            Assert.NotEmpty(result.Warnings);

            var outline = db.Outline("csharp/SampleLib/IPaymentProcessor.cs");
            Assert.Contains(outline, s => s.Kind == SymbolKinds.Interface && s.Name == "IPaymentProcessor");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Typescript_import_is_recorded_as_a_reference()
    {
        using var db = IndexDatabase.OpenOrCreate(fixture.Root);
        CreateIndexer().EnsureFresh(fixture.Root, db);

        var refs = db.RefsFor("helper", kindFilter: null, 50);

        Assert.Contains(refs, r => r.FilePath.EndsWith("math.ts") && r.Kind == RefKinds.Reference);
    }
}
