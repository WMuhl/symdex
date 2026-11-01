using Symdex.Cli.Commands;
using Symdex.Core;

namespace Symdex.Tests;

public sealed class FindQueryTests
{
    [Fact]
    public void Find_unions_like_substring_with_fts_prefix_and_filters_kind()
    {
        var root = Directory.CreateTempSubdirectory("symdex-find-").FullName;
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var fileId = db.UpsertFile("a.cs", "hash", Languages.CSharp, "2026-01-01T00:00:00.0000000Z");
            db.ReplaceSymbolsAndRefs(fileId, [
                new SymbolRow("PaymentProcessor", "Sample.PaymentProcessor", SymbolKinds.Class, null, "pays", 1, 10, null),
                new SymbolRow("Charge", "Sample.Charger.Charge(decimal)", SymbolKinds.Method, "public decimal Charge(decimal amount)", null, 4, 6, "Charger"),
                new SymbolRow("Xyzz", null, SymbolKinds.Function, null, "zzzdocusage", 12, 14, null),
                new SymbolRow("Foo.Bar", "Ns.Foo.Bar", SymbolKinds.Class, null, null, 16, 18, null),
                new SymbolRow("say \"hi\"", null, SymbolKinds.Function, null, null, 20, 21, null),
            ], []);

            var substring = db.Find("aymentP", kind: null, limit: 50);
            Assert.Equal(["PaymentProcessor"], substring.Select(s => s.Name).ToArray());

            var ftsPrefix = db.Find("zzzdoc", kind: null, limit: 50);
            var ftsHit = Assert.Single(ftsPrefix);
            Assert.Equal("Xyzz", ftsHit.Name);

            var methods = db.Find("Charge", SymbolKinds.Method, 50);
            Assert.Equal(["Charge"], methods.Select(s => s.Name).ToArray());
            Assert.All(methods, s => Assert.Equal(SymbolKinds.Method, s.Kind));

            Assert.Empty(db.Find("Charge", SymbolKinds.Class, 50));

            var classes = db.Find("Pay", SymbolKinds.Class, 50);
            Assert.Equal(["PaymentProcessor"], classes.Select(s => s.Name).ToArray());

            var dotted = db.Find("Foo.Bar", kind: null, limit: 50);
            Assert.Contains(dotted, s => s.Name == "Foo.Bar" && s.FqName == "Ns.Foo.Bar");

            var quoted = db.Find("say \"hi\"", kind: null, limit: 50);
            Assert.Contains(quoted, s => s.Name == "say \"hi\"");

            Assert.Empty(db.Find("no-such-symbol", kind: null, limit: 50));
            Assert.Empty(db.Find("Charge", "not-a-kind", 50));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

[Collection(SampleRepoCollection.Name)]
public sealed class FindCommandTests(RestoredSampleRepoFixture fixture)
{
    [Fact]
    public void Kind_filter_and_punctuation_do_not_throw()
    {
        var root = fixture.Root;

        var methods = ConsoleCapture.Run(() =>
            FindCommand.Run(root, json: true, limit: 50, "Charge", SymbolKinds.Method, solution: null));
        Assert.Equal(0, methods.ExitCode);
        var methodLines = Lines(methods.Stdout);
        Assert.NotEmpty(methodLines);
        Assert.All(methodLines, line =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal(SymbolKinds.Method, doc.RootElement.GetProperty("kind").GetString());
        });

        var classes = ConsoleCapture.Run(() =>
            FindCommand.Run(root, json: false, limit: 50, "Charge", SymbolKinds.Class, solution: null));
        Assert.Equal(0, classes.ExitCode);
        Assert.Equal(string.Empty, classes.Stdout.Trim());

        var dotted = ConsoleCapture.Run(() =>
            FindCommand.Run(root, json: false, limit: 50, "Foo.Bar", kind: null, solution: null));
        Assert.Equal(0, dotted.ExitCode);

        var quoted = ConsoleCapture.Run(() =>
            FindCommand.Run(root, json: false, limit: 50, "say \"hi\"", kind: null, solution: null));
        Assert.Equal(0, quoted.ExitCode);
    }

    private static string[] Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
