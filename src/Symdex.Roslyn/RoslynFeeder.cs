using System.Diagnostics;
using Microsoft.Build.Locator;
using Symdex.Core;
using Symdex.Core.Feeding;

namespace Symdex.Roslyn;

/// <summary>
/// C# semantic feeder backed by MSBuildWorkspace. Falls back (via <see cref="FeederResult.Success"/>
/// = false) whenever the workspace can't be loaded or a project fails to compile - the caller
/// (Symdex.Core.Indexer) then re-runs the same files through the tree-sitter feeder instead.
/// </summary>
public sealed class RoslynFeeder : ISymbolFeeder, IDisposable
{
    private static readonly object RegisterLock = new();
    private static bool _registered;
    private object? _warmSession;

    public string Name => "roslyn";

    /// <summary>
    /// When set, the MSBuild workspace opened for a solution stays on this feeder and is
    /// reused for later feeds of that same solution. <see cref="Dispose"/> releases it.
    /// </summary>
    public bool KeepWorkspaceWarm { get; set; }

    public bool CanHandle(string language) => language == Languages.CSharp;

    public FeederResult Feed(FeedRequest request)
    {
        EnsureMsBuildRegistered(request.RepoRoot);
        return RunFeed(request);
    }

    // Split into its own method so the JIT doesn't touch any MSBuildWorkspace/Microsoft.Build
    // type until *after* MSBuildLocator has registered an instance - referencing those types
    // any earlier can make the runtime resolve the wrong (or a missing) MSBuild assembly set.
    private FeederResult RunFeed(FeedRequest request)
    {
        if (!KeepWorkspaceWarm)
        {
            return RoslynWorkspaceIndexer.Feed(request);
        }

        var session = (RoslynWorkspaceSession)(_warmSession ??= new RoslynWorkspaceSession());
        return session.Feed(request);
    }

    public void Dispose()
    {
        (_warmSession as IDisposable)?.Dispose();
        _warmSession = null;
    }

    private static void EnsureMsBuildRegistered(string repoRoot)
    {
        lock (RegisterLock)
        {
            if (_registered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                RegisterMatchingSdk(repoRoot);
            }

            _registered = true;
        }
    }

    // The MSBuild that loads a project must be the SDK `dotnet` would use in that repo
    // (its global.json, otherwise the latest installed). RegisterDefaults() follows the host
    // process instead, so a restore done with one SDK and a workspace opened with another
    // yields a compilation that has no reference assemblies.
    private static void RegisterMatchingSdk(string repoRoot)
    {
        var options = new VisualStudioInstanceQueryOptions
        {
            DiscoveryTypes = DiscoveryType.DotNetSdk,
            WorkingDirectory = repoRoot,
            AllowAllRuntimeVersions = true,
            AllowAllDotnetLocations = true,
        };
        var instances = MSBuildLocator.QueryVisualStudioInstances(options).ToList();

        var wanted = ResolveTargetSdkVersion(repoRoot);
        VisualStudioInstance? chosen = null;
        if (wanted is not null)
        {
            chosen = instances.FirstOrDefault(i => SdkVersionsMatch(i.Version, wanted));
            if (chosen is null)
            {
                var path = FindSdkDirectory(wanted);
                if (path is not null)
                {
                    MSBuildLocator.RegisterMSBuildPath(path);
                    return;
                }
            }
        }

        chosen ??= instances.FirstOrDefault()
            ?? MSBuildLocator.QueryVisualStudioInstances().OrderByDescending(i => i.Version).FirstOrDefault();

        if (chosen is not null)
        {
            MSBuildLocator.RegisterInstance(chosen);
        }
        else
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    private static string? ResolveTargetSdkVersion(string repoRoot)
    {
        var output = RunDotNet("--version", repoRoot);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var line = output.Trim().Split('\r', '\n')[0].Trim();
        return line.Length == 0 ? null : line;
    }

    private static string? FindSdkDirectory(string version)
    {
        var output = RunDotNet("--list-sdks", repoRoot: null);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var raw in output.Split('\r', '\n'))
        {
            var line = raw.Trim();
            var bracket = line.IndexOf('[');
            var close = line.IndexOf(']');
            if (bracket <= 0 || close <= bracket)
            {
                continue;
            }

            var reported = line[..bracket].Trim();
            if (!string.Equals(reported, version, StringComparison.Ordinal))
            {
                continue;
            }

            var path = Path.Combine(line[(bracket + 1)..close].Trim(), reported);
            if (File.Exists(Path.Combine(path, "MSBuild.dll")))
            {
                return path;
            }
        }

        return null;
    }

    private static bool SdkVersionsMatch(Version installed, string reported)
    {
        if (!Version.TryParse(reported, out var wanted))
        {
            return false;
        }

        static int Part(int value) => value < 0 ? 0 : value;
        return installed.Major == wanted.Major
            && installed.Minor == wanted.Minor
            && Part(installed.Build) == Part(wanted.Build);
    }

    private static string? RunDotNet(string arguments, string? repoRoot)
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrEmpty(repoRoot))
            {
                psi.WorkingDirectory = repoRoot;
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
