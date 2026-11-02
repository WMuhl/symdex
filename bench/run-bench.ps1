#requires -Version 7
[CmdletBinding()]
param(
    [string]$TasksPath,
    [string]$ResultsPath,
    [string]$Symdex,
    [string]$Only,
    [string]$Date = (Get-Date -Format 'yyyy-MM-dd'),
    [string]$WorkRoot
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $TasksPath) { $TasksPath = Join-Path $PSScriptRoot 'tasks.json' }
if (-not $ResultsPath) { $ResultsPath = Join-Path $PSScriptRoot "results/$Date.json" }
if (-not $WorkRoot) { $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) 'symdex-bench/work' }
if (-not $Symdex) {
    $name = $(if ($IsWindows) { 'symdex.exe' } else { 'symdex' })
    $Symdex = Join-Path $RepoRoot "src/Symdex.Cli/bin/Release/net10.0/$name"
}
if (-not (Test-Path $Symdex)) {
    throw "symdex binary not found at $Symdex. Build Release first: dotnet build symdex.sln -c Release"
}

$tasks = Get-Content -Raw -Path $TasksPath | ConvertFrom-Json
$rules = $tasks.rules
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-Captured {
    param([string]$File, [string[]]$CmdArgs, [string]$Cwd)
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $File
    $psi.WorkingDirectory = $Cwd
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8
    foreach ($a in $CmdArgs) { [void]$psi.ArgumentList.Add($a) }
    $proc = [Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if (-not $proc.Start()) { throw "failed to start $File" }
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $proc.WaitForExit()
    $sw.Stop()
    [pscustomobject]@{
        ExitCode = $proc.ExitCode
        Stdout   = $outTask.GetAwaiter().GetResult()
        Stderr   = $errTask.GetAwaiter().GetResult()
        ElapsedMs = $sw.ElapsedMilliseconds
    }
}

function Get-MedianMs {
    param([long[]]$Values)
    $sorted = @($Values | Sort-Object)
    $sorted[[int](($sorted.Count - 1) / 2)]
}

function Convert-ToSet {
    param($Items)
    $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in @($Items)) {
        if ($item) { [void]$set.Add([string]$item) }
    }
    return ,$set
}

function Get-LineByteRanges {
    param([byte[]]$Bytes)
    $ranges = [System.Collections.Generic.List[object]]::new()
    if ($Bytes.Length -eq 0) { return $ranges }
    $start = 0
    for ($i = 0; $i -lt $Bytes.Length; $i++) {
        if ($Bytes[$i] -eq 10) {
            $ranges.Add(@($start, $i))
            $start = $i + 1
        }
    }
    if ($start -lt $Bytes.Length) {
        $endByte = $Bytes.Length - 1
        $ranges.Add(@($start, $endByte))
    }
    return ,$ranges
}

function Get-SpanBytes {
    param([string]$Repo, $Spans)
    $byFile = @{}
    foreach ($span in @($Spans)) {
        if (-not $span -or -not $span.File) { continue }
        if (-not $byFile.ContainsKey($span.File)) { $byFile[$span.File] = [System.Collections.Generic.List[int]]::new() }
        for ($ln = $span.Start; $ln -le $span.End; $ln++) { [void]$byFile[$span.File].Add($ln) }
    }
    $total = [long]0
    foreach ($file in $byFile.Keys) {
        $path = Join-Path $Repo ($file -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path $path)) { continue }
        $ranges = Get-LineByteRanges ([IO.File]::ReadAllBytes($path))
        $seen = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($ln in $byFile[$file]) { [void]$seen.Add($ln) }
        foreach ($ln in $seen) {
            if ($ln -ge 1 -and $ln -le $ranges.Count) {
                $range = $ranges[$ln - 1]
                $total += ($range[1] - $range[0] + 1)
            }
        }
    }
    $total
}

function Merge-Spans {
    param($Spans)
    $merged = [System.Collections.Generic.List[object]]::new()
    foreach ($group in (@($Spans) | Group-Object File)) {
        $curStart = $null
        $curEnd = $null
        foreach ($span in ($group.Group | Sort-Object Start, End)) {
            if ($null -eq $curStart) {
                $curStart = $span.Start
                $curEnd = $span.End
                continue
            }
            if ($span.Start -le ($curEnd + 1)) {
                if ($span.End -gt $curEnd) { $curEnd = $span.End }
            } else {
                $merged.Add([pscustomobject]@{ File = $group.Name; Start = $curStart; End = $curEnd })
                $curStart = $span.Start
                $curEnd = $span.End
            }
        }
        if ($null -ne $curStart) {
            $merged.Add([pscustomobject]@{ File = $group.Name; Start = $curStart; End = $curEnd })
        }
    }
    return ,$merged
}

