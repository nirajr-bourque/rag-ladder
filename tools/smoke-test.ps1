<#
.SYNOPSIS
    End-to-end smoke test against a running instance.

.DESCRIPTION
    Walks the demo the way a person would: load the corpus, process it, inspect the review gate,
    commit the graph, then exercise each rung of the ladder and the three stage-10 modes.

    Checks that depend on a live model are skipped automatically when no chat provider is
    configured, and say so rather than failing quietly.

.EXAMPLE
    dotnet run --project src/RagLadder.Api        # in one terminal
    pwsh tools/smoke-test.ps1                     # in another
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:5099',
    [switch] $SkipExtraction,
    [int] $ChunkCap = 120
)

$ErrorActionPreference = 'Stop'
$script:Pass = 0; $script:Fail = 0; $script:Skip = 0

function Check($name, [scriptblock] $test) {
    try {
        $result = & $test
        if ($result -is [string]) { Write-Host ("  SKIP  {0} — {1}" -f $name, $result) -ForegroundColor DarkYellow; $script:Skip++ }
        elseif ($result) { Write-Host ("  ok    {0}" -f $name) -ForegroundColor Green; $script:Pass++ }
        else { Write-Host ("  FAIL  {0}" -f $name) -ForegroundColor Red; $script:Fail++ }
    }
    catch {
        Write-Host ("  FAIL  {0} — {1}" -f $name, $_.Exception.Message) -ForegroundColor Red
        $script:Fail++
    }
}

function Api($method, $path, $body) {
    $args = @{ Method = $method; Uri = "$BaseUrl$path" }
    if ($null -ne $body) { $args.ContentType = 'application/json'; $args.Body = ($body | ConvertTo-Json -Depth 8) }
    Invoke-RestMethod @args
}

Write-Host "`n=== health ===" -ForegroundColor Cyan
$health = Api GET '/api/health'
Write-Host ("  status: {0}" -f $health.status)
$health.providers | ForEach-Object { Write-Host ("    {0,-9} {1,-14} {2}" -f $_.name, $_.status, $_.detail) -ForegroundColor DarkGray }
$liveChat = ($health.providers | Where-Object name -eq 'chat' | Select-Object -First 1).status -eq 'ok'
$realEmbedder = ($health.providers | Where-Object name -eq 'embedder' | Select-Object -First 1).status -eq 'ok'

Check 'the UI is served' { (Invoke-WebRequest "$BaseUrl/" -UseBasicParsing).StatusCode -eq 200 }
Check 'presentation mode is served' { (Invoke-WebRequest "$BaseUrl/?present=1" -UseBasicParsing).StatusCode -eq 200 }
Check 'the embedder probe meets the acceptance band' {
    if (-not $realEmbedder) { return 'dev stand-in; run tools/fetch-models.ps1' }
    $health.embedder.passed
}

Write-Host "`n=== load and process ===" -ForegroundColor Cyan
$doc = Api POST '/api/documents/load-demo'
Write-Host ("  document: {0} ({1})" -f $doc.id, $doc.title)

$request = @{ mode = 'thorough'; skipReview = $true; chunkCap = $ChunkCap; spreadSampling = $true; skipExtraction = [bool]$SkipExtraction -or -not $liveChat }
if ($request.skipExtraction -and -not $SkipExtraction) {
    Write-Host "  no live chat provider — processing vectors only" -ForegroundColor DarkYellow
}
Api POST "/api/documents/$($doc.id)/process" $request | Out-Null

$job = $null
for ($i = 0; $i -lt 1200; $i++) {
    Start-Sleep -Milliseconds 750
    $job = (Api GET "/api/documents/$($doc.id)/status").job
    if ($job.completed -or $job.failed -or $job.awaitingReview) { break }
}
Write-Host ("  {0} — {1}" -f $job.stage, $job.message)
$job.warnings | ForEach-Object { Write-Host ("    ! {0}" -f $_) -ForegroundColor DarkYellow }

Check 'processing finished without failing' { -not $job.failed }

