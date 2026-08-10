<#
.SYNOPSIS
    Start, stop and check the demo. The one script to run day to day.

.DESCRIPTION
    Wraps the things that went wrong often enough to be worth automating:

      - Starting the app before the containers are up, so health comes back degraded.
      - Opening the browser before the server is listening, which leaves the page dead until a
        reload. Start waits for /api/health and only then reports ready.
      - Stopping "dotnet" and taking unrelated processes with it. Stop matches this app only.
      - Not noticing that a provider fell back to SQLite until mid-demo.

    Containers are left alone unless you ask, because restarting Ollama evicts the model from
    memory and costs one slow answer afterwards.

.EXAMPLE
    pwsh tools/demo.ps1 start
    pwsh tools/demo.ps1 stop
    pwsh tools/demo.ps1 restart
    pwsh tools/demo.ps1 status
    pwsh tools/demo.ps1 start -Build          # rebuild first
    pwsh tools/demo.ps1 start -Open           # and open the browser
    pwsh tools/demo.ps1 stop  -Containers     # take the containers down too
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'restart', 'status')]
    [string] $Action = 'status',

    [int]    $Port = 5099,
    [switch] $Build,
    [switch] $Open,
    [switch] $Containers,
    [int]    $TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Resolve the repo root from this script rather than the working directory, so the script works
# from anywhere — the same reason RepoPaths walks up looking for the solution file.
$Repo = Split-Path -Parent $PSScriptRoot
$Dll = Join-Path $Repo 'src/RagLadder.Api/bin/Debug/net9.0/RagLadder.Api.dll'
$LogDir = Join-Path $Repo 'data'
$BaseUrl = "http://localhost:$Port"

