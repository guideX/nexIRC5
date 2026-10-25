[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunDirectory,
    [ValidateRange(30, 3600)]
    [int]$CheckpointTimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\phase54r9'))
$runDirectory = [System.IO.Path]::GetFullPath($RunDirectory)
$prefix = $artifactRoot.TrimEnd('\') + '\'
if (-not $runDirectory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Run directory must be inside $artifactRoot."
}
$sessionPath = Join-Path $runDirectory 'session.json'
$checkpointsPath = Join-Path $runDirectory 'operator-checkpoints.jsonl'
if (-not (Test-Path -LiteralPath $sessionPath -PathType Leaf)) { throw "Run manifest not found: $sessionPath" }
$session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json

function Write-Session {
    $temporaryPath = Join-Path $runDirectory 'session.json.tmp'
    $json = ConvertTo-Json -InputObject $session -Depth 12
    [System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $sessionPath -Force
}

function Read-ConsoleText {
    param([string]$Prompt, [int]$TimeoutSeconds = $CheckpointTimeoutSeconds)
    [Console]::Write($Prompt)
    $text = [System.Text.StringBuilder]::new()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true)
            if ($key.Key -eq [ConsoleKey]::Enter) {
                [Console]::WriteLine()
                return [pscustomobject]@{ Value = $text.ToString(); TimedOut = $false }
            }
            if ($key.Key -eq [ConsoleKey]::Backspace) {
                if ($text.Length -gt 0) {
                    $text.Length--
                    [Console]::Write("`b `b")
                }
                continue
            }
            if (-not [char]::IsControl($key.KeyChar)) {
                [void]$text.Append($key.KeyChar)
                [Console]::Write($key.KeyChar)
            }
        } else {
            Start-Sleep -Milliseconds 100
        }
    }
    [Console]::WriteLine()
    return [pscustomobject]@{ Value = $null; TimedOut = $true }
}

function Sanitize-Comment {
    param([string]$Value)
    $safe = ($Value -replace '[\x00-\x1F\x7F]', ' ').Trim()
    $safe = $safe -replace '(?i)\b(password|passwd|token|secret|credential|authorization)\s*[:=]\s*\S+', '$1=[REDACTED]'
    $safe = $safe -replace '(?i)\bbearer\s+\S+', 'Bearer [REDACTED]'
    $safe = $safe -replace '(?i)\b(sasl|resume)\s*[:=]\s*\S+', '$1=[REDACTED]'
    if ($safe.Length -gt 200) { $safe = $safe.Substring(0, 200) }
    return $safe
}

function Get-ProbeLineCount {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return [System.IO.File]::ReadLines($Path).Count } catch { return $null }
}

function Get-CurrentTelemetryPath {
    if (-not [string]::IsNullOrWhiteSpace($session.Clients.A.ReopenedTelemetryPath)) {
        if ($session.Clients.A.ReopenedInputProbeEnabled) { return $session.Clients.A.ReopenedTelemetryPath }
        return $null
    }
    if ($session.Clients.A.InputProbeEnabled) { return $session.Clients.A.TelemetryPath }
    return $null
}

