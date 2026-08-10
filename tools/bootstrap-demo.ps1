<#
.SYNOPSIS
    Takes a running but empty app to a demo-ready state.

.DESCRIPTION
    Four steps that otherwise have to be clicked through, in the one order that avoids the slow
    path:

      1. Load the committed demo corpus.
      2. Process with extraction OFF. This chunks, embeds and indexes in a couple of minutes.
      3. Import the committed extraction and process again. Every chunk is a cache hit, so the
         graph is built and committed without a single model call.
      4. Warm the answer cache for the demo question at all twelve rungs.

    Step 3 is the point. Extracting the graph with a 3B local model takes about an hour and
    produces 8 usable edges; importing the committed extraction takes a minute and produces 235.
    The seven filters, the funnel and the review gate still run on what is imported — only the
    model call is skipped.

    Safe to re-run. Everything downstream is content-hashed, so a second run is nearly instant.

.EXAMPLE
    pwsh tools/bootstrap-demo.ps1
    pwsh tools/bootstrap-demo.ps1 -SkipWarm          # leave the answer cache cold
    pwsh tools/bootstrap-demo.ps1 -LocalExtraction   # extract with the local model instead (slow)
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:5099',
    [string] $ExtractionFile = 'response.json',
    [string] $Question = 'Who plays Peter Parker?',
    [switch] $SkipWarm,
    [switch] $LocalExtraction
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }

# ----- 0. the app has to be up ------------------------------------------------

try { $health = Invoke-RestMethod "$BaseUrl/api/health" -TimeoutSec 30 }
catch { throw "No app at $BaseUrl. Start it with: dotnet run --project src/RagLadder.Api" }

Write-Host ("health: {0}" -f $health.status) -ForegroundColor $(if ($health.status -eq 'ok') { 'Green' } else { 'Yellow' })
foreach ($p in $health.providers) {
    $colour = if ($p.status -eq 'ok') { 'DarkGray' } else { 'Yellow' }
    Write-Host ("  {0,-9} {1,-16} {2}" -f $p.name, $p.status, $p.detail) -ForegroundColor $colour
}
if (-not $health.embedder.passed) {
    Write-Warning "The embedder probe is below the acceptance band. Retrieval quality will not be representative — do not demo on this."
}

# ----- 1. load ----------------------------------------------------------------

Step 1 'Loading the committed demo corpus'
$doc = Invoke-RestMethod -Method Post "$BaseUrl/api/documents/load-demo" -TimeoutSec 120
Write-Host ("  {0} — {1} ({2} pages)" -f $doc.id, $doc.title, $doc.pageCount)
$docId = $doc.id

function Wait-ForJob($label) {
    $spin = 0
    while ($true) {
        Start-Sleep -Seconds 5
        $job = (Invoke-RestMethod "$BaseUrl/api/documents/$docId/status" -TimeoutSec 60).job
        if (-not $job) { continue }
        Write-Host ("`r  {0}: {1,-58}" -f $label, ($job.message ?? '...').PadRight(58).Substring(0, 58)) -NoNewline
        if ($job.failed) { Write-Host ''; throw "Processing failed: $($job.message)" }
        if ($job.completed) { Write-Host ''; return $job }
        if ($job.awaitingReview) { Write-Host ''; return $job }
        $spin++
    }
}

# ----- 2. chunk, embed, index -------------------------------------------------

if ($LocalExtraction) {
    Step 2 'Processing with local extraction — this is the hour-long path'
    $body = @{ mode = 'quick'; skipReview = $true; skipSectionSummaries = $true } | ConvertTo-Json
} else {
    Step 2 'Processing with extraction OFF — chunks, embeddings and three collections'
    $body = @{ mode = 'quick'; skipExtraction = $true; skipSectionSummaries = $true } | ConvertTo-Json
}
Invoke-RestMethod -Method Post "$BaseUrl/api/documents/$docId/process" -ContentType 'application/json' -Body $body -TimeoutSec 120 | Out-Null
Wait-ForJob 'processing' | Out-Null

$detail = Invoke-RestMethod "$BaseUrl/api/documents/$docId" -TimeoutSec 60
$counts = ($detail.chunkCounts.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ' · '
Write-Host "  chunks: $counts"

# ----- 3. the graph -----------------------------------------------------------

if (-not $LocalExtraction) {
    Step 3 "Importing $ExtractionFile and rebuilding the graph from cache"
    if (-not (Test-Path $ExtractionFile)) {
        throw "Missing $ExtractionFile. Either restore it, or re-run with -LocalExtraction to extract with the local model."
    }
    & "$PSScriptRoot/import-extraction.ps1" -File $ExtractionFile -Process -BaseUrl $BaseUrl -DocumentId $docId
} else {
    Step 3 'Graph already built by the local extraction above'
    $ex = Invoke-RestMethod "$BaseUrl/api/documents/$docId/extraction" -TimeoutSec 120
    Write-Host ("  {0} entities, {1} relations" -f $ex.entities.Count, $ex.relations.Count)
}

# ----- 4. warm ----------------------------------------------------------------

if ($SkipWarm) {
    Write-Host "`nSkipping the warm-up. The first ask at each rung will take minutes." -ForegroundColor DarkYellow
} else {
    Step 4 "Warming all twelve rungs for `"$Question`""
    Write-Host '  Cold, this is the one-off cost — up to half an hour on a CPU model.' -ForegroundColor DarkGray
    & "$PSScriptRoot/warm-cache.ps1" -Question $Question -BaseUrl $BaseUrl -DocumentId $docId
}

# ----- done -------------------------------------------------------------------

Write-Host "`nReady." -ForegroundColor Green
Write-Host "  Open $BaseUrl and go to Ask. Type: $Question"
Write-Host "  Change only the stage and watch the answer change. Tick 'show the work' for the pipeline behind each one."