$detail = Api GET "/api/documents/$($doc.id)"
$counts = $detail.chunkCounts
Write-Host ("  sections: {0}   chunks: {1}" -f $detail.sections.Count,
    (($counts.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '))

Check 'front matter parsed for most sections' {
    ($detail.sections | Where-Object { $_.frontMatter.docType }).Count -gt ($detail.sections.Count * 0.8)
}
Check 'all three collections were indexed' {
    $counts.fixed -gt 20 -and $counts.recursive -gt 20 -and $counts.contextual -gt 20
}
Check 'the fixed strategy is structure-blind (fewer, page-scoped chunks)' {
    $counts.fixed -lt $counts.recursive
}

Write-Host "`n=== traps ===" -ForegroundColor Cyan
$fixedChunks = (Api GET "/api/documents/$($doc.id)/chunks?strategy=fixed&take=500").chunks.rawText
$recursiveChunks = (Api GET "/api/documents/$($doc.id)/chunks?strategy=recursive&take=500").chunks.rawText
$contextualChunks = (Api GET "/api/documents/$($doc.id)/chunks?strategy=contextual&take=500").chunks.text

Check 'trap 1 — the filmography splits under fixed chunking' {
    ($fixedChunks | Where-Object { $_ -match 'Iron Man 2 \(2010\) - Nick Fury' -and $_ -match 'The Marvels \(2023\) - Nick Fury' }).Count -eq 0
}
Check 'trap 1 — recursive chunking keeps it whole' {
    ($recursiveChunks | Where-Object { $_ -match 'Iron Man 2 \(2010\) - Nick Fury' -and $_ -match 'The Marvels \(2023\) - Nick Fury' }).Count -ge 1
}
Check 'trap 6 — the orphan chunk names neither series nor character' {
    $orphan = $recursiveChunks | Where-Object { $_ -match 'confronts the neighbour' } | Select-Object -First 1
    $orphan -and ($orphan -notmatch 'WandaVision')
}
Check 'trap 6 — the contextual prefix supplies the referent' {
    ($contextualChunks | Where-Object { $_ -match 'confronts the neighbour' -and $_ -match 'WandaVision' }).Count -ge 1
}
Check 'trap 11 — the two Fantastic Four records keep different years' {
    $years = $detail.sections |
        Where-Object { $_.frontMatter.subject -eq 'Fantastic Four' -and $_.heading -like 'Section *' } |
        ForEach-Object { $_.frontMatter.year } | Sort-Object
    ($years -join ',') -eq '2005,2015'
}
Check 'both appendices were stripped before ingestion' {
    -not ($recursiveChunks -match 'Corpus equivalent|Tony Stark performer|Question that breaks|Trap 10, spelled out')
}

Write-Host "`n=== review gate and graph ===" -ForegroundColor Cyan
if ($request.skipExtraction) {
    Write-Host "  skipped (no extraction was run)" -ForegroundColor DarkYellow
    $script:Skip += 5
}
else {
    $extraction = Api GET "/api/documents/$($doc.id)/extraction"
    $f = $extraction.funnel
    Write-Host ("  funnel: {0} extracted -> {1} grounded -> {2} conformant ({3} flipped) -> {4} resolved -> {5} verified" -f `
            $f.extracted, $f.grounded, $f.conformant, $f.flipped, $f.resolved, $f.verified)
    $m = $extraction.metrics
    Write-Host ("  grounding {0:P0}  conformance {1:P0}  flip {2:P0}  RELATED_TO {3:P0}  merge ratio {4:N2}" -f `
            $m.groundingPassRate, $m.conformanceRate, $m.directionFlipRate, $m.relatedToShare, $m.entityMergeRatio)
    Write-Host ("  cross-type name collisions blocked: {0}" -f $m.crossTypeNameCollisions)

    Check 'grounding pass rate above 0.70' { $m.groundingPassRate -gt 0.70 }
    Check 'direction flip rate below 0.15' { $m.directionFlipRate -lt 0.15 }
    Check 'RELATED_TO share below 0.20' { $m.relatedToShare -lt 0.20 }

    $commit = Api POST "/api/documents/$($doc.id)/graph/commit"
    Write-Host ("  committed: {0} nodes, {1} edges, {2} derived" -f $commit.nodes, $commit.edges, $commit.derivedEdges)
    Check 'the graph committed with derived collaboration edges' { $commit.nodes -gt 0 -and $commit.derivedEdges -gt 0 }

    Check 'trap 12 — Loki survives as more than one node type' {
        $entities = Api GET "/api/documents/$($doc.id)/graph/entities?q=Loki&limit=50"
        ($entities | Select-Object -ExpandProperty type -Unique).Count -ge 2
    }
}

Write-Host "`n=== the ladder ===" -ForegroundColor Cyan
$question = 'Who did the music for Black Panther?'
foreach ($stage in 0..11) {
    $r = Api POST "/api/ask/stage/$stage" @{ documentId = $doc.id; question = $question }
    $collection = if ($r.retrieval) { $r.retrieval.collection } else { 'none' }
    $flags = @()
    if ($r.retrieval.hybrid) { $flags += 'hybrid' }
    if ($r.retrieval.reranked) { $flags += 'rerank' }
    if ($r.rewrite) { $flags += 'rewrite' }
    if ($r.graph) { $flags += "graph:$($r.graph.mode)" }
    if ($r.router) { $flags += "route:$($r.router.route)" }
    if ($r.trace.Count) { $flags += "agentic:$($r.trace.Count)" }
    Write-Host ("  stage {0,2} {1,-18} {2,-11} {3,4}ms  {4}" -f `
            $stage, $r.stageName, $collection, $r.timings.totalMs, ($flags -join ' '))
}

Check 'stage 0 is flagged unconstrained and skips retrieval' {
    $r = Api POST '/api/ask/stage/0' @{ documentId = $doc.id; question = $question }
    $r.unconstrained -and -not $r.retrieval
}
Check 'stage 1 reads the fixed collection, stage 2 the recursive one' {
    (Api POST '/api/ask/stage/1' @{ documentId = $doc.id; question = $question }).retrieval.collection -eq 'fixed' -and
    (Api POST '/api/ask/stage/2' @{ documentId = $doc.id; question = $question }).retrieval.collection -eq 'recursive'
}
Check 'stage 4 finds an exact figure through the keyword arm' {
    $r = Api POST '/api/ask/stage/4' @{ documentId = $doc.id; question = '3,571,150,070' }
    # Reciprocal rank fusion weights both arms equally, so a pure-number query does not always
    # land at rank 1 — what must hold is that the keyword arm surfaced the figure's own chunk.
    $hit = $r.retrieval.chunks | Where-Object { $_.text -match '3,571,150,070' } | Select-Object -First 1
    $hit -and $hit.arm -in @('keyword', 'both')
}
Check 'stage 5 reports rank deltas' {
    $r = Api POST '/api/ask/stage/5' @{ documentId = $doc.id; question = 'Which crew member is credited with the original score?' }
    $r.retrieval.candidateCount -gt $r.retrieval.chunks.Count -and $null -ne $r.retrieval.candidates[0].rankBefore
}
Check 'stage 7 reads the contextual collection' {
    (Api POST '/api/ask/stage/7' @{ documentId = $doc.id; question = $question }).retrieval.collection -eq 'contextual'
}
Check 'no two stages share a cached answer' {
    # Unique per run: the answer cache lives in the process, so a repeated run would otherwise
    # see its own earlier probe and report a false failure.
    $probe = "cache probe $([guid]::NewGuid().ToString('N').Substring(0,8))"
    $a = Api POST '/api/ask/stage/2' @{ documentId = $doc.id; question = $probe }
    $b = Api POST '/api/ask/stage/4' @{ documentId = $doc.id; question = $probe }
    $again = Api POST '/api/ask/stage/2' @{ documentId = $doc.id; question = $probe }
    -not $a.fromCache -and -not $b.fromCache -and $again.fromCache
}
Check 'compare runs each rung independently' {
    $c = Api POST '/api/compare' @{ documentId = $doc.id; question = $question; stages = @(1, 2) }
    $c.results.Count -eq 2 -and $c.results[0].retrieval.collection -ne $c.results[1].retrieval.collection
}

Write-Host "`n=== stage 10 modes ===" -ForegroundColor Cyan
if ($request.skipExtraction) {
    Write-Host "  skipped (no graph was committed)" -ForegroundColor DarkYellow
    $script:Skip += 3
}
else {
    $people = Api GET "/api/documents/$($doc.id)/graph/entities?type=Person&limit=200"
    Write-Host ("  {0} Person nodes in the graph" -f $people.Count)

    Check 'path mode connects two people' {
        $found = $false
        foreach ($from in $people[0..([Math]::Min(9, $people.Count - 1))]) {
            foreach ($to in $people[0..([Math]::Min(9, $people.Count - 1))]) {
                if ($from.key -eq $to.key) { continue }
                $p = Api GET "/api/documents/$($doc.id)/graph/path?from=$([uri]::EscapeDataString($from.key))&to=$([uri]::EscapeDataString($to.key))&maxHops=8"
                if ($p.found) {
                    Write-Host ("    {0}" -f $p.path.narrative) -ForegroundColor DarkGray
                    $found = $true; break
                }
            }
            if ($found) { break }
        }
        $found
    }
    Check 'aggregate mode returns rows and its Cypher' {
        $a = Api GET "/api/documents/$($doc.id)/graph/aggregate?preset=studio-film-count&minConfidence=0"
        $a.cypher -match 'MATCH' -and $a.columns.Count -gt 0
    }
    Check 'expand mode reaches entities through chunk provenance' {
        $r = Api POST '/api/ask' @{
            documentId = $doc.id; question = 'Who is credited on Iron Man?'
            options    = @{ collection = 'recursive'; topK = 5; useGraphExpansion = $true; graphMode = 'expand'
                graphHops = @{ next = $true; parent = $true; entity = $true; entityRel = $true }; minEdgeConfidence = 0.0
            }
        }
        $r.graph.entitiesTouched.Count -gt 0
    }
}

Write-Host "`n=== golden set and eval ===" -ForegroundColor Cyan
$golden = Api POST "/api/documents/$($doc.id)/golden/load"
Write-Host ("  {0}: {1} questions" -f $golden.name, $golden.questions)
$golden.byType.PSObject.Properties | ForEach-Object { Write-Host ("    {0,-16} {1}" -f $_.Name, $_.Value) -ForegroundColor DarkGray }

Check '52 questions across 13 types, four each' {
    $golden.questions -ge 52 -and
    ($golden.byType.PSObject.Properties | Measure-Object).Count -eq 13 -and
    ($golden.byType.PSObject.Properties | Where-Object { $_.Value -ne 4 }).Count -eq 0
}

$run = Api POST "/api/documents/$($doc.id)/eval" @{ stages = @(0, 1, 2, 4, 10); questionIds = @() }
Write-Host ("  eval {0} running across 5 stages…" -f $run.runId)
for ($i = 0; $i -lt 2400; $i++) {
    Start-Sleep -Milliseconds 750
    $result = Api GET "/api/eval/$($run.runId)"
    if ($result.completed) { break }
}
Write-Host ("  overall: {0}" -f (($result.overallByStage.PSObject.Properties | ForEach-Object { "s$($_.Name)=$([math]::Round($_.Value*100))%" }) -join '  '))
$result.heatmapByType.PSObject.Properties | ForEach-Object {
    $row = ($_.Value.PSObject.Properties | ForEach-Object { "{0,4}" -f [math]::Round($_.Value * 100) }) -join ''
    Write-Host ("    {0,-16}{1}" -f $_.Name, $row) -ForegroundColor DarkGray
}
if ($result.regressions.Count) {
    Write-Host "  regressions:" -ForegroundColor DarkYellow
    $result.regressions | ForEach-Object { Write-Host ("    {0} passed at s{1}, failed at s{2}" -f $_.questionId, $_.fromStage, $_.toStage) -ForegroundColor DarkYellow }
}

Check 'the eval run completed and produced a per-type heatmap' {
    $result.completed -and ($result.heatmapByType.PSObject.Properties | Measure-Object).Count -gt 5
}
Check 'the ungrounded control group is refused by every stage above 0' {
    if (-not $liveChat) { return 'needs a live chat provider' }
    $cells = $result.cells | Where-Object { $_.type -eq 'ungrounded' -and $_.stage -gt 0 }
    ($cells | Where-Object { -not $_.refused }).Count -eq 0
}
Check 'stage 0 answers the ungrounded control group' {
    if (-not $liveChat) { return 'needs a live chat provider' }
    ($result.cells | Where-Object { $_.type -eq 'ungrounded' -and $_.stage -eq 0 -and $_.pass }).Count -gt 0
}

Write-Host ("`n=== {0} passed, {1} failed, {2} skipped ===`n" -f $script:Pass, $script:Fail, $script:Skip) `
    -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
exit ([int]($script:Fail -gt 0))
