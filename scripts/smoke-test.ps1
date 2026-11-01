# Installs a locally packed symdex nupkg and checks that the Roslyn feeder and the
# tree-sitter native library both work from the dotnet tool layout.
param(
    [Parameter(Position = 0)]
    [string]$Nupkg,
    [string]$RepoRoot
)

$ErrorActionPreference = "Stop"
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"

function Fail([string]$Message) {
    [Console]::Error.WriteLine($Message)
    exit 1
}

function Quote-Arg([string]$Value) {
    if ($Value -notmatch '[\s"]') {
        return $Value
    }
    return '"' + ($Value -replace '"', '\"') + '"'
}

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$RepoRoot = (Resolve-Path $RepoRoot).Path

if (-not $Nupkg) {
    $searchDirs = @(
        (Join-Path $RepoRoot "artifacts\package"),
        (Join-Path $RepoRoot "src\Symdex.Cli\bin\Release")
    )
    $found = foreach ($dir in $searchDirs) {
        if (Test-Path $dir) {
            Get-ChildItem -Path $dir -Filter "symdex.*.nupkg" |
                Where-Object { $_.Name -notlike "*.symbols.nupkg" }
        }
    }
    $pick = $found | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $pick) {
        Fail "smoke: no symdex nupkg found; pass -Nupkg or pack into artifacts/package"
    }
    $Nupkg = $pick.FullName
}
$Nupkg = (Resolve-Path $Nupkg).Path

$packageFile = [IO.Path]::GetFileNameWithoutExtension($Nupkg)
if ($packageFile -notmatch '^symdex\.(.+)$') {
    Fail "smoke: expected symdex.<version>.nupkg, got $packageFile"
}
$version = $Matches[1]
$sourceDir = Split-Path -Parent $Nupkg

$ridArch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    "X64" { "x64" }
    "Arm64" { "arm64" }
    "X86" { "x86" }
    default { Fail "smoke: unsupported architecture $([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)" }
}
$nativeRel = "runtimes/win-$ridArch/native/symdex_treesitter.dll"

$work = Join-Path ([IO.Path]::GetTempPath()) ("symdex-smoke-" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $config = Join-Path $work "nuget.config"
    $packageCache = Join-Path $work "packages"
    $configXml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$packageCache" />
  </config>
  <packageSources>
    <clear />
    <add key="local" value="$sourceDir" />
  </packageSources>
</configuration>
"@
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [IO.File]::WriteAllText($config, $configXml, $utf8)

    $toolDir = Join-Path $work "tool"
    Write-Host "smoke: installing symdex $version from $Nupkg"
    & dotnet tool install symdex --tool-path $toolDir --configfile $config --version $version
    if ($LASTEXITCODE -ne 0) {
        Fail "smoke: dotnet tool install failed ($LASTEXITCODE)"
    }

    $exe = Join-Path $toolDir "symdex.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        $exe = Join-Path $toolDir "symdex"
    }
    if (-not (Test-Path -LiteralPath $exe)) {
        Fail "smoke: symdex was not installed into $toolDir"
    }

    $native = Get-ChildItem -Path $toolDir -Recurse -File -Filter "symdex_treesitter.dll" |
        Where-Object { ($_.FullName -replace '\\', '/') -like "*$nativeRel" } |
        Select-Object -First 1
    if (-not $native) {
        $laidOut = Get-ChildItem -Path $toolDir -Recurse -File | ForEach-Object { $_.FullName }
        Fail "smoke: $nativeRel missing from the tool install.`n$($laidOut -join "`n")"
    }
    Write-Host "smoke: native $($native.FullName)"

    $sample = Join-Path $work "sample-repo"
    $python = Join-Path $work "python-sample"
    Copy-Item -Recurse (Join-Path $RepoRoot "tests\fixtures\sample-repo") $sample
    Copy-Item -Recurse (Join-Path $RepoRoot "tests\fixtures\python-sample") $python

    Write-Host "smoke: restoring sample repo"
    & dotnet restore (Join-Path $sample "SampleRepo.sln") --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        Fail "smoke: dotnet restore failed ($LASTEXITCODE)"
    }

    function Invoke-Symdex([string[]]$SymdexArgs) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $exe
        $psi.WorkingDirectory = $work
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.Arguments = ($SymdexArgs | ForEach-Object { Quote-Arg $_ }) -join " "
        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        [void]$process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            Code = $process.ExitCode
            Out = $stdoutTask.Result
            Err = $stderrTask.Result
        }
    }

    function Assert-SymdexRan($Step, $Result) {
        if ($Result.Code -ne 0) {
            Fail "smoke: $Step exited $($Result.Code)`nSTDOUT:`n$($Result.Out)`nSTDERR:`n$($Result.Err)"
        }
        if ($Result.Err -match 'symdex:') {
            Fail "smoke: $Step wrote a symdex warning (Roslyn or native fallback)`n$($Result.Err)`nSTDOUT:`n$($Result.Out)"
        }
    }

    Write-Host "smoke: refs IPaymentProcessor"
    $refs = Invoke-Symdex @("refs", "IPaymentProcessor", "--json", "--repo", $sample)
    Assert-SymdexRan "refs" $refs
    $implementation = $false
    foreach ($line in ($refs.Out -split "`r?`n")) {
        if ($line -match '"kind"\s*:\s*"implementation"' -and $line -match 'SampleLib/PaymentProcessor\.cs') {
            $implementation = $true
        }
    }
    if (-not $implementation) {
        Fail "smoke: refs JSON has no Roslyn implementation of IPaymentProcessor`n$($refs.Out)`nSTDERR:`n$($refs.Err)"
    }
    Write-Host $refs.Out
    if ($refs.Err.Trim().Length -gt 0) {
        Write-Host $refs.Err
    }

    Write-Host "smoke: outline math.ts"
    $ts = Invoke-Symdex @("outline", (Join-Path $sample "ts\math.ts"), "--repo", $sample)
    Assert-SymdexRan "outline math.ts" $ts
    if ($ts.Out -notmatch '(?m)\badd\b') {
        Fail "smoke: outline of math.ts did not contain add`n$($ts.Out)`nSTDERR:`n$($ts.Err)"
    }
    Write-Host $ts.Out

    Write-Host "smoke: outline calc.py"
    $py = Invoke-Symdex @("outline", (Join-Path $python "calc.py"), "--repo", $python)
    Assert-SymdexRan "outline calc.py" $py
    if ($py.Out -notmatch 'Calculator') {
        Fail "smoke: outline of calc.py did not contain Calculator`n$($py.Out)`nSTDERR:`n$($py.Err)"
    }
    Write-Host $py.Out

    Write-Host "smoke: ok"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
