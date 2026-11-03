using System.Text.Json;
using ModelContextProtocol.Client;

namespace Symdex.Tests;

public sealed class McpServerTests(McpSessionFixture fixture) : IClassFixture<McpSessionFixture>
{
    [Fact]
    public async Task Tools_list_exposes_the_four_symdex_tools()
    {
        var tools = await fixture.Client.ListToolsAsync();
        var names = tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(["symdex_callers", "symdex_find", "symdex_outline", "symdex_refs"], names);
        Assert.DoesNotContain(tools, tool => tool.Name.Contains("rebuild", StringComparison.OrdinalIgnoreCase));
        Assert.All(tools, tool => Assert.True(tool.Description?.Length > 40));

        var outline = tools.Single(tool => tool.Name == "symdex_outline");
        AssertRequired(outline, "path");
        AssertOptional(outline, "limit");

        var find = tools.Single(tool => tool.Name == "symdex_find");
        AssertRequired(find, "query");
        AssertOptional(find, "kind");
        AssertOptional(find, "limit");

        var refs = tools.Single(tool => tool.Name == "symdex_refs");
        AssertRequired(refs, "symbol");
        AssertOptional(refs, "limit");

        var callers = tools.Single(tool => tool.Name == "symdex_callers");
        AssertRequired(callers, "symbol");
        AssertOptional(callers, "limit");
    }

