<#
.SYNOPSIS
    Measures how fast each installed Ollama model answers a demo-sized prompt.

.DESCRIPTION
    On CPU the cost of a question is dominated by *prompt prefill*, not by generation, and prefill
    speed tracks the number of *active* parameters. That makes the usual instinct — pick a bigger
    model for better answers — actively wrong on a CPU-only machine, and it makes
    Mixture-of-Experts models unusually attractive: a 20B MoE with 3.6B active parameters prefills
    at roughly 3B speed while answering like something much larger.

    This sends each model a prompt the size of a real stage-7 question (five retrieved chunks,
    about 2,500 tokens) and reports the numbers that decide whether you can demo live.

.EXAMPLE
    pwsh tools/benchmark-models.ps1
    pwsh tools/benchmark-models.ps1 -Models qwen2.5:3b,gpt-oss:20b
    pwsh tools/benchmark-models.ps1 -PromptTokens 3000        # extraction-sized
#>
[CmdletBinding()]
param(
    [string[]] $Models,
    [string] $BaseUrl = 'http://localhost:11434',
    [int] $PromptTokens = 2500,
    [switch] $KeepLoaded
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $Models) {
    $Models = (Invoke-RestMethod "$BaseUrl/api/tags").models.name |
        Where-Object { $_ -notmatch 'minilm|embed|cloud' }
}
# `pwsh -File` hands array arguments over as a single comma-joined string.
$Models = $Models | ForEach-Object { $_ -split ',' } | Where-Object { $_ } | ForEach-Object { $_.Trim() }
if (-not $Models) { throw "No chat models installed. Pull one first: docker exec ragladder-ollama ollama pull qwen2.5:3b" }

# Roughly $PromptTokens worth of corpus-like text, so prefill is measured on realistic content.
# The divisor is calibrated against this sentence's actual tokenisation, not a guess: 27 tokens
# each, measured from prompt_eval_count on a known repeat count.
$sentence = 'Director of photography Nirmal Gnanasekaran shot the feature in Colombo and Kandy during 2018. '
$prompt = ($sentence * [math]::Max(1, [math]::Ceiling($PromptTokens / 27))) +
"`n`nQUESTION: Who was the director of photography? Answer in one short sentence."

Write-Host "`nPrompt: ~$PromptTokens tokens, the size of a stage-7 question with five chunks." -ForegroundColor Cyan
Write-Host "Measuring $($Models.Count) model(s). The first call for each includes load time.`n"

$results = foreach ($model in $Models) {
    Write-Host ("  {0,-20} " -f $model) -NoNewline
    try {
        $body = @{
            model    = $model
            messages = @(@{ role = 'user'; content = $prompt })
            stream   = $false
            options  = @{ num_ctx = 8192; temperature = 0 }
        } | ConvertTo-Json -Depth 5

        # Warm the model so load time does not pollute the measurement.
        $null = Invoke-RestMethod -Method Post "$BaseUrl/api/chat" -ContentType 'application/json' `
            -Body (@{ model = $model; messages = @(@{ role = 'user'; content = 'hi' }); stream = $false } | ConvertTo-Json -Depth 5) `
            -TimeoutSec 1800

        $r = Invoke-RestMethod -Method Post "$BaseUrl/api/chat" -ContentType 'application/json' -Body $body -TimeoutSec 1800

        $prefillSec = $r.prompt_eval_duration / 1e9
        $genSec = $r.eval_duration / 1e9
        $total = $r.total_duration / 1e9
        $rate = if ($prefillSec -gt 0) { $r.prompt_eval_count / $prefillSec } else { 0 }

        Write-Host ("{0,6:N0}s total   {1,6:N1} tok/s prefill" -f $total, $rate) -ForegroundColor $(
            if ($total -lt 30) { 'Green' } elseif ($total -lt 90) { 'Yellow' } else { 'Red' })

        if (-not $KeepLoaded) { docker exec ragladder-ollama ollama stop $model 2>&1 | Out-Null }

        [pscustomobject]@{
            Model        = $model
            TotalSec     = [math]::Round($total, 1)
            PrefillSec   = [math]::Round($prefillSec, 1)
            GenSec       = [math]::Round($genSec, 1)
            PrefillRate  = [math]::Round($rate, 1)
            PromptTokens = $r.prompt_eval_count
            Answer       = ($r.message.content -replace '\s+', ' ').Trim()
        }
    }
    catch {
        Write-Host ("failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

if (-not $results) { return }

Write-Host "`n"
$results | Sort-Object TotalSec | Format-Table Model, TotalSec, PrefillRate, PromptTokens -AutoSize

Write-Host "What the numbers mean for a live demo:" -ForegroundColor Cyan
Write-Host "  under  30 s  clickable live"
Write-Host "  30-90  s     workable if you narrate while it thinks"
Write-Host "  over   90 s  record a replay pass instead (OPERATIONS.md section 11)"
Write-Host ""
foreach ($r in ($results | Sort-Object TotalSec)) {
    Write-Host ("  {0,-20} {1}" -f $r.Model, $r.Answer.Substring(0, [Math]::Min(90, $r.Answer.Length))) -ForegroundColor DarkGray
}
Write-Host ""
