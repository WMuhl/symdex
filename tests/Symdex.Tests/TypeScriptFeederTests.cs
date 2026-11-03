using Symdex.Core;
using Symdex.Core.Feeding;
using Symdex.TreeSitter;

namespace Symdex.Tests;

public sealed class TypeScriptFeederTests : IDisposable
{
    private readonly string _tempDir;

    public TypeScriptFeederTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("symdex-ts-refs-").FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Feed_records_call_member_call_new_and_optional_call()
    {
        const string source = """
            export function run(obj: Box): void {
                helper();
                obj.method();
                obj?.method();
                new Widget();
                new obj.Widget();
                tag`hello ${obj}`;
            }
            """;

        var refs = Feed("calls.ts", source);

        Assert.Single(refs, r => r.SymbolFqName == "helper" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "helper()"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "helper" && r.Kind == RefKinds.Reference);
        Assert.Single(refs, r => r.SymbolFqName == "method" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "obj.method()"));
        Assert.Single(refs, r => r.SymbolFqName == "method" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "obj?.method()"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "method" && r.Kind == RefKinds.Reference);
        Assert.Single(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "new Widget()"));
        Assert.Single(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "new obj.Widget()"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Reference);
        Assert.Single(refs, r => r.SymbolFqName == "tag" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "tag`hello"));
        Assert.Contains(refs, r => r.SymbolFqName == "obj" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "obj.method()"));
        Assert.Contains(refs, r => r.SymbolFqName == "Box" && r.Kind == RefKinds.Reference);
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "run");
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "hello");
    }

    [Fact]
    public void Feed_records_type_reference_extends_and_implements()
    {
        const string source = """
            export class Child extends Base<Arg> implements Contract<Token> {
                value: Widget = other;
            }

            export interface ChildView extends Contract {
            }

            export interface Boxed extends schemas.Format<Inner> {
            }
            """;

        var refs = Feed("types.ts", source);

        Assert.Single(refs, r => r.SymbolFqName == "Base" && r.Kind == RefKinds.Implementation);
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "Base" && r.Kind == RefKinds.Reference);
        Assert.Contains(refs, r => r.SymbolFqName == "Arg" && r.Kind == RefKinds.Reference);
        Assert.Single(refs, r => r.SymbolFqName == "Contract" && r.Kind == RefKinds.Implementation && r.Line == LineOf(source, "implements Contract"));
        Assert.Single(refs, r => r.SymbolFqName == "Contract" && r.Kind == RefKinds.Implementation && r.Line == LineOf(source, "interface ChildView"));
        Assert.Contains(refs, r => r.SymbolFqName == "Token" && r.Kind == RefKinds.Reference);
        Assert.Contains(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Reference);
        Assert.Contains(refs, r => r.SymbolFqName == "other" && r.Kind == RefKinds.Reference);
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "value");
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "Child" || r.SymbolFqName == "ChildView" || r.SymbolFqName == "Boxed");
        Assert.Single(refs, r => r.SymbolFqName == "Format" && r.Kind == RefKinds.Implementation);
        Assert.Contains(refs, r => r.SymbolFqName == "schemas" && r.Kind == RefKinds.Reference);
        Assert.Contains(refs, r => r.SymbolFqName == "Inner" && r.Kind == RefKinds.Reference);
    }

    [Fact]
    public void Feed_records_jsx_element_names_in_tsx_and_jsx()
    {
        const string source = """
            export function view(label: string) {
                return <Widget title={label} />;
            }
            """;

        foreach (var name in new[] { "view.tsx", "view.jsx" })
        {
            var refs = Feed(name, source);
            Assert.Contains(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "<Widget"));
            Assert.Contains(refs, r => r.SymbolFqName == "label" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "{label}"));
            Assert.DoesNotContain(refs, r => r.SymbolFqName == "label" && r.Line == LineOf(source, "function view"));
            Assert.DoesNotContain(refs, r => r.SymbolFqName == "title" || r.SymbolFqName == "view");
        }
    }

    [Fact]
    public void Feed_records_calls_in_javascript_files()
    {
        const string source = """
            export function run(obj) {
                helper();
                obj.method();
                obj?.method();
                return new Widget();
            }
            """;

        var refs = Feed("calls.js", source);

        Assert.Single(refs, r => r.SymbolFqName == "helper" && r.Kind == RefKinds.Call);
        Assert.Single(refs, r => r.SymbolFqName == "method" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "obj.method()"));
        Assert.Single(refs, r => r.SymbolFqName == "method" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "obj?.method()"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "method" && r.Kind == RefKinds.Reference);
        Assert.Single(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Call);
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Reference);
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "run");
        Assert.Contains(refs, r => r.SymbolFqName == "obj" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "obj.method()"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "obj" && r.Line == LineOf(source, "function run"));
    }

    [Fact]
    public void Feed_skips_declaration_names_parameters_property_keys_and_strings()
    {
        const string source = """
            import { helper } from "./helper";

            // helper
            const label = "helper";

            export function add(a: number, b: Widget): Widget {
                const local = a;
                return helper(local);
            }

            const read = (x: Widget) =>
                x;

            try {
            } catch (err) {
                err;
            }

            const bag = { key: value, shorthand };
            bag.prop;
            export { add as plus } from "./math";
            """;

        var refs = Feed("skip.ts", source);

        Assert.Contains(refs, r => r.SymbolFqName == "helper" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "import { helper }"));
        Assert.Contains(refs, r => r.SymbolFqName == "./helper" && r.Line == LineOf(source, "import { helper }"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "helper" && r.Line == LineOf(source, "// helper"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "helper" && r.Line == LineOf(source, "const label"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "label");
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "add" && r.Line == LineOf(source, "function add"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "a" && r.Line == LineOf(source, "function add"));
        Assert.Contains(refs, r => r.SymbolFqName == "a" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "const local"));
        Assert.Contains(refs, r => r.SymbolFqName == "Widget" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "function add"));
        Assert.Single(refs, r => r.SymbolFqName == "helper" && r.Kind == RefKinds.Call && r.Line == LineOf(source, "return helper"));
        Assert.Contains(refs, r => r.SymbolFqName == "local" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "return helper"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "local" && r.Line == LineOf(source, "const local"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "x" && r.Line == LineOf(source, "const read"));
        Assert.Contains(refs, r => r.SymbolFqName == "x" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "    x;"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "err" && r.Line == LineOf(source, "catch (err)"));
        Assert.Contains(refs, r => r.SymbolFqName == "err" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "    err;"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "key" || r.SymbolFqName == "prop" || r.SymbolFqName == "plus");
        Assert.Contains(refs, r => r.SymbolFqName == "value" && r.Kind == RefKinds.Reference);
        Assert.Contains(refs, r => r.SymbolFqName == "shorthand" && r.Kind == RefKinds.Reference);
        Assert.Contains(refs, r => r.SymbolFqName == "add" && r.Kind == RefKinds.Reference && r.Line == LineOf(source, "export { add"));
        Assert.Contains(refs, r => r.SymbolFqName == "./math" && r.Line == LineOf(source, "export { add"));
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "read");
        Assert.DoesNotContain(refs, r => r.SymbolFqName == "bag" && r.Line == LineOf(source, "const bag"));
    }

    [Fact]
    public void Refs_and_callers_find_a_function_called_from_another_file()
    {
        WriteFile("lib.ts", """
            export function duration(): number {
                return 1;
            }

            export class Clock {
                tick(): number {
                    return duration();
                }
            }
            """);
        WriteFile("app.ts", """
            import { duration, Clock } from "./lib";

            export function main(): number {
                const clock = new Clock();
                return duration() + clock.tick();
            }
            """);

        using var db = IndexDatabase.OpenOrCreate(_tempDir);
        var result = new Indexer([new TreeSitterFeeder()]).EnsureFresh(_tempDir, db);
        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.ReindexedFileCount);

        var resolved = Assert.Single(db.ResolveSymbolCandidates("duration"));
        var refs = db.RefsFor(resolved, kindFilter: null, 50);
        Assert.Contains(refs, r => r.FilePath == "app.ts" && r.Kind == RefKinds.Reference && r.Line == 1);
        Assert.Contains(refs, r => r.FilePath == "app.ts" && r.Kind == RefKinds.Call && r.Line == 5);
        Assert.Contains(refs, r => r.FilePath == "lib.ts" && r.Kind == RefKinds.Call && r.Line == 7);
        Assert.DoesNotContain(refs, r => r.FilePath == "lib.ts" && r.Line == 1);

        var callers = db.RefsFor(resolved, [RefKinds.Call, RefKinds.Reference], 50);
        Assert.Contains(callers, r => r.FilePath == "app.ts" && r.Kind == RefKinds.Call);
        Assert.Contains(callers, r => r.FilePath == "lib.ts" && r.Kind == RefKinds.Call);
        Assert.DoesNotContain(callers, r => r.Kind == RefKinds.Implementation);
    }

    private IReadOnlyList<RefRow> Feed(string relativeName, string source)
    {
        var (feeder, file) = WriteAndScan(relativeName, source);
        var result = feeder.Feed(new FeedRequest(_tempDir, [file]));
        Assert.True(result.Success, result.Warning);
        return result.ResultsByFile[file.RepoRelativePath].Refs;
    }

    private (TreeSitterFeeder Feeder, ScannedFile File) WriteAndScan(string relativeName, string source)
    {
        var path = WriteFile(relativeName, source);
        var lang = RepoScanner.LanguageForPath(relativeName)!;
        return (new TreeSitterFeeder(), new ScannedFile(relativeName, path, lang));
    }

    private string WriteFile(string relativeName, string source)
    {
        var path = Path.Combine(_tempDir, relativeName);
        File.WriteAllText(path, source);
        return path;
    }

    private static int LineOf(string source, string uniqueSnippet)
    {
        var index = source.IndexOf(uniqueSnippet, StringComparison.Ordinal);
        Assert.True(index >= 0, uniqueSnippet);
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