function Save-CheckpointRecord {
    param([object]$Record)
    $json = ConvertTo-Json -InputObject $Record -Compress -Depth 8
    [System.IO.File]::AppendAllText($checkpointsPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-Checkpoint {
    param(
        [string]$Id,
        [string]$Title,
        [string]$Instruction,
        [bool]$Required = $true,
        [switch]$ObserveTelemetry,
        [switch]$MutationAttempt
    )
    $stop = $false
    $comment = ''
    Write-Host "`n[$Id] $Title" -ForegroundColor Cyan
    Write-Host $Instruction
    Write-Host 'Evidence source for this result will be recorded as operator-reported.' -ForegroundColor DarkGray
    $startLine = if ($ObserveTelemetry) { Get-ProbeLineCount -Path (Get-CurrentTelemetryPath) } else { $null }
    $answer = Read-ConsoleText -Prompt 'Result [P]ass / [F]ail / [U]nverified / [B]locked / [Q]uit this run: '
    if ($answer.TimedOut) {
        $result = 'BLOCKED'
        $comment = 'No operator response before the configured checkpoint timeout.'
        $stop = $true
    } else {
        switch -Regex ($answer.Value.Trim()) {
            '^(?i:p|pass)$' { $result = 'PASS'; break }
            '^(?i:f|fail)$' { $result = 'FAIL'; break }
            '^(?i:u|unverified)$' { $result = 'UNVERIFIED'; break }
            '^(?i:b|blocked)$' { $result = 'BLOCKED'; break }
            '^(?i:q|quit)$' { $result = 'UNVERIFIED'; $stop = $true; break }
            default { $result = 'UNVERIFIED'; $stop = $true; break }
        }
        if ($stop -and [string]::IsNullOrEmpty($comment)) { $comment = 'Operator stopped at this checkpoint or entered an unrecognized response.' }
        if (-not $stop -and $result -in @('FAIL', 'UNVERIFIED', 'BLOCKED')) {
            $commentAnswer = Read-ConsoleText -Prompt 'Optional short comment (no account names, conversation names, message/draft text, or secrets; Enter to skip): ' -TimeoutSeconds ([Math]::Min(300, $CheckpointTimeoutSeconds))
            if (-not $commentAnswer.TimedOut) { $comment = Sanitize-Comment -Value $commentAnswer.Value }
        }
    }
    $endLine = if ($ObserveTelemetry) { Get-ProbeLineCount -Path (Get-CurrentTelemetryPath) } else { $null }
    $record = [pscustomobject]@{
        RecordType = 'checkpoint'
        Id = $Id
        Title = $Title
        Required = $Required
        Result = $result
        EvidenceProvenance = 'operator-reported'
        RecordedAt = [DateTimeOffset]::Now.ToString('o')
        Comment = $comment
        TelemetryPath = if ($null -ne $startLine) { Get-CurrentTelemetryPath } else { $null }
        TelemetryLineRange = if ($null -ne $startLine -and $null -ne $endLine) { @($startLine, $endLine) } else { $null }
    }
    Save-CheckpointRecord -Record $record
    if ($MutationAttempt -and -not $stop -and $result -in @('PASS', 'FAIL')) {
        $session.RuntimeAcceptanceStarted = $true
        if (-not $session.RuntimeAcceptanceStartedAt) { $session | Add-Member -NotePropertyName RuntimeAcceptanceStartedAt -NotePropertyValue ([DateTimeOffset]::Now.ToString('o')) -Force }
        Write-Session
    }
    return [pscustomobject]@{ Result = $result; Stop = [bool]$stop; Record = $record }
}

function Invoke-OpaqueFingerprintPrompt {
    param([string]$Name, [bool]$AllowList = $false)
    $format = if ($AllowList) { 'comma-separated 6–64 digit hex fingerprints' } else { 'a 6–64 digit hex fingerprint' }
    $answer = Read-ConsoleText -Prompt "$Name ($format, blank if unavailable): " -TimeoutSeconds ([Math]::Min(180, $CheckpointTimeoutSeconds))
    if ($answer.TimedOut -or [string]::IsNullOrWhiteSpace($answer.Value)) { return 'unavailable' }
    $values = if ($AllowList) { $answer.Value.Split(',') } else { @($answer.Value) }
    if ($values.Count -eq 0 -or $values.Count -gt 64 -or @($values | Where-Object { $_.Trim() -notmatch '^[a-fA-F0-9]{6,64}$' }).Count -gt 0) {
        Write-Host 'Value was not an opaque hex fingerprint; it was omitted from the report.' -ForegroundColor Yellow
        return 'unavailable'
    }
    return (($values | ForEach-Object { $_.Trim().ToLowerInvariant() }) -join ',')
}

function Save-MutationFingerprintSnapshot {
    param([string]$Stage, [string]$Label)
    Write-Host "Record safe before/after state fingerprints for $Label. Enter blank when a value is unavailable; do not enter names or content." -ForegroundColor DarkCyan
    $aValues = [ordered]@{
        PinOrder = Invoke-OpaqueFingerprintPrompt -Name "Client A pin-order fingerprints ($Label)" -AllowList
        ActiveView = Invoke-OpaqueFingerprintPrompt -Name "Client A active-view fingerprint ($Label)"
        FocusedControl = Invoke-OpaqueFingerprintPrompt -Name "Client A focused-control fingerprint ($Label)"
        Draft = Invoke-OpaqueFingerprintPrompt -Name "Client A unsent-draft fingerprint ($Label)"
        Transcript = Invoke-OpaqueFingerprintPrompt -Name "Client A transcript fingerprint ($Label)"
        Unread = Invoke-OpaqueFingerprintPrompt -Name "Client A unread-state fingerprint ($Label)"
        ViewIdentity = Invoke-OpaqueFingerprintPrompt -Name "Client A view-identity fingerprint ($Label)"
    }
    $bOrder = Invoke-OpaqueFingerprintPrompt -Name "Client B pin-order fingerprints ($Label)" -AllowList
    Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'baseline'; Stage = $Stage; Client = 'A'; Values = $aValues; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
    Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'baseline'; Stage = $Stage; Client = 'B'; Values = [ordered]@{ PinOrder = $bOrder }; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
}

function Add-RunnerUnavailableRecord {
    param([string]$Reason)
    Save-CheckpointRecord -Record ([pscustomobject]@{
        RecordType = 'runner'
        Id = 'UI54-ATTACHED-RUNNER'
        Result = 'UNVERIFIED'
        EvidenceProvenance = 'no evidence'
        Reason = $Reason
        RecordedAt = [DateTimeOffset]::Now.ToString('o')
    })
}

function Start-ReopenedClientA {
    $old = $session.Clients.A | Select-Object -ExpandProperty Initial
    if ($session.Clients.A.Reopened) { $old = $session.Clients.A.Reopened }
    $oldPid = [int]$old.ProcessId
    Write-Host "`nClose Client A (PID $oldPid) normally from its own window. Use the normal window close; do not choose an IRC leave/PART action." -ForegroundColor Cyan
    $close = Invoke-Checkpoint -Id 'REOPEN-CLOSE-A' -Title 'Close Client A normally' -Instruction 'Confirm that you closed A with the normal window close. The script separately checks whether the exact PID exited.'
    if ($close.Stop) { return $false }

    $exitResult = 'UNVERIFIED'
    $exitDetails = 'The exact Client A PID exit state could not be established; no replacement process was launched.'
    try {
        $oldProcess = Get-Process -Id $oldPid -ErrorAction Stop
        $oldProcess.Refresh()
        if ($oldProcess.HasExited) {
            $exitResult = 'PASS'
            $exitDetails = 'The designated Client A PID no longer exists.'
        } else {
            $exitResult = 'FAIL'
            $exitDetails = 'The designated Client A PID is still running; no replacement process was launched.'
        }
    } catch {
        if ($_.FullyQualifiedErrorId -like 'NoProcessFoundForGivenId*') {
            $exitResult = 'PASS'
            $exitDetails = 'The designated Client A PID no longer exists.'
        }
    }
    Save-CheckpointRecord -Record ([pscustomobject]@{
        RecordType = 'machine-check'
        Id = 'REOPEN-PROCESS-EXIT'
        Result = $exitResult
        EvidenceProvenance = 'automated observation'
        ProcessId = $oldPid
        RecordedAt = [DateTimeOffset]::Now.ToString('o')
        Details = $exitDetails
    })
    if ($exitResult -ne 'PASS') {
        Write-Host "Client A PID $oldPid has not been verified as exited ($exitResult). The package will not start a duplicate Client A." -ForegroundColor Yellow
        return $false
    }

    $exe = $session.Clients.A.Initial.ExecutablePath
    $newTelemetry = Join-Path $runDirectory ("client-a-reopened-{0}.jsonl" -f ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
    $argument = '--phase54r3-input-probe="{0}"' -f $newTelemetry
    $started = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList $argument -PassThru
    $newInfo = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        try {
            $p = Get-Process -Id $started.Id -ErrorAction Stop
            $p.Refresh()
            if ($p.MainWindowHandle -ne [IntPtr]::Zero -and $p.MainWindowTitle -eq 'nexIRC 5') {
                $actualPath = [System.IO.Path]::GetFullPath($p.MainModule.FileName)
                if (-not [string]::Equals($actualPath, [System.IO.Path]::GetFullPath($exe), [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Reopened Client A executable identity changed.'
                }
                $newInfo = [pscustomobject]@{
                    ProcessId = $p.Id
                    ExecutablePath = $actualPath
                    WindowTitle = $p.MainWindowTitle
                    WindowHandle = ('0x{0:X}' -f $p.MainWindowHandle.ToInt64())
                    SessionId = $p.SessionId
                    StartTime = $p.StartTime.ToUniversalTime().ToString('o')
                    WindowVerified = $true
                }
                break
            }
        } catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $newInfo) {
        Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'machine-check'; Id = 'REOPEN-NEW-WINDOW'; Result = 'BLOCKED'; EvidenceProvenance = 'automated observation'; ProcessId = $started.Id; RecordedAt = [DateTimeOffset]::Now.ToString('o'); Details = 'A new usable nexIRC 5 window was not established before the startup deadline.' })
        return $false
    }
    if ($newInfo.ProcessId -eq [int]$session.Clients.B.Initial.ProcessId) {
        throw 'Reopened Client A resolved to Client B process identity; two independent clients are required.'
    }
    if ($newInfo.SessionId -ne [System.Diagnostics.Process]::GetCurrentProcess().SessionId) {
        throw 'Reopened Client A is not in the same interactive Windows session as this acceptance workflow.'
    }
    $probeReady = $false
    $probeDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $probeDeadline -and -not $probeReady) {
        if (Test-Path -LiteralPath $newTelemetry -PathType Leaf) {
            $probeReady = @([System.IO.File]::ReadAllLines($newTelemetry) | Where-Object { $_ -eq 'PROBE_READY' }).Count -gt 0
        }
        if (-not $probeReady) { Start-Sleep -Milliseconds 250 }
    }
    $session.Clients.A | Add-Member -NotePropertyName Reopened -NotePropertyValue $newInfo -Force
    $session.Clients.A | Add-Member -NotePropertyName ReopenedTelemetryPath -NotePropertyValue $newTelemetry -Force
    $session.Clients.A | Add-Member -NotePropertyName ReopenedInputProbeEnabled -NotePropertyValue $probeReady -Force
    Write-Session
    Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'machine-check'; Id = 'REOPEN-NEW-WINDOW'; Result = if ($probeReady) { 'PASS' } else { 'UNVERIFIED' }; EvidenceProvenance = 'automated observation'; ProcessId = $newInfo.ProcessId; Executable = [System.IO.Path]::GetFileName($newInfo.ExecutablePath); SessionId = $newInfo.SessionId; RecordedAt = [DateTimeOffset]::Now.ToString('o'); Details = if ($probeReady) { 'New Client A executable and WPF window verified; new telemetry file contains PROBE_READY.' } else { 'New Client A executable and WPF window verified; opt-in probe readiness was not observed.' } })
    Write-Host "Reopened Client A: PID $($newInfo.ProcessId). Sign in through the supported UI, then verify capability and pin convergence against Client B."
    $reauth = Invoke-Checkpoint -Id 'REOPEN-SESSION' -Title 'Reauthenticate reopened Client A' -Instruction 'Use the normal nexIRC sign-in flow. Confirm the actual server connection and the same account association without entering the account name here.'
    if ($reauth.Stop) { return $false }
    if ($reauth.Result -ne 'PASS') { Write-Host 'Reopened session verification did not PASS; post-restart capability and convergence claims are left unverified.' -ForegroundColor Yellow; return $false }
    $caps = Invoke-Checkpoint -Id 'REOPEN-CAPABILITY' -Title 'Confirm synchronized-pins capability after reopen' -Instruction 'Confirm Client A restored the documented resume/state/pins capability with the actual server.'
    if ($caps.Stop) { return $false }
    if ($caps.Result -ne 'PASS') { Write-Host 'Post-restart capability verification did not PASS; convergence claims are left unverified.' -ForegroundColor Yellow; return $false }
    $converged = Invoke-Checkpoint -Id 'REOPEN-CONVERGENCE' -Title 'Confirm durable pin convergence' -Instruction 'Compare membership and order with still-connected Client B. Confirm no duplicate or stale rows. Record only opaque fingerprints if available.' -ObserveTelemetry
    if ($converged.Stop) { return $false }
    $persistence = Invoke-Checkpoint -Id 'REOPEN-PERSISTENCE' -Title 'Confirm durable state after process restart' -Instruction 'Compare the reopened A pin membership/order with its pre-close baseline and current B. Check any other restart persistence only when the existing Phase 54 contract requires it. Focus and draft are transient unless that contract says otherwise.' -ObserveTelemetry
    if ($persistence.Stop) { return $false }
    $afterAOrder = Invoke-OpaqueFingerprintPrompt -Name 'Client A pin-order fingerprints after reopen' -AllowList
    $afterAView = Invoke-OpaqueFingerprintPrompt -Name 'Client A view-identity fingerprint after reopen'
    $afterBOrder = Invoke-OpaqueFingerprintPrompt -Name 'Client B pin-order fingerprints after reopen' -AllowList
    Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'baseline'; Stage = 'after-reopen'; Client = 'A'; Values = [ordered]@{ PinOrder = $afterAOrder; ViewIdentity = $afterAView }; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
    Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'baseline'; Stage = 'after-reopen'; Client = 'B'; Values = [ordered]@{ PinOrder = $afterBOrder }; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
    return $true
}

