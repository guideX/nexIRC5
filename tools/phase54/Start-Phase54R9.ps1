[CmdletBinding()]
param(
    [int]$ClientAProcessId = 0,
    [int]$ClientBProcessId = 0,
    [string]$ClientATelemetryPath,
    [string]$ClientExecutable,
    [ValidateRange(5, 120)]
    [int]$WindowTimeoutSeconds = 30,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$serverRoot = Join-Path (Split-Path -Parent $repoRoot) 'nexIRCServer'
$clientExe = if ([string]::IsNullOrWhiteSpace($ClientExecutable)) {
    Join-Path $repoRoot 'src\nexIRC.Desktop\bin\Debug\net10.0-windows\nexIRC.Desktop.exe'
} else {
    [System.IO.Path]::GetFullPath($ClientExecutable)
}
$runnerDll = Join-Path $repoRoot 'tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll'

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 12
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Get-RepositorySnapshot {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath (Join-Path $Path '.git'))) {
        return [pscustomobject]@{ Path = $Path; Available = $false; Branch = 'unknown'; Head = 'unknown'; Worktree = 'unknown'; TrackingDivergence = 'unknown' }
    }
    $branch = (& git -C $Path branch --show-current 2>$null | Select-Object -First 1)
    $head = (& git -C $Path rev-parse HEAD 2>$null | Select-Object -First 1)
    $status = @(& git -C $Path status --porcelain=v1 2>$null)
    $upstream = (& git -C $Path rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null | Select-Object -First 1)
    $divergence = 'no upstream'
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($upstream)) {
        $counts = (& git -C $Path rev-list --left-right --count "HEAD...$upstream" 2>$null | Select-Object -First 1)
        if ($LASTEXITCODE -eq 0) { $divergence = ($counts -replace '\s+', ' ').Trim() }
    }
    return [pscustomobject]@{
        Path = $Path
        Available = $true
        Branch = if ($branch) { $branch.Trim() } else { 'unknown' }
        Head = if ($head) { $head.Trim() } else { 'unknown' }
        Worktree = if ($status.Count -eq 0) { 'clean' } else { 'dirty' }
        TrackingDivergence = $divergence
    }
}

function Get-ClientProcessInfo {
    param([int]$ProcessId, [string]$ExpectedPath, [int]$TimeoutSeconds, [string]$Label)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $process = Get-Process -Id $ProcessId -ErrorAction Stop
            $process.Refresh()
            if ($process.HasExited) { throw "$Label (PID $ProcessId) exited during startup." }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero -and -not [string]::IsNullOrWhiteSpace($process.MainWindowTitle)) {
                $actualPath = [System.IO.Path]::GetFullPath($process.MainModule.FileName)
                if (-not [string]::Equals($actualPath, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "$Label executable does not match the requested nexIRC 5 executable."
                }
                if ($process.MainWindowTitle -ne 'nexIRC 5') {
                    throw "$Label PID $ProcessId exposed an unexpected window title '$($process.MainWindowTitle)'."
                }
                return [pscustomobject]@{
                    ProcessId = $process.Id
                    ExecutablePath = $actualPath
                    WindowTitle = $process.MainWindowTitle
                    WindowHandle = ('0x{0:X}' -f $process.MainWindowHandle.ToInt64())
                    SessionId = $process.SessionId
                    StartTime = $process.StartTime.ToUniversalTime().ToString('o')
                    WindowVerified = $true
                }
            }
        } catch [System.ArgumentException] {
            throw "$Label PID $ProcessId no longer exists."
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Label PID $ProcessId did not expose a usable nexIRC 5 main window within $TimeoutSeconds seconds. Check for first-run dialogs, single-instance forwarding, or a hidden window, then inspect it manually."
}

