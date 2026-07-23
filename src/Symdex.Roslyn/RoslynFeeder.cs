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
                RegisterMatchingSdk();
            }

            _registered = true;
        }
    }

    // RegisterDefaults() picks the highest installed SDK, which is not necessarily the one whose
    // bundled compiler matches the Microsoft.CodeAnalysis.* package versions this project
    // references - and which SDK it picks can vary by host process (plain `dotnet run` vs. a
    // `dotnet test` test host), causing flaky "System not found"-style compile errors. Pin to the
    // SDK major version this repo targets (net8.0 / global.json) instead, for determinism.
    private static void RegisterMatchingSdk()
    {
        var instances = Microsoft.Build.Locator.MSBuildLocator.QueryVisualStudioInstances().ToList();
        var chosen = instances.FirstOrDefault(i => i.Version.Major == 8)
            ?? instances.OrderByDescending(i => i.Version).FirstOrDefault();

        if (chosen is not null)
        {
            Microsoft.Build.Locator.MSBuildLocator.RegisterInstance(chosen);
        }
        else
        {
            Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
        }
    }
}
