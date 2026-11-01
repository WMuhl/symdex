# symdex MCP server

`symdex mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server on stdio. Cursor, Claude Code, VS Code, and Codex can call the index directly. The same record fields the CLI prints with `--json` (`kind`, `name`, `signature`, `fqName`, `filePath`, `startLine`, `endLine`, `line`, plus `truncated` / `more`, and `ambiguous` / `query` / `candidates` when a name matches more than one symbol) come back as one JSON object per tool call.

The server stays up for the life of the client. Each tool call refreshes the index first (`reindexed N files` and warnings go to stderr; stdout is only JSON-RPC). There is no `rebuild` tool — dropping the index is slow, and a normal tool call already picks up edits.

`--repo` defaults to the process working directory, which is what MCP clients use when they launch a server in the workspace. `--solution` is passed through to the C# feeder. Paths may be absolute or relative to that working directory.

A short name that matches several symbols is not a failure: the tool returns `ambiguous: true`, the candidate list, and a message asking for a fully-qualified name. `limit` defaults to 50 and is clamped to 200; when the clamp or the requested limit hides further rows, `truncated` is true and `more` is how many of the fetched rows were left out.

## Tools

| Tool | Arguments | Use it instead of |
| --- | --- | --- |
| `symdex_outline` | `path`, `limit?` | reading a file to learn its structure |
| `symdex_find` | `query`, `kind?`, `limit?` | grepping for a declaration |
| `symdex_refs` | `symbol`, `limit?` | grepping for usages |
| `symdex_callers` | `symbol`, `limit?` | grepping for callers |

Results are locations and signatures, not bodies. Read only the reported line spans.

## Installed tool

After `dotnet tool install -g symdex` (or the local tool manifest), the command is `symdex mcp`.

### Cursor

`.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "symdex": {
      "command": "symdex",
      "args": ["mcp"]
    }
  }
}
```

### Claude Code

```bash
claude mcp add symdex -- symdex mcp
```

### VS Code

`.vscode/mcp.json`:

```json
{
  "servers": {
    "symdex": {
      "type": "stdio",
      "command": "symdex",
      "args": ["mcp"]
    }
  }
}
```

### Codex

`~/.codex/config.toml`:

```toml
[mcp_servers.symdex]
command = "symdex"
args = ["mcp"]
```

Optional arguments, on any of the configs above: `--repo`, `<path>`, `--solution`, `<path-to-sln-or-csproj>`.

## Dev variant

From a checkout, build once and launch with `--no-build` so the `dotnet` build log does not share a stream with the protocol:

```bash
dotnet build src/Symdex.Cli
dotnet run --project src/Symdex.Cli --no-build -- mcp
```

Cursor (`.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "symdex": {
      "command": "dotnet",
      "args": ["run", "--project", "src/Symdex.Cli", "--no-build", "--", "mcp"]
    }
  }
}
```

Claude Code:

```bash
claude mcp add symdex -- dotnet run --project src/Symdex.Cli --no-build -- mcp
```

VS Code (`.vscode/mcp.json`):

```json
{
  "servers": {
    "symdex": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "src/Symdex.Cli", "--no-build", "--", "mcp"]
    }
  }
}
```

Codex (`~/.codex/config.toml`):

```toml
[mcp_servers.symdex]
command = "dotnet"
args = ["run", "--project", "src/Symdex.Cli", "--no-build", "--", "mcp"]
```

The dev command is the same `dotnet run --project src/Symdex.Cli -- mcp` form; `--no-build` keeps the client's stdout pipe limited to MCP messages after the project has been built.
