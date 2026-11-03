using System.Runtime.InteropServices;

namespace Symdex.TreeSitter.Native;

[StructLayout(LayoutKind.Sequential)]
public struct TSPoint
{
    public uint Row;
    public uint Column;
}

[StructLayout(LayoutKind.Sequential)]
public struct TSNode
{
    public uint Context0;
    public uint Context1;
    public uint Context2;
    public uint Context3;
    public IntPtr Id;
    public IntPtr Tree;

    public readonly bool IsNull => Id == IntPtr.Zero;
}

[StructLayout(LayoutKind.Sequential)]
public struct TSQueryCapture
{
    public TSNode Node;
    public uint Index;
}

[StructLayout(LayoutKind.Sequential)]
public struct TSQueryMatch
{
    public uint Id;
    public ushort PatternIndex;
    public ushort CaptureCount;
    public IntPtr Captures; // TSQueryCapture*
}

public enum TSQueryError : uint
{
    None = 0,
    Syntax = 1,
    NodeType = 2,
    Field = 3,
    Capture = 4,
    Structure = 5,
    Language = 6,
}

/// <summary>
/// Raw P/Invoke bindings against the vendored tree-sitter runtime + grammars, compiled at build
/// time into a single native library named "symdex_treesitter" (see Symdex.TreeSitter.csproj and
/// scripts/build-native.*). This binds only the subset of the tree-sitter C API that
/// <see cref="Symdex.TreeSitter.TreeSitterFeeder"/> needs.
/// </summary>
internal static partial class NativeMethods
{
    private const string Lib = "symdex_treesitter";

    static NativeMethods()
    {
        NativeLibraryProbe.EnsureResolver();
    }

    [LibraryImport(Lib)]
    public static partial IntPtr ts_parser_new();

    [LibraryImport(Lib)]
    public static partial void ts_parser_delete(IntPtr parser);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool ts_parser_set_language(IntPtr parser, IntPtr language);

    [LibraryImport(Lib)]
    public static partial IntPtr ts_parser_parse_string(IntPtr parser, IntPtr oldTree, byte[] source, uint length);

    [LibraryImport(Lib)]
    public static partial void ts_tree_delete(IntPtr tree);

    [LibraryImport(Lib)]
    public static partial TSNode ts_tree_root_node(IntPtr tree);

    [LibraryImport(Lib)]
    public static partial IntPtr ts_node_type(TSNode node);

    [LibraryImport(Lib)]
    public static partial uint ts_node_start_byte(TSNode node);

    [LibraryImport(Lib)]
    public static partial uint ts_node_end_byte(TSNode node);

    [LibraryImport(Lib)]
    public static partial TSPoint ts_node_start_point(TSNode node);

    [LibraryImport(Lib)]
    public static partial TSPoint ts_node_end_point(TSNode node);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool ts_node_is_named(TSNode node);

    [LibraryImport(Lib)]
    public static partial TSNode ts_node_parent(TSNode node);

    [LibraryImport(Lib)]
    public static partial uint ts_node_child_count(TSNode node);

    [LibraryImport(Lib)]
    public static partial TSNode ts_node_child(TSNode node, uint index);

    [LibraryImport(Lib)]
    public static partial TSNode ts_node_child_by_field_name(TSNode node, byte[] name, uint nameLength);

    [LibraryImport(Lib)]
    public static partial IntPtr ts_query_new(
        IntPtr language, byte[] source, uint sourceLen, out uint errorOffset, out TSQueryError errorType);

    [LibraryImport(Lib)]
    public static partial void ts_query_delete(IntPtr query);

    [LibraryImport(Lib)]
    public static partial IntPtr ts_query_capture_name_for_id(IntPtr query, uint id, out uint length);

    [LibraryImport(Lib)]
    public static partial IntPtr ts_query_cursor_new();

    [LibraryImport(Lib)]
    public static partial void ts_query_cursor_delete(IntPtr cursor);

    [LibraryImport(Lib)]
    public static partial void ts_query_cursor_exec(IntPtr cursor, IntPtr query, TSNode node);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool ts_query_cursor_next_match(IntPtr cursor, out TSQueryMatch match);

    // Grammar entry points - each returns a `const TSLanguage*` for the compiled grammar.
    [LibraryImport(Lib)]
    public static partial IntPtr tree_sitter_c_sharp();

    [LibraryImport(Lib)]
    public static partial IntPtr tree_sitter_typescript();

    [LibraryImport(Lib)]
    public static partial IntPtr tree_sitter_tsx();

    [LibraryImport(Lib)]
    public static partial IntPtr tree_sitter_python();
}
