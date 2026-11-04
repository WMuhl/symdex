# Triggers the first index build for the current repo. The index self-manages after this -
# every symdex verb re-hashes changed files and refreshes the database inline - so this script
# only needs to run once (e.g. right after the skill is installed) and is safe to re-run.

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SkillDir = Split-Path -Parent $ScriptDir
$SkillParent = Split-Path -Parent $SkillDir

function Find-Symdex {
    $candidates = @(
        (Join-Path $SkillParent "symdex.exe"),
        (Join-Path $SkillParent "symdex"),
        (Join-Path $SkillDir "symdex.exe"),
        (Join-Path $SkillDir "symdex")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $onPath = Get-Command symdex -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandType -eq "Application" } |
        Select-Object -First 1
    if ($onPath -and $onPath.Source) {
        return $onPath.Source
    }

    $toolHomes = @()
    if ($env:DOTNET_CLI_HOME) {
        $toolHomes += $env:DOTNET_CLI_HOME
    }
    if ($env:USERPROFILE) {
        $toolHomes += (Join-Path $env:USERPROFILE ".dotnet")
    }
    if ($env:HOME) {
        $toolHomes += (Join-Path $env:HOME ".dotnet")
    }
    foreach ($home in $toolHomes) {
        foreach ($name in @("symdex.exe", "symdex")) {
            $candidate = Join-Path (Join-Path $home "tools") $name
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }
    }

    return $null
}

$symdex = Find-Symdex
if (-not $symdex) {
    [Console]::Error.WriteLine("ensure-index: could not find the symdex binary next to the skill, on PATH, or as a dotnet tool; skipping.")
    exit 0
}

try {
    & $symdex find --limit 1 x *> $null
} catch {
}

exit 0
