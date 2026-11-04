using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Symdex.Tests;

internal static class McpTestSupport
{
    private static readonly string[] MsBuildLocatorVariables =
    [
        "DOTNET_HOST_PATH",
        "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
        "MSBUILD_EXE_PATH",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath",
    ];

    public static string RepoRoot([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", ".."));
    }

    public static string CliExecutable()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var fileName = OperatingSystem.IsWindows() ? "symdex.exe" : "symdex";
        var exe = Path.Combine(RepoRoot(), "src", "Symdex.Cli", "bin", config, tfm, fileName);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"symdex executable not found at '{exe}'.", exe);
        }

        return exe;
    }

    public static Dictionary<string, string?> ScrubMsBuildEnvironment()
    {
        var env = new Dictionary<string, string?>();
        foreach (var name in MsBuildLocatorVariables)
        {
            env[name] = null;
        }

        return env;
    }

    // Left unset, the client first probes `server/discover` with a short timeout; a slow server
    // start (Windows CI) makes it fall back to `initialize` under 2026-07-28, which the server
    // rejects. Pinning an initialize-capable revision skips the probe.
    public static McpClientOptions ClientOptions { get; } = new() { ProtocolVersion = "2025-11-25" };

    public static StdioClientTransport StartTransport(string repoRoot, Action<string> onStderr)
    {
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = CliExecutable(),
            Arguments = ["mcp", "--repo", repoRoot, "--solution", Path.Combine(repoRoot, "SampleRepo.sln")],
            WorkingDirectory = repoRoot,
            Name = "symdex",
            ShutdownTimeout = TimeSpan.FromSeconds(15),
            StandardErrorLines = onStderr,
            EnvironmentVariables = ScrubMsBuildEnvironment(),
        });
    }

    public static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object> args)
    {
        IReadOnlyDictionary<string, object?> arguments = args.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        var result = await client.CallToolAsync(tool, arguments);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.NotEqual(true, result.IsError);
        Assert.DoesNotContain("reindexed", text, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}

public sealed class McpSessionFixture : IAsyncLifetime
{
    private readonly object _stderrGate = new();
    private readonly List<string> _stderr = [];
    private string? _repo;

    public McpClient Client { get; private set; } = null!;

    public string RepoRoot => _repo ?? throw new InvalidOperationException("Fixture is not started.");

    public async Task InitializeAsync()
    {
        _repo = TestUtil.CopyFixture("sample-repo");
        TestUtil.Restore(Path.Combine(_repo, "SampleRepo.sln"));
        Client = await McpClient.CreateAsync(
            McpTestSupport.StartTransport(_repo, AddStderr),
            McpTestSupport.ClientOptions);
    }

    public async Task DisposeAsync()
    {
        if (Client is not null)
        {
            await Client.DisposeAsync();
        }

        if (_repo is null || !Directory.Exists(_repo))
        {
            return;
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_repo, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(200);
            }
        }
    }

    public IReadOnlyList<string> StderrSnapshot()
    {
        lock (_stderrGate)
        {
            return [.. _stderr];
        }
    }

    private void AddStderr(string line)
    {
        lock (_stderrGate)
        {
            _stderr.Add(line);
        }
    }
}
