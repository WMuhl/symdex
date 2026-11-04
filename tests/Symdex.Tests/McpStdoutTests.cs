using System.Diagnostics;
using System.Text.Json;

namespace Symdex.Tests;

public sealed class McpStdoutTests
{
    [Fact(Timeout = 300_000)]
    public async Task Stdout_stays_json_rpc_when_a_reindex_message_is_produced()
    {
        var repo = TestUtil.CopyFixture("sample-repo");
        try
        {
            TestUtil.Restore(Path.Combine(repo, "SampleRepo.sln"));
            await using var server = McpStdioServer.Start(repo);

            var initialize = await server.RequestAsync(1, """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"symdex-tests","version":"0"}}}
                """, TimeSpan.FromSeconds(30));
            Assert.Equal("2.0", initialize.GetProperty("jsonrpc").GetString());
            await server.NotifyAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var tools = await server.RequestAsync(2, """
                {"jsonrpc":"2.0","id":2,"method":"tools/list"}
                """, TimeSpan.FromSeconds(30));
            var names = tools.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .ToArray();
            Assert.Contains("symdex_outline", names);

            InsertMethod(Path.Combine(repo, "csharp", "SampleLib", "PaymentProcessor.cs"), "ExtraFromMcp");
            var outlined = await server.CallOutlineAsync(3, "csharp/SampleLib/PaymentProcessor.cs");
            Assert.Contains(outlined.GetProperty("results").EnumerateArray(), symbol => symbol.GetProperty("name").GetString() == "ExtraFromMcp");
            Assert.Contains(server.StderrLines(), line => line.Contains("reindexed ", StringComparison.Ordinal));

            InsertMethod(Path.Combine(repo, "csharp", "SampleLib", "PaymentProcessor.cs"), "ExtraAfterReindex");
            var again = await server.CallOutlineAsync(4, "csharp/SampleLib/PaymentProcessor.cs");
            Assert.Contains(again.GetProperty("results").EnumerateArray(), symbol => symbol.GetProperty("name").GetString() == "ExtraAfterReindex");

            await File.WriteAllTextAsync(Path.Combine(repo, "bulk_symbols.py"), BulkPython(220));
            var found = await server.RequestAsync(5, """
                {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"symdex_find","arguments":{"query":"zzbulkfn","limit":1000}}}
                """, TimeSpan.FromMinutes(2));
            var findBody = ToolBody(found);
            Assert.Equal(200, findBody.GetProperty("results").GetArrayLength());
            Assert.True(findBody.GetProperty("truncated").GetBoolean());
            Assert.True(findBody.GetProperty("more").GetInt32() >= 1);

            Assert.All(server.StdoutLines(), line =>
            {
                using var doc = JsonDocument.Parse(line);
                Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
            });
            Assert.DoesNotContain(server.StdoutText(), "reindexed", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(repo))
            {
                try
                {
                    Directory.Delete(repo, recursive: true);
                }
                catch (IOException)
                {
                    // The assertion above already ran; a lingering file lock should not hide it.
                }
            }
        }
    }

    private static void InsertMethod(string path, string name)
    {
        var text = File.ReadAllText(path);
        var index = text.LastIndexOf('}');
        Assert.True(index > 0);
        text = text.Insert(index, $"\n    public void {name}() {{ }}\n");
        File.WriteAllText(path, text);
    }

    private static string BulkPython(int count)
    {
        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            lines[i] = $"def zzbulkfn{i}():\n    return {i}\n";
        }

        return string.Join("\n", lines);
    }

    private static JsonElement ToolBody(JsonElement response)
    {
        var text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        using var doc = JsonDocument.Parse(text!);
        return doc.RootElement.Clone();
    }

    private sealed class McpStdioServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly List<string> _stdout = [];
        private readonly List<string> _stderr = [];
        private readonly Task _stdoutPump;
        private readonly Task _stderrPump;

        private McpStdioServer(Process process)
        {
            _process = process;
            _stdoutPump = PumpAsync(process.StandardOutput, _stdout);
            _stderrPump = PumpAsync(process.StandardError, _stderr);
        }

        public static McpStdioServer Start(string repoRoot)
        {
            var psi = new ProcessStartInfo(McpTestSupport.CliExecutable())
            {
                WorkingDirectory = repoRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("mcp");
            psi.ArgumentList.Add("--repo");
            psi.ArgumentList.Add(repoRoot);
            psi.ArgumentList.Add("--solution");
            psi.ArgumentList.Add(Path.Combine(repoRoot, "SampleRepo.sln"));
            foreach (var name in McpTestSupport.ScrubMsBuildEnvironment().Keys)
            {
                psi.Environment.Remove(name);
            }

            var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start symdex mcp");
            return new McpStdioServer(process);
        }

        public async Task NotifyAsync(string json)
        {
            await _process.StandardInput.WriteLineAsync(json);
            await _process.StandardInput.FlushAsync();
        }

        public async Task<JsonElement> RequestAsync(int id, string json, TimeSpan timeout)
        {
            await NotifyAsync(json);
            var line = await WaitForIdAsync(id, timeout);
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }

        public async Task<JsonElement> CallOutlineAsync(int id, string path)
        {
            var payload = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method = "tools/call",
                @params = new
                {
                    name = "symdex_outline",
                    arguments = new { path },
                },
            });
            var response = await RequestAsync(id, payload, TimeSpan.FromMinutes(2));
            return ToolBody(response);
        }

        public IReadOnlyList<string> StdoutLines()
        {
            lock (_stdout)
            {
                return [.. _stdout];
            }
        }

        public string StdoutText() => string.Join("\n", StdoutLines());

        public IReadOnlyList<string> StderrLines()
        {
            lock (_stderr)
            {
                return [.. _stderr];
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
            }

            if (!_process.WaitForExit(5_000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }

            await Task.WhenAll(_stdoutPump, _stderrPump);
            _process.Dispose();
        }

        private async Task<string> WaitForIdAsync(int id, TimeSpan timeout)
        {
            var started = Stopwatch.StartNew();
            while (started.Elapsed < timeout)
            {
                lock (_stdout)
                {
                    var match = _stdout.LastOrDefault(line => IdMatches(line, id));
                    if (match is not null)
                    {
                        return match;
                    }
                }

                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"symdex mcp exited ({_process.ExitCode}) before response {id}.\nstderr:\n{string.Join('\n', StderrLines())}");
                }

                await Task.Delay(50);
            }

            throw new TimeoutException(
                $"timed out waiting for response {id}.\nstdout:\n{StdoutText()}\nstderr:\n{string.Join('\n', StderrLines())}");
        }

        private static bool IdMatches(string line, int id) =>
            line.Contains($"\"id\":{id}", StringComparison.Ordinal) &&
            !line.Contains($"\"id\":{id}0", StringComparison.Ordinal) &&
            !char.IsDigit(NextIdDigit(line, id));

        private static char NextIdDigit(string line, int id)
        {
            var token = $"\"id\":{id}";
            var index = line.IndexOf(token, StringComparison.Ordinal);
            if (index < 0 || index + token.Length >= line.Length)
            {
                return '\0';
            }

            return line[index + token.Length];
        }

        private static async Task PumpAsync(StreamReader reader, List<string> sink)
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                {
                    return;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                lock (sink)
                {
                    sink.Add(line);
                }
            }
        }
    }
}