function Get-SymdexHits {
    param([string]$Stdout, [string]$DefaultFile)
    $hits = [System.Collections.Generic.List[object]]::new()
    $truncated = $false
    $ambiguous = $false
    foreach ($raw in ($Stdout -split "`r?`n")) {
        $line = $raw.Trim()
        if (-not $line) { continue }
        try { $obj = $line | ConvertFrom-Json } catch { continue }
        if ($obj.truncated -eq $true) { $truncated = $true }
        if ($obj.ambiguous -eq $true) { $ambiguous = $true }
        $file = $obj.filePath
        $start = $null
        $end = $null
        if ($obj.PSObject.Properties.Name -contains 'line' -and $null -ne $obj.line -and "$($obj.line)" -match '^\d+$') {
            $start = [int]$obj.line
            $end = $start
        } elseif ($obj.PSObject.Properties.Name -contains 'startLine' -and $null -ne $obj.startLine) {
            $start = [int]$obj.startLine
            $end = $start
            if ($obj.endLine) { $end = [int]$obj.endLine }
            if (-not $file) { $file = $DefaultFile }
        }
        if ($file -and $start) {
            $hits.Add([pscustomobject]@{
                File  = (($file -replace '\\', '/').TrimStart('./'))
                Start = $start
                End   = [Math]::Max($start, $end)
            })
        }
    }
    [pscustomobject]@{ Hits = $hits; Truncated = $truncated; Ambiguous = $ambiguous }
}

function Get-ChosenSpans {
    param($Hits, [int]$SpanCap, [int]$MaxSpans)
    $chosen = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($hit in @($Hits)) {
        if ($chosen.Count -ge $MaxSpans) { break }
        $end = [Math]::Min($hit.End, $hit.Start + $SpanCap - 1)
        $key = "$($hit.File):$($hit.Start)-$end"
        if (-not $seen.Add($key)) { continue }
        $chosen.Add([pscustomobject]@{ File = $hit.File; Start = $hit.Start; End = $end })
    }
    return ,$chosen
}

function Get-Score {
    param($Predicted, $Expected)
    $pred = Convert-ToSet $Predicted
    $exp = Convert-ToSet $Expected
    $tp = 0
    $fn = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $exp) {
        if ($pred.Contains($item)) { $tp++ } else { $fn.Add($item) }
    }
    $fp = 0
    $fpItems = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $pred) {
        if (-not $exp.Contains($item)) {
            $fp++
            if ($fpItems.Count -lt 25) { $fpItems.Add($item) }
        }
    }
    $precision = $null
    $recall = $null
    if ($pred.Count -eq 0 -and $exp.Count -eq 0) {
        $precision = 1.0
        $recall = 1.0
    } elseif ($pred.Count -eq 0) {
        $precision = $null
        $recall = 0.0
    } elseif ($exp.Count -eq 0) {
        $precision = 0.0
        $recall = 1.0
    } else {
        $precision = $tp / $pred.Count
        $recall = $tp / $exp.Count
    }
    [pscustomobject]@{
        Tp             = $tp
        Fp             = $fp
        Fn             = $fn.Count
        Precision      = $precision
        Recall         = $recall
        PredictedCount = $pred.Count
        ExpectedCount  = $exp.Count
        FalseNegatives = @($fn)
        FalsePositives = @($fpItems)
    }
}

function Get-GrepHits {
    param([string]$Repo, [string]$Term)
    $run = Invoke-Captured 'git' @('grep', '-n', '-I', '-F', '--', $Term) $Repo
    if ($run.ExitCode -ne 0 -and $run.ExitCode -ne 1) {
        throw "git grep failed ($($run.ExitCode)): $($run.Stderr)"
    }
    $hits = [System.Collections.Generic.List[object]]::new()
    foreach ($raw in ($run.Stdout -split "`r?`n")) {
        if ($raw -match '^(?<file>.+?):(?<line>\d+):') {
            $hits.Add([pscustomobject]@{
                File = ($Matches.file -replace '\\', '/')
                Line = [int]$Matches.line
            })
        }
    }
    [pscustomobject]@{
        ExitCode = $run.ExitCode
        Stdout   = $run.Stdout
        Hits     = $hits
        ElapsedMs = $run.ElapsedMs
    }
}

