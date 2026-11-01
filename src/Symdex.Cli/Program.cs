using System.CommandLine;
using Symdex.Cli;
using Symdex.Cli.Commands;

var repoOption = new Option<string>("--repo")
{
    Description = "Repository root",
    Recursive = true,
    DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
};
var jsonOption = new Option<bool>("--json")
{
    Description = "Emit JSON lines instead of plain text",
    Recursive = true,
    DefaultValueFactory = _ => false,
};
var limitOption = new Option<int>("--limit")
{
    Description = "Max results (hard cap 200)",
    Recursive = true,
    DefaultValueFactory = _ => OutputWriter.DefaultLimit,
};
var solutionOption = new Option<string?>("--solution")
{
    Description = "Path to a .sln/.csproj for the Roslyn C# feeder (auto-discovered otherwise)",
    Recursive = true,
};

var root = new RootCommand("symdex - local codebase index for coding agents");
root.Add(repoOption);
root.Add(jsonOption);
root.Add(limitOption);
root.Add(solutionOption);

var fileArgument = new Argument<string>("file")
{
    Description = "Path to a source file",
};
var outline = new Command("outline", "List all symbols in a file");
outline.Add(fileArgument);
outline.SetAction(parseResult =>
{
    var repo = parseResult.GetValue(repoOption)!;
    var json = parseResult.GetValue(jsonOption);
    var limit = parseResult.GetValue(limitOption);
    var solution = parseResult.GetValue(solutionOption);
    var file = parseResult.GetValue(fileArgument);
    return RunSafely(() => OutlineCommand.Run(repo, json, limit, file!, solution));
});
root.Add(outline);

var nameArgument = new Argument<string>("name")
{
    Description = "Symbol name or substring to search for",
};
var kindOption = new Option<string?>("--kind")
{
    Description = "Filter results to this symbol kind",
};
var find = new Command("find", "Find symbols by name/fqname/doc (substring + FTS)");
find.Add(nameArgument);
find.Add(kindOption);
find.SetAction(parseResult =>
{
    var repo = parseResult.GetValue(repoOption)!;
    var json = parseResult.GetValue(jsonOption);
    var limit = parseResult.GetValue(limitOption);
    var solution = parseResult.GetValue(solutionOption);
    var name = parseResult.GetValue(nameArgument);
    var kind = parseResult.GetValue(kindOption);
    return RunSafely(() => FindCommand.Run(repo, json, limit, name!, kind, solution));
});
root.Add(find);

var symbolArgument = new Argument<string>("symbol")
{
    Description = "Fully-qualified name or short name of the symbol",
};
var refs = new Command("refs", "List all reference sites for a symbol");
refs.Add(symbolArgument);
refs.SetAction(parseResult =>
{
    var repo = parseResult.GetValue(repoOption)!;
    var json = parseResult.GetValue(jsonOption);
    var limit = parseResult.GetValue(limitOption);
    var solution = parseResult.GetValue(solutionOption);
    var symbol = parseResult.GetValue(symbolArgument);
    return RunSafely(() => RefsCommand.Run(repo, json, limit, symbol!, solution));
});
root.Add(refs);

var callersSymbol = new Argument<string>("symbol")
{
    Description = "Fully-qualified name or short name of the symbol",
};
var callers = new Command("callers", "List call/reference sites for a method or function");
callers.Add(callersSymbol);
callers.SetAction(parseResult =>
{
    var repo = parseResult.GetValue(repoOption)!;
    var json = parseResult.GetValue(jsonOption);
    var limit = parseResult.GetValue(limitOption);
    var solution = parseResult.GetValue(solutionOption);
    var symbol = parseResult.GetValue(callersSymbol);
    return RunSafely(() => CallersCommand.Run(repo, json, limit, symbol!, solution));
});
root.Add(callers);

var rebuild = new Command("rebuild", "Drop and rebuild the whole index");
rebuild.SetAction(parseResult =>
{
    var repo = parseResult.GetValue(repoOption)!;
    var json = parseResult.GetValue(jsonOption);
    var solution = parseResult.GetValue(solutionOption);
    return RunSafely(() => RebuildCommand.Run(repo, json, solution));
});
root.Add(rebuild);

return await root.Parse(args).InvokeAsync();

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
