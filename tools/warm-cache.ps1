<#
.SYNOPSIS
    Answer a question at every rung once, so the demo replays instantly.

.DESCRIPTION
    On a CPU-only machine a cold rung costs between seven seconds and seven minutes, which is not
    watchable in front of an audience. This asks the question at each stage in turn and leaves all
    twelve answers in the durable cache, where they survive a restart.

    Run it the night before, or any time you change the corpus, the graph or the model — all three
    are part of the cache key, so a stale answer is not possible, only a slow one.

.EXAMPLE
    pwsh tools/warm-cache.ps1
    pwsh tools/warm-cache.ps1 -Question 'Who did the music for Spider-Man: Homecoming?'
    pwsh tools/warm-cache.ps1 -Stages 0,1,2,3 -Question 'Who plays Peter Parker?'
    pwsh tools/warm-cache.ps1 -Show
#>
[CmdletBinding()]
param(
    [string[]] $Question = @('Who plays Peter Parker?'),
    [int[]]    $Stages,
    [string]   $BaseUrl = 'http://localhost:5099',
    [string]   $DocumentId,
    [switch]   $Show,
    [switch]   $Clear
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $DocumentId) {
    $docs = Invoke-RestMethod "$BaseUrl/api/documents"
    if (-not $docs) { throw "No documents. Load and process one first." }
    $DocumentId = $docs[0].id
}

if ($Clear) {
    Invoke-RestMethod -Method Delete "$BaseUrl/api/ask/cache" | Out-Null
    Write-Host "Answer cache cleared." -ForegroundColor Yellow
    if (-not $Question) { exit 0 }
}

if ($Show) {
    $cache = Invoke-RestMethod "$BaseUrl/api/ask/cache?documentId=$DocumentId"
    Write-Host ("{0} of {1} answers held" -f $cache.count, $cache.limit) -ForegroundColor Cyan
    # Not $stages — that is the [int[]] parameter, and assigning a joined string to it throws.
    $cache.answers | Group-Object question | Sort-Object Count -Descending | ForEach-Object {
        $warmRungs = ($_.Group | Where-Object { $null -ne $_.stage } | ForEach-Object { $_.stage } | Sort-Object) -join ','
        Write-Host ("  [{0,2}] stages {1,-28} {2}" -f $_.Count, $warmRungs, $_.Name)
    }
    exit 0
}

Write-Host "Document: $DocumentId" -ForegroundColor Cyan
Write-Host "Cold rungs take minutes on a CPU model. This is the one-off cost." -ForegroundColor DarkGray

foreach ($q in $Question) {
    Write-Host ""
    Write-Host "`"$q`"" -ForegroundColor White

    $body = @{ documentId = $DocumentId; question = $q }
    if ($Stages) { $body.stages = @($Stages) }

    # No client-side timeout: a cold stage-9 answer on a 3B CPU model can exceed ten minutes, and
    # abandoning it here would leave the cache half-warm with no way to tell which half.
    $result = Invoke-RestMethod -Method Post "$BaseUrl/api/ask/warm" -ContentType 'application/json' `
        -Body ($body | ConvertTo-Json) -TimeoutSec 0

    foreach ($r in $result.results) {
        if ($r.error) {
            Write-Host ("  {0,2}  FAILED  {1}" -f $r.stage, $r.error) -ForegroundColor Red
            continue
        }
        $answer = $r.answer -replace '\s+', ' '
        if ($answer.Length -gt 96) { $answer = $answer.Substring(0, 95) + '…' }
        $tag = if ($r.fromCache) { 'cached' } else { 'live  ' }
        Write-Host ("  {0,2}  {1}  {2,6}s  {3}" -f $r.stage, $tag, [int]($r.ms / 1000), $answer)
    }
}

$cache = Invoke-RestMethod "$BaseUrl/api/ask/cache?documentId=$DocumentId"
Write-Host ""
Write-Host ("Warm: {0} of {1} answers held. Re-asking any of them is instant." -f $cache.count, $cache.limit) -ForegroundColor Green
