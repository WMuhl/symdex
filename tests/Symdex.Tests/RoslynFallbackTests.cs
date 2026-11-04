using Symdex.Core;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Tests;

[Collection(SampleRepoCollection.Name)]
public sealed class RoslynFallbackTests(RestoredSampleRepoFixture fixture)
{
    private static Indexer CreateIndexer() => new([new RoslynFeeder(), new TreeSitterFeeder()]);

    [Fact]
    public void Unrestored_package_reference_falls_back_to_tree_sitter_and_warns()
    {
        var root = TestUtil.CopyFixture("sample-repo");
        try
        {
            // Package-free projects still compile without restore; using an unrestored
            // PackageReference is what makes the workspace fail and fall back.
            File.WriteAllText(
                Path.Combine(root, "csharp", "SampleLib", "SampleLib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                  </ItemGroup>
                </Project>
                """);
            File.AppendAllText(
                Path.Combine(root, "csharp", "SampleLib", "PaymentProcessor.cs"),
                """

                file class NeedsRestore
                {
                    public string Dump() => Newtonsoft.Json.JsonConvert.SerializeObject(1);
                }
                """);
            AssertTreeSitterFallback(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Broken_csproj_falls_back_to_tree_sitter_and_warns()
    {
        var root = TestUtil.CopyFixture("sample-repo");
        try
        {
            File.WriteAllText(
                Path.Combine(root, "csharp", "SampleLib", "SampleLib.csproj"),
                "<Project><PropertyGroup><ThisIsNotClosed></Project>");
            AssertTreeSitterFallback(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Successful_roslyn_index_stores_no_tree_sitter_rows_for_cs_files()
    {
        var root = fixture.CreateIsolatedCopy();
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var result = CreateIndexer().EnsureFresh(root, db);

            Assert.Empty(result.Warnings);
            var csharp = db.GetAllFiles().Values.Where(f => f.Lang == Languages.CSharp).ToList();
            Assert.NotEmpty(csharp);

            foreach (var file in csharp)
            {
                var outline = db.Outline(file.Path);
                Assert.NotEmpty(outline);
                Assert.All(outline, s =>
                {
                    Assert.False(string.IsNullOrEmpty(s.FqName));
                    Assert.Contains('.', s.FqName!);
                });
            }

            var processor = db.Outline("csharp/SampleLib/PaymentProcessor.cs");
            Assert.Equal(2, processor.Count);
            Assert.Contains(processor, s => s.FqName == "SampleLib.PaymentProcessor");
            Assert.NotEmpty(db.RefsFor("SampleLib.IPaymentProcessor", kindFilter: null, 50));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertTreeSitterFallback(string root)
    {
        using var db = IndexDatabase.OpenOrCreate(root);
        var result = CreateIndexer().EnsureFresh(root, db);

        Assert.Contains(result.Warnings, w => w.Contains("symdex:", StringComparison.Ordinal));

        var outline = db.Outline("csharp/SampleLib/IPaymentProcessor.cs");
        var iface = Assert.Single(outline, s => s.Kind == SymbolKinds.Interface && s.Name == "IPaymentProcessor");
        Assert.Null(iface.FqName);
        Assert.Contains(outline, s => s.Kind == SymbolKinds.Method && s.Name == "Charge" && s.FqName is null);

        var csharp = db.GetAllFiles().Values.Where(f => f.Lang == Languages.CSharp);
        Assert.All(csharp, file => Assert.All(db.Outline(file.Path), s => Assert.Null(s.FqName)));
    }
}
