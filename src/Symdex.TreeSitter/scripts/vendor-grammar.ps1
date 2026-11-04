# Downloads a tree-sitter release tag and copies the files symdex vendors into
# grammars/<name>/. Does not edit grammars/GRAMMARS.md — update that manifest
# with the tag, commit SHA, and file list printed at the end.
#
# Usage: vendor-grammar.ps1 -Name <core|c-sharp|typescript|python> -Tag <tag>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("core", "c-sharp", "typescript", "python")]
    [string]$Name,

    [Parameter(Mandatory = $true)]
    [string]$Tag
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$grammarsRoot = Join-Path (Split-Path -Parent $scriptDir) "grammars"

$specs = @{
    "core" = @{
        Repo = "tree-sitter/tree-sitter"
        Dest = "core"
        Paths = @("LICENSE", "lib/include/tree_sitter/api.h", "lib/src")
        Exclude = @("lib/src/unicode/LICENSE", "lib/src/unicode/README.md", "lib/src/unicode/ICU_SHA")
    }
    "c-sharp" = @{
        Repo = "tree-sitter/tree-sitter-c-sharp"
        Dest = "tree-sitter-c-sharp"
        Paths = @(
            "LICENSE", "src/parser.c", "src/scanner.c", "src/node-types.json",
            "src/tree_sitter/parser.h", "src/tree_sitter/array.h", "src/tree_sitter/alloc.h")
        Exclude = @()
    }
    "typescript" = @{
        Repo = "tree-sitter/tree-sitter-typescript"
        Dest = "tree-sitter-typescript"
        Paths = @("LICENSE", "common/scanner.h", "typescript/src", "tsx/src")
        Exclude = @()
    }
    "python" = @{
        Repo = "tree-sitter/tree-sitter-python"
        Dest = "tree-sitter-python"
        Paths = @(
            "LICENSE", "src/parser.c", "src/scanner.c", "src/node-types.json",
            "src/tree_sitter/parser.h", "src/tree_sitter/array.h", "src/tree_sitter/alloc.h")
        Exclude = @()
    }
}

$spec = $specs[$Name]
$repoUrl = "https://github.com/$($spec.Repo)"
$archive = "$repoUrl/archive/refs/tags/$Tag.tar.gz"

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("symdex-vendor-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $work | Out-Null

try {
    $tarball = Join-Path $work "grammar.tar.gz"
    Write-Host "symdex: downloading $archive"
    Invoke-WebRequest -Uri $archive -OutFile $tarball -UseBasicParsing
    tar -xzf $tarball -C $work
    $extracted = @(Get-ChildItem -Path $work -Directory)
    if ($extracted.Count -ne 1) {
        throw "expected one top-level directory in the tag archive, found $($extracted.Count)"
    }

    $sourceRoot = $extracted[0].FullName
    $destRoot = Join-Path $grammarsRoot $spec.Dest
    if (Test-Path $destRoot) {
        Remove-Item -Recurse -Force $destRoot
    }
    New-Item -ItemType Directory -Force -Path $destRoot | Out-Null

    $copied = @()
    foreach ($relative in $spec.Paths) {
        $src = Join-Path $sourceRoot ($relative -replace "/", "\")
        if (-not (Test-Path $src)) {
            throw "archive is missing $relative"
        }

        if (Test-Path $src -PathType Container) {
            foreach ($file in (Get-ChildItem -Path $src -Recurse -File)) {
                $rel = $file.FullName.Substring($sourceRoot.Length + 1) -replace "\\", "/"
                if ($spec.Exclude -contains $rel) {
                    continue
                }

                $out = Join-Path $destRoot ($rel -replace "/", "\")
                New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
                Copy-Item -Path $file.FullName -Destination $out -Force
                $copied += $rel
            }
        }
        else {
            $rel = $relative
            $out = Join-Path $destRoot ($rel -replace "/", "\")
            New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
            Copy-Item -Path $src -Destination $out -Force
            $copied += $rel
        }
    }

    $commit = ""
    $peeled = & git ls-remote $repoUrl "refs/tags/${Tag}^{}"
    if ($peeled) {
        $commit = ($peeled -split "\s+")[0]
    }
    else {
        $tagLine = & git ls-remote $repoUrl "refs/tags/${Tag}"
        if ($tagLine) {
            $commit = ($tagLine -split "\s+")[0]
        }
    }

    Write-Host ""
    Write-Host "symdex: copied $($copied.Count) files into grammars/$($spec.Dest)"
    Write-Host "  repo:    $repoUrl"
    Write-Host "  tag:     $Tag"
    Write-Host "  commit:  $(if ($commit) { $commit } else { '(could not resolve)' })"
    Write-Host "  files:"
    foreach ($rel in $copied) {
        Write-Host "    $rel"
    }

    $coreApi = Join-Path $grammarsRoot "core\lib\include\tree_sitter\api.h"
    $maxAbi = $null
    $minAbi = $null
    if (Test-Path $coreApi) {
        $apiText = [IO.File]::ReadAllText($coreApi)
        if ($apiText -match "#define TREE_SITTER_LANGUAGE_VERSION\s+(\d+)") { $maxAbi = $Matches[1] }
        if ($apiText -match "#define TREE_SITTER_MIN_COMPATIBLE_LANGUAGE_VERSION\s+(\d+)") { $minAbi = $Matches[1] }
        Write-Host "  core ABI: $minAbi..$maxAbi"
    }

    $parserFiles = @(Get-ChildItem -Path $destRoot -Recurse -Filter parser.c -File)
    foreach ($parser in $parserFiles) {
        $fs = [IO.File]::OpenRead($parser.FullName)
        try {
            $buf = New-Object byte[] 800
            $n = $fs.Read($buf, 0, $buf.Length)
            $head = [Text.Encoding]::UTF8.GetString($buf, 0, $n)
        }
        finally {
            $fs.Close()
        }

        if ($head -match "#define LANGUAGE_VERSION\s+(\d+)") {
            $ver = [int]$Matches[1]
            $relParser = $parser.FullName.Substring($destRoot.Length + 1)
            $note = "LANGUAGE_VERSION $ver ($relParser)"
            if ($null -ne $maxAbi -and $null -ne $minAbi -and ($ver -gt [int]$maxAbi -or $ver -lt [int]$minAbi)) {
                Write-Host "  WARNING: $note is outside core ABI $minAbi..$maxAbi"
            }
            else {
                Write-Host "  $note"
            }
        }
    }

    Write-Host ""
    Write-Host "symdex: update grammars/GRAMMARS.md with this tag, commit, and file list before committing."
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
