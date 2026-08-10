<#
.SYNOPSIS
    Bring your own model: export the extraction prompt, or import a reply produced elsewhere.

.DESCRIPTION
    A small local model can fail the extraction task outright — inventing predicates, paraphrasing
    evidence, omitting the entities its own relations point at. Rather than weakening the filters
    to accommodate that, this externalises the one step the model is bad at.

      1. -Export  writes a single self-contained document with the instructions and every chunk.
      2. Paste that into a chat with a capable model. It replies with one JSON object.
      3. -File    imports that reply into the extraction cache.
      4. Run Process again. Every chunk is a cache hit, so no model calls are made.

    Only the model call moves. The seven filters, entity resolution, the funnel and the review gate
    all still run, so an imported triple must survive grounding and ontology conformance exactly
    like a locally produced one.

.EXAMPLE
    pwsh tools/import-extraction.ps1 -Export
    pwsh tools/import-extraction.ps1 -File response.json
    pwsh tools/import-extraction.ps1 -File response.json -Process
#>
[CmdletBinding(DefaultParameterSetName = 'Import')]
param(
    [Parameter(ParameterSetName = 'Export')][switch] $Export,
    [Parameter(ParameterSetName = 'Export')][string] $OutFile = 'extraction-request.md',
    [Parameter(ParameterSetName = 'Import', Mandatory = $true)][string] $File,
    [Parameter(ParameterSetName = 'Import')][switch] $Process,
    [string] $BaseUrl = 'http://localhost:5099',
    [string] $DocumentId
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $DocumentId) {
    $docs = Invoke-RestMethod "$BaseUrl/api/documents"
    if (-not $docs) { throw "No documents. Load and process one first." }
    $DocumentId = $docs[0].id
}
Write-Host "Document: $DocumentId" -ForegroundColor Cyan

# ---------------------------------------------------------------- export

if ($Export) {
    $markdown = Invoke-RestMethod "$BaseUrl/api/documents/$DocumentId/extraction/prompt"
    Set-Content -Path $OutFile -Value $markdown -Encoding UTF8
    $chunks = ([regex]::Matches($markdown, '(?m)^### chunkId:')).Count
    Write-Host ("Wrote {0} — {1:N0} characters, {2} chunks" -f $OutFile, $markdown.Length, $chunks) -ForegroundColor Green
    Write-Host ""
    Write-Host "Next:"
    Write-Host "  1. Open $OutFile and paste the whole thing into a chat with a capable model."
    Write-Host "  2. Save its JSON reply as response.json."
    Write-Host "  3. pwsh tools/import-extraction.ps1 -File response.json -Process"
    Write-Host ""
    Write-Host "If the document is too large for one message, split it: the chunk sections are"
    Write-Host "independent, so several replies can be merged into one file before importing."
    exit 0
}

# ---------------------------------------------------------------- import

if (-not (Test-Path $File)) { throw "Not found: $File" }
$raw = Get-Content $File -Raw

# Tolerate a reply wrapped in prose or a code fence.
if ($raw -notmatch '^\s*\{') {
    $start = $raw.IndexOf('{')
    $end = $raw.LastIndexOf('}')
    if ($start -lt 0 -or $end -le $start) { throw "No JSON object found in $File." }
    $raw = $raw.Substring($start, $end - $start + 1)
}

try { $payload = $raw | ConvertFrom-Json } catch { throw "Could not parse JSON from ${File}: $($_.Exception.Message)" }
if (-not $payload.chunks) { throw "Expected a top-level 'chunks' array in $File." }

Write-Host ("Importing {0} chunk result(s)…" -f $payload.chunks.Count)
$result = Invoke-RestMethod -Method Post "$BaseUrl/api/documents/$DocumentId/extraction/import" `
    -ContentType 'application/json' -Body $raw -TimeoutSec 300

Write-Host ("  imported {0} of {1} chunks — {2} entities, {3} relations" -f `
        $result.imported, $result.totalChunks, $result.entities, $result.relations) -ForegroundColor Green

if ($result.unknownChunkIds.Count -gt 0) {
    Write-Warning ("  {0} unknown chunk id(s) ignored: {1}" -f `
            $result.unknownChunkIds.Count, ($result.unknownChunkIds -join ', '))
    Write-Host "  Chunk ids must match exactly. Re-export if the document was reprocessed."
}

if ($result.imported -lt $result.totalChunks) {
    Write-Host ("  {0} chunk(s) have no imported result and will fall back to the local model." -f `
        ($result.totalChunks - $result.imported)) -ForegroundColor DarkYellow
}

if (-not $Process) {
    Write-Host ""
    Write-Host "Now run Process again (or re-run with -Process). Imported chunks are cache hits."
    exit 0
}

Write-Host "`nProcessing…" -ForegroundColor Cyan
$body = @{ mode = 'quick'; skipReview = $true; skipSectionSummaries = $true; spreadSampling = $false } | ConvertTo-Json
Invoke-RestMethod -Method Post "$BaseUrl/api/documents/$DocumentId/process" -ContentType 'application/json' -Body $body | Out-Null

$job = $null
for ($i = 0; $i -lt 600; $i++) {
    Start-Sleep -Seconds 5
    $job = (Invoke-RestMethod "$BaseUrl/api/documents/$DocumentId/status").job
    if ($job.completed -or $job.failed) { break }
}

if ($job.failed) { Write-Warning "Processing failed: $($job.message)"; exit 1 }

$ex = Invoke-RestMethod "$BaseUrl/api/documents/$DocumentId/extraction"
$f = $ex.funnel
Write-Host ""
Write-Host ("FUNNEL  extracted {0} -> grounded {1} -> conformant {2} ({3} flipped) -> committed {4}" -f `
        $f.extracted, $f.grounded, $f.conformant, $f.flipped, $ex.relations.Count) -ForegroundColor Green
Write-Host ("        {0} entities" -f $ex.entities.Count)
$ex.entities | Group-Object type | Sort-Object Count -Descending |
    ForEach-Object { Write-Host ("          {0,-12} {1}" -f $_.Name, $_.Count) -ForegroundColor DarkGray }

Write-Host "`nOpen http://localhost:7474 to see the graph, or the Explore tab in the app."
