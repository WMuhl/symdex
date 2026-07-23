using Symdex.Core;
using Symdex.Core.Feeding;
using Symdex.TreeSitter;

namespace Symdex.Tests;

public sealed class TreeSitterFeederTests : IDisposable
{
    private readonly string _tempDir;

    public TreeSitterFeederTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("symdex-ts-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Feed_extracts_class_and_method_from_csharp_file()
    {
        const string source = """
            namespace Demo;

            /// Represents a widget.
            public class Widget
            {
                /// Computes the answer.
                public int Compute()
                {
                    return 42;
                }
            }
            """;

        var (feeder, file) = WriteAndScan("Widget.cs", source);
        var result = feeder.Feed(new FeedRequest(_tempDir, [file]));

        Assert.True(result.Success);
        var symbols = result.ResultsByFile[file.RepoRelativePath].Symbols;

        var cls = Assert.Single(symbols, s => s.Kind == SymbolKinds.Class);
        Assert.Equal("Widget", cls.Name);
        Assert.Equal("Represents a widget.", cls.Doc);

        var method = Assert.Single(symbols, s => s.Kind == SymbolKinds.Method);
        Assert.Equal("Compute", method.Name);
        Assert.Equal("Widget", method.Container);
        Assert.Equal("Computes the answer.", method.Doc);
    }

    [Fact]
    public void Feed_extracts_using_directive_as_reference()
    {
        const string source = """
            using System.Collections.Generic;

            namespace Demo;

            public class Widget {}
            """;

        var (feeder, file) = WriteAndScan("Widget2.cs", source);
        var result = feeder.Feed(new FeedRequest(_tempDir, [file]));

        var refs = result.ResultsByFile[file.RepoRelativePath].Refs;
        Assert.Contains(refs, r => r.SymbolFqName == "System.Collections.Generic" && r.Kind == RefKinds.Reference);
    }

    [Fact]
    public void Feed_extracts_typescript_function_and_import()
    {
        const string source = """
            import { helper } from "./helper";

            /** Adds two numbers. */
            export function add(a: number, b: number): number {
                return a + b + helper();
            }
            """;

        var (feeder, file) = WriteAndScan("math.ts", source);
        var result = feeder.Feed(new FeedRequest(_tempDir, [file]));

        Assert.True(result.Success);
        var fileResult = result.ResultsByFile[file.RepoRelativePath];

        var fn = Assert.Single(fileResult.Symbols, s => s.Kind == SymbolKinds.Function);
        Assert.Equal("add", fn.Name);

        Assert.Contains(fileResult.Refs, r => r.SymbolFqName == "./helper");
    }

    private (TreeSitterFeeder Feeder, ScannedFile File) WriteAndScan(string relativeName, string source)
    {
        var path = Path.Combine(_tempDir, relativeName);
        File.WriteAllText(path, source);
        var lang = RepoScanner.LanguageForPath(relativeName)!;
        return (new TreeSitterFeeder(), new ScannedFile(relativeName, path, lang));
    }
}
