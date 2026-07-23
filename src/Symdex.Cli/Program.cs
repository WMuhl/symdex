using System.CommandLine;
using System.CommandLine.Invocation;
using Symdex.Cli;
using Symdex.Cli.Commands;

var repoOption = new Option<string>("--repo", () => Directory.GetCurrentDirectory(), "Repository root");
var jsonOption = new Option<bool>("--json", () => false, "Emit JSON lines instead of plain text");
var limitOption = new Option<int>("--limit", () => OutputWriter.DefaultLimit, "Max results (hard cap 200)");

var root = new RootCommand("symdex - local codebase index for coding agents");
root.AddGlobalOption(repoOption);
root.AddGlobalOption(jsonOption);
root.AddGlobalOption(limitOption);

var fileArgument = new Argument<string>("file", "Path to a source file");
var outline = new Command("outline", "List all symbols in a file") { fileArgument };
outline.SetHandler(ctx =>
{
    var repo = ctx.ParseResult.GetValueForOption(repoOption)!;
    var json = ctx.ParseResult.GetValueForOption(jsonOption);
    var limit = ctx.ParseResult.GetValueForOption(limitOption);
    var file = ctx.ParseResult.GetValueForArgument(fileArgument);
    ctx.ExitCode = RunSafely(() => OutlineCommand.Run(repo, json, limit, file));
});
root.AddCommand(outline);

var nameArgument = new Argument<string>("name", "Symbol name or substring to search for");
var kindOption = new Option<string?>("--kind", "Filter results to this symbol kind");
var find = new Command("find", "Find symbols by name/fqname/doc (substring + FTS)") { nameArgument, kindOption };
find.SetHandler(ctx =>
{
    var repo = ctx.ParseResult.GetValueForOption(repoOption)!;
    var json = ctx.ParseResult.GetValueForOption(jsonOption);
    var limit = ctx.ParseResult.GetValueForOption(limitOption);
    var name = ctx.ParseResult.GetValueForArgument(nameArgument);
    var kind = ctx.ParseResult.GetValueForOption(kindOption);
    ctx.ExitCode = RunSafely(() => FindCommand.Run(repo, json, limit, name, kind));
});
root.AddCommand(find);

var symbolArgument = new Argument<string>("symbol", "Fully-qualified name or short name of the symbol");
var refs = new Command("refs", "List all reference sites for a symbol") { symbolArgument };
refs.SetHandler(ctx =>
{
    var repo = ctx.ParseResult.GetValueForOption(repoOption)!;
    var json = ctx.ParseResult.GetValueForOption(jsonOption);
    var limit = ctx.ParseResult.GetValueForOption(limitOption);
    var symbol = ctx.ParseResult.GetValueForArgument(symbolArgument);
    ctx.ExitCode = RunSafely(() => RefsCommand.Run(repo, json, limit, symbol));
});
root.AddCommand(refs);

var callers = new Command("callers", "List call/reference sites for a method or function") { symbolArgument };
callers.SetHandler(ctx =>
{
    var repo = ctx.ParseResult.GetValueForOption(repoOption)!;
    var json = ctx.ParseResult.GetValueForOption(jsonOption);
    var limit = ctx.ParseResult.GetValueForOption(limitOption);
    var symbol = ctx.ParseResult.GetValueForArgument(symbolArgument);
    ctx.ExitCode = RunSafely(() => CallersCommand.Run(repo, json, limit, symbol));
});
root.AddCommand(callers);

var rebuild = new Command("rebuild", "Drop and rebuild the whole index");
rebuild.SetHandler(ctx =>
{
    var repo = ctx.ParseResult.GetValueForOption(repoOption)!;
    var json = ctx.ParseResult.GetValueForOption(jsonOption);
    ctx.ExitCode = RunSafely(() => RebuildCommand.Run(repo, json));
});
root.AddCommand(rebuild);

return await root.InvokeAsync(args);

static int RunSafely(Func<int> action)
{
    try
    {
        return action();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"symdex: error: {ex.Message}");
        return 1;
    }
}