function Get-IndexBytes {
    param([string]$Repo)
    $dir = Join-Path $Repo '.index'
    if (-not (Test-Path $dir)) { return 0 }
    $measured = Get-ChildItem $dir -File -Recurse | Measure-Object Length -Sum
    if ($null -eq $measured.Sum) { 0 } else { [long]$measured.Sum }
}

function Get-MachineInfo {
    $cpu = $null
    $ramGb = $null
    if ($IsWindows) {
        $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
        $ramGb = [math]::Round(((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB), 2)
    }
    $sdks = @(& dotnet --list-sdks)
    [ordered]@{
        os = [Environment]::OSVersion.VersionString
        powershell = $PSVersionTable.PSVersion.ToString()
        dotnet = (& dotnet --version)
        sdks = $sdks
        cpu = $cpu
        ramGb = $ramGb
    }
}

function Invoke-SymdexQuery {
    param([string]$Repo, [string[]]$Prefix, [int]$Limit, [string]$Verb, [string[]]$QueryArgs)
    $argList = [System.Collections.Generic.List[string]]::new()
    [void]$argList.Add('--repo')
    [void]$argList.Add($Repo)
    [void]$argList.Add('--json')
    [void]$argList.Add('--limit')
    [void]$argList.Add("$Limit")
    foreach ($a in @($Prefix)) { if ($a) { [void]$argList.Add($a) } }
    [void]$argList.Add($Verb)
    foreach ($a in @($QueryArgs)) { if ($null -ne $a -and "$a" -ne '') { [void]$argList.Add([string]$a) } }
    Invoke-Captured $Symdex $argList.ToArray() $Repo
}

$commit = (Invoke-Captured 'git' @('rev-parse', 'HEAD') $RepoRoot).Stdout.Trim()
$machine = Get-MachineInfo
if (Test-Path $WorkRoot) { Remove-Item -Recurse -Force $WorkRoot }
New-Item -ItemType Directory -Force -Path $WorkRoot | Out-Null

$targetResults = [System.Collections.Generic.List[object]]::new()
$failed = $false

foreach ($target in @($tasks.targets)) {
    if ($Only -and $target.id -ne $Only) { continue }
    Write-Host "=== $($target.id) ==="
    $dest = Join-Path $WorkRoot $target.id
    $entry = [ordered]@{
        id = $target.id
        language = $target.language
        url = $target.url
        tag = $target.tag
        commit = $target.commit
        error = $null
    }
    try {
        $clone = Invoke-Captured 'git' @(
            '-c', 'advice.detachedHead=false',
            'clone', '--depth', '1', '--branch', $target.tag, $target.url, $dest
        ) $WorkRoot
        if ($clone.ExitCode -ne 0) { throw "clone failed ($($clone.ExitCode)): $($clone.Stdout) $($clone.Stderr)" }
        $entry.cloneMs = $clone.ElapsedMs
        $entry.shallowClone = $true
        if ($target.unshallow -eq $true) {
            $deep = Invoke-Captured 'git' @('-C', $dest, 'fetch', '--unshallow') $WorkRoot
            if ($deep.ExitCode -ne 0) { throw "unshallow failed: $($deep.Stderr)" }
            $entry.unshallowMs = $deep.ElapsedMs
            $entry.shallowClone = $false
            $entry.unshallowReason = $target.unshallowReason
        }
        $head = (Invoke-Captured 'git' @('-C', $dest, 'rev-parse', 'HEAD') $WorkRoot).Stdout.Trim()
        $entry.resolvedCommit = $head
        if ($head -ne $target.commit) { throw "commit mismatch: tasks has $($target.commit), clone is $head" }

        $entry.prep = @()
        foreach ($step in @($target.prep)) {
            if (-not $step) { continue }
            $prep = Invoke-Captured $step.file @($step.args) $dest
            $entry.prep += [ordered]@{
                file = $step.file
                args = @($step.args)
                exitCode = $prep.ExitCode
                elapsedMs = $prep.ElapsedMs
                stderrTail = $(if ($prep.Stderr.Length -gt 500) { $prep.Stderr.Substring($prep.Stderr.Length - 500) } else { $prep.Stderr.Trim() })
            }
            if ($prep.ExitCode -ne 0) { throw "prep failed: $($step.file) $($step.args -join ' ')" }
        }

        $indexDir = Join-Path $dest '.index'
        if (Test-Path $indexDir) { Remove-Item -Recurse -Force $indexDir }

        $prefix = @($target.symdexArgs)
        $limit = [int]$rules.limit
        $questionsOut = [System.Collections.Generic.List[object]]::new()
        $coldDone = $false

        foreach ($question in @($target.questions)) {
            Write-Host "  $($question.id)"
            $qArgs = @($question.args)
            $defaultFile = $question.file
            if (-not $coldDone) {
                $cold = Invoke-SymdexQuery $dest $prefix $limit $question.verb $qArgs
                $coldDone = $true
                $entry.cold = [ordered]@{
                    question = $question.id
                    elapsedMs = $cold.ElapsedMs
                    exitCode = $cold.ExitCode
                    stderr = $cold.Stderr.Trim()
                    reindexedFiles = $null
                    warnings = @()
                    indexBytes = (Get-IndexBytes $dest)
                }
                if ($cold.Stderr -match 'reindexed (\d+) files') { $entry.cold.reindexedFiles = [int]$Matches[1] }
                $warns = [System.Collections.Generic.List[string]]::new()
                foreach ($errLine in ($cold.Stderr -split "`r?`n")) {
                    if ($errLine.TrimStart().StartsWith('symdex:')) { $warns.Add($errLine.Trim()) }
                }
                $entry.cold.warnings = @($warns)
                $entry.cold.roslynFallback = @($warns).Count -gt 0 -and $target.language -eq 'csharp'
            }

            $warm = @()
            $last = $null
            for ($i = 0; $i -lt [int]$rules.warmRuns; $i++) {
                $last = Invoke-SymdexQuery $dest $prefix $limit $question.verb $qArgs
                $warm += [long]$last.ElapsedMs
            }
            $parsed = Get-SymdexHits $last.Stdout $defaultFile
            $pred = [System.Collections.Generic.List[string]]::new()
            foreach ($hit in $parsed.Hits) { [void]$pred.Add("$($hit.File):$($hit.Start)") }
            $uniquePred = @($pred | Sort-Object -Unique)
            $chosen = Get-ChosenSpans $parsed.Hits ([int]$rules.spanCapLines) ([int]$rules.maxSpans)
            $merged = Merge-Spans $chosen
            $spanBytes = Get-SpanBytes $dest $merged
            $stdoutBytes = [long]($utf8.GetByteCount($last.Stdout))
            $symScore = Get-Score $uniquePred @($question.expected)
            $warmReindexed = $last.Stderr -match 'reindexed \d+ files'

            $base = [ordered]@{
                kind = $question.baseline
                toolCalls = $null
                outputBytes = 0
                fileBytes = 0
                totalBytes = 0
                approxTokens = 0
                elapsedMs = $null
                precision = $null
                recall = $null
                predictedCount = $null
                filesOpened = @()
            }
            if ($question.baseline -eq 'read') {
                $path = Join-Path $dest (($question.file -replace '/', [IO.Path]::DirectorySeparatorChar))
                $fileBytes = [long](Get-Item $path).Length
                $base.toolCalls = 1
                $base.fileBytes = $fileBytes
                $base.totalBytes = $fileBytes
                $base.approxTokens = [math]::Round($fileBytes / [double]$rules.tokenDivisor, 1)
                $covered = 0
                foreach ($exp in @($question.expected)) {
                    if ($exp.StartsWith("$($question.file):")) { $covered++ }
                }
                $expCount = @($question.expected).Count
                $base.recall = $(if ($expCount -eq 0) { 1.0 } else { $covered / $expCount })
                $base.precision = $null
                $base.predictedCount = $null
                $base.filesOpened = @($question.file)
                $base.note = 'full-file read; no extracted locations, so precision is undefined'
            } else {
                $grep = Get-GrepHits $dest $question.grep
                $files = [System.Collections.Generic.List[string]]::new()
                $seenFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                foreach ($hit in $grep.Hits) {
                    if ($seenFiles.Add($hit.File)) { $files.Add($hit.File) }
                }
                $open = @($files | Select-Object -First ([int]$rules.grepK))
                $fileBytes = [long]0
                foreach ($rel in $open) {
                    $path = Join-Path $dest ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
                    if (Test-Path $path) { $fileBytes += [long](Get-Item $path).Length }
                }
                $grepPred = [System.Collections.Generic.List[string]]::new()
                foreach ($hit in $grep.Hits) { [void]$grepPred.Add("$($hit.File):$($hit.Line)") }
                $grepScore = Get-Score @($grepPred | Sort-Object -Unique) @($question.expected)
                $outBytes = [long]($utf8.GetByteCount($grep.Stdout))
                $base.toolCalls = 1 + $open.Count
                $base.outputBytes = $outBytes
                $base.fileBytes = $fileBytes
                $base.totalBytes = $outBytes + $fileBytes
                $base.approxTokens = [math]::Round(($outBytes + $fileBytes) / [double]$rules.tokenDivisor, 1)
                $base.elapsedMs = $grep.ElapsedMs
                $base.precision = $grepScore.Precision
                $base.recall = $grepScore.Recall
                $base.predictedCount = $grepScore.PredictedCount
                $base.tp = $grepScore.Tp
                $base.fp = $grepScore.Fp
                $base.fn = $grepScore.Fn
                $base.falseNegatives = @($grepScore.FalseNegatives)
                $base.falsePositives = @($grepScore.FalsePositives)
                $base.filesOpened = @($open)
                $base.grepHitCount = $grep.Hits.Count
            }

            $symTotal = $stdoutBytes + $spanBytes
            $ratio = $null
            if ($base.totalBytes -gt 0) { $ratio = [math]::Round($symTotal / $base.totalBytes, 4) }
            $questionsOut.Add([ordered]@{
                id = $question.id
                ask = $question.ask
                verb = $question.verb
                args = @($qArgs)
                notes = $question.notes
                expectedCount = @($question.expected).Count
                warmMs = @($warm)
                warmMedianMs = (Get-MedianMs $warm)
                warmReindexed = [bool]$warmReindexed
                exitCode = $last.ExitCode
                truncated = [bool]$parsed.Truncated
                ambiguous = [bool]$parsed.Ambiguous
                rawLocations = $parsed.Hits.Count
                uniqueLocations = $uniquePred.Count
                symdex = [ordered]@{
                    stdoutBytes = $stdoutBytes
                    spanBytes = $spanBytes
                    totalBytes = $symTotal
                    approxTokens = [math]::Round($symTotal / [double]$rules.tokenDivisor, 1)
                    toolCalls = 1 + @($merged).Count
                    spansRead = @($merged).Count
                    precision = $symScore.Precision
                    recall = $symScore.Recall
                    tp = $symScore.Tp
                    fp = $symScore.Fp
                    fn = $symScore.Fn
                    falseNegatives = @($symScore.FalseNegatives)
                    falsePositives = @($symScore.FalsePositives)
                    duplicateFactor = $(if ($uniquePred.Count -gt 0) { [math]::Round($parsed.Hits.Count / $uniquePred.Count, 2) } else { $null })
                }
                baseline = $base
                contextRatio = $ratio
            })
        }

        $incr = $target.incremental
        $before = Invoke-SymdexQuery $dest $prefix $limit $incr.verb @($incr.args)
        $beforeHits = Get-SymdexHits $before.Stdout $null
        $beforeLines = @($beforeHits.Hits | Where-Object { $_.File -eq $incr.watchFile } | ForEach-Object { $_.Start } | Sort-Object -Unique)
        $touchPath = Join-Path $dest ($incr.touch -replace '/', [IO.Path]::DirectorySeparatorChar)
        [IO.File]::AppendAllText($touchPath, "`n")
        $after = Invoke-SymdexQuery $dest $prefix $limit $incr.verb @($incr.args)
        $afterHits = Get-SymdexHits $after.Stdout $null
        $afterLines = @($afterHits.Hits | Where-Object { $_.File -eq $incr.watchFile } | ForEach-Object { $_.Start } | Sort-Object -Unique)
        $entry.incremental = [ordered]@{
            touch = $incr.touch
            verb = $incr.verb
            args = @($incr.args)
            watchFile = $incr.watchFile
            elapsedMs = $after.ElapsedMs
            stderr = $after.Stderr.Trim()
            reindexedFiles = $null
            linesBefore = @($beforeLines)
            linesAfter = @($afterLines)
            watchedLinesDropped = (@($beforeLines).Count -gt 0 -and @($afterLines).Count -eq 0)
        }
        if ($after.Stderr -match 'reindexed (\d+) files') { $entry.incremental.reindexedFiles = [int]$Matches[1] }

        $entry.questions = @($questionsOut)
    } catch {
        $failed = $true
        $entry.error = "$_"
        Write-Host "  ERROR: $_"
    }
    $targetResults.Add($entry)
}

function Get-Mean {
    param($Values)
    $nums = @($Values | Where-Object { $null -ne $_ })
    if ($nums.Count -eq 0) { return $null }
    ($nums | Measure-Object -Average).Average
}

$summary = [System.Collections.Generic.List[object]]::new()
$allQuestions = @($targetResults | Where-Object { -not $_.error } | ForEach-Object { $_.questions })
foreach ($target in @($targetResults | Where-Object { -not $_.error })) {
    $qs = @($target.questions)
    $symBytes = [long]0
    $baseBytes = [long]0
    foreach ($q in $qs) {
        $symBytes += [long]$q.symdex.totalBytes
        $baseBytes += [long]$q.baseline.totalBytes
    }
    $summary.Add([ordered]@{
        id = $target.id
        questions = $qs.Count
        coldMs = $target.cold.elapsedMs
        indexBytes = $target.cold.indexBytes
        reindexedFiles = $target.cold.reindexedFiles
        roslynFallback = $target.cold.roslynFallback
        symdexBytes = $symBytes
        baselineBytes = $baseBytes
        contextRatio = $(if ($baseBytes -gt 0) { [math]::Round($symBytes / $baseBytes, 4) } else { $null })
        symdexPrecision = (Get-Mean ($qs | ForEach-Object { $_.symdex.precision }))
        symdexRecall = (Get-Mean ($qs | ForEach-Object { $_.symdex.recall }))
        baselinePrecision = (Get-Mean ($qs | ForEach-Object { $_.baseline.precision }))
        baselineRecall = (Get-Mean ($qs | ForEach-Object { $_.baseline.recall }))
        warmMedianMs = (Get-MedianMs @($qs | ForEach-Object { [long]$_.warmMedianMs }))
    })
}

$grandSym = [long]0
$grandBase = [long]0
foreach ($row in $summary) {
    $grandSym += [long]$row.symdexBytes
    $grandBase += [long]$row.baselineBytes
}

$results = [ordered]@{
    date = $Date
    symdexCommit = $commit
    symdexBinary = $Symdex
    machine = $machine
    rules = $rules
    scoring = $tasks.scoring
    summary = @($summary)
    overall = [ordered]@{
        symdexBytes = $grandSym
        baselineBytes = $grandBase
        contextRatio = $(if ($grandBase -gt 0) { [math]::Round($grandSym / $grandBase, 4) } else { $null })
        symdexPrecision = (Get-Mean ($allQuestions | ForEach-Object { $_.symdex.precision }))
        symdexRecall = (Get-Mean ($allQuestions | ForEach-Object { $_.symdex.recall }))
        baselinePrecision = (Get-Mean ($allQuestions | ForEach-Object { $_.baseline.precision }))
        baselineRecall = (Get-Mean ($allQuestions | ForEach-Object { $_.baseline.recall }))
        questionCount = @($allQuestions).Count
    }
    targets = @($targetResults)
}

New-Item -ItemType Directory -Force -Path (Split-Path $ResultsPath) | Out-Null
$json = $results | ConvertTo-Json -Depth 40
[IO.File]::WriteAllText($ResultsPath, $json, $utf8)

Write-Host ''
Write-Host 'target          cold_ms   index_kb  sym_bytes  base_bytes  ratio  sym_P  sym_R  base_P  base_R'
foreach ($row in $summary) {
    $p = { param($n) if ($null -eq $n) { '  n/a' } else { '{0,5:N2}' -f $n } }
    Write-Host ('{0,-14} {1,8} {2,9} {3,10} {4,11} {5,6:N2} {6} {7} {8} {9}' -f `
        $row.id, $row.coldMs, [int]($row.indexBytes / 1KB), $row.symdexBytes, $row.baselineBytes, $row.contextRatio, `
        (& $p $row.symdexPrecision), (& $p $row.symdexRecall), (& $p $row.baselinePrecision), (& $p $row.baselineRecall))
}
Write-Host ('OVERALL sym={0} base={1} ratio={2} P={3} R={4} baseP={5} baseR={6} n={7}' -f `
    $results.overall.symdexBytes, $results.overall.baselineBytes, $results.overall.contextRatio, `
    $results.overall.symdexPrecision, $results.overall.symdexRecall, `
    $results.overall.baselinePrecision, $results.overall.baselineRecall, $results.overall.questionCount)
Write-Host "wrote $ResultsPath"
if ($failed) { exit 1 }
