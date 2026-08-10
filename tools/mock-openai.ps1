<#
.SYNOPSIS
    A tiny OpenAI-compatible chat endpoint for offline testing.

.DESCRIPTION
    Speaks POST /v1/chat/completions and answers deterministically by inspecting the prompt:
    extraction requests get schema-shaped JSON built from the corpus's own credit formatting,
    verification passes everything, and answer requests echo the retrieved context.

    It is a test double, not a model. Use it to prove the OpenAI-compatible client, the extraction
    filter chain and the graph pipeline all work when you have no reachable LLM — then point the
    same configuration at your organisation's real endpoint.

.EXAMPLE
    pwsh tools/mock-openai.ps1                     # terminal 1
    # then configure:
    #   RagLadder:Providers:Chat        = openai
    #   RagLadder:OpenAiCompatible:BaseUrl = http://localhost:11555/v1
    #   RagLadder:OpenAiCompatible:ApiKey  = mock
#>
[CmdletBinding()]
param(
    [int] $Port = 11555
)

$ErrorActionPreference = 'Stop'

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "Mock OpenAI-compatible endpoint on http://localhost:$Port/v1/chat/completions" -ForegroundColor Green
Write-Host "Ctrl+C to stop.`n"

function Get-ChunkText([string] $user) {
    $marker = 'CHUNK'
    $i = $user.IndexOf($marker)
    if ($i -lt 0) { return '' }
    $body = $user.Substring($i)
    $first = $body.IndexOf('---')
    $last = $body.LastIndexOf('---')
    if ($first -lt 0 -or $last -le $first) { return '' }
    $body.Substring($first + 3, $last - $first - 3).Trim()
}

