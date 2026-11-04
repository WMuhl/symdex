using System.Runtime.CompilerServices;
using System.Text.Json;
using Symdex.Cli.Commands;

namespace Symdex.Tests;

[Collection(SampleRepoCollection.Name)]
public sealed class JsonOutputTests(RestoredSampleRepoFixture fixture)
{
    [Fact]
    public void Outline_json_lists_kind_name_and_line_spans_in_source_order()
    {
        var root = fixture.Root;
        var file = Path.Combine(root, "csharp", "SampleLib", "PaymentProcessor.cs");
        var run = ConsoleCapture.Run(() => OutlineCommand.Run(root, json: true, limit: 50, file, solution: null));
        Assert.Equal(0, run.ExitCode);

        var rows = Parse(run.Stdout);
        Assert.Equal(["class", "method"], rows.Select(r => r.GetProperty("kind").GetString()!).ToArray());
        Assert.Equal(["PaymentProcessor", "Charge"], rows.Select(r => r.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(4, rows[0].GetProperty("startLine").GetInt32());
        Assert.Equal(11, rows[0].GetProperty("endLine").GetInt32());
        Assert.Equal(7, rows[1].GetProperty("startLine").GetInt32());
        Assert.Equal(10, rows[1].GetProperty("endLine").GetInt32());
        AssertGolden(run.Stdout, "outline-payment-processor.jsonl");
    }

    [Fact]
    public void Find_json_lists_kind_fqname_path_and_spans_in_path_order()
    {
        var root = fixture.Root;
        var run = ConsoleCapture.Run(() =>
            FindCommand.Run(root, json: true, limit: 50, "Charge", kind: "method", solution: null));
        Assert.Equal(0, run.ExitCode);

        var rows = Parse(run.Stdout);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("method", row.GetProperty("kind").GetString()));
        Assert.Equal(
            ["csharp/SampleLib/IPaymentProcessor.cs", "csharp/SampleLib/PaymentProcessor.cs"],
            rows.Select(r => r.GetProperty("filePath").GetString()!).ToArray());
        Assert.All(rows, row =>
        {
            Assert.Contains("Charge", row.GetProperty("fqName").GetString());
            Assert.True(row.GetProperty("startLine").GetInt32() >= 1);
            Assert.True(row.GetProperty("endLine").GetInt32() >= row.GetProperty("startLine").GetInt32());
        });
        AssertGolden(run.Stdout, "find-charge-methods.jsonl");
    }

    [Fact]
    public void Refs_json_lists_symbol_then_sites_with_kind_path_and_line()
    {
        var root = fixture.Root;
        var run = ConsoleCapture.Run(() =>
            RefsCommand.Run(root, json: true, limit: 50, "SampleLib.IPaymentProcessor", solution: null));
        Assert.Equal(0, run.ExitCode);

        var rows = Parse(run.Stdout);
        Assert.Equal("SampleLib.IPaymentProcessor", rows[0].GetProperty("symbol").GetString());
        var sites = rows.Skip(1).ToList();
        Assert.NotEmpty(sites);
        Assert.All(sites, row =>
        {
            Assert.False(string.IsNullOrEmpty(row.GetProperty("kind").GetString()));
            Assert.EndsWith(".cs", row.GetProperty("filePath").GetString());
            Assert.True(row.GetProperty("line").GetInt32() >= 1);
        });
        var paths = sites.Select(r => r.GetProperty("filePath").GetString()).ToArray();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToArray(), paths);
        AssertGolden(run.Stdout, "refs-ipaymentprocessor.jsonl");
    }

    [Fact]
    public void Callers_json_lists_symbol_then_call_sites()
    {
        var root = fixture.Root;
        var run = ConsoleCapture.Run(() =>
            CallersCommand.Run(root, json: true, limit: 50, "System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)", solution: null));
        Assert.Equal(0, run.ExitCode);

        var rows = Parse(run.Stdout);
        Assert.Equal("System.Decimal SampleLib.IPaymentProcessor.Charge(System.Decimal)", rows[0].GetProperty("symbol").GetString());
        var sites = rows.Skip(1).ToList();
        Assert.Contains(sites, row =>
            row.GetProperty("kind").GetString() == "call" &&
            row.GetProperty("filePath").GetString() == "csharp/SampleLib/CheckoutService.cs");
        AssertGolden(run.Stdout, "callers-charge.jsonl");
    }

    [Fact]
    public void Rebuild_json_reports_reindexed_file_count()
    {
        var root = fixture.CreateIsolatedCopy();
        try
        {
            var run = ConsoleCapture.Run(() => RebuildCommand.Run(root, json: true, solution: null));
            Assert.Equal(0, run.ExitCode);
            var rows = Parse(run.Stdout);
            var count = rows[0].GetProperty("reindexed").GetInt32();
            Assert.True(count >= 5);
            AssertGolden(run.Stdout, "rebuild.jsonl");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static List<JsonElement> Parse(string stdout)
    {
        var rows = new List<JsonElement>();
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            rows.Add(doc.RootElement.Clone());
        }

        return rows;
    }

    private static void AssertGolden(string actual, string name, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "Goldens", name);
        var normalized = actual.Replace("\r\n", "\n");
        if (!normalized.EndsWith('\n'))
        {
            normalized += "\n";
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected, normalized);
    }
}
