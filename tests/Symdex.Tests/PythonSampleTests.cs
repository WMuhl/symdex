using Symdex.Core;
using Symdex.Roslyn;
using Symdex.TreeSitter;

namespace Symdex.Tests;

public sealed class PythonSampleTests : IDisposable
{
    private static readonly string[] JunkDirs = ["__pycache__", ".venv", "venv", ".tox", ".mypy_cache", "site-packages"];

    private readonly string _repoRoot;

    public PythonSampleTests()
    {
        _repoRoot = TestUtil.CopyFixture("python-sample");
        foreach (var dir in JunkDirs)
        {
            var path = Path.Combine(_repoRoot, dir);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "junk.py"), "def junk():\n    return 0\n");
        }
    }

    public void Dispose()
    {
        Directory.Delete(_repoRoot, recursive: true);
    }

    private static Indexer CreateIndexer() => new([new RoslynFeeder(), new TreeSitterFeeder()]);

    [Fact]
    public void Outline_returns_class_method_function_with_spans()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var outline = db.Outline("calc.py");

        var cls = Assert.Single(outline, s => s.Kind == SymbolKinds.Class && s.Name == "Calculator");
        Assert.Equal(5, cls.StartLine);
        Assert.Equal(16, cls.EndLine);
        Assert.Equal("A small calculator.", cls.Doc);

        var method = Assert.Single(outline, s => s.Kind == SymbolKinds.Method && s.Name == "add");
        Assert.Equal("Calculator", method.Container);
        Assert.Equal(13, method.StartLine);
        Assert.Equal(16, method.EndLine);
        Assert.Equal("Add two numbers.", method.Doc);
        Assert.Contains("def add(a, b):", method.Signature);
        Assert.DoesNotContain("return", method.Signature ?? string.Empty);

        var fn = Assert.Single(outline, s => s.Kind == SymbolKinds.Function && s.Name == "helper");
        Assert.Null(fn.Container);
        Assert.Equal(19, fn.StartLine);
        Assert.Equal(21, fn.EndLine);
        Assert.Equal("A constant offset.", fn.Doc);

        var constant = Assert.Single(outline, s => s.Name == "PI");
        Assert.Equal(SymbolKinds.Export, constant.Kind);
        Assert.Equal(3, constant.StartLine);

        Assert.Contains(outline, s => s.Kind == SymbolKinds.Method && s.Name == "__init__" && s.Container == "Calculator");
        Assert.Contains(outline, s => s.Kind == SymbolKinds.Field && s.Name == "precision" && s.Container == "Calculator");
    }

    [Fact]
    public void Find_locates_functions_in_py_and_pyi_files()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var found = db.Find("helper", null, 50);

        Assert.Contains(found, s => s.Name == "helper" && s.FilePath == "calc.py");
        Assert.Contains(found, s => s.Name == "typed_helper" && s.FilePath == "stubs.pyi" && s.Kind == SymbolKinds.Function);
    }

    [Fact]
    public void Refs_and_callers_find_a_function_called_from_another_file()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        CreateIndexer().EnsureFresh(_repoRoot, db);

        var resolved = Assert.Single(db.ResolveSymbolCandidates("helper"));
        var refs = db.RefsFor(resolved, kindFilter: null, 50);

        Assert.Contains(refs, r => r.FilePath == "app.py" && r.Kind == RefKinds.Call && r.Line == 5);
        Assert.Contains(refs, r => r.FilePath == "calc.py" && r.Kind == RefKinds.Call);

        var callers = db.RefsFor(resolved, [RefKinds.Call, RefKinds.Reference], 50);
        Assert.Contains(callers, r => r.FilePath == "app.py" && r.Line == 5);
    }

    [Fact]
    public void Editing_one_py_file_reindexes_only_that_file()
    {
        using var db = IndexDatabase.OpenOrCreate(_repoRoot);
        var first = CreateIndexer().EnsureFresh(_repoRoot, db);
        Assert.Equal(3, first.ReindexedFileCount);

        var indexed = db.GetAllFiles().Keys.OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(["app.py", "calc.py", "stubs.pyi"], indexed);

        File.AppendAllText(Path.Combine(_repoRoot, "calc.py"), "\n# touched\n");
        var second = CreateIndexer().EnsureFresh(_repoRoot, db);

        Assert.Equal(1, second.ReindexedFileCount);
        Assert.Contains(db.Outline("calc.py"), s => s.Name == "Calculator");
    }
}