function Say($text, $colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

# The app is run as the built DLL rather than through `dotnet run`, which spawns a child and makes
# a clean stop unreliable. One process, one PID, one kill.
function Get-AppProcess {
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*RagLadder.Api.dll*' }
}

function Get-PortOwner {
    (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1).OwningProcess
}

function Get-Health {
    try { Invoke-RestMethod "$BaseUrl/api/health" -TimeoutSec 10 } catch { $null }
}

function Show-Health($health) {
    if (-not $health) { Say '  health: unreachable' 'Red'; return }

    $colour = @{ ok = 'Green'; degraded = 'Yellow'; paused = 'Yellow' }[$health.status]
    Say ("  health: {0}" -f $health.status) ($colour ?? 'Red')
    foreach ($p in $health.providers) {
        Say ("    {0,-9} {1,-14} {2}" -f $p.name, $p.status, $p.detail) `
            $(if ($p.status -eq 'ok') { 'DarkGray' } else { 'Yellow' })
    }

    # A silent fallback is the failure that survives a whole demo unnoticed, so it gets called out.
    $fallen = $health.providers | Where-Object { $_.status -ne 'ok' }
    if ($fallen) {
        Say '  One or more providers are not ok. Retrieval or the graph will not be representative.' 'Yellow'
    }
    if ($health.embedder -and -not $health.embedder.passed) {
        Say '  The embedder probe is below the acceptance band. Do not demo on this.' 'Yellow'
    }
}

function Show-Cache {
    try {
        $cache = Invoke-RestMethod "$BaseUrl/api/ask/cache" -TimeoutSec 10
        Say ("  answers cached: {0}/{1}" -f $cache.count, $cache.limit) 'DarkGray'
        $cache.answers | Group-Object question | Sort-Object Count -Descending |
            Select-Object -First 3 | ForEach-Object {
                $rungs = ($_.Group | Where-Object { $null -ne $_.stage } |
                    ForEach-Object { $_.stage } | Sort-Object) -join ','
                Say ("    [{0,2} rungs: {1}] {2}" -f $_.Count, $rungs, $_.Name) 'DarkGray'
            }
        if ($cache.count -eq 0) {
            Say '    nothing warm — the first ask at each rung will take minutes. pwsh tools/warm-cache.ps1' 'DarkYellow'
        }
    } catch { }
}

function Show-Containers {
    try {
        $rows = docker ps --filter 'name=ragladder' --format '{{.Names}}|{{.Status}}' 2>$null
        if (-not $rows) { Say '  containers: none running — docker compose up -d' 'Yellow'; return $false }
        Say '  containers:' 'DarkGray'
        foreach ($row in $rows) {
            $parts = $row -split '\|'
            Say ("    {0,-18} {1}" -f $parts[0], $parts[1]) 'DarkGray'
        }
        return $true
    } catch {
        Say '  containers: docker not responding' 'Yellow'
        return $false
    }
}

# ---------------------------------------------------------------- stop

function Stop-App {
    $running = Get-AppProcess
    if (-not $running) {
        Say 'App is not running.' 'DarkGray'
    } else {
        foreach ($p in $running) {
            Say ("Stopping the app (pid {0})…" -f $p.ProcessId)
            try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop } catch { Say "  $($_.Exception.Message)" 'Yellow' }
        }
        # The port lingers for a moment after the process dies; a start straight after would
        # otherwise fail on an address already in use.
        for ($i = 0; $i -lt 20; $i++) {
            if (-not (Get-PortOwner)) { break }
            Start-Sleep -Milliseconds 250
        }
    }

    if (Get-PortOwner) { Say "  Port $Port is still held by pid $(Get-PortOwner) — not this app." 'Yellow' }
    else { Say "  Port $Port is free." 'DarkGray' }

    if ($Containers) {
        Say 'Stopping the containers…'
        Push-Location $Repo
        try { docker compose down 2>&1 | Out-Null } finally { Pop-Location }
        Say '  Down. Volumes are kept, so the graph and vectors survive.' 'DarkGray'
        Say '  Ollama will reload the model on the next question — expect one slow answer.' 'DarkGray'
    }
}

# ---------------------------------------------------------------- start

function Start-App {
    if (Get-AppProcess) {
        Say "Already running at $BaseUrl." 'Green'
        Show-Health (Get-Health)
        return $true
    }

    $owner = Get-PortOwner
    if ($owner) {
        Say "Port $Port is already in use by pid $owner, and it is not this app." 'Red'
        Say "  Stop it, or pick another port: pwsh tools/demo.ps1 start -Port 8080" 'DarkGray'
        return $false
    }

    Push-Location $Repo
    try {
        # Containers first: starting the app against a cold Qdrant or Neo4j is how you end up
        # demoing on the SQLite fallback without realising.
        if (-not (Show-Containers)) {
            Say 'Starting the containers…'
            docker compose up -d 2>&1 | Out-Null
            Start-Sleep -Seconds 4
            Show-Containers | Out-Null
        }

        if ($Build -or -not (Test-Path $Dll)) {
            Say $(if ($Build) { 'Building…' } else { 'No build output found. Building…' })
            dotnet build (Join-Path $Repo 'RagLadder.sln') --nologo -v q
            if ($LASTEXITCODE -ne 0) { Say 'Build failed.' 'Red'; return $false }
        }

        New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
        $out = Join-Path $LogDir 'app.log'
        $err = Join-Path $LogDir 'app.err'

        Say "Starting the app on port $Port…"
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        Start-Process -FilePath 'dotnet' `
            -ArgumentList @($Dll, '--urls', $BaseUrl) `
            -WorkingDirectory $Repo -WindowStyle Hidden `
            -RedirectStandardOutput $out -RedirectStandardError $err | Out-Null
    } finally {
        Pop-Location
    }

    # Wait for it to actually answer. Reporting ready before this is what leaves a browser tab
    # stuck on "loading…" with no explanation.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $spinner = '|/-\'
    $i = 0
    while ((Get-Date) -lt $deadline) {
        $health = Get-Health
        if ($health) {
            Write-Host "`r  ready.                    "
            Show-Health $health
            Show-Cache
            Say ""
            Say "  $BaseUrl" 'Cyan'
            if ($Open) { Start-Process $BaseUrl }
            return $true
        }
        if (-not (Get-AppProcess)) {
            Write-Host "`r"
            Say 'The app exited during startup. Last lines of data/app.err:' 'Red'
            if (Test-Path (Join-Path $LogDir 'app.err')) {
                Get-Content (Join-Path $LogDir 'app.err') -Tail 15 | ForEach-Object { Say "    $_" 'DarkGray' }
            }
            return $false
        }
        Write-Host ("`r  waiting for health {0} " -f $spinner[$i++ % 4]) -NoNewline
        Start-Sleep -Milliseconds 700
    }

    Write-Host "`r"
    Say "Gave up after $TimeoutSeconds seconds. The process is running but not answering." 'Yellow'
    Say '  Check data/app.log, or raise -TimeoutSeconds.' 'DarkGray'
    return $false
}

# ---------------------------------------------------------------- status

function Show-Status {
    $running = Get-AppProcess
    if ($running) { Say ("App: running (pid {0}) at {1}" -f $running[0].ProcessId, $BaseUrl) 'Green' }
    else { Say 'App: not running' 'DarkYellow' }

    Show-Containers | Out-Null

    if ($running) {
        Show-Health (Get-Health)
        Show-Cache
    } else {
        $owner = Get-PortOwner
        if ($owner) { Say "  Port $Port is held by pid $owner — something else is on it." 'Yellow' }
        Say '  Start it with: pwsh tools/demo.ps1 start' 'DarkGray'
    }
}

# ---------------------------------------------------------------- dispatch

switch ($Action) {
    'stop' { Stop-App }
    'start' { if (-not (Start-App)) { exit 1 } }
    'restart' {
        Stop-App
        Say ''
        if (-not (Start-App)) { exit 1 }
    }
    'status' { Show-Status }
}
