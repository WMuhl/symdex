using System.Text.Json;

namespace Symdex.Cli;

/// <summary>
/// Shared output formatting for every verb: plain text (one record per line) or JSON lines,
/// with the "... +K more (narrow the query)" truncation line when a query exceeds --limit.
/// </summary>
internal static class OutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const int DefaultLimit = 50;
    public const int HardCapLimit = 200;

    public static int EffectiveLimit(int requestedLimit) => Math.Clamp(requestedLimit, 1, HardCapLimit);

    public static void WriteRecords<T>(
        IReadOnlyList<T> records, int limit, bool json, Func<T, string> toPlainText, Func<T, object> toJsonObject)
    {
        var effectiveLimit = EffectiveLimit(limit);
        var shown = records.Take(effectiveLimit).ToList();

        foreach (var record in shown)
        {
            Console.WriteLine(json ? JsonSerializer.Serialize(toJsonObject(record), JsonOptions) : toPlainText(record));
        }

        if (records.Count > effectiveLimit)
        {
            var more = records.Count - effectiveLimit;
            Console.WriteLine(json
                ? JsonSerializer.Serialize(new { truncated = true, more }, JsonOptions)
                : $"... +{more} more (narrow the query)");
        }
    }

    public static void WriteLine(string plainText, bool json, object? jsonObject = null)
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(jsonObject ?? new { message = plainText }, JsonOptions) : plainText);
    }
}
