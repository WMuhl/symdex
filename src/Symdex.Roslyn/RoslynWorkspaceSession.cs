using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Symdex.Core.Feeding;

namespace Symdex.Roslyn;

/// <summary>
/// Holds one MSBuild workspace for repeated feeds of the same solution. Changed documents
/// are re-read from disk onto the open solution; the solution is reopened when the project
/// graph changes or a changed file is not already a document.
/// </summary>
internal sealed class RoslynWorkspaceSession : IDisposable
{
    private readonly Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _solutionPath;

    public FeederResult Feed(FeedRequest request)
    {
        var solutionPath = SolutionResolver.Resolve(request.RepoRoot, request.SolutionPath, out var resolveWarning);
        if (solutionPath is null)
        {
            return RoslynWorkspaceIndexer.Fail(resolveWarning ?? "symdex: could not resolve a .sln/.csproj.");
        }

        solutionPath = Path.GetFullPath(solutionPath);
        if (TryReuse(solutionPath, request, out var updated))
        {
            _solution = updated;
            return RoslynWorkspaceIndexer.Index(updated!, request);
        }

        return OpenFresh(request, solutionPath);
    }

    public void Dispose() => Reset();

    private FeederResult OpenFresh(FeedRequest request, string solutionPath)
    {
        Reset();
        var opened = RoslynWorkspaceIndexer.TryOpen(request);
        if (opened.Failure is not null)
        {
            return opened.Failure;
        }

        _workspace = opened.Workspace;
        _solution = opened.Solution;
        _solutionPath = solutionPath;
        Remember(solutionPath, opened.Solution!);
        return RoslynWorkspaceIndexer.Index(opened.Solution!, request);
    }

    private bool TryReuse(string solutionPath, FeedRequest request, out Solution? updated)
    {
        updated = null;
        if (_solution is null || _solutionPath is null || _workspace is null)
        {
            return false;
        }

        if (!string.Equals(_solutionPath, solutionPath, StringComparison.OrdinalIgnoreCase) || !StampsMatch())
        {
            return false;
        }

        var documents = _solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null)
            .GroupBy(d => Path.GetFullPath(d.FilePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var next = _solution;
        foreach (var file in request.ChangedFiles)
        {
            var fullPath = Path.GetFullPath(file.AbsolutePath);
            if (!documents.TryGetValue(fullPath, out var documentId))
            {
                return false;
            }

            next = next.WithDocumentText(documentId, SourceText.From(File.ReadAllText(fullPath)));
        }

        updated = next;
        return true;
    }

    private void Remember(string solutionPath, Solution solution)
    {
        _stamps.Clear();
        _stamps[solutionPath] = File.GetLastWriteTimeUtc(solutionPath);
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is null)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(project.FilePath);
            _stamps[fullPath] = File.GetLastWriteTimeUtc(fullPath);
        }
    }

    private bool StampsMatch()
    {
        if (_stamps.Count == 0)
        {
            return false;
        }

        foreach (var (path, stamp) in _stamps)
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) != stamp)
            {
                return false;
            }
        }

        return true;
    }

    private void Reset()
    {
        _workspace?.Dispose();
        _workspace = null;
        _solution = null;
        _solutionPath = null;
        _stamps.Clear();
    }
}