if ($Host.Name -ne 'ConsoleHost' -or [Console]::IsInputRedirected -or [Console]::IsOutputRedirected) {
    Write-Host 'This workflow needs a local interactive Windows PowerShell console. Reopen Windows PowerShell or PowerShell 7 and use the run directory shown by Start-Phase54R9.ps1.' -ForegroundColor Red
    Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'workflow'; Id = 'INTERACTIVE-CONSOLE'; Result = 'BLOCKED'; EvidenceProvenance = 'automated observation'; Reason = 'Input or output is redirected, or the host is not ConsoleHost.'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
    return
}
if ([string]::IsNullOrWhiteSpace($session.Clients.A.Initial.ProcessId) -or [string]::IsNullOrWhiteSpace($session.Clients.B.Initial.ProcessId)) {
    throw 'The run manifest does not contain two validated client processes.'
}

$script:StopRequested = $false
$script:CheckpointTimeoutSeconds = $CheckpointTimeoutSeconds
$script:ProbePath = $session.Clients.A.TelemetryPath
$session | Add-Member -NotePropertyName AcceptanceWorkflowStartedAt -NotePropertyValue ([DateTimeOffset]::Now.ToString('o')) -Force
Write-Session

Write-Host "Phase 54R9 guided acceptance — run $($session.RunId)" -ForegroundColor Green
Write-Host "Client A PID: $($session.Clients.A.Initial.ProcessId); Client B PID: $($session.Clients.B.Initial.ProcessId)"
Write-Host 'The script will not authenticate, click, type, mutate pins, or claim operator actions. Perform all requested actions in the real WPF windows.'
Write-Host 'At any status prompt choose Q to stop safely and save partial results. Each prompt has a bounded timeout of up to 15 minutes.'
Write-Host 'Use harmless test conversations and drafts. Never type a password, account name, private message, or draft text into this console.'

