<#
.SYNOPSIS
    Stands up a local Ollama in Docker and pulls the models the demo needs.

.DESCRIPTION
    For networks that block ollama.com. The block is normally on the website host; model downloads
    come from registry.ollama.ai, which is a different host and is usually still reachable. This
    script checks that before doing anything, so you find out in seconds rather than after a
    3.5 GB image pull.

    It pulls one chat model and one embedding model, then prints the exact configuration to apply.
    Nothing here touches huggingface.co.

.EXAMPLE
    pwsh tools/setup-ollama.ps1
    pwsh tools/setup-ollama.ps1 -ChatModel qwen2.5:7b       # better extraction, needs ~5 GB
    pwsh tools/setup-ollama.ps1 -Apply                      # also write user-secrets
#>
[CmdletBinding()]
param(
    [string] $ChatModel = 'qwen2.5:3b',
    [string] $EmbeddingModel = 'all-minilm',
    [string] $BaseUrl = 'http://localhost:11434',
    [switch] $Apply,
    [switch] $SkipChecks
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Step($text) { Write-Host "`n$text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- preflight

if (-not $SkipChecks) {
    Step 'Checking prerequisites'

    try { $null = docker --version } catch { throw "Docker is not installed or not on PATH. Install Docker Desktop first." }
    Write-Host ("  docker            {0}" -f (docker --version))

    try { $null = docker info 2>&1; if ($LASTEXITCODE -ne 0) { throw } }
    catch { throw "Docker is installed but the daemon is not running. Start Docker Desktop and retry." }
    Write-Host "  docker daemon     running"

    # The decisive check: model downloads come from here, not from ollama.com.
    try {
        $null = Invoke-WebRequest "https://registry.ollama.ai/v2/library/$($ChatModel.Split(':')[0])/manifests/$($ChatModel.Split(':')[-1])" `
            -Method Head -TimeoutSec 20 -UseBasicParsing
        Write-Host "  registry.ollama.ai reachable" -ForegroundColor Green
    }
    catch {
        Write-Warning "  registry.ollama.ai is NOT reachable: $($_.Exception.Message)"
        Write-Host "`n  Your network blocks the model registry as well as the website. Two options remain:"
        Write-Host "    1. Point the app at a sanctioned OpenAI-compatible endpoint."
        Write-Host "       Set Providers:Chat to 'openai' and fill in RagLadder:OpenAiCompatible."
        Write-Host "    2. Transfer a GGUF file from a machine with access and import it:"
        Write-Host "         docker cp model.gguf ragladder-ollama:/tmp/"
        Write-Host "         docker exec ragladder-ollama sh -c 'printf ""FROM /tmp/model.gguf"" > /tmp/Modelfile && ollama create demo -f /tmp/Modelfile'"
        Write-Host "    See OPERATIONS.md section 7.3."
        exit 1
    }
}

# ---------------------------------------------------------------- container

Step 'Starting the Ollama container'
$compose = Join-Path $PSScriptRoot '..' 'docker-compose.yml'
if (Test-Path $compose) {
    docker compose -f $compose up -d
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed." }
}
else {
    docker rm -f ragladder-ollama 2>&1 | Out-Null
    docker run -d --name ragladder-ollama -p 11434:11434 -v ragladder-ollama:/root/.ollama ollama/ollama | Out-Null
}

Write-Host '  waiting for the API…'
$ready = $false
foreach ($attempt in 1..60) {
    Start-Sleep -Seconds 2
    try { $version = (Invoke-RestMethod "$BaseUrl/api/version" -TimeoutSec 5).version; $ready = $true; break } catch { }
}
if (-not $ready) { throw "Ollama did not become ready. Check: docker logs ragladder-ollama" }
Write-Host ("  ollama {0} listening on {1}" -f $version, $BaseUrl) -ForegroundColor Green

# ---------------------------------------------------------------- models

foreach ($model in @($ChatModel, $EmbeddingModel)) {
    Step "Pulling $model"
    docker exec ragladder-ollama ollama pull $model
    if ($LASTEXITCODE -ne 0) { throw "Failed to pull $model." }
}

Step 'Verifying'
$tags = (Invoke-RestMethod "$BaseUrl/api/tags").models.name
Write-Host ("  models available: {0}" -f ($tags -join ', '))

$probe = Invoke-RestMethod -Method Post "$BaseUrl/api/embed" -ContentType 'application/json' -Body (@{
        model = $EmbeddingModel
        input = @(
            'the cinematographer shot the film in Sri Lanka',
            'the director of photography filmed the picture in Sri Lanka',
            'quarterly depreciation schedules for rolling stock')
    } | ConvertTo-Json)

function Get-Cosine($a, $b) {
    $dot = 0; $na = 0; $nb = 0
    for ($i = 0; $i -lt $a.Count; $i++) { $dot += $a[$i] * $b[$i]; $na += $a[$i] * $a[$i]; $nb += $b[$i] * $b[$i] }
    $dot / ([math]::Sqrt($na) * [math]::Sqrt($nb))
}
$similar = Get-Cosine $probe.embeddings[0] $probe.embeddings[1]
$unrelated = Get-Cosine $probe.embeddings[0] $probe.embeddings[2]
$dims = $probe.embeddings[0].Count

Write-Host ("  embeddings: {0} dims, similar {1:N3} (want > 0.7), unrelated {2:N3} (want < 0.3)" -f $dims, $similar, $unrelated) `
    -ForegroundColor $(if ($similar -gt 0.7 -and $unrelated -lt 0.3) { 'Green' } else { 'Yellow' })

# ---------------------------------------------------------------- configuration

$settings = [ordered]@{
    'RagLadder:Providers:Chat'         = 'ollama'
    'RagLadder:Providers:Embedder'     = 'ollama'
    'RagLadder:Ollama:BaseUrl'         = $BaseUrl
    'RagLadder:Ollama:ApiKey'          = ''
    'RagLadder:Ollama:ChatModel'       = $ChatModel
    'RagLadder:Ollama:ExtractionModel' = $ChatModel
    'RagLadder:Embedding:OllamaModel'  = $EmbeddingModel
}

Step 'Configuration to apply'
if ($Apply) {
    $project = Join-Path $PSScriptRoot '..' 'src' 'RagLadder.Api'
    foreach ($kv in $settings.GetEnumerator()) {
        if ([string]::IsNullOrEmpty($kv.Value)) { continue }
        dotnet user-secrets --project $project set $kv.Key $kv.Value | Out-Null
    }
    Write-Host '  written to user-secrets' -ForegroundColor Green
}
else {
    Write-Host '  add these (or re-run with -Apply to write them to user-secrets):' -ForegroundColor DarkGray
    foreach ($kv in $settings.GetEnumerator()) {
        Write-Host ("    dotnet user-secrets --project src/RagLadder.Api set ""{0}"" ""{1}""" -f $kv.Key, $kv.Value)
    }
}

Write-Host "`nNext:" -ForegroundColor Green
Write-Host '  dotnet run --project src/RagLadder.Api'
Write-Host '  then check http://localhost:5099/api/health — chat and embedder should both read ok.'
Write-Host "`nIf you also have the ONNX cross-encoder, leave Providers:Reranker as ""onnx""."
Write-Host 'Otherwise set it to "llm" so stage 5 reranks with the chat model instead.'
