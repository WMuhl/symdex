# Compiles the vendored tree-sitter runtime + grammars (..\grammars) into a single native
# library named symdex_treesitter.dll using MSVC (cl.exe). Invoked as an MSBuild pre-build
# step on Windows. If no MSVC compiler can be found, prints a message and exits 0 (success) -
# the resulting build has no tree-sitter support, which is treated as a soft dependency:
# Symdex.TreeSitter.Native.NativeLibraryProbe detects the missing library at run time and the
# indexer falls back to reporting no symbols for tree-sitter-only file types.
param(
    [Parameter(Mandatory = $true)]
    [string]$OutDir
)

$ErrorActionPreference = "Stop"
$env:VSCMD_SKIP_SENDTELEMETRY = "1"
$scriptDir = $PSScriptRoot
$grammars = Join-Path (Split-Path -Parent $scriptDir) "grammars"

# Start-Process -Wait waits for the whole process tree, and cl.exe can leave a long-lived
# vctip.exe telemetry child behind, so wait on the compiler process alone.
function Invoke-Cl {
    param([string]$ClPath, [string[]]$Arguments)
    $p = Start-Process -FilePath $ClPath -ArgumentList $Arguments -NoNewWindow -PassThru
    $null = $p.Handle
    $p.WaitForExit()
    return $p.ExitCode
}

function Import-VcVars {
    param([string]$VcVarsPath)
    $lines = cmd /c "`"$VcVarsPath`" >nul 2>&1 && set"
    foreach ($line in $lines) {
        if ($line -match '^([^=]+)=(.*)$') {
            [System.Environment]::SetEnvironmentVariable($Matches[1], $Matches[2])
        }
    }
}

$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $cl) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    $vcvars = $null
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath 2>$null
        if ($installPath) {
            $candidate = Join-Path $installPath "VC\Auxiliary\Build\vcvars64.bat"
            if (Test-Path $candidate) {
                $vcvars = $candidate
            }
        }
    }

    if (-not $vcvars) {
        Write-Host "symdex: no MSVC compiler (cl.exe) found; skipping tree-sitter native build."
        exit 0
    }

    Import-VcVars -VcVarsPath $vcvars
    $cl = Get-Command cl.exe -ErrorAction SilentlyContinue
    if (-not $cl) {
        Write-Host "symdex: MSVC environment script ran but cl.exe is still not on PATH; skipping."
        exit 0
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid())
New-Item -ItemType Directory -Force -Path $workDir | Out-Null

try {
    $sources = @(
        @{ Path = Join-Path $grammars "core\lib\src\lib.c"; Inc = @(
            (Join-Path $grammars "core\lib\src"),
            (Join-Path $grammars "core\lib\src\wasm"),
            (Join-Path $grammars "core\lib\include")) },
        @{ Path = Join-Path $grammars "tree-sitter-c-sharp\src\parser.c"; Inc = @((Join-Path $grammars "tree-sitter-c-sharp\src")) },
        @{ Path = Join-Path $grammars "tree-sitter-c-sharp\src\scanner.c"; Inc = @((Join-Path $grammars "tree-sitter-c-sharp\src")) },
        @{ Path = Join-Path $grammars "tree-sitter-typescript\typescript\src\parser.c"; Inc = @((Join-Path $grammars "tree-sitter-typescript\typescript\src")) },
        @{ Path = Join-Path $grammars "tree-sitter-typescript\typescript\src\scanner.c"; Inc = @((Join-Path $grammars "tree-sitter-typescript\typescript\src")) },
        @{ Path = Join-Path $grammars "tree-sitter-typescript\tsx\src\parser.c"; Inc = @((Join-Path $grammars "tree-sitter-typescript\tsx\src")) },
        @{ Path = Join-Path $grammars "tree-sitter-typescript\tsx\src\scanner.c"; Inc = @((Join-Path $grammars "tree-sitter-typescript\tsx\src")) },
        @{ Path = Join-Path $grammars "tree-sitter-python\src\parser.c"; Inc = @((Join-Path $grammars "tree-sitter-python\src")) },
        @{ Path = Join-Path $grammars "tree-sitter-python\src\scanner.c"; Inc = @((Join-Path $grammars "tree-sitter-python\src")) }
    )

    $objs = @()
    $i = 0
    foreach ($s in $sources) {
        $i++
        $obj = Join-Path $workDir "obj_$i.obj"
        $incArgs = $s.Inc | ForEach-Object { "/I`"$_`"" }
        $argLine = @("/nologo", "/c", "/std:c11", "/wd4996") + $incArgs + @("/Fo`"$obj`"", "`"$($s.Path)`"")
        $exitCode = Invoke-Cl -ClPath $cl.Source -Arguments $argLine
        if ($exitCode -ne 0) {
            throw "cl.exe failed compiling $($s.Path) (exit $exitCode)"
        }

        $objs += $obj
    }

    # The tree-sitter runtime's public API (ts_parser_new, ts_node_*, ts_query_*, ...) only gets
    # __declspec(dllexport) on GCC/Clang builds (see grammars/core/lib/include/tree_sitter/api.h);
    # MSVC needs an explicit .def file or these symbols aren't exported and DllImport fails at
    # run time with EntryPointNotFoundException. The grammar entry points (tree_sitter_c_sharp
    # etc.) already self-export via their own TS_PUBLIC macro, so they're deliberately left out
    # of exports.def to avoid a duplicate-export warning.
    $defFile = Join-Path $scriptDir "exports.def"
    $dll = Join-Path $OutDir "symdex_treesitter.dll"
    $linkArgs = @("/nologo", "/LD") + ($objs | ForEach-Object { "`"$_`"" }) + @("/Fe:`"$dll`"", "/link", "/DEF:`"$defFile`"")
    $exitCode = Invoke-Cl -ClPath $cl.Source -Arguments $linkArgs
    if ($exitCode -ne 0) {
        throw "cl.exe link step failed (exit $exitCode)"
    }

    Write-Host "symdex: built native\symdex_treesitter.dll"
}
finally {
    Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}