function New-Extraction([string] $chunk) {
    $entities = [System.Collections.ArrayList]::new()
    $relations = [System.Collections.ArrayList]::new()
    $seen = @{}

    # A year range means a series, not a film — "Loki (2021-2023)". Getting this right is what
    # lets the Loki character and the Loki series exist as two nodes, which is trap 12.
    $work = $null; $workType = 'Film'
    $m = [regex]::Match($chunk, 'Section \d+ - (?<t>[^(\n]+?) \((?<y>\d{4})(?<range>\s*[-–]\s*\d{4})?\)')
    if ($m.Success) {
        $work = $m.Groups['t'].Value.Trim()
        $workType = if ($m.Groups['range'].Success) { 'TVSeries' } else { 'Film' }
        $entity = @{ name = $work; type = $workType; evidence = $m.Value }
        if ($workType -eq 'Film') { $entity.year = [int]$m.Groups['y'].Value }
        $null = $entities.Add($entity)
        $seen["$workType|$work"] = $true
    }

    function Add-Entity($name, $type, $ev) {
        if (-not $seen.ContainsKey("$type|$name")) {
            $seen["$type|$name"] = $true
            $null = $script:entities.Add(@{ name = $name; type = $type; evidence = $ev })
        }
    }
    $script:entities = $entities

    foreach ($cm in [regex]::Matches($chunk, '(?<p>[A-Z][\w''.-]+(?: [A-Z][\w''.-]+){0,3}) as (?<c>[A-Z][\w''.-]+(?: [A-Z][\w''.-]+){0,3})')) {
        $p = $cm.Groups['p'].Value.Trim(); $c = $cm.Groups['c'].Value.Trim()
        if ($p.Length -lt 4 -or $c.Length -lt 3) { continue }
        Add-Entity $p 'Person' $cm.Value
        Add-Entity $c 'Character' $cm.Value
        $null = $relations.Add(@{ subject = $p; predicate = 'PLAYED'; object = $c; evidence = $cm.Value; confidence = 0.95 })
        if ($work) { $null = $relations.Add(@{ subject = $p; predicate = 'ACTED_IN'; object = $work; evidence = $cm.Value; confidence = 0.85 }) }
    }
    foreach ($cm in [regex]::Matches($chunk, 'Original score composed by (?<p>[A-Z][\w''.-]+(?: [A-Z][\w''.-]+){0,3})')) {
        $p = $cm.Groups['p'].Value.Trim(); Add-Entity $p 'Person' $cm.Value
        if ($work) { $null = $relations.Add(@{ subject = $p; predicate = 'COMPOSED_FOR'; object = $work; evidence = $cm.Value; confidence = 0.9 }) }
    }
    foreach ($cm in [regex]::Matches($chunk, 'Directors? (?<p>[A-Z][\w''.-]+(?: [A-Z][\w''.-]+){0,3}(?:, [A-Z][\w''.-]+(?: [A-Z][\w''.-]+){0,3})*)')) {
        foreach ($name in $cm.Groups['p'].Value -split ', ') {
            $n = $name.Trim(); if ($n.Length -lt 4) { continue }
            Add-Entity $n 'Person' $cm.Value
            if ($work) { $null = $relations.Add(@{ subject = $n; predicate = 'DIRECTED'; object = $work; evidence = $cm.Value; confidence = 0.92 }) }
        }
    }
    foreach ($cm in [regex]::Matches($chunk, 'Director of photography (?<p>[A-Z][\w''.-]+(?: [A-Z][\w''.-]+){0,3})')) {
        $p = $cm.Groups['p'].Value.Trim(); Add-Entity $p 'Person' $cm.Value
        if ($work) { $null = $relations.Add(@{ subject = $work; predicate = 'SHOT_BY'; object = $p; evidence = $cm.Value; confidence = 0.9 }) }
    }

    @{ entities = @($entities); relations = @($relations) } | ConvertTo-Json -Depth 6 -Compress
}

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $reader = [System.IO.StreamReader]::new($context.Request.InputStream)
        $raw = $reader.ReadToEnd(); $reader.Close()

        $content = 'Not found in the provided documents.'
        try {
            $req = $raw | ConvertFrom-Json
            $system = ($req.messages | Where-Object role -eq 'system' | Select-Object -First 1).content
            $user = ($req.messages | Where-Object role -eq 'user'   | Select-Object -Last 1).content

            if ($system -match 'extract a knowledge graph') {
                $content = New-Extraction (Get-ChunkText $user)
            }
            elseif ($system -match 'fact-checking judge') {
                $n = ([regex]::Matches($user, '(?m)^\s*\[\d+\]')).Count
                $verdicts = 0..([Math]::Max(0, $n - 1)) | ForEach-Object { @{ index = $_; verdict = 'SUPPORTED'; reason = 'mock' } }
                $content = @{ verdicts = @($verdicts) } | ConvertTo-Json -Depth 4 -Compress
            }
            elseif ($system -match 'score how well each passage') {
                $idx = [regex]::Matches($user, '(?m)^\[(\d+)\]') | ForEach-Object { [int]$_.Groups[1].Value }
                $scores = $idx | ForEach-Object { @{ index = $_; score = [math]::Round(1.0 - ($_ * 0.05), 2) } }
                $content = @{ scores = @($scores) } | ConvertTo-Json -Depth 4 -Compress
            }
            elseif ($system -match 'Rewrite the user') { $content = '{"rewritten":"REWRITTEN","keywords":[]}' }
            elseif ($system -match 'Classify the question') { $content = '{"classification":"lookup","rationale":"mock"}' }
            elseif ($system -match 'You plan retrieval') { $content = '{"action":"answer","thought":"mock planner"}' }
            elseif ($system -match 'Identify the two people') { $content = '{"from":null,"to":null}' }
            elseif ($system -match 'Summarise the section') { $content = 'Mock summary of the section.' }
            elseif ($system -match 'from your own knowledge') { $content = 'Mock unconstrained answer from parametric knowledge.' }
            elseif ($user -match 'CONTEXT') {
                if ($user -match '\(no context was retrieved\)') { $content = 'Not found in the provided documents.' }
                else {
                    $start = $user.IndexOf('CONTEXT'); $end = $user.IndexOf('QUESTION')
                    $context = $user.Substring($start, $end - $start)
                    $question = $user.Substring($end)

                    # Crude grounding check, so the ungrounded control group behaves. A real model
                    # judges this properly; the mock approximates it by asking whether the
                    # question's distinctive words appear in the retrieved text at all.
                    $stop = 'what,which,who,whom,whose,when,where,how,many,much,does,did,the,and,for,was,were,with,that,this,from,are,has,have,had,its,their,name,both,total,score,composed,music'
                    $stopSet = @{}; $stop -split ',' | ForEach-Object { $stopSet[$_] = $true }
                    $terms = [regex]::Matches($question.ToLowerInvariant(), '[a-z][a-z0-9'']{3,}') |
                        ForEach-Object { $_.Value } | Where-Object { -not $stopSet.ContainsKey($_) } | Select-Object -Unique
                    $lowerContext = $context.ToLowerInvariant()
                    $hits = @($terms | Where-Object { $lowerContext.Contains($_) }).Count

                    if ($terms.Count -gt 0 -and ($hits / [double]$terms.Count) -lt 0.5) {
                        $content = 'Not found in the provided documents.'
                    }
                    else {
                        $ctxLen = [Math]::Min(900, $context.Length)
                        $content = 'ANSWER-FROM-CONTEXT: ' + ($context.Substring(0, $ctxLen) -replace '\s+', ' ')
                    }
                }
            }
        }
        catch { $content = 'Not found in the provided documents.' }

        $payload = @{
            id      = 'chatcmpl-mock'
            object  = 'chat.completion'
            model   = 'mock'
            choices = @(@{ index = 0; message = @{ role = 'assistant'; content = $content }; finish_reason = 'stop' })
            usage   = @{ prompt_tokens = 0; completion_tokens = 0; total_tokens = 0 }
        } | ConvertTo-Json -Depth 8

        $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        $context.Response.ContentType = 'application/json'
        $context.Response.StatusCode = 200
        $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $context.Response.Close()
    }
}
finally {
    $listener.Stop(); $listener.Close()
}
