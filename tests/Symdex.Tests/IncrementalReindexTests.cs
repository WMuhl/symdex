using System.Text.RegularExpressions;
using Symdex.Cli;
using Symdex.Core;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Tests;

[Collection(SampleRepoCollection.Name)]
public sealed class IncrementalReindexTests(RestoredSampleRepoFixture fixture)
{
    private static Indexer CreateIndexer() => new([new RoslynFeeder(), new TreeSitterFeeder()]);

    [Fact]
    public void EnsureFresh_adds_changes_deletes_and_renames_without_touching_other_files()
    {
        var root = fixture.CreateIsolatedCopy();
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var indexer = CreateIndexer();

            var (_, stderr, _) = ConsoleCapture.Run(() =>
            {
                IndexerFactory.RunAndReport(root, db, solutionPath: null);
                return 0;
            });
            var match = Regex.Match(stderr, @"^reindexed (\d+) files\r?\n$");
            Assert.True(match.Success, stderr);
            Assert.Equal(db.GetAllFiles().Count, int.Parse(match.Groups[1].Value));

            var untouched = Snapshot(db);
            var unchangedPath = "csharp/SampleLib/IPaymentProcessor.cs";
            Assert.Contains(unchangedPath, untouched.Keys);

            var addedRel = "csharp/SampleLib/AddedType.cs";
            var addedAbs = Path.Combine(root, "csharp", "SampleLib", "AddedType.cs");
            File.WriteAllText(addedAbs, """
                namespace SampleLib;

                public class AddedType
                {
                    public int AddedMethod() => 1;
                }
                """);

            var afterAdd = indexer.EnsureFresh(root, db);
            Assert.Equal(1, afterAdd.ReindexedFileCount);
            Assert.Empty(afterAdd.Warnings);
            Assert.Equal(untouched.Count + 1, db.GetAllFiles().Count);
            Assert.Contains(db.Outline(addedRel), s => s.Name == "AddedType" && s.FqName == "SampleLib.AddedType");
            AssertUntouched(db, untouched);

            var hashAfterAdd = db.GetAllFiles()[addedRel].Hash;
            File.AppendAllText(addedAbs, Environment.NewLine + "// touched" + Environment.NewLine);
            var afterChange = indexer.EnsureFresh(root, db);
            Assert.Equal(1, afterChange.ReindexedFileCount);
            Assert.Empty(afterChange.Warnings);
            Assert.Equal(untouched.Count + 1, db.GetAllFiles().Count);
            Assert.NotEqual(hashAfterAdd, db.GetAllFiles()[addedRel].Hash);
            Assert.Contains(db.Outline(addedRel), s => s.Name == "AddedMethod");
            AssertUntouched(db, untouched);

            File.Delete(addedAbs);
            var afterDelete = indexer.EnsureFresh(root, db);
            Assert.Equal(1, afterDelete.ReindexedFileCount);
            Assert.Empty(afterDelete.Warnings);
            Assert.Equal(untouched.Count, db.GetAllFiles().Count);
            Assert.False(db.GetAllFiles().ContainsKey(addedRel));
            Assert.Empty(db.Outline(addedRel));
            AssertUntouched(db, untouched);

            var oldRel = "csharp/SampleLib/PaymentProcessor.cs";
            var newRel = "csharp/SampleLib/RenamedProcessor.cs";
            File.Move(
                Path.Combine(root, "csharp", "SampleLib", "PaymentProcessor.cs"),
                Path.Combine(root, "csharp", "SampleLib", "RenamedProcessor.cs"));
            var renamedUntouched = untouched
                .Where(kv => kv.Key != oldRel)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var afterRename = indexer.EnsureFresh(root, db);
            Assert.Equal(2, afterRename.ReindexedFileCount);
            Assert.Empty(afterRename.Warnings);
            Assert.Equal(untouched.Count, db.GetAllFiles().Count);
            Assert.False(db.GetAllFiles().ContainsKey(oldRel));
            Assert.Empty(db.Outline(oldRel));
            Assert.Contains(db.Outline(newRel), s => s.Name == "PaymentProcessor" && s.FqName == "SampleLib.PaymentProcessor");
            AssertUntouched(db, renamedUntouched);
            var afterRenameSnapshot = Snapshot(db);

            var afterClean = indexer.EnsureFresh(root, db);
            Assert.Equal(0, afterClean.ReindexedFileCount);
            Assert.Empty(afterClean.Warnings);
            AssertUntouched(db, afterRenameSnapshot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, FileSnapshot> Snapshot(IndexDatabase db)
    {
        return db.GetAllFiles().ToDictionary(
            kv => kv.Key,
            kv => new FileSnapshot(kv.Value.Hash, kv.Value.IndexedAt, db.Outline(kv.Key).Select(s => s.Id).ToArray()));
    }

    private static void AssertUntouched(IndexDatabase db, IReadOnlyDictionary<string, FileSnapshot> before)
    {
        var current = db.GetAllFiles();
        foreach (var (path, snap) in before)
        {
            Assert.True(current.TryGetValue(path, out var row), path);
            Assert.Equal(snap.Hash, row.Hash);
            Assert.Equal(snap.IndexedAt, row.IndexedAt);
            Assert.Equal(snap.SymbolIds, db.Outline(path).Select(s => s.Id).ToArray());
        }
    }

    private sealed record FileSnapshot(string Hash, string IndexedAt, long[] SymbolIds);
}
