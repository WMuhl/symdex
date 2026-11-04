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

    [Fact]
    public void Find_escapes_like_wildcards_and_fts_operators_without_duplicating_rows()
    {
        var root = Directory.CreateTempSubdirectory("symdex-find-like-").FullName;
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var fileId = db.UpsertFile("a.ts", "hash", Languages.TypeScript, "2026-01-01T00:00:00.0000000Z");
            db.ReplaceSymbolsAndRefs(fileId, [
                new SymbolRow("_isoDuration", null, SymbolKinds.Function, null, "iso duration value", 1, 2, null),
                new SymbolRow("$ZodISODuration", null, SymbolKinds.Type, null, null, 3, 4, null),
                new SymbolRow("a%b", null, SymbolKinds.Function, null, null, 5, 6, null),
                new SymbolRow("axxxb", null, SymbolKinds.Function, null, null, 7, 8, null),
                new SymbolRow(@"a\b", null, SymbolKinds.Function, null, null, 9, 10, null),
                new SymbolRow("ab", null, SymbolKinds.Function, null, null, 11, 12, null),
                new SymbolRow("100%", null, SymbolKinds.Function, null, null, 13, 14, null),
                new SymbolRow("100", null, SymbolKinds.Function, null, null, 15, 16, null),
                new SymbolRow("star*", null, SymbolKinds.Function, null, null, 17, 18, null),
                new SymbolRow("star", null, SymbolKinds.Function, null, null, 19, 20, null),
                new SymbolRow("start", null, SymbolKinds.Function, null, null, 21, 22, null),
                new SymbolRow("a-b", null, SymbolKinds.Function, null, null, 23, 24, null),
                new SymbolRow("-bar", null, SymbolKinds.Function, null, null, 25, 26, null),
                new SymbolRow("bar", null, SymbolKinds.Function, null, null, 27, 28, null),
                new SymbolRow("name:foo", null, SymbolKinds.Function, null, null, 29, 30, null),
                new SymbolRow("foo", null, SymbolKinds.Function, null, "unrelated doc", 31, 32, null),
                new SymbolRow("(parens)", null, SymbolKinds.Function, null, null, 33, 34, null),
                new SymbolRow("say \"hi\"", null, SymbolKinds.Function, null, "say hi there", 35, 36, null),
                new SymbolRow("typed_helper", null, SymbolKinds.Function, null, "helper docs", 37, 38, null),
                new SymbolRow("plain", null, SymbolKinds.Function, null, null, 39, 40, null),
            ], []);

            Assert.Equal(["_isoDuration"], Names(db.Find("_isoDuration", null, 50)));
            Assert.Equal(["_isoDuration"], Names(db.Find("_iso", null, 50)));
            Assert.DoesNotContain(Names(db.Find("_isoDuration", null, 50)), name => name == "$ZodISODuration");

            Assert.Equal(["a%b"], Names(db.Find("a%b", null, 50)));
            Assert.Equal(["100%"], Names(db.Find("100%", null, 50)));
            Assert.Equal(["100%", "a%b"], Names(db.Find("%", null, 50)));
            Assert.DoesNotContain("axxxb", Names(db.Find("a%b", null, 50)));
            Assert.DoesNotContain("ab", Names(db.Find("a%b", null, 50)));

            Assert.Equal([@"a\b"], Names(db.Find(@"a\b", null, 50)));
            Assert.DoesNotContain("ab", Names(db.Find(@"a\b", null, 50)));

            Assert.Equal(["star*"], Names(db.Find("star*", null, 50)));
            Assert.Equal(["a-b"], Names(db.Find("a-b", null, 50)));
            Assert.Equal(["-bar"], Names(db.Find("-bar", null, 50)));
            Assert.DoesNotContain("plain", Names(db.Find("-bar", null, 50)));
            Assert.Equal(["name:foo"], Names(db.Find("name:foo", null, 50)));
            Assert.DoesNotContain("foo", Names(db.Find("name:foo", null, 50)));
            Assert.Equal(["(parens)"], Names(db.Find("(parens)", null, 50)));
            Assert.Equal(["say \"hi\""], Names(db.Find("say \"hi\"", null, 50)));

            var both = db.Find("helper", null, 50);
            var helper = Assert.Single(both);
            Assert.Equal("typed_helper", helper.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string[] Names(IReadOnlyList<SymbolRecord> symbols) =>
        symbols.Select(s => s.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
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

        var underscore = ConsoleCapture.Run(() =>
            FindCommand.Run(root, json: false, limit: 50, "_processor", kind: null, solution: null));
        Assert.Equal(0, underscore.ExitCode);
        var underscoreLines = Lines(underscore.Stdout);
        Assert.NotEmpty(underscoreLines);
        Assert.All(underscoreLines, line => Assert.Contains("_processor", line));

        var percent = ConsoleCapture.Run(() =>
            FindCommand.Run(root, json: false, limit: 50, "a%b", kind: null, solution: null));
        Assert.Equal(0, percent.ExitCode);
        Assert.Equal(string.Empty, percent.Stdout.Trim());
    }

    private static string[] Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
