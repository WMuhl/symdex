using System.Reflection;
using System.Runtime.InteropServices;

namespace Symdex.TreeSitter.Native;

/// <summary>
/// Loads symdex_treesitter from beside this assembly (build, test, and plain publish output)
/// or from <c>runtimes/&lt;rid&gt;/native</c> in a dotnet tool package.
/// </summary>
internal static class NativeLibraryProbe
{
    private static readonly object Gate = new();
    private static int _resolverRegistered;
    private static bool? _available;

    internal static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryProbe).Assembly, Resolve);
    }

    public static bool IsAvailable
    {
        get
        {
            if (_available is bool cached)
            {
                return cached;
            }

            lock (Gate)
            {
                return _available ??= Probe();
            }
        }
    }

    // TryLoad(libraryName, assembly) does not consult the DllImport resolver, so the tool layout
    // (runtimes/<rid>/native, not beside the managed assembly) has to be probed by path. P/Invoke
    // does consult the resolver registered above.
    private static bool Probe()
    {
        try
        {
            EnsureResolver();
            foreach (var candidate in CandidatePaths(typeof(NativeLibraryProbe).Assembly))
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out _))
                {
                    return true;
                }
            }

            return NativeLibrary.TryLoad("symdex_treesitter", typeof(NativeLibraryProbe).Assembly, null, out _);
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("symdex_treesitter", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in CandidatePaths(assembly))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths(Assembly assembly)
    {
        var fileName = NativeFileName();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ProbeRoots(assembly))
        {
            var beside = Path.Combine(root, fileName);
            if (seen.Add(beside))
            {
                yield return beside;
            }

            var dir = root;
            for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(dir); depth++)
            {
                foreach (var rid in RidCandidates())
                {
                    var candidate = Path.Combine(dir, "runtimes", rid, "native", fileName);
                    if (seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }

                dir = Path.GetDirectoryName(dir);
            }
        }
    }

    private static IEnumerable<string> ProbeRoots(Assembly assembly)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(AppContext.BaseDirectory) && seen.Add(AppContext.BaseDirectory))
        {
            yield return AppContext.BaseDirectory;
        }

        var location = assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            yield break;
        }

        var asmDir = Path.GetDirectoryName(location);
        if (!string.IsNullOrEmpty(asmDir) && seen.Add(asmDir))
        {
            yield return asmDir;
        }
    }

    private static IEnumerable<string> RidCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrEmpty(current) && !string.Equals(current, "unknown", StringComparison.OrdinalIgnoreCase) && seen.Add(current))
        {
            yield return current;
        }

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => null,
        };
        if (arch is null)
        {
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            if (seen.Add("win-" + arch))
            {
                yield return "win-" + arch;
            }

            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (seen.Add("osx-" + arch))
            {
                yield return "osx-" + arch;
            }

            yield break;
        }

        if (OperatingSystem.IsLinux() && seen.Add("linux-" + arch))
        {
            yield return "linux-" + arch;
        }
    }

    private static string NativeFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "symdex_treesitter.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "libsymdex_treesitter.dylib";
        }

        return "libsymdex_treesitter.so";
    }
}
