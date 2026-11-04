using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Symdex.Cli.Mcp;

internal sealed class SymdexTools(IndexSession session)
{
    [McpServerTool(Name = "symdex_outline", Title = "Outline a file", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List the symbols declared in one source file (kind, name, signature, line span) without method bodies. Use this instead of reading a file when you only need its structure. Results are locations, not code — read only the reported startLine-endLine span. A relative path is resolved from the server's working directory (the workspace, when a client launches symdex there).")]
    public Task<OutlineToolResult> Outline(
        [Description("Source file to outline. Repo-relative, such as src/Foo.cs, or absolute.")] string path,
        [Description("Maximum symbols to return. Defaults to 50. Values above 200 are clamped to 200. When more symbols exist, truncated is true and more is how many were left out.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException("path is required.");
        }

        return session.QueryAsync(db => IndexQueries.Outline(db, session.RepoRoot, path, limit), cancellationToken);
    }

    [McpServerTool(Name = "symdex_find", Title = "Find symbols", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find symbols by name, fully-qualified name, or doc comment (substring and full-text). Use this instead of grep when you need declarations. Each result is a location and signature, not a body — open only the reported file and line span. kind filters to one of: class, interface, struct, enum, method, property, field, function, export, type.")]
    public Task<FindToolResult> Find(
        [Description("Symbol name, fully-qualified name, or doc text to search for.")] string query,
        [Description("Optional kind filter: class, interface, struct, enum, method, property, field, function, export, or type.")] string? kind = null,
        [Description("Maximum symbols to return. Defaults to 50. Values above 200 are clamped to 200. When more matches exist, truncated is true and more is how many were left out.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException("query is required.");
        }

        return session.QueryAsync(db => IndexQueries.Find(db, query, kind, limit), cancellationToken);
    }

    [McpServerTool(Name = "symdex_refs", Title = "List references", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List reference, call, and implementation sites for one symbol. Use this instead of grep for usages. Pass a fully-qualified name when you have one (symdex_find returns them). If the short name matches more than one symbol, the result lists the candidates and asks for a fully-qualified name — that is not a failure. Results are file:line locations, not code; read only those lines.")]
    public Task<RefToolResult> Refs(
        [Description("Fully-qualified name, or a short name when it identifies exactly one symbol.")] string symbol,
        [Description("Maximum sites to return. Defaults to 50. Values above 200 are clamped to 200. When more sites exist, truncated is true and more is how many were left out.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new McpException("symbol is required.");
        }

        return session.QueryAsync(db => IndexQueries.Refs(db, symbol, limit), cancellationToken);
    }

    [McpServerTool(Name = "symdex_callers", Title = "List callers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List call and reference sites for a method or function — who calls it. Use this instead of grep for callers. Pass a fully-qualified name when you have one. If the short name matches more than one symbol, the result lists the candidates and asks for a fully-qualified name — that is not a failure. Results are file:line locations, not code; read only those lines.")]
    public Task<RefToolResult> Callers(
        [Description("Fully-qualified name of a method or function, or a short name when it identifies exactly one symbol.")] string symbol,
        [Description("Maximum sites to return. Defaults to 50. Values above 200 are clamped to 200. When more sites exist, truncated is true and more is how many were left out.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new McpException("symbol is required.");
        }

        return session.QueryAsync(db => IndexQueries.Callers(db, symbol, limit), cancellationToken);
    }
}