$prerequisiteSteps = @(
    @{ Id = 'CLIENT-PAIR'; Title = 'Identify Client A and Client B'; Instruction = 'Use the printed PIDs and Task Manager Details view to map the two visible nexIRC 5 windows. Confirm both are usable, distinct windows in this interactive session.' },
    @{ Id = 'SERVER-IDENTITY'; Title = 'Confirm the actual server'; Instruction = 'Connect both clients to the intended actual nexIRC Server using the supported UI. Do not use --demo. If safe, a server label may be recorded below; do not include a login or secret.' },
    @{ Id = 'AUTH-SAME-ACCOUNT'; Title = 'Confirm both clients use the same account'; Instruction = 'Verify through the existing authentication/session UI or safe session evidence. Do not enter or report the account name, password, token, or resume credential.' },
    @{ Id = 'CAPABILITY'; Title = 'Confirm synchronized-pins capability on both clients'; Instruction = 'Confirm the existing contract capability is established on A and B: resume=true, state=true, pins=true (nexirc/resume, nexirc/state, nexirc/pins). Similar conversation names alone do not pass.' },
    @{ Id = 'INITIAL-CONVERGENCE'; Title = 'Confirm initial pin convergence'; Instruction = 'Compare the same initial synchronized membership and order in A and B. Confirm there are no duplicate rows.' },
    @{ Id = 'CONTROLLED-FIXTURE'; Title = 'Prepare controlled test state'; Instruction = 'Use at least four harmless test pins and test conversations. Keep A on an active conversation X, with transcript and known unread state ready. Create a distinctive harmless unsent draft, focus the composer, and note selection/focus without recording text.' },
    @{ Id = 'BASELINE-A'; Title = 'Record Client A baseline'; Instruction = 'Confirm and record safe opaque fingerprints or counts for pin order, active view, focus/control, draft, transcript, unread state, view identity, and relevant JOIN/PART lifecycle. Do not record names or content.' },
    @{ Id = 'BASELINE-B'; Title = 'Record Client B baseline'; Instruction = 'Confirm B has the same pin membership/order and record its opaque order fingerprint or count. Preserve the baseline for both remote actions.' }
)

