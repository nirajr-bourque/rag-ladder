<#
.SYNOPSIS
    Downloads the two local ONNX models the demo runs on, trying several sources.

.DESCRIPTION
    all-MiniLM-L6-v2        sentence embeddings, 384 dims, ~90 MB
    ms-marco-MiniLM-L-6-v2  cross-encoder reranker, ~90 MB

    Both run in-process through ONNX Runtime and cost nothing per call.

    SOURCES, tried in order unless -Source is given:

      huggingface   The upstream repositories. Blocked on many corporate networks.
      fastembed     Qdrant's FastEmbed mirror on storage.googleapis.com. Carries the embedding
                    model only, but is usually reachable when Hugging Face is not.

    If nothing works the app still starts, falls back to stand-ins, and says so on /api/health.
    See OPERATIONS.md section 6 for the routes that need no model files at all.

.EXAMPLE
    pwsh tools/fetch-models.ps1
    pwsh tools/fetch-models.ps1 -Source fastembed
    pwsh tools/fetch-models.ps1 -Force
    pwsh tools/fetch-models.ps1 -FromDirectory D:\transfer\models
#>
[CmdletBinding()]
param(
    [string] $OutputRoot = (Join-Path $PSScriptRoot '..' 'models'),
    [ValidateSet('auto', 'huggingface', 'fastembed')]
    [string] $Source = 'auto',
    [string] $FromDirectory,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $root | Out-Null
Write-Host "Model directory: $root`n"

$embedDir  = Join-Path $root 'all-MiniLM-L6-v2'
$rerankDir = Join-Path $root 'ms-marco-MiniLM-L-6-v2'
New-Item -ItemType Directory -Force -Path $embedDir, $rerankDir | Out-Null

function Test-Model([string] $dir) {
    $model = Join-Path $dir 'model.onnx'
    $vocab = Join-Path $dir 'vocab.txt'
    (Test-Path $model) -and (Test-Path $vocab) -and ((Get-Item $model).Length -gt 1MB)
}

function Get-File([string] $url, [string] $target) {
    Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing -MaximumRedirection 10 -TimeoutSec 900
    if ((Get-Item $target).Length -lt 1KB) { Remove-Item $target -Force; throw "Downloaded file was empty." }
}

# ---------------------------------------------------------------- offline copy

if ($FromDirectory) {
    Write-Host "Copying from $FromDirectory" -ForegroundColor Cyan
    foreach ($pair in @(@{ n = 'all-MiniLM-L6-v2'; d = $embedDir }, @{ n = 'ms-marco-MiniLM-L-6-v2'; d = $rerankDir })) {
        $src = Join-Path $FromDirectory $pair.n
        if (-not (Test-Path $src)) { Write-Warning "  not found: $src"; continue }
        Copy-Item (Join-Path $src '*') $pair.d -Force
        Write-Host ("  {0}: {1}" -f $pair.n, $(if (Test-Model $pair.d) { 'ok' } else { 'INCOMPLETE' }))
    }
    exit 0
}

# ---------------------------------------------------------------- sources

$huggingface = @(
    @{ Dir = $embedDir;  File = 'model.onnx'; Url = 'https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx' }
    @{ Dir = $embedDir;  File = 'vocab.txt';  Url = 'https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt' }
    @{ Dir = $rerankDir; File = 'model.onnx'; Url = 'https://huggingface.co/cross-encoder/ms-marco-MiniLM-L-6-v2/resolve/main/onnx/model.onnx' }
    @{ Dir = $rerankDir; File = 'vocab.txt';  Url = 'https://huggingface.co/cross-encoder/ms-marco-MiniLM-L-6-v2/resolve/main/vocab.txt' }
)

function Invoke-HuggingFace {
    $ok = $true
    foreach ($item in $huggingface) {
        $target = Join-Path $item.Dir $item.File
        if ((Test-Path $target) -and -not $Force) {
            Write-Host ("  skip     {0}/{1}" -f (Split-Path $item.Dir -Leaf), $item.File)
            continue
        }
        try {
            Write-Host ("  download {0}/{1}" -f (Split-Path $item.Dir -Leaf), $item.File)
            Get-File $item.Url $target
            Write-Host ("           {0:N1} MB" -f ((Get-Item $target).Length / 1MB)) -ForegroundColor Green
        }
        catch {
            Write-Warning ("  failed   {0}: {1}" -f $item.File, $_.Exception.Message)
            $ok = $false
        }
    }
    $ok
}

function Invoke-FastEmbed {
    # Qdrant's FastEmbed mirror. Carries the embedding model; no cross-encoder.
    $url = 'https://storage.googleapis.com/qdrant-fastembed/sentence-transformers-all-MiniLM-L6-v2.tar.gz'
    $work = Join-Path ([System.IO.Path]::GetTempPath()) ("fastembed-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        $archive = Join-Path $work 'model.tar.gz'
        Write-Host "  download all-MiniLM-L6-v2 from the Qdrant FastEmbed mirror"
        Get-File $url $archive
        Write-Host ("           {0:N1} MB, extracting" -f ((Get-Item $archive).Length / 1MB))

        tar -xzf $archive -C $work
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

        $model = Get-ChildItem $work -Recurse -Filter 'model.onnx' | Where-Object Length -gt 1MB | Select-Object -First 1
        $vocab = Get-ChildItem $work -Recurse -Filter 'vocab.txt'  | Where-Object Length -gt 1KB | Select-Object -First 1
        if (-not $model -or -not $vocab) { throw "archive did not contain model.onnx and vocab.txt" }

        Copy-Item $model.FullName (Join-Path $embedDir 'model.onnx') -Force
        Copy-Item $vocab.FullName (Join-Path $embedDir 'vocab.txt')  -Force
        Write-Host ("           installed, {0:N1} MB" -f ($model.Length / 1MB)) -ForegroundColor Green
        $true
    }
    catch {
        Write-Warning ("  failed   FastEmbed mirror: {0}" -f $_.Exception.Message)
        $false
    }
    finally {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- run

if ($Source -in 'auto', 'huggingface') {
    Write-Host "Source: huggingface.co" -ForegroundColor Cyan
    $null = Invoke-HuggingFace
}

if (-not (Test-Model $embedDir) -and $Source -in 'auto', 'fastembed') {
    Write-Host "`nSource: Qdrant FastEmbed mirror (storage.googleapis.com)" -ForegroundColor Cyan
    $null = Invoke-FastEmbed
}

# ---------------------------------------------------------------- report

Write-Host ""
$haveEmbed  = Test-Model $embedDir
$haveRerank = Test-Model $rerankDir

Write-Host ("  embedder  {0}" -f $(if ($haveEmbed)  { 'ready' } else { 'MISSING' })) -ForegroundColor $(if ($haveEmbed)  { 'Green' } else { 'Yellow' })
Write-Host ("  reranker  {0}" -f $(if ($haveRerank) { 'ready' } else { 'MISSING' })) -ForegroundColor $(if ($haveRerank) { 'Green' } else { 'Yellow' })

if ($haveEmbed -and $haveRerank) {
    Write-Host "`nBoth models present. Start the app and check /api/health:" -ForegroundColor Green
    Write-Host "  the embedder probe should report the similar pair above 0.7 and the unrelated pair below 0.3."
    exit 0
}

Write-Host ""
if (-not $haveEmbed) {
    Write-Warning "The embedding model is missing. Retrieval will run on a bag-of-words stand-in, which is not demo quality."
    Write-Host "  Set RagLadder:Providers:Embedder to `"ollama`" to serve embeddings from Ollama instead — no model file needed."
}
if (-not $haveRerank) {
    Write-Warning "The cross-encoder is missing. The Qdrant mirror does not carry one."
    Write-Host "  Set RagLadder:Providers:Reranker to `"llm`" to rerank with the chat model instead — no model file needed."
}
Write-Host "  Or copy the files from a machine with access: pwsh tools/fetch-models.ps1 -FromDirectory <path>"
Write-Host "  See OPERATIONS.md section 6 for all routes."
exit 1
