<#
.SYNOPSIS
    Works out which setup route this machine's network allows.

.DESCRIPTION
    Corporate allowlists vary, and the difference decides how you get a model. Run this first:
    it takes about a minute and tells you which section of OPERATIONS.md to follow, rather than
    finding out after a 3.5 GB download fails.

    Nothing is installed or downloaded. Only reachability is checked.

.EXAMPLE
    pwsh tools/check-network.ps1
#>
[CmdletBinding()]
param(
    [int] $TimeoutSeconds = 15
)

$ProgressPreference = 'SilentlyContinue'

function Test-Endpoint {
    param([string] $Name, [string] $Url, [string] $Purpose, [int[]] $OkCodes = @(200, 401, 403, 405))
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Head -TimeoutSec $TimeoutSeconds -UseBasicParsing -ErrorAction Stop
        $reachable = $true; $detail = "HTTP $($response.StatusCode)"
    }
    catch {
        $code = $_.Exception.Response.StatusCode.value__
        if ($code -and $OkCodes -contains $code) { $reachable = $true; $detail = "HTTP $code (reachable)" }
        else { $reachable = $false; $detail = if ($code) { "HTTP $code" } else { ($_.Exception.Message -split "`n")[0].Trim() } }
    }
    [pscustomobject]@{ Name = $Name; Reachable = $reachable; Detail = $detail; Purpose = $Purpose }
}

Write-Host "`nChecking what this network allows. Nothing is downloaded.`n" -ForegroundColor Cyan

$checks = @(
    (Test-Endpoint 'nuget.org'            'https://api.nuget.org/v3/index.json'                                              'NuGet restore — required')
    (Test-Endpoint 'huggingface.co'       'https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt' 'ONNX models, upstream')
    (Test-Endpoint 'FastEmbed mirror'     'https://storage.googleapis.com/qdrant-fastembed/sentence-transformers-all-MiniLM-L6-v2.tar.gz' 'ONNX embedding model, mirror')
    (Test-Endpoint 'ollama.com'           'https://ollama.com/api/tags'                                                       'Ollama Cloud, hosted chat')
    (Test-Endpoint 'registry.ollama.ai'   'https://registry.ollama.ai/v2/library/qwen2.5/manifests/3b'                        'Ollama model downloads, local')
    (Test-Endpoint 'Docker Hub'           'https://registry-1.docker.io/v2/'                                                  'the ollama/ollama image')
)

foreach ($check in $checks) {
    $mark = if ($check.Reachable) { 'OK  ' } else { '--  ' }
    $colour = if ($check.Reachable) { 'Green' } else { 'DarkYellow' }
    Write-Host ("  {0}{1,-20} {2,-28} {3}" -f $mark, $check.Name, $check.Detail, $check.Purpose) -ForegroundColor $colour
}

$reach = @{}
foreach ($check in $checks) { $reach[$check.Name] = $check.Reachable }

Write-Host "`n  docker              " -NoNewline
$hasDocker = $false
try { $null = docker --version; $null = docker info 2>&1; $hasDocker = ($LASTEXITCODE -eq 0) } catch { }
if ($hasDocker) { Write-Host 'running' -ForegroundColor Green } else { Write-Host 'not available' -ForegroundColor DarkYellow }

# ---------------------------------------------------------------- verdict

Write-Host "`n" ('-' * 78)

if (-not $reach['nuget.org']) {
    Write-Host "`nSTOP. nuget.org is unreachable, so the project cannot even restore." -ForegroundColor Red
    Write-Host "  Check for a private feed: dotnet nuget list source"
    Write-Host "  The repo ships a NuGet.config pinned to nuget.org; a proxy is likely intercepting it."
    exit 1
}

Write-Host "`nEMBEDDINGS" -ForegroundColor Cyan
if ($reach['huggingface.co']) {
    Write-Host "  Route: the normal one." -ForegroundColor Green
    Write-Host "    pwsh tools/fetch-models.ps1"
    Write-Host "  You get both the embedder and the cross-encoder reranker. OPERATIONS.md section 6."
}
elseif ($reach['FastEmbed mirror']) {
    Write-Host "  Route: the Qdrant mirror. Hugging Face is blocked but the mirror is not." -ForegroundColor Green
    Write-Host "    pwsh tools/fetch-models.ps1        # falls back automatically"
    Write-Host "  You get the embedder. The mirror carries no cross-encoder, so set:"
    Write-Host "    RagLadder:Providers:Reranker = llm"
    Write-Host "  OPERATIONS.md section 6.2."
}
elseif ($reach['registry.ollama.ai'] -and $hasDocker) {
    Write-Host "  Route: serve embeddings from a local Ollama. No model file needed." -ForegroundColor Green
    Write-Host "    pwsh tools/setup-ollama.ps1"
    Write-Host "  OPERATIONS.md section 6.3."
}
else {
    Write-Host "  Route: offline transfer required." -ForegroundColor Yellow
    Write-Host "  Fetch the model files on a machine with access, then:"
    Write-Host "    pwsh tools/fetch-models.ps1 -FromDirectory <path>"
    Write-Host "  OPERATIONS.md section 6.4."
}

Write-Host "`nCHAT MODEL" -ForegroundColor Cyan
if ($reach['ollama.com']) {
    Write-Host "  Route: Ollama Cloud. Sign up, create a key." -ForegroundColor Green
    Write-Host "  OPERATIONS.md section 7.1."
}
elseif ($reach['registry.ollama.ai'] -and $hasDocker) {
    Write-Host "  Route: Ollama in Docker, locally." -ForegroundColor Green
    Write-Host "  ollama.com is blocked but registry.ollama.ai is not — models still download."
    Write-Host "    docker compose up -d"
    Write-Host "    pwsh tools/setup-ollama.ps1 -Apply"
    Write-Host "  OPERATIONS.md section 7.2."
}
elseif ($reach['registry.ollama.ai'] -and -not $hasDocker) {
    Write-Host "  Route: Ollama in Docker — but Docker is not available." -ForegroundColor Yellow
    Write-Host "  Install Docker Desktop, or install Ollama natively, then re-run this check."
    Write-Host "  OPERATIONS.md section 7.2."
}
else {
    Write-Host "  Route: a sanctioned OpenAI-compatible endpoint." -ForegroundColor Yellow
    Write-Host "  Both Ollama hosts are blocked. Ask what your organisation does allow —"
    Write-Host "  Azure OpenAI, an internal gateway, or a self-hosted vLLM all work:"
    Write-Host "    RagLadder:Providers:Chat = openai"
    Write-Host "  OPERATIONS.md section 7.3."
}

Write-Host "`nWithout a chat model the ladder still runs, but nothing generates an answer and there"
Write-Host "is no knowledge graph. See OPERATIONS.md section 16.`n"