$prerequisitesPassed = $true
foreach ($step in $prerequisiteSteps) {
    $result = Invoke-Checkpoint -Id $step.Id -Title $step.Title -Instruction $step.Instruction
    if ($result.Stop) { $script:StopRequested = $true; $prerequisitesPassed = $false; break }
    if ($step.Id -eq 'SERVER-IDENTITY') {
        $label = Read-ConsoleText -Prompt 'Optional sanitized server label (hostname or short safe label; blank to omit): ' -TimeoutSeconds ([Math]::Min(180, $CheckpointTimeoutSeconds))
        $safeLabel = 'not recorded'
        if (-not $label.TimedOut -and $label.Value -match '^[A-Za-z0-9._:\[\]-]{1,100}$') { $safeLabel = $label.Value }
        elseif (-not [string]::IsNullOrWhiteSpace($label.Value)) { Write-Host 'Server label omitted because it did not match the safe label format.' -ForegroundColor Yellow }
        Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'environment'; Key = 'ServerIdentityLabel'; Value = $safeLabel; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
    }
    if ($step.Id -eq 'BASELINE-A') {
        $baseline = [ordered]@{
            PinOrder = Invoke-OpaqueFingerprintPrompt -Name 'Client A pin-order fingerprint list' -AllowList
            ActiveView = Invoke-OpaqueFingerprintPrompt -Name 'Client A active-view fingerprint'
            FocusedControl = Invoke-OpaqueFingerprintPrompt -Name 'Client A focused-control fingerprint'
            Draft = Invoke-OpaqueFingerprintPrompt -Name 'Client A unsent-draft fingerprint'
            Transcript = Invoke-OpaqueFingerprintPrompt -Name 'Client A transcript fingerprint'
            Unread = Invoke-OpaqueFingerprintPrompt -Name 'Client A unread-state fingerprint'
            ViewIdentity = Invoke-OpaqueFingerprintPrompt -Name 'Client A view-identity fingerprint'
        }
        Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'baseline'; Stage = 'initial'; Client = 'A'; Values = $baseline; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
    }
    if ($step.Id -eq 'BASELINE-B') {
        $pinOrder = Invoke-OpaqueFingerprintPrompt -Name 'Client B pin-order fingerprint list' -AllowList
        Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'baseline'; Stage = 'initial'; Client = 'B'; Values = [ordered]@{ PinOrder = $pinOrder }; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
    }
    if ($result.Result -ne 'PASS') { $prerequisitesPassed = $false }
}