    [Fact]
    public async Task Outline_returns_symbols_without_bodies()
    {
        var result = await McpTestSupport.CallAsync(fixture.Client, "symdex_outline", new Dictionary<string, object>
        {
            ["path"] = "csharp/SampleLib/PaymentProcessor.cs",
        });

        var symbols = result.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "class" && symbol.GetProperty("name").GetString() == "PaymentProcessor");
        Assert.Contains(symbols, symbol => symbol.GetProperty("kind").GetString() == "method" && symbol.GetProperty("name").GetString() == "Charge");
        Assert.All(symbols, symbol => Assert.DoesNotContain("return", symbol.GetProperty("signature").GetString() ?? string.Empty));
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Find_locates_the_interface_and_can_filter_by_kind()
    {
        var found = await McpTestSupport.CallAsync(fixture.Client, "symdex_find", new Dictionary<string, object>
        {
            ["query"] = "IPaymentProcessor",
        });
        Assert.Contains(
            found.GetProperty("results").EnumerateArray(),
            symbol => symbol.GetProperty("kind").GetString() == "interface" && symbol.GetProperty("fqName").GetString()!.Contains("IPaymentProcessor"));

        var methods = await McpTestSupport.CallAsync(fixture.Client, "symdex_find", new Dictionary<string, object>
        {
            ["query"] = "Charge",
            ["kind"] = "method",
        });
        var methodResults = methods.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(methodResults);
        Assert.All(methodResults, symbol => Assert.Equal("method", symbol.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Find_treats_like_metacharacters_as_literals()
    {
        var underscore = await McpTestSupport.CallAsync(fixture.Client, "symdex_find", new Dictionary<string, object>
        {
            ["query"] = "_processor",
        });
        var underscoreHits = underscore.GetProperty("results").EnumerateArray().ToList();
        Assert.NotEmpty(underscoreHits);
        Assert.All(underscoreHits, symbol => Assert.Contains("_processor", symbol.GetProperty("fqName").GetString()));

        var percent = await McpTestSupport.CallAsync(fixture.Client, "symdex_find", new Dictionary<string, object>
        {
            ["query"] = "a%b",
        });
        Assert.Empty(percent.GetProperty("results").EnumerateArray());

        var starred = await McpTestSupport.CallAsync(fixture.Client, "symdex_find", new Dictionary<string, object>
        {
            ["query"] = "Charge(decimal)",
        });
        Assert.Empty(starred.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task Refs_finds_implementation_and_usage_sites()
    {
        var result = await McpTestSupport.CallAsync(fixture.Client, "symdex_refs", new Dictionary<string, object>
        {
            ["symbol"] = "IPaymentProcessor",
        });

        Assert.False(result.TryGetProperty("ambiguous", out _));
        Assert.Contains("IPaymentProcessor", result.GetProperty("symbol").GetString());
        var sites = result.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(sites, site => site.GetProperty("kind").GetString() == "implementation" && site.GetProperty("filePath").GetString()!.EndsWith("PaymentProcessor.cs"));
        Assert.Contains(sites, site => site.GetProperty("filePath").GetString()!.EndsWith("CheckoutService.cs"));
    }

    [Fact]
    public async Task Callers_finds_the_call_site_for_a_fully_qualified_method()
    {
        var found = await McpTestSupport.CallAsync(fixture.Client, "symdex_find", new Dictionary<string, object>
        {
            ["query"] = "Charge",
            ["kind"] = "method",
        });
        var fqName = found.GetProperty("results").EnumerateArray()
            .Select(symbol => symbol.GetProperty("fqName").GetString())
            .Single(name => name!.Contains("IPaymentProcessor", StringComparison.Ordinal));

        var result = await McpTestSupport.CallAsync(fixture.Client, "symdex_callers", new Dictionary<string, object>
        {
            ["symbol"] = fqName!,
        });

        Assert.Contains(
            result.GetProperty("results").EnumerateArray(),
            site => site.GetProperty("kind").GetString() == "call" && site.GetProperty("filePath").GetString()!.EndsWith("CheckoutService.cs"));
    }

    [Fact]
    public async Task Ambiguous_symbol_returns_candidates_instead_of_failing()
    {
        var result = await McpTestSupport.CallAsync(fixture.Client, "symdex_refs", new Dictionary<string, object>
        {
            ["symbol"] = "Charge",
        });

        Assert.True(result.GetProperty("ambiguous").GetBoolean());
        Assert.Equal("Charge", result.GetProperty("query").GetString());
        Assert.Contains("fully-qualified", result.GetProperty("message").GetString());
        Assert.True(result.GetProperty("candidates").GetArrayLength() >= 2);
        Assert.Empty(result.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task Unknown_symbol_is_an_empty_result()
    {
        var result = await McpTestSupport.CallAsync(fixture.Client, "symdex_callers", new Dictionary<string, object>
        {
            ["symbol"] = "ThisSymbolDoesNotExist",
        });

        Assert.False(result.TryGetProperty("ambiguous", out _));
        Assert.Empty(result.GetProperty("results").EnumerateArray());
        Assert.Contains("No symbol matched", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Limit_truncates_and_reports_how_many_were_omitted()
    {
        var result = await McpTestSupport.CallAsync(fixture.Client, "symdex_outline", new Dictionary<string, object>
        {
            ["path"] = "csharp/SampleLib/PaymentProcessor.cs",
            ["limit"] = 1,
        });

        Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("more").GetInt32() >= 1);
    }

    [Fact]
    public async Task Reindex_notice_is_written_to_stderr()
    {
        await McpTestSupport.CallAsync(fixture.Client, "symdex_outline", new Dictionary<string, object>
        {
            ["path"] = "csharp/SampleLib/IPaymentProcessor.cs",
        });

        Assert.Contains(fixture.StderrSnapshot(), line => line.Contains("reindexed ", StringComparison.Ordinal));
    }

    private static void AssertRequired(McpClientTool tool, string name)
    {
        var schema = tool.JsonSchema;
        Assert.True(schema.TryGetProperty("properties", out var properties), schema.ToString());
        Assert.True(properties.TryGetProperty(name, out _), schema.ToString());
        var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(name, required);
    }

    private static void AssertOptional(McpClientTool tool, string name)
    {
        var schema = tool.JsonSchema;
        Assert.True(schema.GetProperty("properties").TryGetProperty(name, out _), schema.ToString());
        var required = schema.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [];
        Assert.DoesNotContain(name, required);
    }
}
