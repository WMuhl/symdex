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

    [Fact]
    public void Qualified_csharp_name_matches_on_a_dotted_suffix_and_optional_parameters()
    {
        const string iface = "System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)";
        const string impl = "System.Decimal SampleLib.PaymentProcessor.Charge(System.Decimal)";
        const string per = "Humanizer.ByteRate Humanizer.ByteSizeExtensions.Per(Humanizer.ByteSize, System.TimeSpan)";
        const string create =
            "(T Zero, System.Collections.Frozen.FrozenDictionary<T, System.String> Humanized) Humanizer.EnumCache<T>.CreateInfo()";

        var root = Directory.CreateTempSubdirectory("symdex-qualified-").FullName;
        try
        {
            using var db = IndexDatabase.OpenOrCreate(root);
            var fileId = db.UpsertFile("a.cs", "hash", Languages.CSharp, "2026-01-01T00:00:00.0000000Z");
            db.ReplaceSymbolsAndRefs(fileId, [
                new SymbolRow("Charge", iface, SymbolKinds.Method, null, null, 1, 2, "IPaymentProcessor"),
                new SymbolRow("Charge", impl, SymbolKinds.Method, null, null, 4, 8, "PaymentProcessor"),
                new SymbolRow("Bar", "void Ns.Foo.Bar()", SymbolKinds.Method, null, null, 10, 11, "Foo"),
                new SymbolRow("Bar", "void Ns.Foo.Bar(System.Int32)", SymbolKinds.Method, null, null, 12, 13, "Foo"),
                new SymbolRow("Bar", "void Ns.Foo.Bar(System.String)", SymbolKinds.Method, null, null, 14, 15, "Foo"),
                new SymbolRow("Bar", "System.Int32 Ns.Foo.Bar", SymbolKinds.Property, null, null, 16, 16, "Foo"),
                new SymbolRow("Per", per, SymbolKinds.Method, null, null, 18, 19, "ByteSizeExtensions"),
                new SymbolRow("CreateInfo", create, SymbolKinds.Method, null, null, 21, 22, "EnumCache"),
                new SymbolRow("Echo", "T Ns.Foo.Echo<T>(T)", SymbolKinds.Method, null, null, 24, 25, "Foo"),
                new SymbolRow("Items", "void Ns.Foo.Items(System.String[])", SymbolKinds.Method, null, null, 27, 28, "Foo"),
                new SymbolRow("Parse", "void Ns.Foo.Parse(System.Int32)", SymbolKinds.Method, null, null, 30, 31, "Foo"),
                new SymbolRow(".ctor", "SampleLib.CheckoutService.CheckoutService(SampleLib.IPaymentProcessor)", SymbolKinds.Method, null, null, 33, 36, "CheckoutService"),
                new SymbolRow("CheckoutService", "SampleLib.CheckoutService", SymbolKinds.Class, null, null, 32, 40, null),
                new SymbolRow("Helper", null, SymbolKinds.Function, null, null, 42, 43, null),
                new SymbolRow("Charge", null, SymbolKinds.Function, null, null, 45, 46, null),
            ], []);

            Assert.Equal([iface], db.ResolveSymbolCandidates(iface));
            Assert.Equal([iface], db.ResolveSymbolCandidates("SampleLib.IPaymentProcessor.Charge"));
            Assert.Equal([iface], db.ResolveSymbolCandidates("IPaymentProcessor.Charge"));
            Assert.Equal([iface], db.ResolveSymbolCandidates("IPaymentProcessor.Charge(decimal)"));
            Assert.Equal([iface], db.ResolveSymbolCandidates("IPaymentProcessor.Charge(System.Decimal)"));
            Assert.Equal([impl], db.ResolveSymbolCandidates("PaymentProcessor.Charge"));
            Assert.Equal([impl], db.ResolveSymbolCandidates("PaymentProcessor.Charge(decimal)"));
            Assert.Equal([impl], db.ResolveSymbolCandidates("PaymentProcessor.Charge(decimal amount)"));
            Assert.Equal([impl], db.ResolveSymbolCandidates("PaymentProcessor.Charge(Decimal)"));
            Assert.Empty(db.ResolveSymbolCandidates("Processor.Charge"));
            Assert.Empty(db.ResolveSymbolCandidates("XIPaymentProcessor.Charge"));

            var charge = db.ResolveSymbolCandidates("Charge");
            Assert.Equal([
                "Charge",
                iface,
                impl,
            ], charge);
            foreach (var candidate in charge.Where(candidate => candidate.Contains('(', StringComparison.Ordinal)))
            {
                Assert.Equal([candidate], db.ResolveSymbolCandidates(candidate));
            }

            Assert.Equal([
                "System.Int32 Ns.Foo.Bar",
                "void Ns.Foo.Bar()",
                "void Ns.Foo.Bar(System.Int32)",
                "void Ns.Foo.Bar(System.String)",
            ], db.ResolveSymbolCandidates("Foo.Bar"));
            Assert.Equal(["void Ns.Foo.Bar()"], db.ResolveSymbolCandidates("Foo.Bar()"));
            Assert.Equal(["void Ns.Foo.Bar(System.Int32)"], db.ResolveSymbolCandidates("Foo.Bar(int)"));
            Assert.Equal(["void Ns.Foo.Bar(System.String)"], db.ResolveSymbolCandidates("Bar(string)"));
            Assert.Equal(["void Ns.Foo.Bar(System.Int32)"], db.ResolveSymbolCandidates("Foo.Bar(out int value)"));
            Assert.Empty(db.ResolveSymbolCandidates("Foo.Bar(bool)"));

            Assert.Equal([per], db.ResolveSymbolCandidates("ByteSizeExtensions.Per(ByteSize, TimeSpan)"));
            Assert.Equal([per], db.ResolveSymbolCandidates("ByteSizeExtensions.Per"));
            Assert.Empty(db.ResolveSymbolCandidates("Per(List<string>)"));
            Assert.Empty(db.ResolveSymbolCandidates("ByteSizeExtensions.Per(ByteSize)"));
            Assert.Equal(
                [per],
                db.ResolveSymbolCandidates("ByteSizeExtensions.Per(Humanizer.ByteSize, System.TimeSpan)"));

            Assert.Equal([create], db.ResolveSymbolCandidates("EnumCache<T>.CreateInfo"));
            Assert.Equal([create], db.ResolveSymbolCandidates("EnumCache<T>.CreateInfo()"));
            Assert.Empty(db.ResolveSymbolCandidates("Cache<T>.CreateInfo"));

            Assert.Equal(["T Ns.Foo.Echo<T>(T)"], db.ResolveSymbolCandidates("Foo.Echo<T>"));
            Assert.Equal(["T Ns.Foo.Echo<T>(T)"], db.ResolveSymbolCandidates("Echo<T>(T)"));
            Assert.Equal(["void Ns.Foo.Items(System.String[])"], db.ResolveSymbolCandidates("Foo.Items(string[])"));
            Assert.Equal(["void Ns.Foo.Parse(System.Int32)"], db.ResolveSymbolCandidates("Foo.Parse(int)"));
            Assert.Empty(db.ResolveSymbolCandidates("Foo.Parse(int?)"));

            Assert.Equal(
                ["SampleLib.CheckoutService.CheckoutService(SampleLib.IPaymentProcessor)"],
                db.ResolveSymbolCandidates("CheckoutService.CheckoutService"));
            Assert.Equal(
                ["SampleLib.CheckoutService.CheckoutService(SampleLib.IPaymentProcessor)"],
                db.ResolveSymbolCandidates("CheckoutService.CheckoutService(IPaymentProcessor)"));
            Assert.Equal(["SampleLib.CheckoutService"], db.ResolveSymbolCandidates("CheckoutService"));

            Assert.Equal(["Helper"], db.ResolveSymbolCandidates("Helper"));
            Assert.Empty(db.ResolveSymbolCandidates("Ns.Helper"));
            Assert.DoesNotContain("Charge", db.ResolveSymbolCandidates("IPaymentProcessor.Charge"));

            Assert.Equal("Charge", SymbolNames.FindHintSegment("PaymentProcessor.Charge(decimal)"));
            Assert.Equal("CreateInfo", SymbolNames.FindHintSegment("EnumCache<T>.CreateInfo()"));
            Assert.Equal("DoesNotExist", SymbolNames.FindHintSegment("DoesNotExist"));

            var hinted = ConsoleCapture.Run(() => SymbolResolution.ReportNotFound("Processor.Charge"));
            Assert.Equal(0, hinted.ExitCode);
            Assert.Equal(string.Empty, hinted.Stdout.Trim());
            Assert.Contains("symdex find Charge", hinted.Stderr);
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
        Assert.Contains("symdex find DoesNotExist", missingRefs.Stderr);

        var missingCallers = ConsoleCapture.Run(() => CallersCommand.Run(_root, json: true, limit: 50, "DoesNotExist", solution: null));
        Assert.Equal(0, missingCallers.ExitCode);
        Assert.Equal(string.Empty, missingCallers.Stdout.Trim());
        Assert.Contains("symdex find DoesNotExist", missingCallers.Stderr);
    }

    [Fact]
    public void Qualified_method_names_resolve_and_candidates_round_trip()
    {
        var iface = ConsoleCapture.Run(() =>
            CallersCommand.Run(_root, json: false, limit: 50, "IPaymentProcessor.Charge", solution: null));
        Assert.Equal(0, iface.ExitCode);
        Assert.Contains("System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)", iface.Stdout);
        Assert.Contains("csharp/SampleLib/CheckoutService.cs", iface.Stdout);
        Assert.DoesNotContain("ambiguous", iface.Stderr);

        var withNamespace = ConsoleCapture.Run(() =>
            CallersCommand.Run(_root, json: false, limit: 50, "SampleLib.IPaymentProcessor.Charge", solution: null));
        Assert.Equal(0, withNamespace.ExitCode);
        Assert.Contains("System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)", withNamespace.Stdout);

        var impl = ConsoleCapture.Run(() =>
            RefsCommand.Run(_root, json: false, limit: 50, "PaymentProcessor.Charge(decimal)", solution: null));
        Assert.Equal(0, impl.ExitCode);
        Assert.Contains("System.Decimal SampleLib.PaymentProcessor.Charge(System.Decimal)", impl.Stdout);
        Assert.DoesNotContain("IPaymentProcessor", impl.Stdout);

        var boundary = ConsoleCapture.Run(() =>
            CallersCommand.Run(_root, json: false, limit: 50, "Processor.Charge", solution: null));
        Assert.Equal(0, boundary.ExitCode);
        Assert.Equal(string.Empty, boundary.Stdout.Trim());
        Assert.Contains("symdex find Charge", boundary.Stderr);

        var ambiguous = ConsoleCapture.Run(() =>
            CallersCommand.Run(_root, json: true, limit: 50, "Charge", solution: null));
        Assert.Equal(2, ambiguous.ExitCode);
        using var doc = JsonDocument.Parse(ambiguous.Stdout.Trim());
        var candidates = doc.RootElement.GetProperty("candidates").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(2, candidates.Length);
        foreach (var candidate in candidates)
        {
            Assert.Contains("(System.Decimal)", candidate);
            var again = ConsoleCapture.Run(() =>
                CallersCommand.Run(_root, json: false, limit: 50, candidate, solution: null));
            Assert.Equal(0, again.ExitCode);
            Assert.StartsWith(candidate, again.Stdout.Trim());
            Assert.DoesNotContain("ambiguous", again.Stderr);
        }
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
