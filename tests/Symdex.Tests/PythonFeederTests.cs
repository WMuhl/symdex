using Symdex.Core;
using Symdex.Core.Feeding;
using Symdex.TreeSitter;

namespace Symdex.Tests;

public sealed class PythonFeederTests : IDisposable
{
    private readonly string _tempDir;

    public PythonFeederTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("symdex-py-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Feed_extracts_class_method_function_assignment_and_docstring()
    {
        const string source =
            "PI = 3\n" +
            "\n" +
            "class Widget:\n" +
            "    \"\"\"A widget.\"\"\"\n" +
            "\n" +
            "    scale = 1\n" +
            "\n" +
            "    @staticmethod\n" +
            "    def compute(value):\n" +
            "        \"\"\"Compute it.\"\"\"\n" +
            "        return value\n" +
            "\n" +
            "def helper():\n" +
            "    return 1\n";

        var (feeder, file) = WriteAndScan("widget.py", source);
        var result = feeder.Feed(new FeedRequest(_tempDir, [file]));

        Assert.True(result.Success);
        var symbols = result.ResultsByFile[file.RepoRelativePath].Symbols;

        var constant = Assert.Single(symbols, s => s.Name == "PI");
        Assert.Equal(SymbolKinds.Export, constant.Kind);
        Assert.Equal(1, constant.StartLine);
        Assert.Null(constant.Container);

        var cls = Assert.Single(symbols, s => s.Kind == SymbolKinds.Class);
        Assert.Equal("Widget", cls.Name);
        Assert.Equal(3, cls.StartLine);
        Assert.InRange(cls.EndLine, 11, 12);
        Assert.Equal("A widget.", cls.Doc);

        var field = Assert.Single(symbols, s => s.Name == "scale");
        Assert.Equal(SymbolKinds.Field, field.Kind);
        Assert.Equal("Widget", field.Container);
        Assert.Equal(6, field.StartLine);

        var method = Assert.Single(symbols, s => s.Name == "compute");
        Assert.Equal(SymbolKinds.Method, method.Kind);
        Assert.Equal("Widget", method.Container);
        Assert.Equal(8, method.StartLine);
        Assert.InRange(method.EndLine, 11, 12);
        Assert.Equal("Compute it.", method.Doc);
        Assert.Contains("@staticmethod", method.Signature);
        Assert.Contains("def compute(value):", method.Signature);
        Assert.DoesNotContain("return", method.Signature);

        var fn = Assert.Single(symbols, s => s.Name == "helper");
        Assert.Equal(SymbolKinds.Function, fn.Kind);
        Assert.Null(fn.Container);
        Assert.Equal(13, fn.StartLine);
    }

    [Fact]
    public void Feed_records_import_call_and_attribute_call()
    {
        const string source =
            "from app import helper\n" +
            "\n" +
            "def run(helper):\n" +
            "    return helper() + Widget.add(1)\n";

        var (feeder, file) = WriteAndScan("run.py", source);
        var result = feeder.Feed(new FeedRequest(_tempDir, [file]));
        var refs = result.ResultsByFile[file.RepoRelativePath].Refs;

        Assert.Contains(refs, r => r.SymbolFqName == "helper" && r.Kind == RefKinds.Reference && r.Line == 1);
        Assert.Contains(refs, r => r.SymbolFqName == "app" && r.Kind == RefKinds.Reference && r.Line == 1);
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "helper" && r.Line == 3);
        Assert.Single(refs, r => r.SymbolFqName == "helper" && r.Kind == RefKinds.Call && r.Line == 4);
        Assert.Single(refs, r => r.SymbolFqName == "add" && r.Kind == RefKinds.Call && r.Line == 4);
        Assert.Contains(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Reference && r.Line == 4);
    }

    [Fact]
    public void Feed_extracts_a_function_nested_under_a_statement()
    {
        const string source = "if flag:\n    def gated():\n        return 2\n";

        var (feeder, file) = WriteAndScan("gated.py", source);
        var result = feeder.Feed(new FeedRequest(_tempDir, [file]));
        var symbols = result.ResultsByFile[file.RepoRelativePath].Symbols;

        var fn = Assert.Single(symbols, s => s.Name == "gated");
        Assert.Equal(SymbolKinds.Function, fn.Kind);
        Assert.Equal(2, fn.StartLine);
        Assert.DoesNotContain("return", fn.Signature ?? string.Empty);
    }

    private (TreeSitterFeeder Feeder, ScannedFile File) WriteAndScan(string relativeName, string source)
    {
        var path = Path.Combine(_tempDir, relativeName);
        File.WriteAllText(path, source);
        var lang = RepoScanner.LanguageForPath(relativeName)!;
        return (new TreeSitterFeeder(), new ScannedFile(relativeName, path, lang));
    }
}
