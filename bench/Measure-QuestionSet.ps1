function Measure-QuestionSet {
    param(
        [string]$Dest,
        [string[]]$Prefix,
        $Questions,
        [string]$Language
    )

    $limit = [int]$rules.limit
    $questionsOut = [System.Collections.Generic.List[object]]::new()
    $coldEntry = $null

    foreach ($question in @($Questions)) {
        Write-Host "  $($question.id)"
        $qArgs = @($question.args)
        $defaultFile = $question.file
        if ($null -eq $coldEntry) {
            $cold = Invoke-SymdexQuery $Dest $Prefix $limit $question.verb $qArgs
            $warns = [System.Collections.Generic.List[string]]::new()
            foreach ($errLine in ($cold.Stderr -split "`r?`n")) {
                if ($errLine.TrimStart().StartsWith('symdex:')) { $warns.Add($errLine.Trim()) }
            }
            $reindexedFiles = $null
            if ($cold.Stderr -match 'reindexed (\d+) files') { $reindexedFiles = [int]$Matches[1] }
            $fallback = @($warns | Where-Object { $_ -notmatch "^symdex: using '" })
            $coldEntry = [ordered]@{
                question = $question.id
                elapsedMs = $cold.ElapsedMs
                exitCode = $cold.ExitCode
                stderr = $cold.Stderr.Trim()
                reindexedFiles = $reindexedFiles
                warnings = @($warns)
                indexBytes = (Get-IndexBytes $Dest)
                roslynFallback = $fallback.Count -gt 0 -and $Language -eq 'csharp'
            }
        }

        $warm = @()
        $last = $null
        for ($i = 0; $i -lt [int]$rules.warmRuns; $i++) {
            $last = Invoke-SymdexQuery $Dest $Prefix $limit $question.verb $qArgs
            $warm += [long]$last.ElapsedMs
        }
        $parsed = Get-SymdexHits $last.Stdout $defaultFile
        $pred = [System.Collections.Generic.List[string]]::new()
        foreach ($hit in $parsed.Hits) { [void]$pred.Add("$($hit.File):$($hit.Start)") }
        $uniquePred = @($pred | Sort-Object -Unique)
        $chosen = Get-ChosenSpans $parsed.Hits ([int]$rules.spanCapLines) ([int]$rules.maxSpans)
        $merged = Merge-Spans $chosen
        $spanBytes = Get-SpanBytes $Dest $merged
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
            $path = Join-Path $Dest (($question.file -replace '/', [IO.Path]::DirectorySeparatorChar))
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
            $grep = Get-GrepHits $Dest $question.grep
            $files = [System.Collections.Generic.List[string]]::new()
            $seenFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($hit in $grep.Hits) {
                if ($seenFiles.Add($hit.File)) { $files.Add($hit.File) }
            }
            $open = @($files | Select-Object -First ([int]$rules.grepK))
            $fileBytes = [long]0
            foreach ($rel in $open) {
                $path = Join-Path $Dest ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
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

    [pscustomobject]@{
        Cold = $coldEntry
        Questions = @($questionsOut)
    }
}
