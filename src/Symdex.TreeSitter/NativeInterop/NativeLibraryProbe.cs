using System.Runtime.InteropServices;

namespace Symdex.TreeSitter.Native;

/// <summary>Checks once whether the native library built by scripts/build-native.* is loadable.</summary>
internal static class NativeLibraryProbe
{
    public static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            return NativeLibrary.TryLoad("symdex_treesitter", typeof(NativeLibraryProbe).Assembly, null, out _);
        }
        catch
        {
            return false;
        }
    }
}