if (-not $script:StopRequested -and $prerequisitesPassed -and $session.Clients.A.Reused -and $session.Clients.A.InputProbeEnabled) {
    $probeAssociation = Invoke-Checkpoint -Id 'INPUT-PROBE-ASSOCIATION' -Title 'Confirm reused Client A telemetry belongs to its live process' -Instruction 'Confirm the supplied PROBE_READY file was created for this exact live Client A process and is not a stale file from another run. If you cannot establish this, choose UNVERIFIED; the workflow will skip telemetry and the attached runner.' -Required:$false
    if ($probeAssociation.Stop) { $script:StopRequested = $true }
    if ($probeAssociation.Result -ne 'PASS') {
        $session.Clients.A.InputProbeEnabled = $false
        Write-Session
        Write-Host 'The reused Client A telemetry association was not verified. Telemetry and the attached runner are disabled for this process.' -ForegroundColor Yellow
    }
}

if (-not $script:StopRequested -and $prerequisitesPassed) {
    Write-Host "`nRemote MOVE_TOP — keep Client A active on X with the composer focused and the harmless draft unsent. From Client B, use the real UI to move a different, nonactive pinned test conversation to the top. Wait for both projections to settle." -ForegroundColor Cyan
    $moveAction = Invoke-Checkpoint -Id 'MOVE-TOP-ACTION-PERFORMED' -Title 'Confirm the actual Client B MOVE_TOP action' -Instruction 'PASS if you executed the real MOVE_TOP action in B; FAIL if you attempted it but the UI action failed; UNVERIFIED if you cannot establish whether it occurred; BLOCKED if you could not attempt it.' -MutationAttempt
    if ($moveAction.Stop) { $script:StopRequested = $true }
    if (-not $script:StopRequested -and $moveAction.Result -in @('PASS', 'FAIL')) {
        $moveServer = Invoke-Checkpoint -Id 'MOVE-TOP-SERVER-ACCEPTANCE' -Title 'MOVE_TOP server acceptance' -Instruction 'PASS only if reliable existing server-side or authenticated server-ACK evidence independently shows this exact mutation was accepted. A changed order or A/B convergence alone is not server acceptance.'
        if ($moveServer.Stop) { $script:StopRequested = $true }
        if (-not $script:StopRequested) {
            $moveProjection = Invoke-Checkpoint -Id 'MOVE-TOP-CONVERGENCE' -Title 'MOVE_TOP projection and convergence' -Instruction 'Confirm the B-side order changed as requested, A received the remote projection, both clients converged, pin count/membership stayed correct, and no duplicate rows appeared.' -ObserveTelemetry
            if ($moveProjection.Stop) { $script:StopRequested = $true }
        }
        if (-not $script:StopRequested) {
            $movePreservation = Invoke-Checkpoint -Id 'MOVE-TOP-PRESERVATION' -Title 'MOVE_TOP preservation on Client A' -Instruction 'Confirm A still has active X, composer focus and selection as expected, the unsent draft fingerprint survives, transcript/unread/view identity are preserved, and no PART or unrelated lifecycle change occurred.' -ObserveTelemetry
            if ($movePreservation.Stop) { $script:StopRequested = $true }
        }
        if (-not $script:StopRequested) { Save-MutationFingerprintSnapshot -Stage 'after-move' -Label 'after remote MOVE_TOP' }
    }

    if (-not $script:StopRequested) {
        Write-Host "`nRemote nonactive UNPIN — keep A active on X with the composer focused and the harmless draft unsent. From B's actual UI, unpin a different nonactive test conversation. Leave at least two pins for the attached runner's separate focus checks." -ForegroundColor Cyan
        $unpinAction = Invoke-Checkpoint -Id 'UNPIN-NONACTIVE-ACTION-PERFORMED' -Title 'Confirm the actual Client B nonactive UNPIN action' -Instruction 'PASS if you executed the real UNPIN action in B; FAIL if you attempted it but the UI action failed; UNVERIFIED if you cannot establish whether it occurred; BLOCKED if you could not attempt it.' -MutationAttempt
        if ($unpinAction.Stop) { $script:StopRequested = $true }
        if (-not $script:StopRequested -and $unpinAction.Result -in @('PASS', 'FAIL')) {
            $unpinServer = Invoke-Checkpoint -Id 'UNPIN-NONACTIVE-SERVER-ACCEPTANCE' -Title 'Nonactive UNPIN server acceptance' -Instruction 'PASS only with independent existing server-side or authenticated server-ACK evidence for this mutation. Visual removal by itself does not pass this gate.'
            if ($unpinServer.Stop) { $script:StopRequested = $true }
            if (-not $script:StopRequested) {
                $unpinProjection = Invoke-Checkpoint -Id 'UNPIN-NONACTIVE-CONVERGENCE' -Title 'Nonactive UNPIN projection and convergence' -Instruction 'Confirm the chosen pin is removed in B and remotely removed in A, A/B converge, remaining order is correct, and no stale or duplicate row remains.' -ObserveTelemetry
                if ($unpinProjection.Stop) { $script:StopRequested = $true }
            }
            if (-not $script:StopRequested) {
                $unpinPreservation = Invoke-Checkpoint -Id 'UNPIN-NONACTIVE-PRESERVATION' -Title 'Nonactive UNPIN preservation on Client A' -Instruction 'Confirm X remains active, composer focus/selection and unsent draft survive, transcript/unread/view identity are preserved, no unexpected PART occurs, and unrelated lifecycle state remains unchanged.' -ObserveTelemetry
                if ($unpinPreservation.Stop) { $script:StopRequested = $true }
            }
            if (-not $script:StopRequested) { Save-MutationFingerprintSnapshot -Stage 'after-unpin' -Label 'after remote nonactive UNPIN' }
        }
    }

    if (-not $script:StopRequested) {
        if ($session.Clients.A.InputProbeEnabled -and (Test-Path -LiteralPath $session.Clients.A.TelemetryPath -PathType Leaf)) {
            Write-Host "`nLaunching the existing attached manual runner against Client A. It never sends input to or closes an attached client. Follow its two remote focus prompts and perform the corresponding MOVE_TOP/UNPIN in B's real UI." -ForegroundColor Cyan
            $runnerDll = Join-Path $repoRoot 'tools\nexIRC.UiAcceptance\bin\Debug\net10.0-windows\nexIRC.UiAcceptance.dll'
            $runnerExe = Join-Path $repoRoot 'src\nexIRC.Desktop\bin\Debug\net10.0-windows\nexIRC.Desktop.exe'
            & dotnet $runnerDll --mode manual --pid ([string]$session.Clients.A.Initial.ProcessId) --client $runnerExe --telemetry $session.Clients.A.TelemetryPath --timeout 30 --checkpoint-timeout $CheckpointTimeoutSeconds --report $session.RunnerReportPath
            $runnerExitCode = $LASTEXITCODE
            Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'runner'; Id = 'UI54-ATTACHED-RUNNER'; ExitCode = $runnerExitCode; ReportPath = $session.RunnerReportPath; EvidenceProvenance = 'automated observation'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
            $runnerAction = Invoke-Checkpoint -Id 'RUNNER-REMOTE-ACTIONS-PERFORMED' -Title 'Confirm attached runner actions' -Instruction 'Report PASS only if you performed both instructed real UI mutations in Client B during the runner prompts. If the runner timed out before either action, choose Q or UNVERIFIED and explain briefly.' -MutationAttempt
            if ($runnerAction.Stop) { $script:StopRequested = $true }
        } else {
            $why = if ($session.Clients.A.Reused) { 'Reused Client A had no verified telemetry file association; no empty or fabricated telemetry was created.' } else { 'The launched Client A did not expose a ready opt-in probe before the bounded startup deadline.' }
            Add-RunnerUnavailableRecord -Reason $why
            Write-Host "Attached UIA runner skipped: $why" -ForegroundColor Yellow
        }
    }

    if (-not $script:StopRequested) {
        $gateC = @(
            @{ Id = 'MOUSE-PINNED-SELECTION'; Title = 'Mouse selection of a pinned row'; Instruction = 'In A, click a pinned row with the real mouse. Confirm it selects the intended destination and preserves the state required by the existing contract.' },
            @{ Id = 'MOUSE-CONTEXT-PIN'; Title = 'Mouse PIN through the WPF context menu'; Instruction = 'Use a harmless unpinned test conversation. Right-click its actual row and choose Pin conversation. If the click hits a Grid or no WPF row/menu action is observed, record FAIL or UNVERIFIED.' },
            @{ Id = 'MOUSE-MOVE-TOP'; Title = 'Mouse MOVE_TOP through the WPF context menu'; Instruction = 'Right-click a nonfirst pinned test row and choose Move to top. Confirm actual order and membership changes through A and B.' },
            @{ Id = 'MOUSE-UNPIN'; Title = 'Mouse UNPIN through the WPF context menu'; Instruction = 'Right-click a nonactive pinned test row and choose Unpin conversation. Confirm only the selected pin is removed and the other client converges.' },
            @{ Id = 'KEYBOARD-NAVIGATION'; Title = 'Keyboard navigation in the conversation and Pinned lists'; Instruction = 'Use actual arrow/tab navigation and confirm focus, selected-item state, and intended destination. The probe records classifications only; it does not record key values.' },
            @{ Id = 'KEYBOARD-CONTEXT'; Title = 'Apps key or Shift+F10 context menu'; Instruction = 'Focus a real row and open its context menu using Apps or Shift+F10. Confirm WPF context-menu opening and visible/focused menu items. A Win32 message without WPF event delivery is not a pass.' },
            @{ Id = 'KEYBOARD-PIN-ACTION'; Title = 'Keyboard activation of a pin action'; Instruction = 'Choose a context-menu action using only the keyboard. Confirm the intended WPF action and expected projection change. Note any incomplete Apps-key event delivery.' },
            @{ Id = 'PINNED-ACCESSIBILITY'; Title = 'Pinned list accessibility'; Instruction = 'Confirm the list and rows have accessible names and roles, enabled/focusable state, selected-item state, correct UIA targeting, and no duplicates. Use the attached runner report where present.' },
            @{ Id = 'GATE-C-PRESERVATION'; Title = 'Gate C focus and lifecycle preservation'; Instruction = 'Confirm selection, focus, transcript/unread/draft and view identity remain as required, and no unexpected PART or unrelated conversation lifecycle event occurred.' }
        )
        foreach ($step in $gateC) {
            $checkpoint = Invoke-Checkpoint -Id $step.Id -Title $step.Title -Instruction $step.Instruction -ObserveTelemetry
            if ($checkpoint.Stop) { $script:StopRequested = $true; break }
        }
    }

    if (-not $script:StopRequested) {
        $restartBefore = Invoke-Checkpoint -Id 'REOPEN-BASELINE' -Title 'Record pins before close/reopen' -Instruction 'Record A/B synchronized membership and order, and the relevant view identity. Draft and focus need not survive process restart unless the existing contract explicitly requires them.' -ObserveTelemetry
        if ($restartBefore.Stop) { $script:StopRequested = $true }
        if (-not $restartBefore.Stop) {
            $beforeAOrder = Invoke-OpaqueFingerprintPrompt -Name 'Client A pin-order fingerprints before reopen' -AllowList
            $beforeAView = Invoke-OpaqueFingerprintPrompt -Name 'Client A view-identity fingerprint before reopen'
            $beforeBOrder = Invoke-OpaqueFingerprintPrompt -Name 'Client B pin-order fingerprints before reopen' -AllowList
            Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'baseline'; Stage = 'before-reopen'; Client = 'A'; Values = [ordered]@{ PinOrder = $beforeAOrder; ViewIdentity = $beforeAView }; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
            Save-CheckpointRecord -Record ([pscustomobject]@{ RecordType = 'baseline'; Stage = 'before-reopen'; Client = 'B'; Values = [ordered]@{ PinOrder = $beforeBOrder }; EvidenceProvenance = 'operator-reported'; RecordedAt = [DateTimeOffset]::Now.ToString('o') })
        }
    }
    if (-not $script:StopRequested) { $null = Start-ReopenedClientA }
}

if (-not $prerequisitesPassed -and -not $script:StopRequested) {
    Write-Host 'A required prerequisite did not PASS. Later acceptance claims are blocked; the remaining gates will be recorded as having no evidence.' -ForegroundColor Yellow
    $session | Add-Member -NotePropertyName PrerequisitesPassed -NotePropertyValue $false -Force
} else {
    $session | Add-Member -NotePropertyName PrerequisitesPassed -NotePropertyValue ([bool]$prerequisitesPassed) -Force
}
$session | Add-Member -NotePropertyName AcceptanceWorkflowCompletedAt -NotePropertyValue ([DateTimeOffset]::Now.ToString('o')) -Force
$session | Add-Member -NotePropertyName OperatorStoppedAtCheckpoint -NotePropertyValue ([bool]$script:StopRequested) -Force
Write-Session
Write-Host "`nCheckpoint journal saved: $checkpointsPath" -ForegroundColor Green
Write-Host 'Run Complete-Phase54R9.ps1 -RunDirectory <this run directory> to write or refresh a sanitized report. The launcher runs it automatically.'
