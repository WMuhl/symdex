using Symdex.Core;
using Symdex.Core.Feeding;

namespace Symdex.Roslyn;

/// <summary>
/// C# semantic feeder backed by MSBuildWorkspace. Falls back (via <see cref="FeederResult.Success"/>
/// = false) whenever the workspace can't be loaded or a project fails to compile - the caller
/// (Symdex.Core.Indexer) then re-runs the same files through the tree-sitter feeder instead.
/// </summary>
public sealed class RoslynFeeder : ISymbolFeeder
{
    private static readonly object RegisterLock = new();
    private static bool _registered;

    public string Name => "roslyn";

    public bool CanHandle(string language) => language == Languages.CSharp;

    public FeederResult Feed(FeedRequest request)
    {
        EnsureMsBuildRegistered();
        return RunFeed(request);
    }

    // Split into its own method so the JIT doesn't touch any MSBuildWorkspace/Microsoft.Build
    // type until *after* MSBuildLocator.RegisterDefaults() has run - referencing those types
    // any earlier can make the runtime resolve the wrong (or a missing) MSBuild assembly set.
    private static FeederResult RunFeed(FeedRequest request) => RoslynWorkspaceIndexer.Feed(request);

    private static void EnsureMsBuildRegistered()
    {
        lock (RegisterLock)
        {
            if (_registered)
            {
                return;
            }

            if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
            {
                Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
            }

            _registered = true;
        }
    }
}
