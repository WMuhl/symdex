namespace Symdex.Core;

public sealed record FileRecord(long Id, string Path, string Hash, string Lang, string IndexedAt);

/// <summary>A symbol as produced by a feeder, not yet assigned a database id.</summary>
public sealed record SymbolRow(
    string Name,
    string? FqName,
    string Kind,
    string? Signature,
    string? Doc,
    int StartLine,
    int EndLine,
    string? Container);

/// <summary>A reference site as produced by a feeder, not yet assigned a database id.</summary>
public sealed record RefRow(
    string SymbolFqName,
    int Line,
    string Kind);

/// <summary>A symbol as read back from the database, joined with its owning file path.</summary>
public sealed record SymbolRecord(
    long Id,
    string Name,
    string? FqName,
    string Kind,
    string? Signature,
    string? Doc,
    string FilePath,
    int StartLine,
    int EndLine,
    string? Container);

/// <summary>A reference site as read back from the database, joined with its file path.</summary>
public sealed record RefRecord(
    string SymbolFqName,
    string Kind,
    string FilePath,
    int Line);

public static class SymbolKinds
{
    public const string Class = "class";
    public const string Interface = "interface";
    public const string Struct = "struct";
    public const string Enum = "enum";
    public const string Method = "method";
    public const string Property = "property";
    public const string Field = "field";
    public const string Function = "function";
    public const string Export = "export";
    public const string Type = "type";
}

public static class SymbolIdentity
{
    public static string Key(string kind, string? fqName, string name) =>
        kind + "\0" + (fqName ?? name);
}

public static class RefKinds
{
    public const string Reference = "reference";
    public const string Call = "call";
    public const string Implementation = "implementation";
    public const string Override = "override";
}

public static class Languages
{
    public const string CSharp = "csharp";
    public const string TypeScript = "typescript";
    public const string JavaScript = "javascript";
    public const string Python = "python";
}
