using System.Runtime.InteropServices;
using System.Text;

namespace Symdex.TreeSitter.Native;

internal readonly record struct TsMatch(ushort PatternIndex, IReadOnlyDictionary<string, TSNode> Captures);

/// <summary>Thin managed wrapper over a compiled tree-sitter query and its cursor iteration.</summary>
internal sealed class TsQuery : IDisposable
{
    private readonly IntPtr _handle;
    private readonly Dictionary<uint, string> _captureNames = [];

    private TsQuery(IntPtr handle)
    {
        _handle = handle;
    }

    public static TsQuery Compile(IntPtr language, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var handle = NativeMethods.ts_query_new(language, bytes, (uint)bytes.Length, out var errorOffset, out var errorType);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Failed to compile tree-sitter query: {errorType} at byte offset {errorOffset}.");
        }

        return new TsQuery(handle);
    }

    public IEnumerable<TsMatch> Execute(TSNode root)
    {
        var cursor = NativeMethods.ts_query_cursor_new();
        try
        {
            NativeMethods.ts_query_cursor_exec(cursor, _handle, root);
            var captureSize = Marshal.SizeOf<TSQueryCapture>();

            while (NativeMethods.ts_query_cursor_next_match(cursor, out var match))
            {
                var captures = new Dictionary<string, TSNode>();
                for (var i = 0; i < match.CaptureCount; i++)
                {
                    var capturePtr = (nint)match.Captures + i * captureSize;
                    var capture = Marshal.PtrToStructure<TSQueryCapture>(capturePtr);
                    captures[GetCaptureName(capture.Index)] = capture.Node;
                }

                yield return new TsMatch(match.PatternIndex, captures);
            }
        }
        finally
        {
            NativeMethods.ts_query_cursor_delete(cursor);
        }
    }

    private string GetCaptureName(uint id)
    {
        if (_captureNames.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var ptr = NativeMethods.ts_query_capture_name_for_id(_handle, id, out var length);
        var name = Marshal.PtrToStringUTF8(ptr, (int)length);
        _captureNames[id] = name;
        return name;
    }

    public void Dispose() => NativeMethods.ts_query_delete(_handle);
}