if (-not (Test-Path -LiteralPath $clientExe -PathType Leaf)) {
    Write-Host "nexIRC 5 executable not found: $clientExe" -ForegroundColor Red
    Write-Host 'Build it from the client repository root, then rerun:' -ForegroundColor Yellow
    Write-Host '  dotnet build .\nexIRC5.sln -c Debug'
    exit 2
}
if (-not (Test-Path -LiteralPath $runnerDll -PathType Leaf)) {
    Write-Host "UI acceptance runner not found: $runnerDll" -ForegroundColor Red
    Write-Host 'Build it from the client repository root, then rerun:' -ForegroundColor Yellow
    Write-Host '  dotnet build .\nexIRC5.sln -c Debug'
    exit 2
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'dotnet was not found on PATH. Install/use the .NET 10 SDK, then rerun this launcher.' -ForegroundColor Red
    exit 2
}
if ($Host.Name -ne 'ConsoleHost' -or [Console]::IsInputRedirected -or [Console]::IsOutputRedirected) {
    Write-Host 'Phase 54R9 needs a local interactive Windows PowerShell console with visible, non-redirected input and output.' -ForegroundColor Red
    Write-Host "Open Windows PowerShell or PowerShell 7, then run: & `"$PSScriptRoot\Start-Phase54R9.ps1`""
    exit 2
}

$artifactRoot = Join-Path $repoRoot 'artifacts\phase54r9'
[System.IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = Join-Path $artifactRoot $runId
if (Test-Path -LiteralPath $runDirectory) { throw "Run directory collision; no files were changed: $runDirectory" }
[System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null

$clientAInfo = $null
$clientBInfo = $null
$launchedA = $false
$launchedB = $false
$telemetryPath = $null
$probeEnabled = $false
try {
    if ($ClientAProcessId -gt 0) {
        $clientAInfo = Get-ClientProcessInfo -ProcessId $ClientAProcessId -ExpectedPath $clientExe -TimeoutSeconds $WindowTimeoutSeconds -Label 'Client A'
        if (-not [string]::IsNullOrWhiteSpace($ClientATelemetryPath)) {
            $telemetryPath = [System.IO.Path]::GetFullPath($ClientATelemetryPath)
            if (-not (Test-Path -LiteralPath $telemetryPath -PathType Leaf)) {
                throw "Client A telemetry path was supplied but does not exist: $telemetryPath. Reused clients need the opt-in Phase 54R3 probe from process startup."
            }
            $repoPrefix = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
            $artifactPrefix = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
            if ($telemetryPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -and
                -not $telemetryPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Reused Client A telemetry inside the client repository must be stored under the ignored artifacts\phase54r9 directory.'
            }
            $probeEnabled = @([System.IO.File]::ReadAllLines($telemetryPath) | Where-Object { $_ -eq 'PROBE_READY' }).Count -gt 0
        }
    } else {
        $telemetryPath = Join-Path $runDirectory 'client-a-input.jsonl'
        $probeArgument = '--phase54r3-input-probe="{0}"' -f $telemetryPath
        $startedA = Start-Process -FilePath $clientExe -WorkingDirectory (Split-Path -Parent $clientExe) -ArgumentList $probeArgument -PassThru
        $launchedA = $true
        $clientAInfo = Get-ClientProcessInfo -ProcessId $startedA.Id -ExpectedPath $clientExe -TimeoutSeconds $WindowTimeoutSeconds -Label 'Client A'
        $probeDeadline = [DateTime]::UtcNow.AddSeconds($WindowTimeoutSeconds)
        while ([DateTime]::UtcNow -lt $probeDeadline -and -not $probeEnabled) {
            if (Test-Path -LiteralPath $telemetryPath -PathType Leaf) {
                $probeEnabled = @([System.IO.File]::ReadAllLines($telemetryPath) | Where-Object { $_ -eq 'PROBE_READY' }).Count -gt 0
            }
            if (-not $probeEnabled) { Start-Sleep -Milliseconds 250 }
        }
    }

    if ($ClientBProcessId -gt 0) {
        $clientBInfo = Get-ClientProcessInfo -ProcessId $ClientBProcessId -ExpectedPath $clientExe -TimeoutSeconds $WindowTimeoutSeconds -Label 'Client B'
    } else {
        $startedB = Start-Process -FilePath $clientExe -WorkingDirectory (Split-Path -Parent $clientExe) -PassThru
        $launchedB = $true
        $clientBInfo = Get-ClientProcessInfo -ProcessId $startedB.Id -ExpectedPath $clientExe -TimeoutSeconds $WindowTimeoutSeconds -Label 'Client B'
    }

    if ($clientAInfo.ProcessId -eq $clientBInfo.ProcessId) { throw 'Client A and Client B resolve to the same process; two independent WPF processes are required.' }
    if ($clientAInfo.SessionId -ne [System.Diagnostics.Process]::GetCurrentProcess().SessionId -or
        $clientBInfo.SessionId -ne [System.Diagnostics.Process]::GetCurrentProcess().SessionId) {
        throw 'Both clients must run in the same interactive Windows session as this PowerShell console.'
    }

    $session = [ordered]@{
        SchemaVersion = '1.0'
        Phase = '54R9'
        RunId = $runId
        RunDirectory = $runDirectory
        StartedAt = [DateTimeOffset]::Now.ToString('o')
        ClientRepository = Get-RepositorySnapshot -Path $repoRoot
        ServerRepository = Get-RepositorySnapshot -Path $serverRoot
        Clients = [ordered]@{
            A = [ordered]@{ Initial = $clientAInfo; Reused = ($ClientAProcessId -gt 0); LauncherOwned = $launchedA; InputProbeEnabled = $probeEnabled; TelemetryPath = $telemetryPath }
            B = [ordered]@{ Initial = $clientBInfo; Reused = ($ClientBProcessId -gt 0); LauncherOwned = $launchedB; InputProbeEnabled = $false; TelemetryPath = $null }
        }
        ServerLaunchedByPackage = $false
        RuntimeAcceptanceStarted = $false
        RunnerReportPath = (Join-Path $runDirectory 'uiacceptance-client-a.json')
        CheckpointsPath = (Join-Path $runDirectory 'operator-checkpoints.jsonl')
        HistoricalEvidence = @(
            'Phase 54R5 demo UIA and input reports are historical only.'
            'Phase 54R6 closeout reports no authenticated two-client operator run; neither is R9 runtime evidence.'
        )
    }
    Write-JsonFile -Path (Join-Path $runDirectory 'session.json') -Value $session
} catch {
    $failure = $_.Exception.Message -replace '[\r\n]+', ' '
    $startupStatus = [ordered]@{
        SchemaVersion = '1.0'
        Phase = '54R9'
        RunId = $runId
        Status = 'STARTUP_FAILED'
        StartedAt = [DateTimeOffset]::Now.ToString('o')
        Failure = $failure
        ClientA = if ($null -ne $clientAInfo) { [ordered]@{ ProcessId = $clientAInfo.ProcessId; Executable = [System.IO.Path]::GetFileName($clientAInfo.ExecutablePath); SessionId = $clientAInfo.SessionId; WindowVerified = [bool]$clientAInfo.WindowVerified } } else { $null }
        ClientB = if ($null -ne $clientBInfo) { [ordered]@{ ProcessId = $clientBInfo.ProcessId; Executable = [System.IO.Path]::GetFileName($clientBInfo.ExecutablePath); SessionId = $clientBInfo.SessionId; WindowVerified = [bool]$clientBInfo.WindowVerified } } else { $null }
        LauncherStartedClientA = $launchedA
        LauncherStartedClientB = $launchedB
        ServerLaunchedByPackage = $false
    }
    try { Write-JsonFile -Path (Join-Path $runDirectory 'startup-status.json') -Value $startupStatus } catch { }
    Write-Host "Startup did not establish two validated clients: $failure" -ForegroundColor Red
    Write-Host "The isolated run directory is preserved for inspection: $runDirectory" -ForegroundColor Yellow
    if ($null -ne $clientAInfo) { Write-Host "Client A PID: $($clientAInfo.ProcessId)" }
    if ($null -ne $clientBInfo) { Write-Host "Client B PID: $($clientBInfo.ProcessId)" }
    Write-Host 'The launcher does not start a server and does not terminate any client process.'
    exit 2
}

Write-Host "Phase 54R9 run: $runId" -ForegroundColor Cyan
Write-Host "Run directory: $runDirectory"
Write-Host "Client A: PID $($clientAInfo.ProcessId), $($clientAInfo.ExecutablePath), session $($clientAInfo.SessionId), probe enabled=$probeEnabled"
Write-Host "Client B: PID $($clientBInfo.ProcessId), $($clientBInfo.ExecutablePath), session $($clientBInfo.SessionId)"
Write-Host 'No server was started. Use the actual server and authenticate both clients through the nexIRC UI.'
Write-Host 'Map the two visible windows to the printed PIDs before labeling them A and B. Do not enter passwords or account names into this console.'

if ($PrepareOnly) {
    Write-Host 'Preparation complete. To run the guided checkpoints:'
    Write-Host "  & `"$repoRoot\tools\phase54\Invoke-Phase54R9Acceptance.ps1`" -RunDirectory `"$runDirectory`""
    Write-Host 'To generate the sanitized report afterward:'
    Write-Host "  & `"$repoRoot\tools\phase54\Complete-Phase54R9.ps1`" -RunDirectory `"$runDirectory`""
    return
}

try {
    & (Join-Path $PSScriptRoot 'Invoke-Phase54R9Acceptance.ps1') -RunDirectory $runDirectory
} catch {
    Write-Host ("Guided workflow stopped: " + ($_.Exception.Message -replace '[\r\n]+', ' ')) -ForegroundColor Red
} finally {
    try {
        & (Join-Path $PSScriptRoot 'Complete-Phase54R9.ps1') -RunDirectory $runDirectory
    } catch {
        Write-Host ("Sanitized report generation could not finish automatically. Run Complete-Phase54R9.ps1 manually. " + ($_.Exception.Message -replace '[\r\n]+', ' ')) -ForegroundColor Yellow
    }
}
