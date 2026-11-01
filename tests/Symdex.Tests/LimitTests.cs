using System.Text;
using System.Text.Json;
using Symdex.Cli;
using Symdex.Cli.Commands;

namespace Symdex.Tests;

public sealed class LimitTests
{
    [Fact]
    public void Limit_prints_truncation_line_and_hard_caps_at_200()
    {
        Assert.Equal(200, OutputWriter.HardCapLimit);
        Assert.Equal(200, OutputWriter.EffectiveLimit(10_000));
        Assert.Equal(1, OutputWriter.EffectiveLimit(0));
        Assert.Equal(3, OutputWriter.EffectiveLimit(3));

        var small = WriteRepo(8);
        try
        {
            var plain = ConsoleCapture.Run(() => FindCommand.Run(small, json: false, limit: 3, "Zed", kind: null, solution: null));
            Assert.Equal(0, plain.ExitCode);
            var lines = NonEmpty(plain.Stdout);
            Assert.Equal(4, lines.Length);
            Assert.Equal("... +5 more (narrow the query)", lines[^1]);
            Assert.All(lines[..^1], line => Assert.Contains("Zed", line));

            var json = ConsoleCapture.Run(() => FindCommand.Run(small, json: true, limit: 3, "Zed", kind: null, solution: null));
            var jsonLines = NonEmpty(json.Stdout);
            Assert.Equal(4, jsonLines.Length);
            using var truncated = JsonDocument.Parse(jsonLines[^1]);
            Assert.True(truncated.RootElement.GetProperty("truncated").GetBoolean());
            Assert.Equal(5, truncated.RootElement.GetProperty("more").GetInt32());
        }
        finally
        {
            Directory.Delete(small, recursive: true);
        }

        var capped = WriteRepo(210);
        try
        {
            var plain = ConsoleCapture.Run(() => FindCommand.Run(capped, json: false, limit: 500, "Zed", kind: null, solution: null));
            Assert.Equal(0, plain.ExitCode);
            var lines = NonEmpty(plain.Stdout);
            Assert.Equal(201, lines.Length);
            Assert.Equal(200, lines.Count(line => !line.StartsWith("... ", StringComparison.Ordinal)));
            Assert.Equal("... +1 more (narrow the query)", lines[^1]);
        }
        finally
        {
            Directory.Delete(capped, recursive: true);
        }
    }

    private static string WriteRepo(int methodCount)
    {
        var root = Directory.CreateTempSubdirectory("symdex-limit-").FullName;
        var source = new StringBuilder();
        source.AppendLine("public class Bag");
        source.AppendLine("{");
        for (var i = 0; i < methodCount; i++)
        {
            source.AppendLine($"    public void Zed{i:000}() {{ }}");
        }

        source.AppendLine("}");
        File.WriteAllText(Path.Combine(root, "Bag.cs"), source.ToString());
        return root;
    }

    private static string[] NonEmpty(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
