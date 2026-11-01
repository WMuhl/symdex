using System.Text.Json;
using Symdex.Cli.Commands;
using Symdex.Core;

namespace Symdex.Tests;

public sealed class SymbolResolutionRulesTests
{
    [Fact]
    public void Exact_fqname_wins_over_a_colliding_name()
    {
        var root = Directory.CreateTempSubdirectory("symdex-resolve-").FullName;
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var fileId = db.UpsertFile("a.cs", "hash", Languages.CSharp, "2026-01-01T00:00:00.0000000Z");
            db.ReplaceSymbolsAndRefs(fileId, [
                new SymbolRow("Widget", "Alpha.Widget", SymbolKinds.Class, null, null, 1, 4, null),
                new SymbolRow("Alpha.Widget", "Other.Alpha.Widget", SymbolKinds.Class, null, null, 6, 8, null),
                new SymbolRow("Widget", "Beta.Widget", SymbolKinds.Class, null, null, 10, 12, null),
                new SymbolRow("Helper", null, SymbolKinds.Function, null, null, 14, 16, null),
            ], []);

            Assert.Equal(["Alpha.Widget"], db.ResolveSymbolCandidates("Alpha.Widget"));
            var exact = SymbolResolution.Resolve(db, "Alpha.Widget");
            Assert.Equal(ResolveOutcome.Resolved, exact.Outcome);
            Assert.Equal("Alpha.Widget", exact.Resolved);

            var byName = SymbolResolution.Resolve(db, "Widget");
            Assert.Equal(ResolveOutcome.Ambiguous, byName.Outcome);
            Assert.Equal(["Alpha.Widget", "Beta.Widget"], byName.Candidates);
            var (stdout, stderr, code) = ConsoleCapture.Run(() =>
                SymbolResolution.ReportAmbiguous("Widget", byName.Candidates, json: false));
            Assert.Equal(2, code);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("ambiguous symbol 'Widget'", stderr);
            Assert.Contains("Alpha.Widget", stderr);
            Assert.Contains("Beta.Widget", stderr);

            var helper = SymbolResolution.Resolve(db, "Helper");
            Assert.Equal(ResolveOutcome.Resolved, helper.Outcome);
            Assert.Equal("Helper", helper.Resolved);

            var missing = SymbolResolution.Resolve(db, "DoesNotExist");
            Assert.Equal(ResolveOutcome.NotFound, missing.Outcome);
            Assert.Empty(missing.Candidates);
            Assert.Empty(db.RefsFor("DoesNotExist", kindFilter: null, 50));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

[Collection(SampleRepoCollection.Name)]
public sealed class SymbolResolutionCliTests : IClassFixture<AmbiguousRepoFixture>
{
    private readonly string _root;

    public SymbolResolutionCliTests(AmbiguousRepoFixture fixture) => _root = fixture.Root;

    [Fact]
    public void Ambiguous_short_name_exits_2_for_refs_and_callers()
    {
        var refs = ConsoleCapture.Run(() => RefsCommand.Run(_root, json: false, limit: 50, "Widget", solution: null));
        Assert.Equal(2, refs.ExitCode);
        Assert.Contains("ambiguous symbol 'Widget'", refs.Stderr);
        Assert.Contains("Alpha.Widget", refs.Stderr);
        Assert.Contains("Beta.Widget", refs.Stderr);

        var refsJson = ConsoleCapture.Run(() => RefsCommand.Run(_root, json: true, limit: 50, "Widget", solution: null));
        Assert.Equal(2, refsJson.ExitCode);
        using var doc = JsonDocument.Parse(refsJson.Stdout.Trim());
        Assert.True(doc.RootElement.GetProperty("ambiguous").GetBoolean());
        Assert.Equal("Widget", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal(
            ["Alpha.Widget", "Beta.Widget"],
            doc.RootElement.GetProperty("candidates").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var callers = ConsoleCapture.Run(() => CallersCommand.Run(_root, json: false, limit: 50, "Widget", solution: null));
        Assert.Equal(2, callers.ExitCode);
        Assert.Contains("ambiguous symbol 'Widget'", callers.Stderr);
    }

    [Fact]
    public void Exact_fqname_resolves_and_missing_symbol_is_empty()
    {
        var refs = ConsoleCapture.Run(() => RefsCommand.Run(_root, json: false, limit: 50, "Alpha.Widget", solution: null));
        Assert.Equal(0, refs.ExitCode);
        Assert.Contains("Alpha.Widget", refs.Stdout);
        Assert.DoesNotContain("ambiguous", refs.Stderr);

        var callers = ConsoleCapture.Run(() => CallersCommand.Run(_root, json: true, limit: 50, "Alpha.Widget", solution: null));
        Assert.Equal(0, callers.ExitCode);
        using (var doc = JsonDocument.Parse(callers.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0]))
        {
            Assert.Equal("Alpha.Widget", doc.RootElement.GetProperty("symbol").GetString());
        }

        var missingRefs = ConsoleCapture.Run(() => RefsCommand.Run(_root, json: false, limit: 50, "DoesNotExist", solution: null));
        Assert.Equal(0, missingRefs.ExitCode);
        Assert.Equal(string.Empty, missingRefs.Stdout.Trim());

        var missingCallers = ConsoleCapture.Run(() => CallersCommand.Run(_root, json: true, limit: 50, "DoesNotExist", solution: null));
        Assert.Equal(0, missingCallers.ExitCode);
        Assert.Equal(string.Empty, missingCallers.Stdout.Trim());
    }
}

public sealed class AmbiguousRepoFixture : IDisposable
{
    public string Root { get; }

    public AmbiguousRepoFixture(RestoredSampleRepoFixture sample)
    {
        Root = sample.CreateIsolatedCopy();
        var dir = Path.Combine(Root, "csharp", "SampleLib");
        File.WriteAllText(Path.Combine(dir, "AlphaWidget.cs"), """
            namespace Alpha;

            public class Widget
            {
                public void Go()
                {
                }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "BetaWidget.cs"), """
            namespace Beta;

            public class Widget
            {
                public void Go()
                {
                }
            }
            """);
    }

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
    }
}
