using Symdex.Core;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Tests;

public sealed class MultiTargetTests
{
    [Fact]
    public void Multi_target_project_stores_each_symbol_and_ref_once()
    {
        var root = TestUtil.CopyFixture("multi-tfm");
        try
        {
            TestUtil.Restore(Path.Combine(root, "Multi.csproj"));
            using var db = IndexDatabase.OpenOrCreate(root);
            var result = new Indexer([new RoslynFeeder(), new TreeSitterFeeder()]).EnsureFresh(root, db);

            Assert.Empty(result.Warnings);

            var symbols = db.GetAllFiles().Keys.SelectMany(db.Outline).ToList();
            Assert.DoesNotContain(
                symbols.GroupBy(s => (s.FilePath, s.Name, s.FqName, s.Kind, s.StartLine, s.EndLine)),
                group => group.Count() > 1);
            Assert.Contains(symbols, s => s.Name == "IWorker" && s.FqName == "MultiTfm.IWorker");
            Assert.Contains(symbols, s => s.Name == "Worker" && s.FqName == "MultiTfm.Worker");
            Assert.Equal(symbols.Count, symbols.Select(s => s.FqName).Distinct(StringComparer.Ordinal).Count());

            var refs = db.AllRefs();
            Assert.NotEmpty(refs);
            Assert.DoesNotContain(
                refs.GroupBy(r => (r.SymbolFqName, r.Kind, r.FilePath, r.Line)),
                group => group.Count() > 1);
            Assert.Contains(refs, r => r.SymbolFqName == "MultiTfm.IWorker" && r.Kind == RefKinds.Implementation && r.FilePath.EndsWith("Worker.cs"));
            Assert.Contains(refs, r => r.Kind == RefKinds.Call && r.FilePath.EndsWith("App.cs") && r.SymbolFqName.Contains("Run", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
