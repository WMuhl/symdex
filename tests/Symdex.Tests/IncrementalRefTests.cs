using Symdex.Core;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Tests;

[Collection(SampleRepoCollection.Name)]
public sealed class IncrementalRefTests(RestoredSampleRepoFixture fixture)
{
    private static Indexer CreateIndexer() => new([new RoslynFeeder(), new TreeSitterFeeder()]);

    [Fact]
    public void Body_edit_keeps_cross_file_refs_and_matches_a_fresh_index()
    {
        var root = fixture.CreateIsolatedCopy();
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var indexer = CreateIndexer();
            var first = indexer.EnsureFresh(root, db);
            Assert.Empty(first.Warnings);

            var checkout = Path.Combine(root, "csharp", "SampleLib", "CheckoutService.cs");
            File.AppendAllText(checkout, Environment.NewLine);

            var incremental = indexer.EnsureFresh(root, db);
            Assert.Equal(1, incremental.ReindexedFileCount);
            Assert.Empty(incremental.Warnings);

            var charge = db.Find("Charge", SymbolKinds.Method, 50)
                .Single(s => s.FilePath.EndsWith("IPaymentProcessor.cs"));
            var callers = db.RefsFor(charge.FqName!, [RefKinds.Call, RefKinds.Reference], 50);
            Assert.Contains(callers, r => r.FilePath.EndsWith("CheckoutService.cs") && r.Kind == RefKinds.Call);

            var typeRefs = db.RefsFor("SampleLib.IPaymentProcessor", kindFilter: null, 50);
            Assert.Contains(typeRefs, r => r.FilePath.EndsWith("CheckoutService.cs") && r.Kind == RefKinds.Reference);

            AssertSameRows(root, db);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Symbol_change_refreshes_refs_in_other_files_to_match_a_fresh_index()
    {
        var root = fixture.CreateIsolatedCopy();
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var indexer = CreateIndexer();
            Assert.Empty(indexer.EnsureFresh(root, db).Warnings);

            var iface = Path.Combine(root, "csharp", "SampleLib", "IPaymentProcessor.cs");
            var text = File.ReadAllText(iface);
            var close = text.LastIndexOf('}');
            File.WriteAllText(iface, text[..close] + "    int Extra() => 1;\n" + text[close..]);

            var incremental = indexer.EnsureFresh(root, db);
            Assert.Equal(1, incremental.ReindexedFileCount);
            Assert.Empty(incremental.Warnings);
            Assert.Contains(db.Outline("csharp/SampleLib/IPaymentProcessor.cs"), s => s.Name == "Extra");

            var callers = db.RefsFor(
                db.Find("Charge", SymbolKinds.Method, 50).Single(s => s.FilePath.EndsWith("IPaymentProcessor.cs")).FqName!,
                [RefKinds.Call, RefKinds.Reference],
                50);
            Assert.Contains(callers, r => r.FilePath.EndsWith("CheckoutService.cs") && r.Kind == RefKinds.Call);

            AssertSameRows(root, db);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertSameRows(string modifiedRoot, IndexDatabase incremental)
    {
        var expectedRoot = TestUtil.CopyTree(modifiedRoot);
        try
        {
            using var fresh = IndexDatabase.OpenOrCreate(expectedRoot);
            var result = CreateIndexer().EnsureFresh(expectedRoot, fresh);
            Assert.Empty(result.Warnings);
            Assert.Equal(SymbolDump(fresh), SymbolDump(incremental));
            Assert.Equal(RefDump(fresh), RefDump(incremental));
            Assert.Equal(
                fresh.GetAllFiles().OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}|{kv.Value.Hash}|{kv.Value.Lang}").ToArray(),
                incremental.GetAllFiles().OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}|{kv.Value.Hash}|{kv.Value.Lang}").ToArray());
        }
        finally
        {
            Directory.Delete(expectedRoot, recursive: true);
        }
    }

    private static string[] SymbolDump(IndexDatabase db)
    {
        var rows = new List<string>();
        foreach (var path in db.GetAllFiles().Keys.OrderBy(path => path, StringComparer.Ordinal))
        {
            foreach (var symbol in db.Outline(path))
            {
                rows.Add(string.Join("|",
                    symbol.FilePath,
                    symbol.StartLine,
                    symbol.EndLine,
                    symbol.Kind,
                    symbol.Name,
                    symbol.FqName,
                    symbol.Signature,
                    symbol.Container,
                    symbol.Doc));
            }
        }

        return rows.OrderBy(row => row, StringComparer.Ordinal).ToArray();
    }

    private static string[] RefDump(IndexDatabase db) =>
        db.AllRefs()
            .Select(r => string.Join("|", r.FilePath, r.Line, r.Kind, r.SymbolFqName))
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();
}
