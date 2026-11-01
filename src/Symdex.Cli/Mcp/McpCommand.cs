using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Symdex.Cli.Mcp;

internal static class McpCommand
{
    public static async Task<int> RunAsync(string repo, string? solution, CancellationToken cancellationToken)
    {
        // Stdio is the MCP transport (Console.OpenStandardOutput). Everything else — freshness
        // lines, warnings, host logs — must stay off that stream.
        Console.SetOut(Console.Error);

        var session = new IndexSession(repo, solution);
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = [],
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = "symdex",
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.Services.AddSingleton(session);
            builder.Services
                .AddMcpServer(options =>
                {
                    options.ServerInfo = new Implementation
                    {
                        Name = "symdex",
                        Version = "0.1.0",
                    };
                    options.ServerInstructions =
                        "Prefer symdex tools over grep or reading whole files to find where a symbol is declared or used. Results are locations and signatures, not source bodies; read only the reported line spans. When a tool returns ambiguous candidates, call it again with one fully-qualified name. The index refreshes itself when files change.";
                })
                .WithStdioServerTransport()
                .WithTools<SymdexTools>();

            using var host = builder.Build();
            await host.RunAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"symdex: error: {ex.Message}");
            return 1;
        }
        finally
        {
            session.Dispose();
        }
    }
}
